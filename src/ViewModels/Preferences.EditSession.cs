using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SourceGit.ViewModels
{
    public partial class Preferences
    {
        private EditSession _editSession;
        public EditSession BeginEdit()
        {
            if (_editSession != null) throw new InvalidOperationException("A preferences edit is already open.");
            return _editSession = new EditSession(this);
        }
        public sealed class EditSession : IDisposable
        {
            private readonly Preferences _owner;
            private readonly Dictionary<string, JsonNode> _original = new();
            private readonly List<Action> _restore = new();
            private bool _finished;
            internal EditSession(Preferences owner)
            {
                _owner = owner;
                var json = JsonNode.Parse(JsonSerializer.Serialize(owner, JsonCodeGen.Default.Preferences));
                void Track<T>(string name, Func<T> get, Action<T> set)
                {
                    var value = get();
                    _original[name] = json[name]?.DeepClone();
                    _restore.Add(() => set(value));
                }
                Track(nameof(Locale), () => owner.Locale, v => owner.Locale = v);
                Track(nameof(Theme), () => owner.Theme, v => owner.Theme = v);
                Track(nameof(ThemeOverrides), () => owner.ThemeOverrides, v => owner.ThemeOverrides = v);
                Track(nameof(DefaultFontFamily), () => owner.DefaultFontFamily, v => owner.DefaultFontFamily = v);
                Track(nameof(MonospaceFontFamily), () => owner.MonospaceFontFamily, v => owner.MonospaceFontFamily = v);
                Track(nameof(DefaultFontSize), () => owner.DefaultFontSize, v => owner.DefaultFontSize = v);
                Track(nameof(EditorFontSize), () => owner.EditorFontSize, v => owner.EditorFontSize = v);
                Track(nameof(EditorTabWidth), () => owner.EditorTabWidth, v => owner.EditorTabWidth = v);
                Track(nameof(Zoom), () => owner.Zoom, v => owner.Zoom = v);
                Track(nameof(DateTimeFormat), () => owner.DateTimeFormat, v => owner.DateTimeFormat = v);
                Track(nameof(Use24Hours), () => owner.Use24Hours, v => owner.Use24Hours = v);
                Track(nameof(UseSystemWindowFrame), () => owner.UseSystemWindowFrame, v => owner.UseSystemWindowFrame = v);
                Track(nameof(UseFixedTabWidth), () => owner.UseFixedTabWidth, v => owner.UseFixedTabWidth = v);
                Track(nameof(UseAutoHideScrollBars), () => owner.UseAutoHideScrollBars, v => owner.UseAutoHideScrollBars = v);
                Track(nameof(Check4UpdatesOnStartup), () => owner.Check4UpdatesOnStartup, v => owner.Check4UpdatesOnStartup = v);
                Track(nameof(ShowLocalChangesByDefault), () => owner.ShowLocalChangesByDefault, v => owner.ShowLocalChangesByDefault = v);
                Track(nameof(ShowChangesInCommitDetailByDefault), () => owner.ShowChangesInCommitDetailByDefault, v => owner.ShowChangesInCommitDetailByDefault = v);
                Track(nameof(MaxHistoryCommits), () => owner.MaxHistoryCommits, v => owner.MaxHistoryCommits = v);
                Track(nameof(SubjectGuideLength), () => owner.SubjectGuideLength, v => owner.SubjectGuideLength = v);
                Track(nameof(DisplayTimeAsPeriodInHistories), () => owner.DisplayTimeAsPeriodInHistories, v => owner.DisplayTimeAsPeriodInHistories = v);
                Track(nameof(ShowTagsInGraph), () => owner.ShowTagsInGraph, v => owner.ShowTagsInGraph = v);
                Track(nameof(UseCompactBranchNamesInGraph), () => owner.UseCompactBranchNamesInGraph, v => owner.UseCompactBranchNamesInGraph = v);
                Track(nameof(EnableCompactFoldersInChangesTree), () => owner.EnableCompactFoldersInChangesTree, v => owner.EnableCompactFoldersInChangesTree = v);
                Track(nameof(GitInstallPath), () => owner.GitInstallPath, v => owner.GitInstallPath = v);
                Track(nameof(GitDefaultCloneDir), () => owner.GitDefaultCloneDir, v => owner.GitDefaultCloneDir = v);
                Track(nameof(UseLibsecretInsteadOfGCM), () => owner.UseLibsecretInsteadOfGCM, v => owner.UseLibsecretInsteadOfGCM = v);
                Track(nameof(UseStashAndReapplyByDefault), () => owner.UseStashAndReapplyByDefault, v => owner.UseStashAndReapplyByDefault = v);
                Track(nameof(IgnoreCRAtEOLInDiff), () => owner.IgnoreCRAtEOLInDiff, v => owner.IgnoreCRAtEOLInDiff = v);
                Track(nameof(EnableAutoFetch), () => owner.EnableAutoFetch, v => owner.EnableAutoFetch = v);
                Track(nameof(AutoFetchInterval), () => owner.AutoFetchInterval, v => owner.AutoFetchInterval = v);
                Track(nameof(ShellOrTerminalType), () => owner.ShellOrTerminalType, v => owner.ShellOrTerminalType = v);
                Track(nameof(ShellOrTerminalPath), () => owner.ShellOrTerminalPath, v => owner.ShellOrTerminalPath = v);
                Track(nameof(ShellOrTerminalArgs), () => owner.ShellOrTerminalArgs, v => owner.ShellOrTerminalArgs = v);
                Track(nameof(ExternalMergeToolType), () => owner.ExternalMergeToolType, v => owner.ExternalMergeToolType = v);
                Track(nameof(ExternalMergeToolPath), () => owner.ExternalMergeToolPath, v => owner.ExternalMergeToolPath = v);
                Track(nameof(ExternalMergeToolDiffArgs), () => owner.ExternalMergeToolDiffArgs, v => owner.ExternalMergeToolDiffArgs = v);
                Track(nameof(ExternalMergeToolMergeArgs), () => owner.ExternalMergeToolMergeArgs, v => owner.ExternalMergeToolMergeArgs = v);
            }
            internal string PersistedContent(string content)
            {
                var json = JsonNode.Parse(content);
                foreach (var (name, value) in _original) json[name] = value?.DeepClone();
                return json.ToJsonString();
            }
            public void SaveCandidate()
            {
                if (_finished) throw new InvalidOperationException("Preferences edit is closed.");
                _owner._editSession = null;
                try { _owner.Save(requireSuccess: true); }
                finally { _owner._editSession = this; }
            }
            public void Accept()
            {
                _finished = true;
                _owner._editSession = null;
            }
            public void Dispose()
            {
                if (_finished) return;
                // Keep save suppression in place while setters restore previews.
                try { foreach (var restore in _restore) restore(); }
                finally { _owner._editSession = null; _finished = true; }
            }
        }
    }
}
