using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace SourceGit.Commands
{
    public sealed class QueryConflictBase : Command
    {
        public QueryConflictBase(string repo, string indexEntries, int stage = 1)
        {
            if (stage is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(stage));
            WorkingDirectory = repo;
            foreach (var line in indexEntries.Split('\n'))
            {
                var tab = line.IndexOf('\t');
                var fields = (tab >= 0 ? line[..tab] : line).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length != 3 || fields[2] != stage.ToString(System.Globalization.CultureInfo.InvariantCulture)) continue;
                var oid = fields[1];
                if (oid.Length is not (40 or 64) || !oid.All(Uri.IsHexDigit)) throw new IOException("Invalid ancestor entry");
                _oid = oid;
                break;
            }
        }

        public byte[] GetResult()
        {
            if (_oid == null) return null; // add/add has no ancestor file
            Args = $"cat-file -s {_oid}";
            var size = ReadToEnd();
            if (!size.IsSuccess || !long.TryParse(size.StdOut.Trim(), out var length)) throw new IOException("Cannot read ancestor size");
            if (length > Models.MergeConflictTextFile.MaxBytes) throw new IOException("Ancestor is too large");
            Args = $"cat-file blob {_oid}";
            using var process = new Process { StartInfo = CreateGitStartInfo(true) };
            Native.ProcessLauncher.Start(process);
            var errors = process.StandardError.ReadToEndAsync();
            using var output = new MemoryStream();
            var buffer = new byte[16384];
            var oversized = false;
            int count;
            while ((count = process.StandardOutput.BaseStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + count > Models.MergeConflictTextFile.MaxBytes) oversized = true;
                if (!oversized) output.Write(buffer, 0, count);
            }
            process.WaitForExit();
            errors.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || oversized) throw new IOException("Cannot read ancestor content");
            return output.ToArray();
        }

        private readonly string _oid;
    }
}
