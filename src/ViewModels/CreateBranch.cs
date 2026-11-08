using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class CreateBranch : Popup
    {
        [Required(ErrorMessageResourceType = typeof(ValidationMessages), ErrorMessageResourceName = nameof(ValidationMessages.BranchNameRequired))]
        [CustomValidation(typeof(CreateBranch), nameof(ValidateBranchName))]
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value, true);
        }

        public object BasedOn
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

        public bool CheckoutAfterCreated
        {
            get => _repo.UIStates.CheckoutBranchOnCreateBranch;
            set
            {
                if (_repo.UIStates.CheckoutBranchOnCreateBranch != value)
                {
                    _repo.UIStates.CheckoutBranchOnCreateBranch = value;
                    OnPropertyChanged();
                    UpdateOverrideTip();
                }
            }
        }

        public bool IsBareRepository
        {
            get => _repo.IsBare;
        }

        public string OverrideTip
        {
            get => _overrideTip;
            private set => SetProperty(ref _overrideTip, value);
        }

        public bool AllowOverwrite
        {
            get => _allowOverwrite;
            set
            {
                if (SetProperty(ref _allowOverwrite, value))
                    ValidateProperty(_name, nameof(Name));
            }
        }

        public CreateBranch(Repository repo, Models.Branch branch)
        {
            _repo = repo;
            _baseOnRevision = branch.Head;
            _committerDate = branch.CommitterDate;
            _head = branch.Head;

            if (!branch.IsLocal)
                Name = branch.Name;

            BasedOn = branch;
            DealWithLocalChanges = Preferences.Instance.UseStashAndReapplyByDefault ?
                Models.DealWithLocalChanges.StashAndReapply :
                Models.DealWithLocalChanges.DoNothing;
            UpdateOverrideTip();
        }

        public CreateBranch(Repository repo, Models.Commit commit)
        {
            _repo = repo;
            _baseOnRevision = commit.SHA;
            _committerDate = commit.CommitterTime;
            _head = commit.SHA;

            BasedOn = commit;
            DealWithLocalChanges = Preferences.Instance.UseStashAndReapplyByDefault ?
                Models.DealWithLocalChanges.StashAndReapply :
                Models.DealWithLocalChanges.DoNothing;
            UpdateOverrideTip();
        }

        public CreateBranch(Repository repo, Models.Tag tag)
        {
            _repo = repo;
            _baseOnRevision = tag.SHA;
            _committerDate = tag.CreatorDate;
            _head = tag.SHA;

            BasedOn = tag;
            DealWithLocalChanges = Preferences.Instance.UseStashAndReapplyByDefault ?
                Models.DealWithLocalChanges.StashAndReapply :
                Models.DealWithLocalChanges.DoNothing;
            UpdateOverrideTip();
        }

        public static ValidationResult ValidateBranchName(string name, ValidationContext ctx)
        {
            if (ctx.ObjectInstance is CreateBranch creator)
            {
                if (!Models.RefName.IsValidBranchName(name))
                    return new ValidationResult(App.Text("Validation.InvalidBranchName"));

                if (!creator._allowOverwrite)
                {
                    foreach (var b in creator._repo.Branches)
                    {
                        if (b.FriendlyName.Equals(name, StringComparison.Ordinal))
                            return new ValidationResult(App.Text("Validation.BranchExists"));
                    }
                }

                return ValidationResult.Success;
            }

            return new ValidationResult(App.Text("Validation.BranchUnavailable"));
        }

        public override async Task<bool> Sure()
        {
            using var lockWatcher = _repo.LockWatcher();

            var log = _repo.CreateLog($"Create Branch '{_name}'");
            Use(log);

            if (CheckoutAfterCreated)
            {
                if (_repo.CurrentBranch is { IsDetachedHead: true } && !_repo.CurrentBranch.Head.Equals(_baseOnRevision, StringComparison.Ordinal))
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
            }

            var succ = false;
            var protectedChanges = new ProtectedLocalChanges(_repo, log);
            try
            {
                Models.Branch created = new()
                {
                    Name = _name,
                    FullName = $"refs/heads/{_name}",
                    CommitterDate = _committerDate,
                    Head = _head,
                    IsLocal = true,
                };

                if (CheckoutAfterCreated && !_repo.IsBare)
                {
                    if (DealWithLocalChanges == Models.DealWithLocalChanges.DoNothing)
                    {
                        succ = await new Commands.Checkout(_repo.FullPath)
                            .Use(log)
                            .BranchAsync(_name.Quoted(), _baseOnRevision, false, _allowOverwrite);
                    }
                    else if (DealWithLocalChanges == Models.DealWithLocalChanges.StashAndReapply)
                    {
                        if (!await protectedChanges.SaveAsync("CREATE_BRANCH_AUTO_STASH")) return false;
                        succ = await new Commands.Checkout(_repo.FullPath).Use(log).BranchAsync(_name.Quoted(), _baseOnRevision, false, _allowOverwrite);
                    }
                    else
                    {
                        succ = await new Commands.Checkout(_repo.FullPath)
                            .Use(log)
                            .BranchAsync(_name.Quoted(), _baseOnRevision, true, _allowOverwrite);
                    }

                    if (succ)
                    {
                        try { await _repo.AutoUpdateSubmodulesAsync(log); }
                        catch (Exception ex) { Native.OS.LogException(ex); _repo.SendNotification(App.Text("Repository.SubmodulesPending"), true); }

                    }
                }
                else
                {
                    succ = await new Commands.Branch(_repo.FullPath, _name)
                        .Use(log)
                        .CreateAsync(_baseOnRevision, _allowOverwrite);
                }

                if (succ)
                {
                    var actualHead = await new Commands.QueryRevisionByRefName(_repo.FullPath, ("refs/heads/" + _name).Quoted()).GetResultAsync();
                    if (!string.IsNullOrEmpty(actualHead)) created.Head = actualHead;
                    if (BasedOn is Models.Branch { IsLocal: false } basedOn && _name.Equals(basedOn.Name, StringComparison.Ordinal))
                    {
                        var tracking = await new Commands.Branch(_repo.FullPath, _name) { RaiseError = false }
                            .Use(log)
                            .SetUpstreamAsync(basedOn);

                        created.Upstream = tracking ? basedOn.FullName : string.Empty;
                        if (!tracking) _repo.SendNotification(App.Text("Repository.TrackingPending"), true);
                    }
                    else
                    {
                        created.Upstream = string.Empty;
                    }

                    _repo.RefreshAfterCreateBranch(created, CheckoutAfterCreated);
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

        private void UpdateOverrideTip()
        {
            OverrideTip = App.Text(CheckoutAfterCreated ? "Branch.OverrideCheckoutTip" : "Branch.OverrideCreateTip");
        }

        private readonly Repository _repo = null;
        private readonly string _baseOnRevision = null;
        private readonly ulong _committerDate = 0;
        private readonly string _head = string.Empty;
        private string _name = null;
        private string _overrideTip = "-B";
        private bool _allowOverwrite = false;
    }
}
