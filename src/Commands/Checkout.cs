using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class Checkout : Command
    {
        public Checkout(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
        }

        public async Task<bool> BranchAsync(string branch, bool force)
        {
            var builder = new StringBuilder();
            builder.Append("checkout --progress ");
            if (force)
                builder.Append("--force ");
            else
                builder.Append("--no-overwrite-ignore ");
            builder.Append(branch);

            Args = builder.ToString();
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> FastForwardAsync(string remote)
        {
            Args = $"merge --ff-only --no-overwrite-ignore {remote.Quoted()}";
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> BranchAsync(string branch, string basedOn, bool force, bool allowOverwrite)
        {
            var builder = new StringBuilder();
            builder.Append("checkout --progress ");
            if (force)
                builder.Append("--force ");
            else
                builder.Append("--no-overwrite-ignore ");
            builder.Append(allowOverwrite ? "-B " : "-b ");
            builder.Append(branch);
            builder.Append(" ");
            builder.Append(basedOn);

            Args = builder.ToString();
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> CommitAsync(string commitId, bool force)
        {
            var option = force ? "--force" : "--no-overwrite-ignore";
            Args = $"checkout {option} --detach --progress {commitId}";
            return await ExecAsync().ConfigureAwait(false);
        }

        public Task<bool> UseTheirsAsync(List<string> files) => UseConflictStageAsync(files, true);

        public Task<bool> UseMineAsync(List<string> files) => UseConflictStageAsync(files, false);

        private async Task<bool> UseConflictStageAsync(List<string> files, bool theirs)
        {
            if (files.Count == 0) return true;
            var temporary = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(temporary, string.Join('\0', files) + '\0').ConfigureAwait(false);
                Args = $"--literal-pathspecs checkout {(theirs ? "--theirs" : "--ours")} --pathspec-file-nul --pathspec-from-file={temporary.Quoted()}";
                return await ExecAsync().ConfigureAwait(false);
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Native.OS.LogException(ex); }
            }
        }

        public async Task<bool> FileWithRevisionAsync(string file, string revision)
        {
            Args = $"--literal-pathspecs checkout --no-overlay {revision} -- {file.Quoted()}";
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> MultipleFilesWithRevisionAsync(List<string> files, string revision)
        {
            var builder = new StringBuilder();
            builder
                .Append("--literal-pathspecs checkout --no-overlay ")
                .Append(revision)
                .Append(" --");

            foreach (var f in files)
                builder.Append(' ').Append(f.Quoted());

            Args = builder.ToString();
            return await ExecAsync().ConfigureAwait(false);
        }
    }
}
