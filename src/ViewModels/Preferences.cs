using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public partial class Preferences : ObservableObject
    {
        [JsonIgnore]
        public static Preferences Instance
        {
            get
            {
                if (_instance != null)
                    return _instance;

                Native.StartupTrace.Mark("preferences-loading");
                _instance = Load();
                Native.StartupTrace.Mark("preferences-loaded");
                _instance._isLoading = false;

                _instance.PrepareGit();
                Native.StartupTrace.Mark("git-ready");
                _instance.PrepareShellOrTerminal();
                Native.StartupTrace.Mark("terminal-ready");
                _instance.PrepareExternalDiffMergeTool();
                _instance.PrepareWorkspaces();

                return _instance;
            }
        }

        [JsonIgnore]
        public int LoadRecovery { get; private set; }

        public string Locale
        {
            get => _locale;
            set
            {
                if (SetProperty(ref _locale, value) && !_isLoading)
                {
                    App.SetLocale(value);
                    _interfaceFonts = null;
                    _codeFonts = null;
                    OnPropertyChanged(nameof(InterfaceFonts));
                    OnPropertyChanged(nameof(CodeFonts));
                }
            }
        }

        public string Theme
        {
            get => _theme;
            set
            {
                if (SetProperty(ref _theme, Models.TreeGitThemes.Normalize(value)) && !_isLoading)
                    App.SetTheme(_theme, _themeOverrides);
            }
        }

        public string ThemeOverrides
        {
            get => _themeOverrides;
            set
            {
                if (SetProperty(ref _themeOverrides, value) && !_isLoading)
                    App.SetTheme(_theme, value);
            }
        }

        public string DefaultFontFamily
        {
            get => _defaultFontFamily;
            set
            {
                value ??= string.Empty;
                if (SetProperty(ref _defaultFontFamily, value) && !_isLoading)
                    App.SetFonts(value, _monospaceFontFamily);
            }
        }

        public string MonospaceFontFamily
        {
            get => _monospaceFontFamily;
            set
            {
                value ??= string.Empty;
                if (SetProperty(ref _monospaceFontFamily, value) && !_isLoading)
                    App.SetFonts(_defaultFontFamily, value);
            }
        }

        public bool UseSystemWindowFrame
        {
            get => Native.OS.UseSystemWindowFrame;
            set => Native.OS.UseSystemWindowFrame = value;
        }

        [JsonIgnore]
        public IReadOnlyList<Models.FontOption> InterfaceFonts => _interfaceFonts ??= Models.FontOption.Create(_defaultFontFamily, App.Text("Preferences.Appearance.DefaultFont.Default"));

        [JsonIgnore]
        public IReadOnlyList<Models.FontOption> CodeFonts => _codeFonts ??= Models.FontOption.Create(_monospaceFontFamily, App.Text("Preferences.Appearance.MonospaceFont.Default"));

        private IReadOnlyList<Models.FontOption> _interfaceFonts;
        private IReadOnlyList<Models.FontOption> _codeFonts;

        public double DefaultFontSize
        {
            get => _defaultFontSize;
            set => SetProperty(ref _defaultFontSize, value);
        }

        public double EditorFontSize
        {
            get => _editorFontSize;
            set => SetProperty(ref _editorFontSize, value);
        }

        public int EditorTabWidth
        {
            get => _editorTabWidth;
            set => SetProperty(ref _editorTabWidth, value);
        }

        public double Zoom
        {
            get => _zoom;
            set => SetProperty(ref _zoom, value);
        }

        public LayoutInfo Layout
        {
            get => _layout;
            set => SetProperty(ref _layout, value);
        }

        public bool ShowLocalChangesByDefault
        {
            get;
            set;
        } = false;

        public bool ShowChangesInCommitDetailByDefault
        {
            get;
            set;
        } = true;

        public int MaxHistoryCommits
        {
            get => _maxHistoryCommits;
            set => SetProperty(ref _maxHistoryCommits, value);
        }

        public int SubjectGuideLength
        {
            get => _subjectGuideLength;
            set => SetProperty(ref _subjectGuideLength, value);
        }

        public int DateTimeFormat
        {
            get => Models.DateTimeFormat.ActiveIndex;
            set
            {
                if (value != Models.DateTimeFormat.ActiveIndex &&
                    value >= 0 &&
                    value < Models.DateTimeFormat.Supported.Count)
                {
                    Models.DateTimeFormat.ActiveIndex = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool Use24Hours
        {
            get => Models.DateTimeFormat.Use24Hours;
            set
            {
                if (value != Models.DateTimeFormat.Use24Hours)
                {
                    Models.DateTimeFormat.Use24Hours = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool UseFixedTabWidth
        {
            get => _useFixedTabWidth;
            set => SetProperty(ref _useFixedTabWidth, value);
        }

        public bool UseAutoHideScrollBars
        {
            get => _useAutoHideScrollBars;
            set => SetProperty(ref _useAutoHideScrollBars, value);
        }

        public bool UseGitHubStyleAvatar
        {
            get => _useGitHubStyleAvatar;
            set => SetProperty(ref _useGitHubStyleAvatar, value);
        }

        public bool Check4UpdatesOnStartup
        {
            get => _check4UpdatesOnStartup;
            set => SetProperty(ref _check4UpdatesOnStartup, value);
        }

        public string IgnoreUpdateTag
        {
            get => _ignoreUpdateTag;
            set => SetProperty(ref _ignoreUpdateTag, value);
        }

        public bool ShowTagsInGraph
        {
            get => _showTagsInGraph;
            set => SetProperty(ref _showTagsInGraph, value);
        }

        public bool UseCompactBranchNamesInGraph
        {
            get => _useCompactBranchNamesInGraph;
            set => SetProperty(ref _useCompactBranchNamesInGraph, value);
        }

        public bool UseTwoColumnsLayoutInHistories
        {
            get => _useTwoColumnsLayoutInHistories;
            set => SetProperty(ref _useTwoColumnsLayoutInHistories, value);
        }

        public bool DisplayTimeAsPeriodInHistories
        {
            get => _displayTimeAsPeriodInHistories;
            set => SetProperty(ref _displayTimeAsPeriodInHistories, value);
        }

        public bool UseSideBySideDiff
        {
            get => _useSideBySideDiff;
            set => SetProperty(ref _useSideBySideDiff, value);
        }

        public bool UseSyntaxHighlighting
        {
            get => _useSyntaxHighlighting;
            set => SetProperty(ref _useSyntaxHighlighting, value);
        }

        public bool IgnoreCRAtEOLInDiff
        {
            get => _ignoreCRAtEOLInDiff;
            set => SetProperty(ref _ignoreCRAtEOLInDiff, value);
        }

        public bool UseStashAndReapplyByDefault
        {
            get;
            set;
        } = false;

        public bool EnableAutoFetch
        {
            get;
            set;
        } = false;

        public int AutoFetchInterval
        {
            get;
            set;
        } = 10;

        public bool IgnoreWhitespaceChangesInDiff
        {
            get => _ignoreWhitespaceChangesInDiff;
            set => SetProperty(ref _ignoreWhitespaceChangesInDiff, value);
        }

        public bool EnableDiffViewWordWrap
        {
            get => _enableDiffViewWordWrap;
            set => SetProperty(ref _enableDiffViewWordWrap, value);
        }

        public bool ShowHiddenSymbolsInDiffView
        {
            get => _showHiddenSymbolsInDiffView;
            set => SetProperty(ref _showHiddenSymbolsInDiffView, value);
        }

        public bool UseFullTextDiff
        {
            get => _useFullTextDiff;
            set => SetProperty(ref _useFullTextDiff, value);
        }

        public int LFSImageActiveIdx
        {
            get => _lfsImageActiveIdx;
            set => SetProperty(ref _lfsImageActiveIdx, value);
        }

        public int ImageDiffActiveIdx
        {
            get => _imageDiffActiveIdx;
            set => SetProperty(ref _imageDiffActiveIdx, value);
        }

        public bool EnableCompactFoldersInChangesTree
        {
            get => _enableCompactFoldersInChangesTree;
            set => SetProperty(ref _enableCompactFoldersInChangesTree, value);
        }

        public Models.ChangeViewMode ChangeViewMode
        {
            get => _changeViewMode;
            set => SetProperty(ref _changeViewMode, value);
        }

        public string GitInstallPath
        {
            get => Native.OS.GitExecutable;
            set
            {
                if (Native.OS.GitExecutable != value)
                {
                    Native.OS.GitExecutable = value;
                    OnPropertyChanged();
                }
            }
        }

        public string GitDefaultCloneDir
        {
            get => _gitDefaultCloneDir;
            set => SetProperty(ref _gitDefaultCloneDir, value);
        }

        public bool UseLibsecretInsteadOfGCM
        {
            get => Native.OS.CredentialHelper.Equals("libsecret", StringComparison.Ordinal);
            set
            {
                var helper = value ? "libsecret" : "manager";
                if (OperatingSystem.IsLinux() && !Native.OS.CredentialHelper.Equals(helper, StringComparison.Ordinal))
                {
                    Native.OS.CredentialHelper = helper;
                    OnPropertyChanged();
                }
            }
        }

        public int ShellOrTerminalType
        {
            get => _shellOrTerminalType;
            set
            {
                if (SetProperty(ref _shellOrTerminalType, value) && !_isLoading)
                {
                    if (value >= 0 && value < Models.ShellOrTerminal.Supported.Count)
                        Native.OS.SetShellOrTerminal(Models.ShellOrTerminal.Supported[value]);
                    else
                        Native.OS.SetShellOrTerminal(null);

                    OnPropertyChanged(nameof(ShellOrTerminalPath));
                    OnPropertyChanged(nameof(ShellOrTerminalArgs));
                }
            }
        }

        public string ShellOrTerminalPath
        {
            get => Native.OS.ShellOrTerminal;
            set
            {
                if (value != Native.OS.ShellOrTerminal)
                {
                    Native.OS.ShellOrTerminal = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ShellOrTerminalArgs
        {
            get => Native.OS.ShellOrTerminalArgs;
            set
            {
                if (value != Native.OS.ShellOrTerminalArgs)
                {
                    Native.OS.ShellOrTerminalArgs = value;
                    OnPropertyChanged();
                }
            }
        }

        public int ExternalMergeToolType
        {
            get => Native.OS.ExternalMergerType;
            set
            {
                if (Native.OS.ExternalMergerType != value)
                {
                    Native.OS.ExternalMergerType = value;
                    OnPropertyChanged();

                    if (!_isLoading)
                    {
                        Native.OS.AutoSelectExternalMergeToolExecFile();
                        OnPropertyChanged(nameof(ExternalMergeToolPath));
                        OnPropertyChanged(nameof(ExternalMergeToolDiffArgs));
                        OnPropertyChanged(nameof(ExternalMergeToolMergeArgs));
                    }
                }
            }
        }

        public string ExternalMergeToolPath
        {
            get => Native.OS.ExternalMergerExecFile;
            set
            {
                if (!Native.OS.ExternalMergerExecFile.Equals(value, StringComparison.Ordinal))
                {
                    Native.OS.ExternalMergerExecFile = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ExternalMergeToolDiffArgs
        {
            get => Native.OS.ExternalDiffArgs;
            set
            {
                if (!Native.OS.ExternalDiffArgs.Equals(value, StringComparison.Ordinal))
                {
                    Native.OS.ExternalDiffArgs = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ExternalMergeToolMergeArgs
        {
            get => Native.OS.ExternalMergeArgs;
            set
            {
                if (!Native.OS.ExternalMergeArgs.Equals(value, StringComparison.Ordinal))
                {
                    Native.OS.ExternalMergeArgs = value;
                    OnPropertyChanged();
                }
            }
        }

        public List<RepositoryNode> RepositoryNodes
        {
            get;
            set;
        } = [];

        public List<Workspace> Workspaces
        {
            get;
            set;
        } = [];

        public AvaloniaList<Models.CustomAction> CustomActions
        {
            get;
            set;
        } = [];

        public AvaloniaList<AI.Service> OpenAIServices
        {
            get;
            set;
        } = [];

        public double LastCheckUpdateTime
        {
            get => _lastCheckUpdateTime;
            set => SetProperty(ref _lastCheckUpdateTime, value);
        }

        public void SetCanModify()
        {
            _isReadonly = LoadRecovery == 2;
        }

        public bool IsGitConfigured()
        {
            var path = GitInstallPath;
            return !string.IsNullOrEmpty(path) && File.Exists(path);
        }

        public bool ShouldCheck4UpdateOnStartup()
        {
            return _check4UpdatesOnStartup;
        }

        public Workspace GetActiveWorkspace()
        {
            foreach (var w in Workspaces)
            {
                if (w.IsActive)
                    return w;
            }

            var first = Workspaces[0];
            first.IsActive = true;
            return first;
        }

        public void AddNode(RepositoryNode node, RepositoryNode to, bool save)
        {
            var collection = to == null ? RepositoryNodes : to.SubNodes;
            collection.Add(node);
            SortNodes(collection);

            if (save)
                Save();
        }

        public void SortNodes(List<RepositoryNode> collection)
        {
            collection?.Sort((l, r) =>
            {
                if (l.IsRepository != r.IsRepository)
                    return l.IsRepository ? 1 : -1;

                return Models.NumericSort.Compare(l.Name, r.Name);
            });
        }

        public RepositoryNode FindNode(string id)
        {
            return FindNodeRecursive(id, RepositoryNodes);
        }

        public RepositoryNode FindOrCreateGroupRecursive(string path)
        {
            List<RepositoryNode> collection = RepositoryNodes;
            RepositoryNode node = null;

            foreach (var name in path.Split('/'))
            {
                node = FindOrCreateGroupInCollection(collection, name);
                collection = node.SubNodes;
            }

            return node;
        }

        public RepositoryNode FindOrAddNodeByRepositoryPath(string repo, RepositoryNode parent, bool shouldMoveNode, bool save = true)
        {
            var normalized = repo.Replace('\\', '/').TrimEnd('/');

            var node = FindNodeRecursive(normalized, RepositoryNodes);
            if (node == null)
            {
                node = new RepositoryNode()
                {
                    Id = normalized,
                    Name = Path.GetFileName(normalized),
                    Bookmark = 0,
                    IsRepository = true,
                };

                AddNode(node, parent, save);
            }
            else if (shouldMoveNode)
            {
                MoveNode(node, parent, save);
            }

            return node;
        }

        public void MoveNode(RepositoryNode node, RepositoryNode to, bool save)
        {
            if (to == null && RepositoryNodes.Contains(node))
                return;
            if (to != null && to.SubNodes.Contains(node))
                return;

            RemoveNode(node, false);
            AddNode(node, to, false);

            if (save)
                Save();
        }

        public void RemoveNode(RepositoryNode node, bool save)
        {
            RemoveNodeRecursive(node, RepositoryNodes);

            if (save)
                Save();
        }

        public void SortByRenamedNode(RepositoryNode node)
        {
            var container = FindNodeContainer(node, RepositoryNodes);
            SortNodes(container);
            Save();
        }

        public void AutoRemoveInvalidNode()
        {
            RemoveInvalidRepositoriesRecursive(RepositoryNodes);
        }

        public void Save(bool requireSuccess = false)
        {
            if (requireSuccess && LoadRecovery == 2) throw new IOException("The original settings cannot be safely saved in this session.");
            if (requireSuccess && (_isLoading || _isReadonly)) throw new IOException("Preferences are read-only.");
            if (_isLoading || _isReadonly)
                return;

            var tmpfile = Path.Combine(Native.OS.BasicDirectories.ConfigDir, "preference_tmp.json");
            var content = JsonSerializer.Serialize(this, JsonCodeGen.Default.Preferences);
            if (_editSession != null) content = _editSession.PersistedContent(content);
            File.WriteAllText(tmpfile, content);

            var finalFile = Path.Combine(Native.OS.BasicDirectories.ConfigDir, "preference.json");
            File.Move(tmpfile, finalFile, true);
        }

        private static Preferences Load()
        {
            var path = Path.Combine(Native.OS.BasicDirectories.ConfigDir, "preference.json");
            if (!File.Exists(path))
                return new Preferences();

            Exception failure = null;
            try
            {
                using var stream = File.OpenRead(path);
                var loaded = JsonSerializer.Deserialize(stream, JsonCodeGen.Default.Preferences);
                if (loaded != null)
                {
                    if (loaded.Layout == null || loaded.Workspaces == null || loaded.RepositoryNodes == null || loaded.CustomActions == null || loaded.OpenAIServices == null)
                        throw new JsonException("Required preference sections are missing.");
                    foreach (var workspace in loaded.Workspaces)
                        if (workspace?.Repositories == null || workspace.Repositories.Exists(string.IsNullOrWhiteSpace))
                            throw new JsonException("The workspace repository list is incomplete.");
                    ValidateNodes(loaded.RepositoryNodes);
                    return loaded;
                }
            }
            catch (Exception error) { failure = error; }

            // A new build must not replace an unreadable existing configuration during update startup.
            if (Native.TreeGitUpdateInstaller.BootstrapPlan != null)
                throw new InvalidDataException("The existing preferences could not be loaded after update.", failure);

            var recovered = new Preferences { LoadRecovery = 2 };
            if (failure is IOException or UnauthorizedAccessException) return recovered;
            try
            {
                var backup = Path.Combine(Path.GetDirectoryName(path)!, "preference-recovery-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8] + ".json");
                File.Copy(path, backup, false);
                recovered.LoadRecovery = 1;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            return recovered;
        }

        private static void ValidateNodes(List<RepositoryNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node?.SubNodes == null || string.IsNullOrWhiteSpace(node.Id))
                    throw new JsonException("The saved repository list is incomplete.");
                ValidateNodes(node.SubNodes);
            }
        }

        private void PrepareGit()
        {
            var path = Native.OS.GitExecutable;
            if (string.IsNullOrEmpty(path) || !File.Exists(path) || Native.OS.GitVersion < Models.GitVersions.MINIMAL ||
                (OperatingSystem.IsWindows() && Native.GitExecutableLocator.IsManagedBundle(path)))
                GitInstallPath = Native.OS.FindGitExecutable();
        }

        private void PrepareShellOrTerminal()
        {
            if (_shellOrTerminalType >= 0 && _shellOrTerminalType < Models.ShellOrTerminal.Supported.Count &&
                !string.IsNullOrEmpty(Native.OS.ShellOrTerminal) &&
                (!OperatingSystem.IsWindows() || File.Exists(Native.OS.ShellOrTerminal)))
                return;

            for (int i = 0; i < Models.ShellOrTerminal.Supported.Count; i++)
            {
                var shell = Models.ShellOrTerminal.Supported[i];
                if (Native.OS.TestShellOrTerminal(shell))
                {
                    ShellOrTerminalType = i;
                    Native.OS.SetShellOrTerminal(shell);
                    return;
                }
            }
            Native.OS.SetShellOrTerminal(null);
        }

        private void PrepareExternalDiffMergeTool()
        {
            var mergerType = Native.OS.ExternalMergerType;
            if (mergerType > 0 && mergerType < Models.ExternalMerger.Supported.Count)
            {
                var merger = Models.ExternalMerger.Supported[mergerType];
                if (string.IsNullOrEmpty(Native.OS.ExternalDiffArgs))
                    Native.OS.ExternalDiffArgs = merger.DiffCmd;
                if (string.IsNullOrEmpty(Native.OS.ExternalMergeArgs))
                    Native.OS.ExternalMergeArgs = merger.MergeCmd;
            }
        }

        private void PrepareWorkspaces()
        {
            if (Workspaces.Count == 0)
            {
                Workspaces.Add(new Workspace() { Name = "Default" });
                return;
            }

            foreach (var workspace in Workspaces)
            {
                if (!workspace.RestoreOnStartup)
                {
                    workspace.Repositories.Clear();
                    workspace.ActiveIdx = 0;
                }
            }
        }

        private RepositoryNode FindNodeRecursive(string id, List<RepositoryNode> collection)
        {
            foreach (var node in collection)
            {
                if (node.Id == id)
                    return node;

                var sub = FindNodeRecursive(id, node.SubNodes);
                if (sub != null)
                    return sub;
            }

            return null;
        }

        private RepositoryNode FindOrCreateGroupInCollection(List<RepositoryNode> collection, string name)
        {
            foreach (var node in collection)
            {
                if (!node.IsRepository && node.Name.Equals(name, StringComparison.Ordinal))
                    return node;
            }

            var added = new RepositoryNode()
            {
                Id = Guid.NewGuid().ToString(),
                Name = name,
                IsRepository = false,
                IsExpanded = true,
            };
            collection.Add(added);

            SortNodes(collection);
            return added;
        }

        private List<RepositoryNode> FindNodeContainer(RepositoryNode node, List<RepositoryNode> collection)
        {
            foreach (var sub in collection)
            {
                if (node == sub)
                    return collection;

                var subCollection = FindNodeContainer(node, sub.SubNodes);
                if (subCollection != null)
                    return subCollection;
            }

            return null;
        }

        private bool RemoveNodeRecursive(RepositoryNode node, List<RepositoryNode> collection)
        {
            if (collection.Contains(node))
            {
                collection.Remove(node);
                return true;
            }

            foreach (var one in collection)
            {
                if (RemoveNodeRecursive(node, one.SubNodes))
                    return true;
            }

            return false;
        }

        private bool RemoveInvalidRepositoriesRecursive(List<RepositoryNode> collection)
        {
            bool changed = false;

            for (int i = collection.Count - 1; i >= 0; i--)
            {
                var node = collection[i];
                if (node.IsInvalid)
                {
                    collection.RemoveAt(i);
                    changed = true;
                }
                else if (!node.IsRepository)
                {
                    changed |= RemoveInvalidRepositoriesRecursive(node.SubNodes);
                }
            }

            return changed;
        }

        private static Preferences _instance = null;

        private bool _isLoading = true;
        private bool _isReadonly = true;
        private string _locale = "zh_CN";
        private string _theme = "Light";
        private string _themeOverrides = string.Empty;
        private string _defaultFontFamily = string.Empty;
        private string _monospaceFontFamily = string.Empty;
        private double _defaultFontSize = 13;
        private double _editorFontSize = 13;
        private int _editorTabWidth = 4;
        private double _zoom = 1.0;
        private LayoutInfo _layout = new();

        private int _maxHistoryCommits = 20000;
        private int _subjectGuideLength = 50;
        private bool _useFixedTabWidth = true;
        private bool _useAutoHideScrollBars = true;
        private bool _useGitHubStyleAvatar = true;
        private bool _useCompactBranchNamesInGraph = true;

        private bool _check4UpdatesOnStartup = true;
        private double _lastCheckUpdateTime = 0;
        private string _ignoreUpdateTag = string.Empty;

        private bool _showTagsInGraph = true;
        private bool _useTwoColumnsLayoutInHistories = false;
        private bool _displayTimeAsPeriodInHistories = false;
        private bool _useSideBySideDiff = false;
        private bool _ignoreWhitespaceChangesInDiff = false;
        private bool _ignoreCRAtEOLInDiff = true;
        private bool _useSyntaxHighlighting = false;
        private bool _enableDiffViewWordWrap = false;
        private bool _showHiddenSymbolsInDiffView = false;
        private bool _useFullTextDiff = false;
        private int _lfsImageActiveIdx = 0;
        private int _imageDiffActiveIdx = 0;
        private bool _enableCompactFoldersInChangesTree = false;
        private Models.ChangeViewMode _changeViewMode = Models.ChangeViewMode.List;

        private string _gitDefaultCloneDir = string.Empty;
        private int _shellOrTerminalType = -1;
    }
}
