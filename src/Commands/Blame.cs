using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public partial class Blame : Command
    {
        [GeneratedRegex(@"^\^?([0-9a-f]+)\s+(.*)\s+\((.*)\s+(\d+)\s+[\-\+]?\d+\s+\d+\) (.*)")]
        private static partial Regex REG_FORMAT();

        public Blame(string repo, string file, string revision, bool ignoreWhitespace)
        {
            WorkingDirectory = repo;
            Context = repo;
            RaiseError = false;

            var builder = new StringBuilder();
            builder.Append("blame --encoding=UTF-8 -f -t ");
            if (ignoreWhitespace)
                builder.Append("-w ");
            builder.Append(revision).Append(" -- ").Append(file.Quoted());

            Args = builder.ToString();
        }

        public async Task<Models.BlameData> ReadAsync()
        {
            // Keep each byte intact until the Git metadata and source line are separated.
            var rs = await ReadToEndAsync(outputEncoding: Encoding.Latin1).ConfigureAwait(false);
            if (!rs.IsSuccess)
                return _result;

            var lines = rs.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                ParseLine(line);

                if (_result.IsBinary)
                    return _result;
            }

            if (_needUnifyCommitSHA)
            {
                foreach (var line in _result.LineInfos)
                {
                    if (line.CommitSHA.Length > _minSHALen)
                        line.CommitSHA = line.CommitSHA.Substring(0, _minSHALen);
                }
            }

            if (_content.Length > 0)
                _content.Length--; // Remove the last newline character

            _result.Content = _content.ToString();
            return _result;
        }

        private void ParseLine(string line)
        {
            if (line.Contains('\0'))
            {
                _result.IsBinary = true;
                _result.LineInfos.Clear();
                return;
            }

            var match = REG_FORMAT().Match(line);
            if (!match.Success)
                return;

            // Force using `\n` as line-ending
            var content = match.Groups[5].Value;
            _content.Append(Ascii.IsValid(content) ? content : Models.DisplayEncoding.Decode(Encoding.Latin1.GetBytes(content))).Append('\n');

            var commit = match.Groups[1].Value;
            var file = DecodeMetadata(match.Groups[2].Value).Trim();
            var author = DecodeMetadata(match.Groups[3].Value);
            var timestamp = ulong.Parse(match.Groups[4].Value);

            var info = new Models.BlameLineInfo()
            {
                IsFirstInGroup = commit != _lastSHA,
                CommitSHA = commit,
                File = file,
                Author = author,
                Timestamp = timestamp,
            };

            _result.LineInfos.Add(info);
            _lastSHA = commit;

            if (line[0] == '^')
            {
                _needUnifyCommitSHA = true;
                _minSHALen = Math.Min(_minSHALen, commit.Length);
            }
        }

        private static string DecodeMetadata(string raw) =>
            Ascii.IsValid(raw) ? raw : Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(raw));

        private readonly Models.BlameData _result = new Models.BlameData();
        private readonly StringBuilder _content = new StringBuilder();
        private string _lastSHA = string.Empty;
        private bool _needUnifyCommitSHA = false;
        private int _minSHALen = 64;
    }
}
