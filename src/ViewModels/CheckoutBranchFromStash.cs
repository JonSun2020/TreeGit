using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class CheckoutBranchFromStash : Popup
    {
        public Models.Stash Target
        {
            get;
        }

        [Required(ErrorMessageResourceType = typeof(ValidationMessages), ErrorMessageResourceName = nameof(ValidationMessages.BranchNameRequired))]
        [CustomValidation(typeof(CheckoutBranchFromStash), nameof(ValidateBranchName))]
        public string BranchName
        {
            get => _branchName;
            set => SetProperty(ref _branchName, value, true);
        }

        public CheckoutBranchFromStash(Repository repo, Models.Stash stash)
        {
            _repo = repo;
            Target = stash;
        }

        public static ValidationResult ValidateBranchName(string name, ValidationContext ctx)
        {
            if (ctx.ObjectInstance is CheckoutBranchFromStash caller)
            {
                if (!Models.RefName.IsValidBranchName(name))
                    return new ValidationResult(App.Text("TreeGit.Message.BranchNameFormat"));

                foreach (var b in caller._repo.Branches)
                {
                    if (b.FriendlyName.Equals(name, StringComparison.Ordinal))
                        return new ValidationResult(App.Text("TreeGit.Message.BranchExists"));
                }

                return ValidationResult.Success;
            }

            return new ValidationResult(App.Text("TreeGit.Message.BranchContext"));
        }

        public override async Task<bool> Sure()
        {
            using var lockWatcher = _repo.LockWatcher();
            SetProgressDescription("TreeGit.Progress.CheckoutBranchFromStash");

            var log = _repo.CreateLog($"Checkout Branch '{_branchName}'");
            Use(log);

            var succ = await new Commands.Stash(_repo.FullPath)
                .Use(log)
                .CheckoutBranchAsync(Target.Name, _branchName);

            if (succ)
            {
                _repo.MarkWorkingCopyDirtyManually();
                _repo.MarkStashesDirtyManually();
            }

            log.Complete();
            return true;
        }

        private readonly Repository _repo;
        private string _branchName = string.Empty;
    }
}
