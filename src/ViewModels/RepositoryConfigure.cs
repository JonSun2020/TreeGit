using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class RepositoryConfigure : ObservableObject
    {
        public string UserName
        {
            get;
            set;
        }

        public string UserEmail
        {
            get;
            set;
        }

        public List<string> Remotes
        {
            get;
        }

        public string DefaultRemote { get; set; }

        public int PreferredMergeMode { get; set; }

        public bool GPGCommitSigningEnabled
        {
            get;
            set;
        }

        public bool GPGTagSigningEnabled
        {
            get;
            set;
        }

        public string GPGUserSigningKey
        {
            get;
            set;
        }

        public string HttpProxy
        {
            get => _httpProxy;
            set => SetProperty(ref _httpProxy, value);
        }

        public string ConventionalTypesOverride
        {
            get => _repo.Settings.ConventionalTypesOverride;
            set
            {
                if (_repo.Settings.ConventionalTypesOverride != value)
                {
                    _repo.Settings.ConventionalTypesOverride = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool EnablePruneOnFetch
        {
            get;
            set;
        }

        public bool AskBeforeAutoUpdatingSubmodules { get; set; }

        public bool EnableRecursiveWhenAutoUpdatingSubmodules { get; set; }

        public AvaloniaList<Models.CommitTemplate> CommitTemplates { get; } = [];
        public bool UseGlobalIdentity { get; set; }
        public bool AutoRefreshEnabled { get; set; }
        public bool BackgroundFetchEnabled { get; set; }

        public Models.CommitTemplate SelectedCommitTemplate
        {
            get => _selectedCommitTemplate;
            set => SetProperty(ref _selectedCommitTemplate, value);
        }

        public AvaloniaList<Models.IssueTracker> IssueTrackers
        {
            get;
        } = [];

        public Models.IssueTracker SelectedIssueTracker
        {
            get => _selectedIssueTracker;
            set => SetProperty(ref _selectedIssueTracker, value);
        }

        public List<string> AvailableOpenAIServices
        {
            get;
            private set;
        }

        public string PreferredOpenAIService
        {
            get => _repo.Settings.PreferredOpenAIService;
            set => _repo.Settings.PreferredOpenAIService = value;
        }

        public AvaloniaList<Models.CustomAction> CustomActions
        {
            get => _repo.Settings.CustomActions;
        }

        public Models.CustomAction SelectedCustomAction
        {
            get => _selectedCustomAction;
            set => SetProperty(ref _selectedCustomAction, value);
        }

        public RepositoryConfigure(Repository repo)
        {
            _repo = repo;

            Remotes = new List<string>();
            foreach (var remote in _repo.Remotes)
                Remotes.Add(remote.Name);

            DefaultRemote = repo.Settings.DefaultRemote;
            PreferredMergeMode = repo.Settings.PreferredMergeMode >= 0 && repo.Settings.PreferredMergeMode < Models.MergeMode.Supported.Length ? repo.Settings.PreferredMergeMode : 0;
            AskBeforeAutoUpdatingSubmodules = repo.Settings.AskBeforeAutoUpdatingSubmodules;
            EnableRecursiveWhenAutoUpdatingSubmodules = repo.Settings.EnableRecursiveWhenAutoUpdatingSubmodules;
            AutoRefreshEnabled = repo.Settings.AutoRefreshEnabled;
            BackgroundFetchEnabled = repo.Settings.BackgroundFetchEnabled;
            foreach (var template in repo.Settings.CommitTemplates)
                CommitTemplates.Add(new Models.CommitTemplate { Name = template.Name, Content = template.Content });
            _cachedLocal = new Commands.Config(repo.FullPath).ReadAll(true);
            UseGlobalIdentity = !_cachedLocal.ContainsKey("user.name") && !_cachedLocal.ContainsKey("user.email");
            _cached = new Commands.Config(repo.FullPath).ReadAll();
            if (_cached.TryGetValue("user.name", out var name))
                UserName = name;
            if (_cached.TryGetValue("user.email", out var email))
                UserEmail = email;
            if (_cached.TryGetValue("commit.gpgsign", out var gpgCommitSign))
                GPGCommitSigningEnabled = gpgCommitSign == "true";
            if (_cached.TryGetValue("tag.gpgsign", out var gpgTagSign))
                GPGTagSigningEnabled = gpgTagSign == "true";
            if (_cached.TryGetValue("user.signingkey", out var signingKey))
                GPGUserSigningKey = signingKey;
            if (_cached.TryGetValue("http.proxy", out var proxy))
                HttpProxy = proxy;
            if (_cached.TryGetValue("fetch.prune", out var prune))
                EnablePruneOnFetch = (prune == "true");

            // Read the actual sources, including before background repository metadata has loaded.
            var rules = new List<Models.IssueTracker>();
            var sharedFile = Path.Combine(repo.FullPath, ".issuetracker");
            if (File.Exists(sharedFile))
                Commands.IssueTracker.ReadFromConfig(new Commands.Config(repo.FullPath, sharedFile) { RaiseError = false }.ReadAll(), rules, true);
            Commands.IssueTracker.ReadFromConfig(_cachedLocal, rules, false);
            foreach (var rule in rules)
            {
                IssueTrackers.Add(CopyRule(rule));
                _originalIssueTrackers.Add(CopyRule(rule));
            }
        }

        public void ClearHttpProxy()
        {
            HttpProxy = string.Empty;
        }

        public void AddCommitTemplate()
        {
            var template = new Models.CommitTemplate() { Name = "New Template" };
            CommitTemplates.Add(template);
            SelectedCommitTemplate = template;
        }

        public void RemoveSelectedCommitTemplate()
        {
            if (_selectedCommitTemplate != null)
                CommitTemplates.Remove(_selectedCommitTemplate);
            SelectedCommitTemplate = null;
        }

        public List<string> GetRemoteVisitUrls()
        {
            var outs = new List<string>();
            foreach (var remote in _repo.Remotes)
            {
                if (remote.TryGetVisitURL(out var url))
                    outs.Add(url);
            }
            return outs;
        }

        public void AddIssueTracker(string name, string regex, string url)
        {
            var rule = new Models.IssueTracker()
            {
                IsShared = false,
                Name = name,
                RegexString = regex,
                URLTemplate = url,
            };

            IssueTrackers.Add(rule);
            SelectedIssueTracker = rule;
        }

        public void RemoveIssueTracker()
        {
            if (_selectedIssueTracker is { } rule)
                IssueTrackers.Remove(rule);

            SelectedIssueTracker = null;
        }

        public void AddNewCustomAction()
        {
            SelectedCustomAction = _repo.Settings.AddNewCustomAction();
        }

        public void RemoveSelectedCustomAction()
        {
            _repo.Settings.RemoveCustomAction(_selectedCustomAction);
            SelectedCustomAction = null;
        }

        public void MoveSelectedCustomActionUp()
        {
            if (_selectedCustomAction != null)
                _repo.Settings.MoveCustomActionUp(_selectedCustomAction);
        }

        public void MoveSelectedCustomActionDown()
        {
            if (_selectedCustomAction != null)
                _repo.Settings.MoveCustomActionDown(_selectedCustomAction);
        }

        public async Task SaveAsync()
        {
            if (_repo.Settings.LoadRecovery == 2) throw new IOException(App.Text("Repository.ConfigRecovery.Readonly"));
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rule in IssueTrackers)
            {
                if (string.IsNullOrWhiteSpace(rule.Name) || rule.Name.IndexOfAny(['\r', '\n', '\0']) >= 0)
                    throw new InvalidOperationException("提交文本链接的名称不能为空或包含换行符。");
                if (!names.Add(rule.Name))
                    throw new InvalidOperationException("提交文本链接的名称不能重复：" + rule.Name);
                if (!rule.IsRegexValid || string.IsNullOrWhiteSpace(rule.URLTemplate))
                    throw new InvalidOperationException("请检查提交文本链接的匹配表达式和链接模板：" + rule.Name);
            }
            var cached = new Dictionary<string, string>(_cached);
            var cachedLocal = new Dictionary<string, string>(_cachedLocal);
            using var watcher = _repo.LockWatcher();
            using var transaction = new Models.RepositoryConfigurationTransaction(_repo.FullPath, _repo.Settings.StoragePath);
            _saveTransaction = transaction;
            try
            {
                await SetIfChangedAsync("user.name", UseGlobalIdentity ? null : UserName, "", true);
                await SetIfChangedAsync("user.email", UseGlobalIdentity ? null : UserEmail, "", true);
                await SetIfChangedAsync("commit.gpgsign", GPGCommitSigningEnabled ? "true" : "false", "false");
                await SetIfChangedAsync("tag.gpgsign", GPGTagSigningEnabled ? "true" : "false", "false");
                await SetIfChangedAsync("user.signingkey", GPGUserSigningKey, "");
                await SetIfChangedAsync("http.proxy", HttpProxy, "");
                await SetIfChangedAsync("fetch.prune", EnablePruneOnFetch ? "true" : "false", "false");
                var linksChanged = await ApplyIssueTrackerChangesAsync();
                var settings = _repo.Settings;
                var staged = settings.Copy();
                staged.DefaultRemote = DefaultRemote ?? string.Empty;
                staged.PreferredMergeMode = PreferredMergeMode;
                staged.AskBeforeAutoUpdatingSubmodules = AskBeforeAutoUpdatingSubmodules;
                staged.EnableRecursiveWhenAutoUpdatingSubmodules = EnableRecursiveWhenAutoUpdatingSubmodules;
                staged.AutoRefreshEnabled = AutoRefreshEnabled;
                staged.BackgroundFetchEnabled = BackgroundFetchEnabled;
                staged.CommitTemplates = new AvaloniaList<Models.CommitTemplate>(CommitTemplates);
                var content = staged.Serialize();
                transaction.WriteSettings(content);
                transaction.Commit();

                settings.DefaultRemote = staged.DefaultRemote;
                settings.PreferredMergeMode = staged.PreferredMergeMode;
                settings.AskBeforeAutoUpdatingSubmodules = staged.AskBeforeAutoUpdatingSubmodules;
                settings.EnableRecursiveWhenAutoUpdatingSubmodules = staged.EnableRecursiveWhenAutoUpdatingSubmodules;
                settings.AutoRefreshEnabled = staged.AutoRefreshEnabled;
                settings.BackgroundFetchEnabled = staged.BackgroundFetchEnabled;
                settings.CommitTemplates = staged.CommitTemplates;
                settings.MarkSaved(content);
                if (linksChanged)
                {
                    _repo.IssueTrackers.Clear();
                    _repo.IssueTrackers.AddRange(IssueTrackers);
                }
            }
            catch
            {
                _cached.Clear();
                foreach (var pair in cached) _cached[pair.Key] = pair.Value;
                _cachedLocal.Clear();
                foreach (var pair in cachedLocal) _cachedLocal[pair.Key] = pair.Value;
                throw;
            }
            finally { _saveTransaction = null; }
        }

        private async Task SetIfChangedAsync(string key, string value, string defValue, bool forceLocal = false)
        {
            var remove = string.IsNullOrWhiteSpace(value);
            if (remove && !_cachedLocal.ContainsKey(key)) return;
            var current = forceLocal ? _cachedLocal.GetValueOrDefault(key) : _cached.GetValueOrDefault(key, defValue);
            if (!remove && value == current) return;
            if (!await new Commands.Config(_repo.FullPath, _saveTransaction.GitConfigPath) { RaiseError = false }.SetAsync(key, value))
                throw new InvalidOperationException("无法保存仓库配置：" + key);
            if (remove) { _cachedLocal.Remove(key); _cached.Remove(key); }
            else { _cachedLocal[key] = value; _cached[key] = value; }
        }

        private async Task<bool> ApplyIssueTrackerChangesAsync()
        {
            static async Task RequireSuccess(Task<bool> operation, string name)
            {
                if (!await operation)
                    throw new InvalidOperationException("无法保存提交文本链接：" + name);
            }

            var changed = false;
            var oldRules = new Dictionary<string, Models.IssueTracker>();
            foreach (var rule in _originalIssueTrackers)
                oldRules.Add(rule.Name, rule);

            foreach (var rule in IssueTrackers)
            {
                if (oldRules.TryGetValue(rule.Name, out var old))
                {
                    if (old.IsShared != rule.IsShared)
                    {
                        changed = true;
                        await RequireSuccess(new Commands.IssueTracker(_repo.FullPath, old.IsShared, _saveTransaction.IssueTrackerPath(old.IsShared)) { RaiseError = false }.RemoveAsync(old.Name), old.Name);
                        await RequireSuccess(new Commands.IssueTracker(_repo.FullPath, rule.IsShared, _saveTransaction.IssueTrackerPath(rule.IsShared)) { RaiseError = false }.AddAsync(rule), rule.Name);
                    }
                    else
                    {
                        if (!string.Equals(old.RegexString, rule.RegexString, StringComparison.Ordinal))
                        {
                            changed = true;
                            await RequireSuccess(new Commands.IssueTracker(_repo.FullPath, old.IsShared, _saveTransaction.IssueTrackerPath(old.IsShared)) { RaiseError = false }.UpdateRegexAsync(rule), rule.Name);
                        }

                        if (!string.Equals(old.URLTemplate, rule.URLTemplate, StringComparison.Ordinal))
                        {
                            changed = true;
                            await RequireSuccess(new Commands.IssueTracker(_repo.FullPath, old.IsShared, _saveTransaction.IssueTrackerPath(old.IsShared)) { RaiseError = false }.UpdateURLTemplateAsync(rule), rule.Name);
                        }
                    }

                    oldRules.Remove(rule.Name);
                }
                else
                {
                    changed = true;
                    await RequireSuccess(new Commands.IssueTracker(_repo.FullPath, rule.IsShared, _saveTransaction.IssueTrackerPath(rule.IsShared)) { RaiseError = false }.AddAsync(rule), rule.Name);
                }
            }

            if (oldRules.Count > 0)
            {
                changed = true;

                foreach (var kv in oldRules)
                    await RequireSuccess(new Commands.IssueTracker(_repo.FullPath, kv.Value.IsShared, _saveTransaction.IssueTrackerPath(kv.Value.IsShared)) { RaiseError = false }.RemoveAsync(kv.Key), kv.Key);
            }

            return changed;
        }

        private static Models.IssueTracker CopyRule(Models.IssueTracker rule) => new()
        {
            IsShared = rule.IsShared, Name = rule.Name, RegexString = rule.RegexString, URLTemplate = rule.URLTemplate,
        };

        private readonly List<Models.IssueTracker> _originalIssueTrackers = [];
        private Models.RepositoryConfigurationTransaction _saveTransaction;
        private readonly Repository _repo;
        private readonly Dictionary<string, string> _cached;
        private readonly Dictionary<string, string> _cachedLocal;
        private string _httpProxy;
        private Models.CommitTemplate _selectedCommitTemplate = null;
        private Models.IssueTracker _selectedIssueTracker = null;
        private Models.CustomAction _selectedCustomAction = null;
    }
}
