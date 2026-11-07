using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace SourceGit.ViewModels
{
    public class CheckoutRemoteBranch : Popup
    {
        public Models.Branch RemoteBranch { get; }

        [CustomValidation(typeof(CheckoutRemoteBranch), nameof(ValidateName))]
        public string Name
        {
            get => _name;
            set
            {
                if (SetProperty(ref _name, value, true))
                {
                    UpdateRemote = CanUpdateRemote;
                    OnPropertyChanged(nameof(CanUpdateRemote));
                    OnPropertyChanged(nameof(Hint));
                    OnPropertyChanged(nameof(HasLocalChanges));
                }
            }
        }

        private Models.Branch Existing => _repo.Branches.Find(b => b.IsLocal && b.Name == _name);
        public bool CanUpdateRemote => Existing is { Ahead.Count: 0, Behind.Count: > 0, HasWorktree: false };
        public bool UpdateRemote
        {
            get => _updateRemote;
            set { if (SetProperty(ref _updateRemote, value)) OnPropertyChanged(nameof(HasLocalChanges)); }
        }
        public bool HasLocalChanges => _repo.LocalChangesCount > 0 && Existing?.HasWorktree != true && (Existing?.IsCurrent != true || UpdateRemote);
        public Models.DealWithLocalChanges DealWithLocalChanges { get; set; }
        public string Hint => App.Text(Existing is { HasWorktree: true } ? "Checkout.OtherWorktree" :
            Existing is { Upstream: null or "" } ? "CheckoutRemote.Associate" :
            Existing is { Ahead.Count: > 0, Behind.Count: > 0 } ? "CheckoutRemote.Diverged" :
            Existing is { Ahead.Count: > 0 } ? "CheckoutRemote.Ahead" :
            Existing is { IsCurrent: true } && !CanUpdateRemote ? "CheckoutRemote.Current" :
            Existing != null ? "CheckoutRemote.Existing" : "CheckoutRemote.First");

        public CheckoutRemoteBranch(Repository repo, Models.Branch remote)
        {
            _repo = repo;
            RemoteBranch = remote;
            var tracked = repo.Branches.FindAll(b => b.IsLocal && b.Upstream == remote.FullName);
            tracked.Sort((a, b) =>
            {
                var current = b.IsCurrent.CompareTo(a.IsCurrent);
                if (current != 0) return current;
                var sameName = (b.Name == remote.Name).CompareTo(a.Name == remote.Name);
                return sameName != 0 ? sameName : StringComparer.Ordinal.Compare(a.Name, b.Name);
            });
            DealWithLocalChanges = Preferences.Instance.UseStashAndReapplyByDefault ? Models.DealWithLocalChanges.StashAndReapply : Models.DealWithLocalChanges.DoNothing;
            Name = tracked.Count > 0 ? tracked[0].Name : remote.Name;
        }

        public static ValidationResult ValidateName(string name, ValidationContext context)
        {
            if (context.ObjectInstance is not CheckoutRemoteBranch checkout) return new ValidationResult(App.Text("CheckoutRemote.InvalidName"));
            if (!Models.RefName.IsValidBranchName(name)) return new ValidationResult(App.Text("CheckoutRemote.InvalidName"));
            foreach (var branch in checkout._repo.Branches)
            {
                if (!branch.IsLocal) continue;
                if (branch.Name == name && !string.IsNullOrEmpty(branch.Upstream) && branch.Upstream != checkout.RemoteBranch.FullName)
                    return new ValidationResult(App.Text("CheckoutRemote.NameInUse"));
                if (branch.Name.StartsWith(name + "/", StringComparison.Ordinal) || name.StartsWith(branch.Name + "/", StringComparison.Ordinal))
                    return new ValidationResult(App.Text("CheckoutRemote.NameInUse"));
            }
            return ValidationResult.Success;
        }

        public override async Task<bool> Sure()
        {
            var name = Name;
            var updateRemote = UpdateRemote;
            using var watcher = _repo.LockWatcher();
            var log = _repo.CreateLog($"Checkout remote '{RemoteBranch.FriendlyName}'");
            Use(log);
            string stash = null;
            var restored = false;
            var checkoutSucceeded = false;
            try
            {
                var before = await new Commands.QueryBranches(_repo.FullPath).GetResultAsync();
                var remote = before.Find(b => !b.IsLocal && b.FullName == RemoteBranch.FullName);
                var local = before.Find(b => b.IsLocal && b.Name == name);
                if (remote == null) { _repo.SendNotification(App.Text("CheckoutRemote.RemoteGone"), true); return false; }
                if (local != null && !string.IsNullOrEmpty(local.Upstream) && local.Upstream != remote.FullName) { _repo.SendNotification(App.Text("CheckoutRemote.NameInUse"), true); return false; }
                if (local?.HasWorktree == true)
                {
                    var worktree = _repo.Worktrees.Find(w => w.IsAttachedTo(local));
                    if (worktree == null) { _repo.SendNotification(App.Text("Checkout.OtherWorktree"), true); return false; }
                    if (string.IsNullOrEmpty(local.Upstream) && !await new Commands.Branch(_repo.FullPath, name).Use(log).SetUpstreamAsync(remote)) return false;
                    await Dispatcher.UIThread.InvokeAsync(() => _repo.OpenWorktree(worktree));
                    return true;
                }
                if (local?.IsCurrent == true && !updateRemote)
                    return !string.IsNullOrEmpty(local.Upstream) || await new Commands.Branch(_repo.FullPath, name).Use(log).SetUpstreamAsync(remote);
                if (_repo.CurrentBranch is { IsDetachedHead: true } detached)
                {
                    var refs = await new Commands.QueryRefsContainsCommit(_repo.FullPath, detached.Head).GetResultAsync();
                    if (refs.Count == 0 && !await App.AskConfirmAsync(App.Text("Checkout.WarnLostCommits"))) return false;
                }
                var original = before.Find(b => b.IsCurrent);
                if (DealWithLocalChanges == Models.DealWithLocalChanges.StashAndReapply && await new Commands.CountLocalChanges(_repo.FullPath, false).GetResultAsync() > 0)
                {
                    if (!await new Commands.Stash(_repo.FullPath).Use(log).PushAsync("REMOTE_CHECKOUT_AUTO_STASH", false)) return false;
                    stash = await new Commands.QueryRevisionByRefName(_repo.FullPath, "refs/stash").GetResultAsync();
                    if (string.IsNullOrEmpty(stash)) { _repo.SendNotification(App.Text("CheckoutRemote.StashKept"), true); return false; }
                }
                var force = DealWithLocalChanges == Models.DealWithLocalChanges.Discard;
                var success = local != null
                    ? await new Commands.Checkout(_repo.FullPath).Use(log).BranchAsync(name.Quoted(), force)
                    : await new Commands.Checkout(_repo.FullPath).Use(log).BranchAsync(name.Quoted(), remote.FullName.Quoted(), force, false);
                checkoutSucceeded = success;
                if (success && (local == null || string.IsNullOrEmpty(local.Upstream)))
                    success = await new Commands.Branch(_repo.FullPath, name).Use(log).SetUpstreamAsync(remote);
                if (success && local != null && updateRemote)
                    success = await new Commands.Checkout(_repo.FullPath).Use(log).FastForwardAsync(remote.FullName);
                if (success) await _repo.AutoUpdateSubmodulesAsync(log);
                if (stash != null)
                {
                    var after = await new Commands.QueryBranches(_repo.FullPath).GetResultAsync();
                    var current = after.Find(b => b.IsCurrent);
                    if (checkoutSucceeded || (current?.FullName == original?.FullName && current?.Head == original?.Head))
                    {
                        restored = await new Commands.Stash(_repo.FullPath).Use(log).ApplyAsync(stash, true);
                        if (restored && await new Commands.QueryRevisionByRefName(_repo.FullPath, "refs/stash").GetResultAsync() == stash)
                            await new Commands.Stash(_repo.FullPath).Use(log).DropAsync("stash@{0}");
                        success &= restored;
                    }
                    else success = false;
                }
                return success;
            }
            finally
            {
                if (stash != null && !restored) _repo.SendNotification(App.Text("CheckoutRemote.StashKept"), true);
                var after = await new Commands.QueryBranches(_repo.FullPath).GetResultAsync();
                var current = after.Find(b => b.IsCurrent && b.IsLocal && b.Name == name);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (current != null) _repo.RefreshAfterCreateBranch(current, true);
                    else { _repo.RefreshBranches(); _repo.MarkWorkingCopyDirtyManually(); }
                });
                log.Complete();
            }
        }

        private readonly Repository _repo;
        private string _name = string.Empty;
        private bool _updateRemote;
    }
}
