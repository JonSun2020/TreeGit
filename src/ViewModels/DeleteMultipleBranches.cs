using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class DeleteMultipleBranches : Popup
    {
        public List<Models.Branch> Targets
        {
            get;
        }

        public bool Force
        {
            get;
            set;
        } = false;

        public DeleteMultipleBranches(Repository repo, List<Models.Branch> branches, bool isLocal)
        {
            _repo = repo;
            _isLocal = isLocal;
            Targets = branches;
        }

        public override async Task<bool> VerifyCredentialsAsync()
        {
            if (_isLocal) return true;
            var checkedRemotes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var branch in Targets)
            {
                var remote = _repo.Remotes.Find(r => r.Name == branch.Remote);
                if (remote != null && checkedRemotes.Add(remote.Name) && !await VerifyRemoteAsync(_repo.FullPath, remote, true)) return false;
            }
            return true;
        }
        public override async Task<bool> Sure()
        {
            using var lockWatcher = _repo.LockWatcher();
            SetProgressDescription("TreeGit.Progress.DeleteMultipleBranches");

            var log = _repo.CreateLog("Delete Multiple Branches");
            Use(log);

            var success = true;
            for (var index = 0; index < Targets.Count; index++)
            {
                var target = Targets[index];
                BeginOperationPart(index, Targets.Count);
                success = _isLocal
                    ? await new Commands.Branch(_repo.FullPath, target.Name)
                        .Use(log)
                        .DeleteLocalAsync(Force)
                    : await DeleteRemoteBranchAsync(target, log);
                if (!success) break;
            }

            log.Complete();
            _repo.MarkBranchesDirtyManually();
            return success;
        }

        private async Task<bool> DeleteRemoteBranchAsync(Models.Branch branch, CommandLog log)
        {
            var exists = false;
            var remote = _repo.Remotes.Find(x => x.Name.Equals(branch.Remote, StringComparison.Ordinal));
            if (remote != null)
            {
                var query = new Commands.DoesBranchExistOnRemote(_repo.FullPath, remote, branch);
                exists = await query.Use(log)
                    .GetResultAsync()
                    .ConfigureAwait(false);
                if (!query.Succeeded) return false;
            }

            if (exists)
                return await new Commands.Push(_repo.FullPath, remote, $"refs/heads/{branch.Name}", true)
                    .Use(log)
                    .ExecAsync()
                    .ConfigureAwait(false);
            else
                return await new Commands.Branch(_repo.FullPath, branch.Name)
                    .Use(log)
                    .DeleteRemoteAsync(branch.Remote, Force)
                    .ConfigureAwait(false);
        }

        private Repository _repo = null;
        private bool _isLocal = false;
    }
}
