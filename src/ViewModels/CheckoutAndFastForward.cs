using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class CheckoutAndFastForward : Popup
    {
        public Models.Branch LocalBranch
        {
            get;
        }

        public Models.Branch RemoteBranch
        {
            get;
        }

        public bool HasLocalChanges
        {
            get => _repo.LocalChangesCount > 0;
        }

        public Models.DealWithLocalChanges DealWithLocalChanges
        {
            get;
            set;
        }

        public CheckoutAndFastForward(Repository repo, Models.Branch localBranch, Models.Branch remoteBranch)
        {
            _repo = repo;
            LocalBranch = localBranch;
            RemoteBranch = remoteBranch;
            _sourceRevision = remoteBranch.Head;
            _expectedLocalHead = localBranch.Head;

            DealWithLocalChanges = Preferences.Instance.UseStashAndReapplyByDefault ?
                Models.DealWithLocalChanges.StashAndReapply :
                Models.DealWithLocalChanges.DoNothing;
        }

        public override async Task<bool> Sure()
        {
            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = $"Checkout and Fast-Forward '{LocalBranch.Name}' ...";

            var log = _repo.CreateLog($"Checkout and Fast-Forward '{LocalBranch.Name}' ...");
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

            var currentTarget = await new Commands.QueryRevisionByRefName(_repo.FullPath, LocalBranch.FullName.Quoted()).GetResultAsync();
            if (currentTarget != _expectedLocalHead)
            {
                log.Complete();
                _repo.RefreshBranches();
                _repo.SendNotification(App.Text("Checkout.DestinationChanged"), true);
                return false;
            }
            var safety = new Commands.CheckMergeSafety(_repo.FullPath, [_sourceRevision]);
            if (!await safety.GetResultAsync())
            {
                log.Complete();
                _repo.SendNotification(App.Text(safety.Obstruction == null ? "WorkingCopy.RefreshFailed" : "Merge.LocalFilesBlocked"), true);
                return false;
            }
            var protectedChanges = new ProtectedLocalChanges(_repo, log);
            var checkoutSucceeded = false;
            try
            {
                if (DealWithLocalChanges == Models.DealWithLocalChanges.StashAndReapply &&
                    !await protectedChanges.SaveAsync("CHECKOUT_AND_FASTFORWARD_AUTO_STASH")) return false;
                checkoutSucceeded = await new Commands.Checkout(_repo.FullPath).Use(log)
                    .BranchAsync(LocalBranch.Name.Quoted(), DealWithLocalChanges == Models.DealWithLocalChanges.Discard);
                if (!checkoutSucceeded) return false;
                // A fast-forward must never be implemented as checkout -B, which
                // can erase newer local commits when the dialog's data is stale.
                var success = await new Commands.Checkout(_repo.FullPath).Use(log).FastForwardAsync(_sourceRevision);
                if (success)
                {
                    try { await _repo.AutoUpdateSubmodulesAsync(log); }
                    catch (System.Exception ex) { Native.OS.LogException(ex); _repo.SendNotification(App.Text("Repository.SubmodulesPending"), true); }
                    LocalBranch.Behind.Clear();
                    LocalBranch.Head = _sourceRevision;
                    LocalBranch.CommitterDate = RemoteBranch.CommitterDate;
                }
                _repo.RefreshAfterCheckoutBranch(LocalBranch);
                return success;
            }
            finally
            {
                try { await protectedChanges.RestoreAsync(checkoutSucceeded); }
                finally { log.Complete(); _repo.RefreshBranches(); _repo.MarkWorkingCopyDirtyManually(); }
            }
        }

        private Repository _repo;
        private readonly string _sourceRevision, _expectedLocalHead;
    }
}
