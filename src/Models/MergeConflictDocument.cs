using System;
using System.Collections.Generic;

using AvaloniaEdit.Document;

namespace SourceGit.Models
{
    // The editable document is the saved result. Comparison padding never enters it.
    public sealed class MergeConflictDocument
    {
        private sealed class Region
        {
            public ConflictRegion Info;
            public TextAnchor Start;
            public TextAnchor End;
            public string Original;
            public string Ours;
            public string Theirs;
            public int OursEndLine;
            public int TheirsStartLine;
            public bool Mapped = true;
            public int MarkerSize;
            public bool FinalNewline;
        }

        private readonly record struct AnchorPosition(int Region, int Start, int End, bool Mapped);

        // Bracket the text operation so undo/redo also restores anchors deleted by
        // selections, including Ctrl+A/Delete. Ordinary typing stores no snapshots.
        private sealed class RestorePositions(MergeConflictDocument owner, AnchorPosition[] positions, bool before) : IUndoableOperation
        {
            public void Undo() { if (before) owner.Restore(positions); }
            public void Redo() { if (!before) owner.Restore(positions); }
        }

        public TextDocument Document { get; }
        public List<ConflictRegion> Regions { get; } = [];
        public List<ConflictLine> OursLines { get; } = [];
        public List<ConflictLine> TheirsLines { get; } = [];
        public int SourceLineCount { get; }
        public int UnsolvedCount { get; private set; }
        public bool HasChanges { get; private set; }
        public event EventHandler Changed;

        public MergeConflictDocument(string text)
        {
            _original = text;
            Document = new TextDocument(text);
            SourceLineCount = Document.LineCount;
            Document.UndoStack.SizeLimit = Math.Clamp(16 * 1024 * 1024 / Math.Max(1, text.Length * 2), 8, 256);
            Parse();
            BuildComparison();
            Refresh();
            Document.Changing += OnChanging;
            Document.Changed += OnChanged;
            Document.TextChanged += (_, _) => Refresh();
        }

        public int GetResultOffset(int index) => _regions[index].Start.Offset;
        public bool CanResolve(int index) => index >= 0 && index < _regions.Count && _regions[index].Mapped && _regions[index].Start.Offset <= _regions[index].End.Offset;
        public int SourceLineForOffset(int offset)
        {
            foreach (var region in _regions)
                if (region.Mapped && offset >= region.Start.Offset && offset <= region.End.Offset) return region.Info.StartLineInOriginal + 1;
            return 0;
        }

        public bool Resolve(int index, ConflictResolution resolution)
        {
            if (!CanResolve(index)) return false;
            var region = _regions[index];
            if (resolution != ConflictResolution.None && region.Info.IsResolved) return false;
            var result = resolution switch
            {
                ConflictResolution.UseOurs => region.Ours,
                ConflictResolution.UseTheirs => region.Theirs,
                ConflictResolution.UseBothMineFirst => region.Ours + region.Theirs,
                ConflictResolution.UseBothTheirsFirst => region.Theirs + region.Ours,
                _ => region.Original,
            };
            if (resolution != ConflictResolution.None && !region.FinalNewline)
            {
                if (result.EndsWith("\r\n", StringComparison.Ordinal)) result = result[..^2];
                else if (result.EndsWith('\n') || result.EndsWith('\r')) result = result[..^1];
            }
            region.Info.ResolutionType = resolution;
            _resolving = index;
            try { Document.Replace(region.Start.Offset, region.End.Offset - region.Start.Offset, result); }
            finally { _resolving = -1; }
            return true;
        }

        public IReadOnlyList<int> UnresolvedOffsets() => _unresolvedOffsets;

        private static char Marker(string line)
        {
            if (line.Length < 7 || line[0] is not ('<' or '>' or '=' or '|')) return '\0';
            var length = 1;
            while (length < line.Length && line[length] == line[0]) length++;
            return length >= 7 && (length == line.Length || char.IsWhiteSpace(line[length])) ? line[0] : '\0';
        }

