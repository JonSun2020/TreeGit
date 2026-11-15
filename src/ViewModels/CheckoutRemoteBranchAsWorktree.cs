using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class CheckoutRemoteBranchAsWorktree : Popup
    {
        public Models.Branch RemoteBranch
        {
            get => _remoteBranch;
        }

        [CustomValidation(typeof(CheckoutRemoteBranchAsWorktree), nameof(ValidateWorktreePath))]
        public string WorktreePath
        {
            get => _worktreePath;
            set => SetProperty(ref _worktreePath, value, true);
        }

        [CustomValidation(typeof(CheckoutRemoteBranchAsWorktree), nameof(ValidateBranchName))]
        public string BranchName
        {
            get => _branchName;
            set => SetProperty(ref _branchName, value, true);
        }

        public bool Tracking
        {
            get => _tracking;
            set => SetProperty(ref _tracking, value);
        }

        public CheckoutRemoteBranchAsWorktree(Repository repo, Models.Branch remoteBranch)
        {
            _repo = repo;
            _remoteBranch = remoteBranch;
            _worktreePath = Path.Combine(repo.GetRecommandedWorktreeDir(), remoteBranch.Name.Replace('/', '-').Replace('\\', '-'));
            _branchName = remoteBranch.Name;
            _tracking = true;
        }

        public static ValidationResult ValidateWorktreePath(string path, ValidationContext ctx)
        {
            if (ctx.ObjectInstance is not CheckoutRemoteBranchAsWorktree creator)
                return new ValidationResult(App.Text("TreeGit.Message.CheckoutContext"));

            if (string.IsNullOrEmpty(path))
                return new ValidationResult(App.Text("TreeGit.Message.WorktreePath"));

            var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(creator._repo.FullPath, path);
            var info = new DirectoryInfo(fullPath);
            if (info.Exists)
            {
                var files = info.GetFiles();
                if (files.Length > 0)
                    return new ValidationResult(App.Text("TreeGit.Message.PathNotEmpty"));

                var folders = info.GetDirectories();
                if (folders.Length > 0)
                    return new ValidationResult(App.Text("TreeGit.Message.PathNotEmpty"));
            }

            return ValidationResult.Success;
        }

        public static ValidationResult ValidateBranchName(string name, ValidationContext ctx)
        {
            if (ctx.ObjectInstance is not CheckoutRemoteBranchAsWorktree creator)
                return new ValidationResult(App.Text("TreeGit.Message.CheckoutContext"));

            var test = !string.IsNullOrEmpty(name) ? name : Path.GetFileName(creator._worktreePath.TrimEnd('/').TrimEnd('\\'));
            if (!Models.RefName.IsValidBranchName(test))
                return new ValidationResult(App.Text("TreeGit.Message.InvalidBranchNamed", test));

            foreach (var b in creator._repo.Branches)
            {
                if (b.IsLocal && b.Name.Equals(test, StringComparison.Ordinal))
                    return new ValidationResult(App.Text("TreeGit.Message.BranchExistsNamed", test));
            }

            return ValidationResult.Success;
        }

        public override async Task<bool> Sure()
        {
            using var lockWatcher = _repo.LockWatcher();
            SetProgressDescription("TreeGit.Progress.CheckoutRemoteBranchAsWorktree", _remoteBranch.FriendlyName);

            var log = _repo.CreateLog("Add Worktree");
            Use(log);

            var succ = await new Commands.Worktree(_repo.FullPath)
                .Use(log)
                .AddWithRemoteBranchAsync(_worktreePath, _branchName, _remoteBranch.FriendlyName, _tracking);

            log.Complete();
            return succ;
        }

        private readonly Repository _repo;
        private readonly Models.Branch _remoteBranch;
        private string _worktreePath;
        private string _branchName;
        private bool _tracking;
    }
}
