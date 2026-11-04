using System.IO;
using System.Threading.Tasks;
using System;

namespace SourceGit.ViewModels
{
    public abstract class InProgressContext
    {
        public string Name
        {
            get;
            protected set;
        }

        public virtual async Task<bool> ContinueAsync(CommandLog log, string message = null)
        {
            return _continueCmd != null && await _continueCmd.Use(log).ExecAsync();
        }

        public async Task<bool> SkipAsync(CommandLog log)
        {
            return _skipCmd != null && await _skipCmd.Use(log).ExecAsync();
        }

        public async Task<bool> AbortAsync(CommandLog log)
        {
            var success = _abortCmd != null && await _abortCmd.Use(log).ExecAsync();
            if (success) OnAborted();
            return success;
        }

        protected virtual void OnAborted()
        {
        }

        internal static string ReadStateFile(string file)
        {
            // An external Git process can replace or remove its state while the
            // watcher is refreshing the view. A transient state is not a UI error.
            try { return File.ReadAllText(file).Trim(); }
            catch (IOException) { return string.Empty; }
            catch (System.UnauthorizedAccessException) { return string.Empty; }
        }

        protected async Task<bool> ContinueWithStoredMessageAsync(CommandLog log, string message, string file, string stepFile)
        {
            if (string.IsNullOrWhiteSpace(message) || !File.Exists(file)) return await ContinueAsync(log);
            var original = await File.ReadAllBytesAsync(file);
            var step = ReadStateFile(stepFile);
            var edited = System.Text.Encoding.UTF8.GetBytes(message);
            var success = false;
            try
            {
                await File.WriteAllBytesAsync(file, edited);
                success = await _continueCmd.Use(log).ExecAsync();
                return success;
            }
            finally
            {
                // A failed preflight must preserve Git's recovery metadata. A
                // later conflict has its own message and must never be restored.
                if (!success && ReadStateFile(stepFile) == step && File.Exists(file) &&
                    (await File.ReadAllBytesAsync(file)).AsSpan().SequenceEqual(edited))
                    await File.WriteAllBytesAsync(file, original);
            }
        }

        protected Commands.Command _continueCmd = null;
        protected Commands.Command _skipCmd = null;
        protected Commands.Command _abortCmd = null;
    }

    public class CherryPickInProgress : InProgressContext
    {
        public Models.Commit Head
        {
            get;
        }

        public string HeadName
        {
            get;
        }

        public CherryPickInProgress(Repository repo)
        {
            Name = "Cherry-Pick";

            _continueCmd = new Commands.Command
            {
                WorkingDirectory = repo.FullPath,
                Context = repo.FullPath,
                Editor = Commands.Command.EditorType.None,
                Args = "-c core.commentChar=\"^\" -c core.commentString=\"±\" cherry-pick --continue",
            };

            _skipCmd = new Commands.Command
            {
                WorkingDirectory = repo.FullPath,
                Context = repo.FullPath,
                Args = "cherry-pick --skip",
            };

            _abortCmd = new Commands.Command
            {
                WorkingDirectory = repo.FullPath,
                Context = repo.FullPath,
                Args = "cherry-pick --abort",
            };

            var headSHA = File.ReadAllText(Path.Combine(repo.GitDir, "CHERRY_PICK_HEAD")).Trim();
            Head = new Commands.QuerySingleCommit(repo.FullPath, headSHA).GetResult() ?? new Models.Commit() { SHA = headSHA };
            HeadName = Head.GetFriendlyName();
        }
    }

    public class RebaseInProgress : InProgressContext
    {
        public string HeadName
        {
            get;
        }

        public string BaseName
        {
            get;
        }

        public Models.Commit StoppedAt
        {
            get;
        }

        public Models.Commit Onto
        {
            get;
        }

        public string MessageFile { get; }

        public RebaseInProgress(Repository repo)
        {
            _gitDir = repo.GitDir;
            Name = "Rebase";

            _continueCmd = new Commands.Command
            {
                WorkingDirectory = repo.FullPath,
                Context = repo.FullPath,
                Editor = Commands.Command.EditorType.RebaseEditor,
                Args = "-c core.commentChar=\"^\" -c core.commentString=\"±\" rebase --continue",
            };

            _skipCmd = new Commands.Command
            {
                WorkingDirectory = repo.FullPath,
                Context = repo.FullPath,
                Args = "rebase --skip",
            };

            _abortCmd = new Commands.Command
            {
                WorkingDirectory = repo.FullPath,
                Context = repo.FullPath,
                Args = "rebase --abort",
            };

            _stateDirectory = Path.Combine(repo.GitDir, Directory.Exists(Path.Combine(repo.GitDir, "rebase-merge")) ? "rebase-merge" : "rebase-apply");
            _applyBackend = Path.GetFileName(_stateDirectory) == "rebase-apply";
            MessageFile = Path.Combine(_stateDirectory, _applyBackend ? "final-commit" : "message");
            HeadName = ReadStateFile(Path.Combine(_stateDirectory, "head-name"));
            if (HeadName.StartsWith("refs/heads/"))
                HeadName = HeadName.Substring(11);
            else if (HeadName.StartsWith("refs/tags/"))
                HeadName = HeadName.Substring(10);

            var stoppedSHA = ReadStateFile(Path.Combine(_stateDirectory, _applyBackend ? "original-commit" : "stopped-sha"));
            if (string.IsNullOrEmpty(stoppedSHA) && !string.IsNullOrEmpty(HeadName))
                stoppedSHA = new Commands.QueryRevisionByRefName(repo.FullPath, HeadName).GetResult();

            if (!string.IsNullOrEmpty(stoppedSHA))
                StoppedAt = new Commands.QuerySingleCommit(repo.FullPath, stoppedSHA).GetResult() ?? new Models.Commit() { SHA = stoppedSHA };

            var ontoSHA = ReadStateFile(Path.Combine(_stateDirectory, "onto"));
            Onto = new Commands.QuerySingleCommit(repo.FullPath, ontoSHA).GetResult() ?? new Models.Commit() { SHA = ontoSHA };
            BaseName = Onto.GetFriendlyName();
        }

