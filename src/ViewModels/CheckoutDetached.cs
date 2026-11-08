using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class CheckoutDetached : Popup
    {
        public object Target
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

        public CheckoutDetached(Repository repo, Models.Commit commit)
        {
            _repo = repo;
            _revision = commit.SHA;

            Target = commit;
            DealWithLocalChanges = Preferences.Instance.UseStashAndReapplyByDefault ?
                Models.DealWithLocalChanges.StashAndReapply :
                Models.DealWithLocalChanges.DoNothing;
        }

        public CheckoutDetached(Repository repo, Models.Tag tag)
        {
            _repo = repo;
            _revision = tag.SHA;

            Target = tag;
            DealWithLocalChanges = Preferences.Instance.UseStashAndReapplyByDefault ?
                Models.DealWithLocalChanges.StashAndReapply :
                Models.DealWithLocalChanges.DoNothing;
        }

        public override async Task<bool> Sure()
        {
            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = $"Checkout Commit '{_revision}' ...";

            var log = _repo.CreateLog("Checkout Commit");
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
                        .CommitAsync(_revision, false);
                }
                else if (DealWithLocalChanges == Models.DealWithLocalChanges.StashAndReapply)
                {
                    if (!await protectedChanges.SaveAsync("CHECKOUT_AUTO_STASH")) return false;
                    succ = await new Commands.Checkout(_repo.FullPath).Use(log).CommitAsync(_revision, false);
                }
                else
                {
                    succ = await new Commands.Checkout(_repo.FullPath)
                        .Use(log)
                        .CommitAsync(_revision, true);
                }

                if (succ)
                {
                    try { await _repo.AutoUpdateSubmodulesAsync(log); }
                    catch (System.Exception ex) { Native.OS.LogException(ex); _repo.SendNotification(App.Text("Repository.SubmodulesPending"), true); }

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
        private readonly string _revision = string.Empty;
    }
}