        private void Parse()
        {
            for (var lineNumber = 1; lineNumber <= Document.LineCount; lineNumber++)
            {
                var start = Document.GetLineByNumber(lineNumber);
                var startText = Document.GetText(start);
                if (Marker(startText) != '<') continue;
                var markerSize = 0;
                while (markerSize < startText.Length && startText[markerSize] == '<') markerSize++;
                DocumentLine separator = null, ancestor = null, end = null;
                for (var line = start.NextLine; line != null; line = line.NextLine)
                {
                    var content = Document.GetText(line);
                    var marker = Marker(content);
                    var size = 0;
                    while (size < content.Length && content[size] == (content.Length > 0 ? content[0] : '\0')) size++;
                    if (size != markerSize) continue;
                    if (marker == '<') break;
                    if (marker == '|' && separator == null) ancestor ??= line;
                    if (marker == '=' && separator == null) separator = line;
                    if (marker == '>') { end = line; break; }
                }
                // Malformed markers remain fully editable; saving waits for their removal.
                if (separator == null || end == null) continue;
                var oursEnd = ancestor ?? separator;
                var info = new ConflictRegion
                {
                    StartLineInOriginal = start.LineNumber - 1,
                    EndLineInOriginal = end.LineNumber - 1,
                    StartMarker = Document.GetText(start),
                    SeparatorMarker = Document.GetText(separator),
                    EndMarker = Document.GetText(end),
                };
                for (var line = start.NextLine; line != oursEnd; line = line.NextLine) info.OursContent.Add(Document.GetText(line));
                for (var line = separator.NextLine; line != end; line = line.NextLine) info.TheirsContent.Add(Document.GetText(line));
                var offset = end.Offset + end.TotalLength;
                var region = new Region
                {
                    Info = info,
                    Start = Anchor(start.Offset, false),
                    End = Anchor(offset, true),
                    Original = Document.GetText(start.Offset, offset - start.Offset),
                    Ours = Document.GetText(start.Offset + start.TotalLength, oursEnd.Offset - start.Offset - start.TotalLength),
                    Theirs = Document.GetText(separator.Offset + separator.TotalLength, end.Offset - separator.Offset - separator.TotalLength),
                    OursEndLine = oursEnd.LineNumber - 1,
                    TheirsStartLine = separator.LineNumber,
                    MarkerSize = markerSize,
                    FinalNewline = end.DelimiterLength > 0 || offset < Document.TextLength,
                };
                Regions.Add(info); _regions.Add(region);
                lineNumber = end.LineNumber;
            }
        }

        private void BuildComparison()
        {
            if (_regions.Count == 0) return;
            var regionIndex = 0;
            var oursNumber = 1;
            var theirsNumber = 1;
            foreach (var line in Document.Lines)
            {
                var index = line.LineNumber - 1;
                while (regionIndex < _regions.Count && index > _regions[regionIndex].Info.EndLineInOriginal) regionIndex++;
                var region = regionIndex < _regions.Count ? _regions[regionIndex] : null;
                var text = Document.GetText(line);
                if (region == null || index < region.Info.StartLineInOriginal)
                {
                    OursLines.Add(new(ConflictLineType.Common, text, oursNumber++));
                    TheirsLines.Add(new(ConflictLineType.Common, text, theirsNumber++));
                }
                else
                {
                    var ours = index > region.Info.StartLineInOriginal && index < region.OursEndLine;
                    var theirs = index >= region.TheirsStartLine && index < region.Info.EndLineInOriginal;
                    OursLines.Add(ours ? new(ConflictLineType.Ours, text, oursNumber++) : new());
                    TheirsLines.Add(theirs ? new(ConflictLineType.Theirs, text, theirsNumber++) : new());
                }
            }
        }

        private TextAnchor Anchor(int offset, bool end)
        {
            var anchor = Document.CreateAnchor(offset);
            anchor.SurviveDeletion = true;
            anchor.MovementType = end ? AnchorMovementType.BeforeInsertion : AnchorMovementType.AfterInsertion;
            return anchor;
        }