        public override async Task<bool> ContinueAsync(CommandLog log, string message = null)
        {
            if (string.IsNullOrWhiteSpace(message)) return await base.ContinueAsync(log);
            if (_applyBackend) return await ContinueWithStoredMessageAsync(log, message, MessageFile, Path.Combine(_stateDirectory, "next"));
            var file = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(file, message);
                _continueCmd.EditorMessageFile = file;
                _continueCmd.EditorMessageGitDirectory = _gitDir;
                return await base.ContinueAsync(log);
            }
            finally
            {
                _continueCmd.EditorMessageFile = null;
                _continueCmd.EditorMessageGitDirectory = null;
                File.Delete(file);
            }
        }

        protected override void OnAborted()
        {
            var jobFile = Path.Combine(_gitDir, "sourcegit.interactive_rebase");
            if (File.Exists(jobFile))
                File.Delete(jobFile);
        }

        private readonly string _gitDir;
        private readonly string _stateDirectory;
        private readonly bool _applyBackend;
    }

    public class PatchInProgress : InProgressContext
    {
        public string Subject { get; }
        public string Step { get; }
        public string MessageFile { get; }

        public PatchInProgress(Repository repo)
        {
            Name = "AM";
            _stateDirectory = Path.Combine(repo.GitDir, "rebase-apply");
            MessageFile = Path.Combine(_stateDirectory, "final-commit");
            Subject = ReadStateFile(MessageFile).Split('\n')[0];
            Step = ReadStateFile(Path.Combine(_stateDirectory, "next"));
            _continueCmd = new Commands.Command { WorkingDirectory = repo.FullPath, Context = repo.FullPath, Args = "am --continue" };
            _skipCmd = new Commands.Command { WorkingDirectory = repo.FullPath, Context = repo.FullPath, Args = "am --skip" };
            _abortCmd = new Commands.Command { WorkingDirectory = repo.FullPath, Context = repo.FullPath, Args = "am --abort" };
        }

        public override Task<bool> ContinueAsync(CommandLog log, string message = null) =>
            string.IsNullOrWhiteSpace(message) ? base.ContinueAsync(log) : ContinueWithStoredMessageAsync(log, message, MessageFile, Path.Combine(_stateDirectory, "next"));

        private readonly string _stateDirectory;
    }

    public class RevertInProgress : InProgressContext
    {
        public Models.Commit Head
        {
            get;
        }

        public RevertInProgress(Repository repo)
        {
            Name = "Revert";

            _continueCmd = new Commands.Command
            {
                WorkingDirectory = repo.FullPath,
                Context = repo.FullPath,
                Editor = Commands.Command.EditorType.None,
                Args = "-c core.commentChar=\"^\" -c core.commentString=\"±\" revert --continue",
            };

            _skipCmd = new Commands.Command
            {
                WorkingDirectory = repo.FullPath,
                Context = repo.FullPath,
                Args = "revert --skip",
            };

            _abortCmd = new Commands.Command
            {
                WorkingDirectory = repo.FullPath,
                Context = repo.FullPath,
                Args = "revert --abort",
            };

            var headSHA = File.ReadAllText(Path.Combine(repo.GitDir, "REVERT_HEAD")).Trim();
            Head = new Commands.QuerySingleCommit(repo.FullPath, headSHA).GetResult() ?? new Models.Commit() { SHA = headSHA };
        }
    }

    public class MergeInProgress : InProgressContext
    {
        public string Current
        {
            get;
        }

        public Models.Commit Source
        {
            get;
        }

        public string SourceName
        {
            get;
        }

        public MergeInProgress(Repository repo)
        {
            Name = "Merge";

            _continueCmd = new Commands.Command
            {
                WorkingDirectory = repo.FullPath,
                Context = repo.FullPath,
                Editor = Commands.Command.EditorType.None,
                Args = "-c core.commentChar=\"^\" -c core.commentString=\"±\" merge --continue",
            };

            _abortCmd = new Commands.Command
            {
                WorkingDirectory = repo.FullPath,
                Context = repo.FullPath,
                Args = "merge --abort",
            };

            Current = new Commands.QueryCurrentBranch(repo.FullPath).GetResult();

            var sourceSHA = File.ReadAllText(Path.Combine(repo.GitDir, "MERGE_HEAD")).Trim();
            Source = new Commands.QuerySingleCommit(repo.FullPath, sourceSHA).GetResult() ?? new Models.Commit() { SHA = sourceSHA };
            SourceName = Source.GetFriendlyName();
        }
    }
}
