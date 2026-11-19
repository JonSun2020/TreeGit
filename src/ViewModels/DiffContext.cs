using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class DiffContext : ObservableObject
    {
        public IDiffViewSettings Settings { get; }
        public string Title
        {
            get;
        }

        public string OldSource => App.Text("Diff.Source.Old", _option.BinaryOldPath == "/dev/null" ? App.Text("Diff.Source.Empty") :
            _option.BinaryOldRevision == _option.BinaryNewRevision + "^" ? App.Text("Diff.Source.Parent") : DescribeRevision(_option.BinaryOldRevision));
        public string NewSource => App.Text("Diff.Source.New", DescribeRevision(_option.BinaryNewRevision));
        public string SourceDescription => OldSource + " → " + NewSource;
        private static string DescribeRevision(string revision)
        {
            if (revision == null) return App.Text("Diff.Source.WorkingTree");
            if (revision.Length == 0) return App.Text("Diff.Source.Index");
            if (revision == Models.EmptyTreeHash.Guess(revision)) return App.Text("Diff.Source.Empty");
            if (revision == "HEAD") return App.Text("Diff.Source.Head");
            if (revision.Length > 10 && revision.All(Uri.IsHexDigit)) return App.Text("Diff.Source.Commit", revision[..10]);
            return revision;
        }

        public int OldMode
        {
            get => _oldMode;
            private set => SetProperty(ref _oldMode, value);
        }

        public int NewMode
        {
            get => _newMode;
            private set => SetProperty(ref _newMode, value);
        }

        public bool IsTextDiff
        {
            get => _isTextDiff;
            private set => SetProperty(ref _isTextDiff, value);
        }

        public bool IsIgnoreWhitespaceVisible
        {
            get => _isIgnoreWhitespaceVisible;
            private set => SetProperty(ref _isIgnoreWhitespaceVisible, value);
        }

        public object Content
        {
            get => _content;
            private set
            {
                if (SetProperty(ref _content, value)) OnPropertyChanged(nameof(CanChooseEncoding));
            }
        }
        public bool CanChooseEncoding => Content is TextDiffContext or Models.BinaryDiff or Models.NoOrEOLChange;

        public int UnifiedLines
        {
            get => _unifiedLines;
            private set => SetProperty(ref _unifiedLines, value);
        }

        public DiffContext(string repo, Models.DiffOption option, DiffContext previous = null, bool standalone = false)
        {
            Settings = standalone ? Preferences.Instance.StandaloneDiffSettings ??= new() : Preferences.Instance;
            _repo = repo;
            _option = option;

            if (previous != null && string.Equals(previous._repo, repo, StringComparison.Ordinal) &&
                previous._option.ToString() == option.ToString())
            {
                _isTextDiff = previous._isTextDiff;
                _isIgnoreWhitespaceVisible = previous._isIgnoreWhitespaceVisible;
                _content = previous._content;
                _oldMode = previous._oldMode;
                _newMode = previous._newMode;
                _unifiedLines = previous._unifiedLines;
                _info = previous._info;
            }

            if (string.IsNullOrEmpty(_option.OrgPath) || _option.OrgPath == "/dev/null")
                Title = _option.Path;
            else
                Title = $"{_option.OrgPath} → {_option.Path}";

            LoadContent();
        }

        public void IncrUnified()
        {
            UnifiedLines = _unifiedLines + 1;
            LoadContent();
        }

        public void DecrUnified()
        {
            UnifiedLines = Math.Max(4, _unifiedLines - 1);
            LoadContent();
        }

        public void OpenExternalMergeTool()
        {
            new Commands.DiffTool(_repo, _option).Open();
        }

        public void OpenReadOnlyComparison() => App.OpenInternalComparison(_repo, _option);

        public void RefreshLocalizedText()
        {
            OnPropertyChanged(nameof(OldSource));
            OnPropertyChanged(nameof(NewSource));
            OnPropertyChanged(nameof(SourceDescription));
        }

        public void CheckSettings()
        {
            var pref = Settings;
            if (_info == null || _info.CodePage != Models.DisplayEncoding.CodePage(pref.DisplayEncodingIndex))
            {
                LoadContent();
                return;
            }

            if (Content is TextDiffContext ctx)
            {
                if ((pref.UseFullTextDiff && _info.UnifiedLines != _entireFileLines) ||
                    (!pref.UseFullTextDiff && _info.UnifiedLines == _entireFileLines) ||
                    (pref.IgnoreWhitespaceChangesInDiff != _info.IgnoreWhitespace))
                {
                    LoadContent();
                    return;
                }

                if (ctx.IsSideBySide() != pref.UseSideBySideDiff && !ctx.Data.IsNewOrDeletedFile)
                    Content = ctx.SwitchMode();
            }
            else if (Content is Models.NoOrEOLChange)
            {
                if (pref.IgnoreWhitespaceChangesInDiff != _info.IgnoreWhitespace)
                    LoadContent();
            }
        }

        private void LoadContent()
        {
            var loadVersion = Interlocked.Increment(ref _loadVersion);
            if (_option.Path.EndsWith('/'))
            {
                OldMode = 0;
                NewMode = 160000;
                IsTextDiff = false;
                IsIgnoreWhitespaceVisible = false;
                Content = null;
                _info = null;
                return;
            }

            Task.Run(async () =>
            {
                var pref = Settings;
                var numLines = pref.UseFullTextDiff ? _entireFileLines : _unifiedLines;
                var ignoreWhitespace = pref.IgnoreWhitespaceChangesInDiff;
                var ignoreCRAtEOL = pref.IgnoreCRAtEOLInDiff;

                var codePage = Models.DisplayEncoding.CodePage(pref.DisplayEncodingIndex);
                var latest = await new Commands.Diff(_repo, _option, numLines, ignoreWhitespace, ignoreCRAtEOL, codePage)
                    .ReadAsync()
                    .ConfigureAwait(false);

                var info = new Info(_option, numLines, ignoreWhitespace, codePage, latest);
                if (_info != null && info.IsSame(_info))
                    return;

                object rs = null;
                if ((latest.IsBinary || Models.DisplayEncoding.IsWide(codePage)) && ImageSource.GetDecoder(_option.Path) == Models.ImageDecoder.None)
                {
                    var decoded = await TryWideDiffAsync(latest, codePage, numLines, ignoreWhitespace, ignoreCRAtEOL).ConfigureAwait(false);
                    if (decoded != null)
                    {
                        latest.TextDiff = decoded.Lines.Count > 0 ? decoded : null;
                        latest.IsBinary = false;
                    }
                }
                if (latest.TextDiff is { } textDiff)
                {
                    rs = textDiff;
                }
                else if (latest.LFSDiff is { } lfs)
                {
                    var imgDecoder = ImageSource.GetDecoder(_option.Path);
                    if (imgDecoder != Models.ImageDecoder.None)
                        rs = new LFSImageDiff(_repo, lfs, imgDecoder);
                    else
                        rs = lfs;
                }
                else if (latest.IsBinary)
                {
                    var imgDecoder = ImageSource.GetDecoder(_option.Path);
                    if (imgDecoder != Models.ImageDecoder.None)
                        rs = await CreateImageDiffAsync(imgDecoder).ConfigureAwait(false);
                    else
                        rs = await CreateBinaryDiffAsync().ConfigureAwait(false);
                }
                else if (latest.IsSubmoduleChange)
                {
                    rs = await CreateSubmoduleDiffAsync(latest.OldHash, latest.NewHash).ConfigureAwait(false);
                }
                else if (IsEmptyFileHash(latest.OldHash) || IsEmptyFileHash(latest.NewHash))
                {
                    rs = new Models.EmptyFile();
                }
                else
                {
                    rs = new Models.NoOrEOLChange();
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (loadVersion != Volatile.Read(ref _loadVersion)) return;
                    OldMode = latest.OldMode;
                    NewMode = latest.NewMode;

                    if (rs is Models.TextDiff cur)
                    {
                        IsTextDiff = true;
                        IsIgnoreWhitespaceVisible = true;

                        var option = cur.IsDisplayOnly ? _option.AsDisplayOnly() : _option;
                        if (Settings.UseSideBySideDiff && !cur.IsNewOrDeletedFile)
                            Content = new TwoSideTextDiff(option, cur, _content as TextDiffContext);
                        else
                            Content = new CombinedTextDiff(option, cur, _content as TextDiffContext);
                    }
                    else
                    {
                        IsTextDiff = false;
                        IsIgnoreWhitespaceVisible = (rs is Models.NoOrEOLChange);
                        Content = rs;
                    }

                    // Publish the cache identity with its content on the UI
                    // thread. A newly selected context must never inherit a
                    // completed identity while the matching content is pending.
                    _info = info;
                });
            });
        }

        private async Task<Models.ImageDiff> CreateImageDiffAsync(Models.ImageDecoder imgDecoder)
        {
            var oldPath = string.IsNullOrEmpty(_option.OrgPath) ? _option.Path : _option.OrgPath;
            var imgDiff = new Models.ImageDiff();
            var fullPath = Path.Combine(_repo, _option.Path);

            if (_option.Revisions.Count == 2) // Two revisions are specified, compare them
            {
                if (_option.Revisions[0].Equals("-R", StringComparison.Ordinal)) // `-R` means the old side is the working tree
                {
                    var oldImage = await ImageSource.FromFileAsync(fullPath, imgDecoder).ConfigureAwait(false);
                    imgDiff.Old = oldImage.Bitmap;
                    imgDiff.OldFileSize = oldImage.Size;
                }
                else
                {
                    var oldImage = await ImageSource.FromRevisionAsync(_repo, _option.Revisions[0], oldPath, imgDecoder).ConfigureAwait(false);
                    imgDiff.Old = oldImage.Bitmap;
                    imgDiff.OldFileSize = oldImage.Size;
                }

                if (string.IsNullOrEmpty(_option.Revisions[1])) // Empty string in the second revision means the new side is the working tree
                {
                    var newImage = await ImageSource.FromFileAsync(fullPath, imgDecoder).ConfigureAwait(false);
                    imgDiff.New = newImage.Bitmap;
                    imgDiff.NewFileSize = newImage.Size;
                }
                else
                {
                    var newImage = await ImageSource.FromRevisionAsync(_repo, _option.Revisions[1], _option.Path, imgDecoder).ConfigureAwait(false);
                    imgDiff.New = newImage.Bitmap;
                    imgDiff.NewFileSize = newImage.Size;
                }
            }
            else if (_option.IsUnstaged) // Unstaged change compared to staged or HEAD
            {
                if (!oldPath.Equals("/dev/null", StringComparison.Ordinal))
                {
                    var oldImage = await ImageSource.FromRevisionAsync(_repo, string.Empty, oldPath, imgDecoder).ConfigureAwait(false);
                    imgDiff.Old = oldImage.Bitmap;
                    imgDiff.OldFileSize = oldImage.Size;
                }

                var newImage = await ImageSource.FromFileAsync(fullPath, imgDecoder).ConfigureAwait(false);
                imgDiff.New = newImage.Bitmap;
                imgDiff.NewFileSize = newImage.Size;
            }
            else // Staged change compared to the last commit (HEAD)
            {
                var oldImage = await ImageSource.FromRevisionAsync(_repo, "HEAD", oldPath, imgDecoder).ConfigureAwait(false);
                imgDiff.Old = oldImage.Bitmap;
                imgDiff.OldFileSize = oldImage.Size;

                var newImage = await ImageSource.FromRevisionAsync(_repo, string.Empty, _option.Path, imgDecoder).ConfigureAwait(false);
                imgDiff.New = newImage.Bitmap;
                imgDiff.NewFileSize = newImage.Size;
            }

            return imgDiff;
        }

        private async Task<Models.TextDiff> TryWideDiffAsync(Models.DiffResult result, int codePage, int context, bool ignoreSpace, bool ignoreCR)
        {
            try
            {
                var current = await Commands.WideTextPreview.ReadAsync(_repo, _option.BinaryNewRevision, _option.BinaryNewPath, result.NewHash, codePage).ConfigureAwait(false);
                if (current == null) return null;
                var old = await Commands.WideTextPreview.ReadAsync(_repo, _option.BinaryOldRevision, _option.BinaryOldPath, result.OldHash, codePage).ConfigureAwait(false);
                if (old == null || (codePage == 0 && Models.DisplayEncoding.DetectWide(old) == 0 && Models.DisplayEncoding.DetectWide(current) == 0)) return null;
                // A revision can change the encoding itself. Preserve the old
                // revision's explicit byte order when viewing the new Unicode text.
                var oldMark = Models.DisplayEncoding.WidePreamble(old);
                var before = Models.DisplayEncoding.Decode(old, Models.DisplayEncoding.IsWide(codePage) && oldMark != 0 ? oldMark : codePage);
                var after = Models.DisplayEncoding.Decode(current, codePage);
                if (!Models.DisplayEncoding.IsReadableText(before) || !Models.DisplayEncoding.IsReadableText(after) ||
                    before.AsSpan().Count('\n') + after.AsSpan().Count('\n') > 100000) return null;
                return Models.TextDiff.FromDecodedText(before, after, context, ignoreSpace, ignoreCR, result);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }

        private Task<Models.BinaryDiff> CreateBinaryDiffAsync()
        {
            return Task.FromResult(new Models.BinaryDiff
            {
                Repository = _repo,
                OldFilePath = _option.BinaryOldPath,
                FilePath = _option.BinaryNewPath,
                OldRevision = _option.BinaryOldRevision,
                NewRevision = _option.BinaryNewRevision,
            });
        }

        private async Task<Models.SubmoduleDiff> CreateSubmoduleDiffAsync(string oldRevision, string newRevision)
        {
            var submoduleDiff = new Models.SubmoduleDiff();
            var submoduleRoot = $"{_repo}/{_option.Path}".Replace('\\', '/').TrimEnd('/');
            submoduleDiff.FullPath = submoduleRoot;

            if (IsValidSubmoduleHash(oldRevision))
                submoduleDiff.Old = await new Commands.QuerySubmoduleRevision(submoduleRoot, oldRevision)
                    .GetResultAsync()
                    .ConfigureAwait(false);

            if (IsValidSubmoduleHash(newRevision))
                submoduleDiff.New = await new Commands.QuerySubmoduleRevision(submoduleRoot, newRevision)
                    .GetResultAsync()
                    .ConfigureAwait(false);

            return submoduleDiff;
        }

        private bool IsValidSubmoduleHash(string hash)
        {
            for (int i = 0; i < hash.Length; i++)
            {
                if (hash[i] != '0')
                    return true;
            }

            return false;
        }

        private bool IsEmptyFileHash(string hash)
        {
            if (string.IsNullOrEmpty(hash))
                return false;

            if (hash.Length == 40)
                return hash.Equals(Models.EmptyFile.SHA1, StringComparison.Ordinal);

            if (hash.Length == 64)
                return hash.Equals(Models.EmptyFile.SHA256, StringComparison.Ordinal);

            return false;
        }

        private class Info
        {
            public string Argument { get; }
            public int UnifiedLines { get; }
            public bool IgnoreWhitespace { get; }
            public int CodePage { get; }
            public string OldHash { get; }
            public string NewHash { get; }

            public Info(Models.DiffOption option, int unifiedLines, bool ignoreWhitespace, int codePage, Models.DiffResult result)
            {
                Argument = option.ToString();
                UnifiedLines = unifiedLines;
                IgnoreWhitespace = ignoreWhitespace;
                CodePage = codePage;
                OldHash = result.OldHash;
                NewHash = result.NewHash;
            }

            public bool IsSame(Info other)
            {
                return Argument.Equals(other.Argument, StringComparison.Ordinal) &&
                    UnifiedLines == other.UnifiedLines && CodePage == other.CodePage &&
                    IgnoreWhitespace == other.IgnoreWhitespace &&
                    OldHash.Equals(other.OldHash, StringComparison.Ordinal) &&
                    NewHash.Equals(other.NewHash, StringComparison.Ordinal);
            }
        }

        private readonly int _entireFileLines = 999999999;
        private readonly string _repo;
        private readonly Models.DiffOption _option = null;
        private int _oldMode = 0;
        private int _newMode = 0;
        private int _unifiedLines = 4;
        private bool _isTextDiff = false;
        private bool _isIgnoreWhitespaceVisible = true;
        private object _content = null;
        private Info _info = null;
        private int _loadVersion;
    }
}
