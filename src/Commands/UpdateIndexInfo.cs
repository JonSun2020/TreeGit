using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class UpdateIndexInfo
    {
        public UpdateIndexInfo(string repo, List<Models.Change> changes)
        {
            _repo = repo;
            _changes = new List<Models.Change>(changes);
        }

        private string BuildPatch(string emptyHash)
        {
            var builder = new StringBuilder();
            void Remove(string path) => builder.Append("0 ").Append(emptyHash).Append('\t').Append(path).Append('\0');
            void Restore(Models.Change change, string path)
            {
                var data = change.DataForAmend ?? throw new InvalidDataException("Missing original file information.");
                if (data.FileMode is not ("100644" or "100755" or "120000" or "160000") || data.ObjectHash?.Length != emptyHash.Length)
                    throw new InvalidDataException("Invalid original file information.");
                builder.Append(data.FileMode).Append(' ').Append(data.ObjectHash).Append('\t').Append(path).Append('\0');
            }
            foreach (var c in _changes)
            {
                if (c.Path.Contains('\0') || c.OriginalPath?.Contains('\0') == true) throw new InvalidDataException("Invalid file path.");
                if (c.Index == Models.ChangeState.Renamed)
                {
                    Remove(c.Path);
                    Restore(c, c.OriginalPath);
                }
                else if (c.Index is Models.ChangeState.Added or Models.ChangeState.Copied)
                {
                    Remove(c.Path);
                }
                else
                {
                    Restore(c, c.Path);
                }
            }
            return builder.ToString();
        }

        public async Task<bool> ExecAsync()
        {
            var starter = new ProcessStartInfo();
            starter.WorkingDirectory = _repo;
            starter.FileName = Native.OS.GitExecutable;
            starter.Arguments = "-c core.editor=true update-index -z --index-info";
            starter.UseShellExecute = false;
            starter.CreateNoWindow = true;
            starter.WindowStyle = ProcessWindowStyle.Hidden;
            starter.RedirectStandardInput = true;
            starter.RedirectStandardOutput = false;
            starter.RedirectStandardError = true;
            starter.StandardInputEncoding = new UTF8Encoding(false);
            starter.StandardErrorEncoding = Encoding.UTF8;

            try
            {
                var hash = await new HeadHashQuery(_repo).ReadAsync().ConfigureAwait(false);
                var patch = BuildPatch(new string('0', hash.Length));
                using var proc = Process.Start(starter)!;
                var errors = proc.StandardError.ReadToEndAsync();
                await proc.StandardInput.WriteAsync(patch);
                proc.StandardInput.Close();

                var err = await errors.ConfigureAwait(false);
                await proc.WaitForExitAsync().ConfigureAwait(false);
                var rs = proc.ExitCode == 0;

                if (!rs)
                    Models.Notification.Send(_repo, err, true);

                return rs;
            }
            catch (Exception e)
            {
                Models.Notification.Send(_repo, "Failed to update index: " + e.Message, true);
                return false;
            }
        }

        private readonly string _repo;
        private readonly List<Models.Change> _changes;

        private sealed class HeadHashQuery : Command
        {
            public HeadHashQuery(string repo)
            {
                WorkingDirectory = repo;
                Context = repo;
                Args = "rev-parse --verify HEAD";
                UseCredentialManager = false;
                Editor = EditorType.None;
            }

            public async Task<string> ReadAsync()
            {
                var result = await ReadToEndAsync().ConfigureAwait(false);
                var hash = result.StdOut.Trim();
                if (!result.IsSuccess || hash.Length is not (40 or 64)) throw new IOException("The current commit could not be read.");
                return hash;
            }
        }
    }
}
