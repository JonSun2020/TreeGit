using System.Threading.Tasks;

using Avalonia.Threading;

namespace SourceGit.ViewModels
{
    public class Checkout : Popup
    {
        public string BranchName
        {
            get => _branch.Name;
        }

        public bool HasLocalChanges
        {
            get => !_branch.HasWorktree && _repo.LocalChangesCount > 0;
        }

        public bool HasOtherWorktree => _branch.HasWorktree;

        public Models.DealWithLocalChanges DealWithLocalChanges
        {
            get;
            set;
        }

        public Checkout(Repository repo, Models.Branch branch)
        {
            _repo = repo;
            _branch = branch;

            DealWithLocalChanges = Preferences.Instance.UseStashAndReapplyByDefault ?
                Models.DealWithLocalChanges.StashAndReapply :
                Models.DealWithLocalChanges.DoNothing;
        }

        public override async Task<bool> Sure()
        {
            var worktree = _repo.Worktrees.Find(w => w.IsAttachedTo(_branch));
            if (worktree != null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => _repo.OpenWorktree(worktree));
                return true;
            }
            using var lockWatcher = _repo.LockWatcher();
            var branchName = BranchName;
            ProgressDescription = $"Checkout '{branchName}' ...";

            var log = _repo.CreateLog($"Checkout '{branchName}'");
            Use(log);

            if (_repo.CurrentBranch is { IsDetachedHead: true })
            {
                var refs = await new Commands.QueryRefsContainsCommit(_repo.FullPath, _repo.CurrentBranch.Head).GetResultAsync();
                if (refs.Count == 0)
                {
                    var msg = App.Text("Checkout.WarnLostCommits");
                    var shouldContinue = await App.AskConfirmAsync(msg);
                    if (!shouldContinue)
                        return true;
                }
            }

            var succ = false;
            var protectedChanges = new ProtectedLocalChanges(_repo, log);
            try
            {

                if (DealWithLocalChanges == Models.DealWithLocalChanges.DoNothing)
                {
                    succ = await new Commands.Checkout(_repo.FullPath)
                        .Use(log)
                        .BranchAsync(branchName.Quoted(), false);
                }
                else if (DealWithLocalChanges == Models.DealWithLocalChanges.StashAndReapply)
                {
                    if (!await protectedChanges.SaveAsync("CHECKOUT_AUTO_STASH")) return false;
                    succ = await new Commands.Checkout(_repo.FullPath).Use(log).BranchAsync(branchName.Quoted(), false);
                }
                else
                {
                    succ = await new Commands.Checkout(_repo.FullPath)
                        .Use(log)
                        .BranchAsync(branchName.Quoted(), true);
                }

                if (succ)
                {
                    try { await _repo.AutoUpdateSubmodulesAsync(log); }
                    catch (System.Exception ex) { Native.OS.LogException(ex); _repo.SendNotification(App.Text("Repository.SubmodulesPending"), true); }


                    _repo.RefreshAfterCheckoutBranch(_branch);
                }
                else
                {
                    _repo.MarkWorkingCopyDirtyManually();
                }

                return succ;
            }
            finally
            {
                try { await protectedChanges.RestoreAsync(succ); }
                finally { log.Complete(); _repo.RefreshBranches(); _repo.MarkWorkingCopyDirtyManually(); }
            }
        }

        private readonly Repository _repo = null;
        private readonly Models.Branch _branch = null;
    }
}