        private void OnChanging(object sender, DocumentChangeEventArgs e)
        {
            _pending = null;
            if (!Document.UndoStack.AcceptChanges) return;
            var positions = new List<AnchorPosition>();
            for (var i = 0; i < _regions.Count; i++)
            {
                var r = _regions[i];
                var start = r.Start.Offset;
                var end = r.End.Offset;
                if (start >= e.Offset && start <= e.Offset + e.RemovalLength || end >= e.Offset && end <= e.Offset + e.RemovalLength)
                    positions.Add(new(i, start, end, r.Mapped));
                if (_resolving != i && e.RemovalLength > 0 &&
                    (e.Offset <= start && e.Offset + e.RemovalLength > start || e.Offset < end && e.Offset + e.RemovalLength >= end))
                    r.Mapped = false;
            }
            if (positions.Count == 0) return;
            _pending = positions.ToArray();
            Document.UndoStack.PushOptional(new RestorePositions(this, _pending, true));
        }

        private void OnChanged(object sender, DocumentChangeEventArgs e)
        {
            if (_resolving >= 0 && Document.UndoStack.AcceptChanges)
            {
                var region = _regions[_resolving];
                region.Start = Anchor(e.Offset, false);
                region.End = Anchor(e.Offset + e.InsertionLength, true);
                region.Mapped = true;
            }
            foreach (var region in _regions)
                if (region.Start.Offset > region.End.Offset) region.Mapped = false;
            if (_pending == null || !Document.UndoStack.AcceptChanges) return;
            var positions = new AnchorPosition[_pending.Length];
            for (var i = 0; i < positions.Length; i++)
            {
                var item = _pending[i];
                var region = _regions[item.Region];
                positions[i] = new(item.Region, region.Start.Offset, region.End.Offset, region.Mapped);
            }
            Document.UndoStack.PushOptional(new RestorePositions(this, positions, false));
            _pending = null;
        }

        private void Restore(AnchorPosition[] positions)
        {
            foreach (var p in positions)
            {
                _regions[p.Region].Start = Anchor(p.Start, false);
                _regions[p.Region].End = Anchor(p.End, true);
                _regions[p.Region].Mapped = p.Mapped;
            }
            Refresh();
        }

        private void Refresh()
        {
            var open = false;
            var count = 0;
            var regionIndex = 0;
            _unresolvedOffsets.Clear();
            foreach (var r in _regions) r.Info.IsResolved = true;
            foreach (var line in Document.Lines)
            {
                if (line.Length < 7 || Document.GetCharAt(line.Offset) is not ('<' or '>' or '=' or '|')) continue;
                var kind = Marker(Document.GetText(line));
                if (kind == '\0') continue;
                while (regionIndex < _regions.Count && (!_regions[regionIndex].Mapped || _regions[regionIndex].End.Offset <= line.Offset)) regionIndex++;
                var active = regionIndex < _regions.Count ? _regions[regionIndex] : null;
                var belongs = active != null && active.Start.Offset <= line.Offset && line.Offset < active.End.Offset;
                if (belongs)
                {
                    var text = Document.GetText(line);
                    var size = 0;
                    while (size < text.Length && text[size] == text[0]) size++;
                    if (size != active.MarkerSize) continue;
                    active.Info.IsResolved = false;
                }
                if (kind == '<') { count++; _unresolvedOffsets.Add(line.Offset); open = true; }
                else if (kind == '|' || kind == '=' && (open || belongs)) { if (!open) { count++; _unresolvedOffsets.Add(line.Offset); open = true; } }
                else if (kind == '>') { if (!open) { count++; _unresolvedOffsets.Add(line.Offset); } open = false; }
            }
            foreach (var region in _regions)
                if (!region.Mapped) region.Info.IsResolved = count == 0;
            UnsolvedCount = count;
            HasChanges = !string.Equals(Document.Text, _original, StringComparison.Ordinal);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private readonly string _original;
        private readonly List<Region> _regions = [];
        private AnchorPosition[] _pending;
        private readonly List<int> _unresolvedOffsets = [];
        private int _resolving = -1;
    }
}
