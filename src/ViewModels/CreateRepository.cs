using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class CreateRepository : Popup
    {
        [Required(ErrorMessageResourceType = typeof(ValidationMessages), ErrorMessageResourceName = nameof(ValidationMessages.ParentFolderRequired))]
        [CustomValidation(typeof(CreateRepository), nameof(ValidateParentFolder))]
        public string ParentFolder
        {
            get => _parentFolder;
            set => SetProperty(ref _parentFolder, value, true);
        }

        [Required(ErrorMessageResourceType = typeof(ValidationMessages), ErrorMessageResourceName = nameof(ValidationMessages.NameRequired))]
        [CustomValidation(typeof(CreateRepository), nameof(ValidateName))]
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value, true);
        }

        public bool CreateOnGitHub
        {
            get => _createOnGitHub;
            set => SetProperty(ref _createOnGitHub, value);
        }

        public bool IsPrivate
        {
            get => _isPrivate;
            set => SetProperty(ref _isPrivate, value);
        }

        public string GitHubToken
        {
            get => _gitHubToken;
            set
            {
                if (!SetProperty(ref _gitHubToken, value)) return;
                _verifiedToken = null;
                Account = string.Empty;
            }
        }

        public string Account { get => _account; private set => SetProperty(ref _account, value); }
        public string Error => string.IsNullOrEmpty(_errorKey) ? string.Empty : App.Text(_errorKey);

        private void SetError(string key)
        {
            _errorKey = key;
            OnPropertyChanged(nameof(Error));
        }

        public override void RefreshLocalizedText()
        {
            base.RefreshLocalizedText();
            OnPropertyChanged(nameof(Error));
        }
        public bool CanEditName => _localPath == null && _createdRemote == null && !_remoteRequestUncertain;
        public bool CanEditParentFolder => _localPath == null;
        public bool CanEditGitHub => _createdRemote == null;
        public bool CanVerifyAccount => CanEditGitHub && !_verifying;
        public bool CanChangeRemoteChoice => _createdRemote == null && !_remoteRequestUncertain;
        public bool CanConnectExisting => _uncertainRemote != null;
        public bool HasCreatedRemote => _createdRemote != null;
        public string RemoteUrl => (_createdRemote ?? _uncertainRemote)?.BrowserUrl;

        public CreateRepository(string pageId)
        {
            _pageId = pageId;
            var preferences = Preferences.Instance;
            _parentFolder = preferences.GetActiveWorkspace()?.DefaultCloneDir;
            if (string.IsNullOrEmpty(_parentFolder)) _parentFolder = preferences.GitDefaultCloneDir;
        }

        public static ValidationResult ValidateParentFolder(string path, ValidationContext _)
            => Directory.Exists(path) && Path.IsPathFullyQualified(path)
                ? ValidationResult.Success : new ValidationResult(App.Text("TreeGit.Message.PathMissing"));

        public static ValidationResult ValidateName(string name, ValidationContext _)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name is "." or ".." ||
                name != name.Trim() || name.EndsWith('.') || Regex.IsMatch(name, "[<>:\"/\\\\|?*\\x00-\\x1f]") ||
                Regex.IsMatch(name, @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|\z)", RegexOptions.IgnoreCase))
                return new ValidationResult(App.Text("TreeGit.CreateRepository.InvalidName"));
            return ValidationResult.Success;
        }

        public async Task VerifyAccount()
        {
            if (_verifying) return;
            SetError(null);
            if (string.IsNullOrWhiteSpace(_gitHubToken) || _gitHubToken.IndexOfAny(['\r', '\n', '\0']) >= 0) { SetError("TreeGit.CreateRepository.TokenRequired"); return; }
            var token = _gitHubToken.Trim();
            _verifying = true;
            OnPropertyChanged(nameof(CanVerifyAccount));
            try
            {
                using var api = CreateApi();
                var login = await api.GetAccountAsync(token);
                if (_gitHubToken.Trim() != token) return;
                Account = login;
                _verifiedToken = token;
            }
            catch (Exception ex)
            {
                if (_gitHubToken?.Trim() == token)
                    SetError(ex is Native.GitHubRepositoryCreator.Failure failure ? failure.Key : "TreeGit.CreateRepository.ConnectionFailed");
            }
            finally
            {
                _verifying = false;
                OnPropertyChanged(nameof(CanVerifyAccount));
            }
        }

        public void ConnectExistingRemote()
        {
            if (_uncertainRemote == null) return;
            _createdRemote = _uncertainRemote;
            _uncertainRemote = null;
            SetError(null);
            RefreshState();
        }

        public override async Task<bool> Sure()
        {
            SetError(null);
            if (_remoteRequestUncertain && _name != _requestedName)
            {
                SetError("TreeGit.CreateRepository.LocationChanged");
                return false;
            }
            var target = Path.GetFullPath(Path.Combine(_parentFolder, _name));
            if (_localPath == null && (Directory.Exists(target) || File.Exists(target)))
            {
                SetError("TreeGit.CreateRepository.PathExists");
                return false;
            }
            if (_localPath != null && !string.Equals(_localPath, target, StringComparison.Ordinal))
            {
                SetError("TreeGit.CreateRepository.LocationChanged");
                return false;
            }

            if (_createOnGitHub && _createdRemote == null)
            {
                if (!Regex.IsMatch(_name, @"\A[A-Za-z0-9_.-]+\z")) { SetError("TreeGit.CreateRepository.GitHubName"); return false; }
                if (string.IsNullOrEmpty(_verifiedToken) || _verifiedToken != _gitHubToken.Trim()) { SetError("TreeGit.CreateRepository.VerifyFirst"); return false; }
                var submittedThisTime = false;
                try
                {
                    using var api = CreateApi();
                    // Revalidate the account before creating anything remotely.
                    if (await api.GetAccountAsync(_verifiedToken) != Account) { SetError("TreeGit.CreateRepository.VerifyFirst"); return false; }
                    if (_remoteRequestUncertain && Account != _requestedOwner) { SetError("TreeGit.CreateRepository.SameAccountRequired"); return false; }
                    var existing = await api.FindAsync(_verifiedToken, Account, _name);
                    if (existing != null)
                    {
                        if (_remoteRequestUncertain)
                        {
                            _uncertainRemote = existing;
                            RefreshState();
                            SetError("TreeGit.CreateRepository.ConfirmExisting");
                        }
                        else SetError("TreeGit.CreateRepository.RemoteExists");
                        return false;
                    }
                    _remoteRequestUncertain = true;
                    _requestedOwner = Account;
                    _requestedName = _name;
                    RefreshState();
                    submittedThisTime = true;
                    _createdRemote = await api.CreateAsync(_verifiedToken, Account, _name, _isPrivate);
                    _remoteRequestUncertain = false;
                    RefreshState();
                }
                catch (Native.GitHubRepositoryCreator.Failure ex)
                {
                    if (submittedThisTime && !ex.Uncertain) _remoteRequestUncertain = false;
                    SetError(ex.Key);
                    RefreshState();
                    return false;
                }
                catch { SetError("TreeGit.CreateRepository.ConnectionFailed"); RefreshState(); return false; }
            }

            try
            {
                SetProgressDescription("TreeGit.Progress.Init", target);
                if (_localPath == null)
                {
                    // Never initialize an existing directory, repository, or link.
                    if (Directory.Exists(target) || File.Exists(target)) { SetError("TreeGit.CreateRepository.PathExists"); return false; }
                    Directory.CreateDirectory(target);
                    _localPath = target;
                    RefreshState();
                }
                if (!_initialized)
                {
                    // A failed attempt may have left data. Preserve it for inspection.
                    if (Directory.EnumerateFileSystemEntries(target).Any())
                    {
                        SetError("TreeGit.CreateRepository.PartialLocal");
                        return false;
                    }
                    var log = new CommandLog("Initialize");
                    Use(log);
                    var command = new Commands.Init(_pageId, target) { RaiseError = false };
                    var success = await command.Use(log).ExecAsync();
                    log.Complete();
                    if (!success) { SetError(_createdRemote == null ? "TreeGit.CreateRepository.LocalFailed" : "TreeGit.CreateRepository.RemoteCreatedLocalFailed"); return false; }
                    _initialized = true;
                }
                if (_createdRemote != null)
                {
                    var query = new Commands.QueryRemotes(target);
                    var remotes = await query.GetResultAsync();
                    if (!query.Succeeded) { SetError("TreeGit.CreateRepository.ConnectFailed"); return false; }
                    var origin = remotes.Find(r => r.Name == "origin");
                    if (origin != null && origin.URL != _createdRemote.CloneUrl) { SetError("TreeGit.CreateRepository.ConnectFailed"); return false; }
                    if (origin == null && !await new Commands.Remote(target) { RaiseError = false }.AddAsync("origin", _createdRemote.CloneUrl))
                    {
                        SetError("TreeGit.CreateRepository.ConnectFailed");
                        return false;
                    }
                    if (OperatingSystem.IsWindows())
                    {
                        try
                        {
#if INTEGRATION_TESTS
                            // Mock creation fixtures must not leave credentials on CI hosts.
                            if (GitHubTestFactory == null)
#endif
                            {
                                if (!await new Commands.Config(target) { RaiseError = false }.SetAsync("sourcegit.credentialmanager", "false"))
                                    throw new IOException();
                                Native.RepositoryCredentials.Save(target, new Uri(_createdRemote.CloneUrl), _createdRemote.Owner, _verifiedToken);
                                Native.RepositoryCredentials.SetCredentialManagerMode(target, false);
                            }
                        }
                        catch { Models.Notification.Send(_pageId, App.Text("TreeGit.CreateRepository.CredentialSaveFailed"), true); }
                    }
                }
                var node = Preferences.Instance.FindOrAddNodeByRepositoryPath(target, null, true);
                await node.UpdateStatusAsync(false, null);
                Welcome.Instance.Refresh();
                node.Open();
                return true;
            }
            catch { SetError(_createdRemote == null ? "TreeGit.CreateRepository.LocalFailed" : "TreeGit.CreateRepository.RemoteCreatedLocalFailed"); return false; }
        }

        private void RefreshState()
        {
            OnPropertyChanged(nameof(CanEditName));
            OnPropertyChanged(nameof(CanEditParentFolder));
            OnPropertyChanged(nameof(CanEditGitHub));
            OnPropertyChanged(nameof(CanVerifyAccount));
            OnPropertyChanged(nameof(CanChangeRemoteChoice));
            OnPropertyChanged(nameof(CanConnectExisting));
            OnPropertyChanged(nameof(HasCreatedRemote));
            OnPropertyChanged(nameof(RemoteUrl));
        }

        public override void Cleanup()
        {
            GitHubToken = string.Empty;
            _verifiedToken = null;
            base.Cleanup();
        }

        private static Native.GitHubRepositoryCreator CreateApi()
        {
#if INTEGRATION_TESTS
            if (GitHubTestFactory != null) return GitHubTestFactory();
#endif
            return new Native.GitHubRepositoryCreator();
        }
#if INTEGRATION_TESTS
        internal static Func<Native.GitHubRepositoryCreator> GitHubTestFactory;
#endif
        private readonly string _pageId;
        private string _parentFolder;
        private string _name = string.Empty;
        private string _gitHubToken = string.Empty;
        private string _verifiedToken;
        private string _account = string.Empty;
        private string _errorKey;
        private string _localPath;
        private string _requestedOwner;
        private string _requestedName;
        private bool _initialized;
        private bool _verifying;
        private bool _createOnGitHub;
        private bool _isPrivate = true;
        private bool _remoteRequestUncertain;
        private Native.GitHubRepositoryCreator.Repository _createdRemote;
        private Native.GitHubRepositoryCreator.Repository _uncertainRemote;
    }
}
