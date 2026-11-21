using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public partial class Diff : Command
    {
        [GeneratedRegex(@"^@@ \-(\d+),?\d* \+(\d+),?\d* @@")]
        private static partial Regex REG_INDICATOR();

        [GeneratedRegex(@"^index\s([0-9a-f]{6,64})\.\.([0-9a-f]{6,64})(\s[1-9]{6})?")]
        private static partial Regex REG_HASH_CHANGE();

        private const char PREFIX_CONTEXT = ' ';
        private const char PREFIX_DELETED = '-';
        private const char PREFIX_ADDED = '+';
        private const char PREFIX_COMMAND = '\\';

        private const string FILE_MODE_OLD = "old mode ";
        private const string FILE_MODE_NEW = "new mode ";
        private const string FILE_MODE_DELETED = "deleted file mode ";
        private const string FILE_MODE_ADDED = "new file mode ";

        private const string LFS_SPECIFIER = "version https://git-lfs.github.com/spec/";
        private const string LFS_OID_PREFIX = "oid sha256:";
        private const string LFS_SIZE_PREFIX = "size ";

        private const string SPECIAL_DIFF_START = "diff ";
        private const string SPECIAL_BINARY = "Binary files ";
        private const string SPECIAL_SUBMODULE = "Subproject commit ";

        private const int MAX_INLINE_CONTENT_LENGTH = 1024;
        private const int MAX_INLINE_CHUNKS_PER_LINE = 16;

        public Diff(string repo, Models.DiffOption opt, int numContextLines, bool ignoreWhitespace, bool ignoreCRAtEOL, int displayCodePage = 0)
        {
            _result.TextDiff = new Models.TextDiff();
            _displayCodePage = displayCodePage;

            WorkingDirectory = repo;
            Context = repo;

            var builder = new StringBuilder(256);
            builder.Append("--literal-pathspecs diff --no-color --no-ext-diff --full-index --patch ");
            if (ignoreWhitespace)
                builder.Append("--ignore-space-change --ignore-blank-lines ");
            if (ignoreCRAtEOL)
                builder.Append("--ignore-cr-at-eol ");
            builder.Append("--unified=").Append(numContextLines).Append(' ');
            builder.Append(opt.ToString());

            Args = builder.ToString();
        }

        public async Task<Models.DiffResult> ReadAsync()
        {
            try
            {
                CancellationToken.ThrowIfCancellationRequested();
                using var proc = new Process();
                proc.StartInfo = CreateGitStartInfo(true);
                Native.ProcessLauncher.Start(proc);
                using var cancellation = CancellationToken.Register(StopGit);
                try
                {
                    proc.BeginErrorReadLine();
                    await ReadPatchAsync(proc.StandardOutput.BaseStream).ConfigureAwait(false);
                    await proc.WaitForExitAsync(CancellationToken).ConfigureAwait(false);
                }
                finally { StopGit(); }

                void StopGit()
                {
                    try { if (!proc.HasExited) Native.OS.TerminateProcess(proc); }
                    catch (InvalidOperationException) { }
                }
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                // Ignore exceptions.
            }

            if (_isLFS && (_invalidLfs || _oldLfsFields is not (0 or 7) || _newLfsFields is not (0 or 7)))
            {
                _isLFS = false;
                _result.LFSDiff = null;
            }

            if (_isLFS || _result.IsBinary || _result.TextDiff.Lines.Count == 0)
            {
                _result.TextDiff = null;
            }
            else
            {
                if (_isInChunk)
                {
                    ProcessInlineHighlights();
                    _isInChunk = false;
                }

                if (_result.TextDiff.Lines.Count < 4)
                {
                    var isSubmoduleChange = true;

                    for (int i = 1; i < _result.TextDiff.Lines.Count; i++)
                    {
                        var line = _result.TextDiff.Lines[i];
                        if (!line.Content.StartsWith(SPECIAL_SUBMODULE, StringComparison.Ordinal))
                        {
                            isSubmoduleChange = false;
                            break;
                        }
                    }

                    if (isSubmoduleChange)
                    {
                        _result.IsSubmoduleChange = true;
                        _result.TextDiff = null;
                        return _result;
                    }
                }

                _result.TextDiff.MaxLineNumber = Math.Max(_newLine, _oldLine);
                _result.TextDiff.OldMode = _result.OldMode;
                _result.TextDiff.NewMode = _result.NewMode;
                _result.TextDiff.OldHash = _result.OldHash;
                _result.TextDiff.NewHash = _result.NewHash;
            }

            return _result;
        }

        private async Task ReadPatchAsync(Stream stream)
        {
            var pool = ArrayPool<byte>.Shared;
            var initial = pool.Rent(64 * 1024);
            var buffer = initial;
            var length = 0;
            var scanned = 0;
            try
            {
                while (true)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(length), CancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        if (length > 0)
                            ParseLine(new ArraySegment<byte>(buffer, 0, length));
                        return;
                    }

                    length += read;
                    var lineStart = 0;
                    while (scanned < length)
                    {
                        CancellationToken.ThrowIfCancellationRequested();
                        var lineEnd = Array.IndexOf(buffer, (byte)'\n', scanned, length - scanned);
                        if (lineEnd < 0) break;

                        ParseLine(new ArraySegment<byte>(buffer, lineStart, lineEnd - lineStart));
                        if (_result.IsBinary)
                        {
                            await stream.CopyToAsync(Stream.Null, CancellationToken).ConfigureAwait(false);
                            return;
                        }

                        scanned = lineStart = lineEnd + 1;
                    }

                    // Preserve the unfinished line. Grow only for a line that
                    // spans the entire buffer, irrespective of total patch size.
                    length -= lineStart;
                    if (length == buffer.Length)
                        Array.Resize(ref buffer, checked(buffer.Length * 2));
                    else if (lineStart > 0)
                    {
                        buffer.AsSpan(lineStart, length).CopyTo(buffer);
                    }
                    scanned = length;
                }
            }
            finally
            {
                pool.Return(initial, clearArray: true);
            }
        }

        private void ParseLine(ArraySegment<byte> lineBytes)
        {
            if (lineBytes.Count == 0) return;
            var bytes = lineBytes.AsSpan();
            if (_isInChunk)
            {
                var prefix = (char)bytes[0];
                if (prefix is PREFIX_DELETED or PREFIX_ADDED or PREFIX_CONTEXT)
                {
                    ParseChunkBodyLine(prefix, DecodePatchLine(bytes), bytes[1..].ToArray());
                    return;
                }

                if (prefix == PREFIX_COMMAND)
                {
                    if (bytes[1..].SequenceEqual(" No newline at end of file"u8))
                        _last.NoNewLineEndOfFile = true;
                    return;
                }

                ProcessInlineHighlights();
                _isInChunk = false;
            }

            var line = Encoding.UTF8.GetString(bytes);
            if (ParseChunkStartLine(line))
            {
                _isInChunk = true;
                return;
            }

            // Fallback to diff headers to support type-changed diff (multiple headers).
            ParseDiffHeaderLine(line);
        }

        private string DecodePatchLine(ReadOnlySpan<byte> bytes)
        {
            var firstLine = bytes[0] switch
            {
                (byte)'-' => _oldLine == 1,
                (byte)'+' => _newLine == 1,
                _ => _oldLine == 1 || _newLine == 1,
            };
            return _displayCodePage != 0 || firstLine
                ? Models.DisplayEncoding.Decode(bytes[1..], _displayCodePage)
                : Models.DisplayEncoding.DecodeNarrow(bytes[1..]);
        }

        private void ParseDiffHeaderLine(string line)
        {
            if (line.StartsWith(SPECIAL_DIFF_START, StringComparison.Ordinal))
                return;

            if (ParseFileModeChange(line))
                return;

            var match = REG_HASH_CHANGE().Match(line);
            if (match.Success)
            {
                if (string.IsNullOrEmpty(_result.OldHash))
                    _result.OldHash = match.Groups[1].Value;
                _result.NewHash = match.Groups[2].Value;
                return;
            }

            if (line.StartsWith(SPECIAL_BINARY, StringComparison.Ordinal))
                _result.IsBinary = true;
        }

        private bool ParseChunkStartLine(string line)
        {
            var match = REG_INDICATOR().Match(line);
            if (match.Success)
            {
                _oldLine = int.Parse(match.Groups[1].Value);
                _newLine = int.Parse(match.Groups[2].Value);
                _last = new Models.TextDiffLine(Models.TextDiffLineType.Indicator, line, null, 0, 0);
                _result.TextDiff.Lines.Add(_last);
                return true;
            }

            return false;
        }

        private void ParseChunkBodyLine(char prefix, string content, byte[] rawContent)
        {
            ParseLFSChange(prefix, content);

            if (prefix == PREFIX_DELETED)
            {
                _result.TextDiff.DeletedLines++;
                _last = new Models.TextDiffLine(Models.TextDiffLineType.Deleted, content, rawContent, _oldLine, 0);
                _deleted.Add(_last);
                _oldLine++;
            }
            else if (prefix == PREFIX_ADDED)
            {
                _result.TextDiff.AddedLines++;
                _last = new Models.TextDiffLine(Models.TextDiffLineType.Added, content, rawContent, 0, _newLine);
                _added.Add(_last);
                _newLine++;
            }
            else
            {
                ProcessInlineHighlights();

                _last = new Models.TextDiffLine(Models.TextDiffLineType.Normal, content, rawContent, _oldLine, _newLine);
                _result.TextDiff.Lines.Add(_last);
                _oldLine++;
                _newLine++;
            }
        }

        private bool ParseFileModeChange(string line)
        {
            if (line.StartsWith(FILE_MODE_OLD, StringComparison.Ordinal))
            {
                _result.OldMode = int.Parse(line.AsSpan(9));
                return true;
            }

            if (line.StartsWith(FILE_MODE_NEW, StringComparison.Ordinal))
            {
                _result.NewMode = int.Parse(line.AsSpan(9));
                return true;
            }

            if (line.StartsWith(FILE_MODE_DELETED, StringComparison.Ordinal))
            {
                _result.OldMode = int.Parse(line.AsSpan(18));
                return true;
            }

            if (line.StartsWith(FILE_MODE_ADDED, StringComparison.Ordinal))
            {
                _result.NewMode = int.Parse(line.AsSpan(14));
                return true;
            }

            return false;
        }

        private bool ParseLFSChange(char prefix, string content)
        {
            if (!_isLFS)
            {
                var atStart = (_oldLine == 1 && _newLine == 1 && prefix == PREFIX_CONTEXT) ||
                    (_oldLine == 1 && _newLine is 0 or 1 && prefix == PREFIX_DELETED) ||
                    (_oldLine == 0 && _newLine == 1 && prefix == PREFIX_ADDED);
                if (!atStart || !content.StartsWith(LFS_SPECIFIER, StringComparison.Ordinal)) return false;
                _isLFS = true;
                _result.LFSDiff = new Models.LFSDiff();
            }
            if (prefix is PREFIX_DELETED or PREFIX_CONTEXT) ReadLfsLine(content, _result.LFSDiff.Old, ref _oldLfsFields);
            if (prefix is PREFIX_ADDED or PREFIX_CONTEXT) ReadLfsLine(content, _result.LFSDiff.New, ref _newLfsFields);
            return true;
        }

        private void ReadLfsLine(string content, Models.LFSObject value, ref int fields)
        {
            content = content.TrimEnd('\r');
            var field = 0;
            if (content == LFS_SPECIFIER + "v1") field = 1;
            else if (content.StartsWith(LFS_OID_PREFIX, StringComparison.Ordinal))
            {
                field = 2; value.Oid = content.Substring(LFS_OID_PREFIX.Length);
                if (!Models.LFSObject.IsOid(value.Oid)) _invalidLfs = true;
            }
            else if (content.StartsWith(LFS_SIZE_PREFIX, StringComparison.Ordinal))
            {
                field = 4;
                if (!long.TryParse(content.AsSpan(LFS_SIZE_PREFIX.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var size)) _invalidLfs = true;
                else value.Size = size;
            }
            else if (content.StartsWith("ext-", StringComparison.Ordinal)) return;
            else _invalidLfs = true;
            if ((fields & field) != 0) _invalidLfs = true;
            fields |= field;
        }

        private void ProcessInlineHighlights()
        {
            if (_deleted.Count > 0)
            {
                if (_added.Count == _deleted.Count)
                {
                    for (int i = _added.Count - 1; i >= 0; i--)
                    {
                        var left = _deleted[i];
                        var right = _added[i];

                        if (left.Content.Length > MAX_INLINE_CONTENT_LENGTH || right.Content.Length > MAX_INLINE_CONTENT_LENGTH)
                            continue;

                        var chunks = Models.TextInlineChange.Compare(left.Content, right.Content);
                        if (chunks.Count > MAX_INLINE_CHUNKS_PER_LINE)
                            continue;

                        foreach (var chunk in chunks)
                        {
                            if (chunk.DeletedCount > 0)
                                left.Highlights.Add(new Models.TextRange(chunk.DeletedStart, chunk.DeletedCount));

                            if (chunk.AddedCount > 0)
                                right.Highlights.Add(new Models.TextRange(chunk.AddedStart, chunk.AddedCount));
                        }
                    }
                }

                _result.TextDiff.Lines.AddRange(_deleted);
                _deleted.Clear();
            }

            if (_added.Count > 0)
            {
                _result.TextDiff.Lines.AddRange(_added);
                _added.Clear();
            }
        }

        private readonly int _displayCodePage;
        private readonly Models.DiffResult _result = new Models.DiffResult();
        private readonly List<Models.TextDiffLine> _deleted = new List<Models.TextDiffLine>();
        private readonly List<Models.TextDiffLine> _added = new List<Models.TextDiffLine>();
        private Models.TextDiffLine _last = null;
        private int _oldLine = 0;
        private int _newLine = 0;
        private bool _isInChunk = false;
        private bool _isLFS = false;
        private bool _invalidLfs;
        private int _oldLfsFields;
        private int _newLfsFields;
    }
}
