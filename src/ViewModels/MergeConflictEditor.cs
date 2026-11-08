using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Avalonia;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class MergeConflictEditor : ObservableObject
    {
        public string FilePath => _filePath;
        public string RepositoryPath => _repo.FullPath;
        public object Mine { get; }
        public object Theirs { get; }
        public TextDocument ResultDocument => _buffer?.Document;
        public List<Models.ConflictLine> OursLines => _comparison?.Local ?? [];
        public List<Models.ConflictLine> BaseLines => _comparison?.Base ?? [];
        public List<Models.ConflictLine> TheirsLines => _comparison?.Remote ?? [];
        public IReadOnlyList<Models.MergeConflictRange> ComparisonRanges => _comparison?.Ranges ?? [];
        public string BaseHint => _baseHint;
        public string BaseSample => _baseText == null ? string.Empty : _baseText[..Math.Min(4096, _baseText.Length)];
        public IReadOnlyList<Models.ConflictRegion> ConflictRegions => _buffer?.Regions ?? [];
        public int MaxLineNumber => Math.Max(OursLines.Count, TheirsLines.Count);
        public int UnsolvedCount => _buffer?.UnsolvedCount ?? 0;
        public bool HasComparison => ConflictRegions.Count > 0;
        public bool HasChanges => _buffer?.HasChanges == true || _convertedEncoding;
        public bool CanSave => _buffer != null && UnsolvedCount == 0 && !IsSaving;
        public bool CanChangeEncoding => _buffer != null && (!_inputCodec.HasPreamble || HasChanges) && !IsSaving;
        public IReadOnlyList<Models.MergeConflictEncoding> EncodingChoices => Models.MergeConflictEncoding.Choices;
        public IReadOnlyList<int> UnresolvedOffsets => _buffer?.UnresolvedOffsets() ?? [];
        public bool CanResolve(int index) => !IsSaving && _buffer?.CanResolve(index) == true;
        public int SourceLineForOffset(int offset) => _buffer?.SourceLineForOffset(offset) ?? 0;
        public int TotalConflictCount => ConflictRegions.Count;
        public string ConflictPosition => $"{(_activeConflictIndex >= 0 ? _activeConflictIndex + 1 : 0)}/{TotalConflictCount}";
        public int ActiveConflictIndex
        {
            get => _activeConflictIndex;
            set
            {
                if (value >= 0 && value < TotalConflictCount && SetProperty(ref _activeConflictIndex, value))
                    OnPropertyChanged(nameof(ConflictPosition));
            }
        }
        public int ResultOffset(int index) => _buffer?.GetResultOffset(index) ?? 0;
        public int RegionForOffset(int offset) => _buffer?.RegionForOffset(offset) ?? -1;
        public int RegionForSourceLine(int line)
        {
            return _comparison?.RegionAtLine(line) ?? -1;
        }
        public bool IsActiveSourceLine(int line) => _activeConflictIndex >= 0 && RegionForSourceLine(line) == _activeConflictIndex;
        public bool IsActiveResultLine(int line) => _buffer?.ContainsResultLine(_activeConflictIndex, line) == true;
        public int RegionForResultLine(int line)
        {
            // Empty resolved blocks still have a visible caret location.
            var index = _buffer?.RegionForOffset(ResultDocument.GetLineByNumber(line).Offset) ?? -1;
            return index;
        }

        public string Error { get => _error; private set => SetProperty(ref _error, value); }
        public bool IsSaving { get => _isSaving; private set { if (SetProperty(ref _isSaving, value)) NotifyState(); } }
        public Vector ScrollOffset { get => _scrollOffset; set => SetProperty(ref _scrollOffset, value); }

        public Models.MergeConflictEncoding SelectedEncoding
        {
            get => _selectedEncoding;
            set
            {
                if (value == null || value == _selectedEncoding || !CanChangeEncoding) return;
                try
                {
                    if (HasChanges)
                    {
                        _outputCodec = value.CodePage == _inputCodec.CodePage ? _inputCodec : _inputCodec.ConvertTo(value.CodePage);
                        _convertedEncoding = _outputCodec.CodePage != _inputCodec.CodePage || _outputCodec.HasPreamble != _inputCodec.HasPreamble;
                    }
                    else Load(_expectedBytes, value.CodePage);
                    SetProperty(ref _selectedEncoding, value);
                    NotifyState();
                }
                catch (Exception ex) when (ex is DecoderFallbackException or EncoderFallbackException or FormatException)
                {
                    Error = App.Text("MergeConflictEditor.InvalidEncoding");
                    OnPropertyChanged(nameof(SelectedEncoding));
                }
            }
        }

        public MergeConflictEditor(Repository repo, Models.Commit head, string filePath)
        {
            _repo = repo;
            _filePath = filePath;
            (Mine, Theirs) = repo.InProgressContext switch
            {
                CherryPickInProgress cherryPick => (head, cherryPick.Head),
                RebaseInProgress rebase => (rebase.Onto, rebase.StoppedAt),
                RevertInProgress revert => (head, revert.Head),
                MergeInProgress merge => (head, merge.Source),
                _ => (head, (object)"Stash or Patch"),
            };
            try
            {
                var path = CheckedPath();
                if (new FileInfo(path).Length > Models.MergeConflictTextFile.MaxBytes) { Error = App.Text("MergeConflictEditor.TooLarge"); return; }
                _expectedBytes = File.ReadAllBytes(path);
                _expectedIndex = new Commands.QueryConflictEntries(repo.FullPath, filePath).GetResult();
                if (string.IsNullOrEmpty(_expectedIndex)) { Error = App.Text("MergeConflictEditor.StateChanged"); return; }
                try
                {
                    var bytes = new Commands.QueryConflictBase(repo.FullPath, _expectedIndex).GetResult();
                    if (bytes != null) _baseText = Models.MergeConflictTextFile.Read(bytes).Text;
                    else _baseHint = App.Text("MergeConflictEditor.EmptyBase");
                }
                catch (Exception ex)
                {
                    Native.OS.LogException(ex);
                    _baseHint = App.Text("MergeConflictEditor.BaseUnavailable");
                }
                Load(_expectedBytes);
            }
            catch (Exception ex)
            {
                Native.OS.LogException(ex);
                Error = App.Text("MergeConflictEditor.CannotOpen");
            }
        }

        public Models.ConflictLineState GetLineState(int line)
        {
            var index = RegionForSourceLine(line + 1);
            if (index < 0) return Models.ConflictLineState.Normal;
            var region = ConflictRegions[index];
            var range = ComparisonRanges[index];
            if (line == range.Start) return region.IsResolved ? Models.ConflictLineState.ResolvedBlockStart : Models.ConflictLineState.ConflictBlockStart;
            if (line == range.End) return region.IsResolved ? Models.ConflictLineState.ResolvedBlockEnd : Models.ConflictLineState.ConflictBlockEnd;
            return region.IsResolved ? Models.ConflictLineState.ResolvedBlock : Models.ConflictLineState.ConflictBlock;
        }

        public bool ResolveRegion(int index, Models.ConflictResolution resolution)
        {
            if (!CanResolve(index)) return false;
            ActiveConflictIndex = index;
            return _buffer.Resolve(index, resolution);
        }

        public void ClearErrorMessage() => Error = string.Empty;

        public async Task<bool> SaveAndStageAsync()
        {
            if (_buffer == null || IsSaving) return false;
            if (UnsolvedCount > 0) { Error = App.Text("MergeConflictEditor.StillUnresolved"); return false; }
            IsSaving = true;
            using var watcher = _repo.LockWatcher();
            var log = _repo.CreateLog($"Resolve '{_filePath}'");
            string temporary = null;
            try
            {
                var path = CheckedPath();
                if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) { Error = App.Text("MergeConflictEditor.ReadOnly"); return false; }
                var current = await File.ReadAllBytesAsync(path);
                if (!current.AsSpan().SequenceEqual(_expectedBytes)) { Error = App.Text("MergeConflictEditor.FileChanged"); return false; }
                if (await new Commands.QueryConflictEntries(_repo.FullPath, _filePath).GetResultAsync() != _expectedIndex)
                { Error = App.Text("MergeConflictEditor.StateChanged"); return false; }

                var content = _outputCodec.Encode(ResultDocument.Text);
                temporary = Path.Combine(Path.GetDirectoryName(path), "." + Path.GetFileName(path) + ".treegit-merge-" + Guid.NewGuid().ToString("N"));
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await stream.WriteAsync(content);
                    stream.Flush(true);
                }
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(path));
                if (!(await File.ReadAllBytesAsync(path)).AsSpan().SequenceEqual(_expectedBytes))
                { Error = App.Text("MergeConflictEditor.FileChanged"); return false; }
                if (await new Commands.QueryConflictEntries(_repo.FullPath, _filePath).GetResultAsync() != _expectedIndex)
                { Error = App.Text("MergeConflictEditor.StateChanged"); return false; }
                File.Replace(temporary, path, null);
                temporary = null;
                _expectedBytes = content;
                var command = new Commands.Command
                {
                    WorkingDirectory = _repo.FullPath, Context = _repo.FullPath,
                    Args = $"--literal-pathspecs add -- {_filePath.Quoted()}",
                };
                var staged = await command.Use(log).ExecAsync();
                _repo.MarkWorkingCopyDirtyManually();
                if (!staged) { Error = App.Text("MergeConflictEditor.StageFailed"); return false; }
                return true;
            }
            catch (EncoderFallbackException) { Error = App.Text("MergeConflictEditor.Unencodable"); return false; }
            catch (ArgumentOutOfRangeException) { Error = App.Text("MergeConflictEditor.TooLarge"); return false; }
            catch (Exception ex)
            {
                Native.OS.LogException(ex);
                Error = App.Text("MergeConflictEditor.SaveFailed");
                return false;
            }
            finally
            {
                if (temporary != null && File.Exists(temporary))
                {
                    try { File.Delete(temporary); } catch (IOException) { }
                }
                log.Complete();
                IsSaving = false;
            }
        }

        private string CheckedPath()
        {
            var root = Path.GetFullPath(_repo.FullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var path = Path.GetFullPath(Path.Combine(root, _filePath));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, comparison)) throw new IOException("Path is outside repository");
            for (var entry = path; entry != null; entry = Path.GetDirectoryName(entry))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked paths are not editable");
                if (entry.Equals(root, comparison)) break;
            }
            return path;
        }

        private void Load(byte[] bytes, int? codePage = null)
        {
            var codec = Models.MergeConflictTextFile.Read(bytes, codePage);
            var buffer = new Models.MergeConflictDocument(codec.Text);
            if (_buffer != null) _buffer.Changed -= OnDocumentChanged;
            _inputCodec = _outputCodec = codec;
            _buffer = buffer;
            _comparison = new Models.MergeConflictComparison(buffer, _baseText);
            _buffer.Changed += OnDocumentChanged;
            _activeConflictIndex = buffer.Regions.Count > 0 ? Math.Clamp(_activeConflictIndex, 0, buffer.Regions.Count - 1) : -1;
            _convertedEncoding = false;
            _selectedEncoding = EncodingChoices.First(e => e.CodePage == codec.CodePage);
            foreach (var property in new[] { nameof(ResultDocument), nameof(OursLines), nameof(BaseLines), nameof(TheirsLines), nameof(BaseHint), nameof(ConflictRegions), nameof(MaxLineNumber), nameof(HasComparison), nameof(SelectedEncoding), nameof(TotalConflictCount), nameof(ActiveConflictIndex), nameof(ConflictPosition) }) OnPropertyChanged(property);
            NotifyState();
        }

        private void OnDocumentChanged(object sender, EventArgs e)
        {
            NotifyState();
        }

        private void NotifyState()
        {
            foreach (var property in new[] { nameof(UnsolvedCount), nameof(HasChanges), nameof(CanSave), nameof(CanChangeEncoding), nameof(UnresolvedOffsets) }) OnPropertyChanged(property);
        }

        private readonly Repository _repo;
        private readonly string _filePath;
        private Models.MergeConflictDocument _buffer;
        private Models.MergeConflictComparison _comparison;
        private string _baseText;
        private string _baseHint = string.Empty;
        private Models.MergeConflictTextFile _inputCodec, _outputCodec;
        private Models.MergeConflictEncoding _selectedEncoding;
        private byte[] _expectedBytes;
        private string _expectedIndex;
        private string _error = string.Empty;
        private bool _isSaving, _convertedEncoding;
        private Vector _scrollOffset;
        private int _activeConflictIndex = -1;
    }
}
