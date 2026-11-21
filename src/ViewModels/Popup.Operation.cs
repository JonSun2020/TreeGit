using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public partial class Popup
    {
        public sealed record OutputLine(string Text, bool IsError);
        public bool UsesOperationPanel => GetType().Name is "Pull" or "Push" or "PushTag" or "PushRevision" or "Fetch" or "FetchInto" or
            "Clone" or "PruneRemote" or "AddSubmodule" or "UpdateSubmodules" or "Checkout" or "CheckoutRemoteBranch" or
            "CreateBranch" or "DeleteBranch" or "DeleteMultipleBranches" or "CreateTag" or "DeleteTag" or "DeleteMultipleTags" or "AddRemote" or
            "LFSFetch" or "LFSPull" or "LFSPush";
        public bool OperationPanelVisible { get => _operationPanelVisible; private set => SetProperty(ref _operationPanelVisible, value); }
        public bool ShowFullOutput { get => _showFullOutput; set => SetProperty(ref _showFullOutput, value); }
        public bool OperationPhaseFailed => _operationStage is "AuthenticationFailed" or "ConnectionFailed" or "GitUnavailable" or "VerificationTimeout" or "Failed";
        public string OperationStage => _operationStage == "Running" ? App.Text("TreeGit.Operation.Running", DialogTitle) : App.Text("TreeGit.Operation." + _operationStage);
        public string StatusDescription => UsesOperationPanel ? OperationStage : ProgressDescription;
        public bool ShowStatus => InProgress || UsesOperationPanel && OperationPhaseFailed;
        public bool CanCancelOperation => InProgress && !_operationCancelRequested;
        public bool IsOperationActivityRunning => CanCancelOperation && !OperationPhaseFailed;
        public bool CanCloseOperation => OperationPanelVisible && !InProgress;
        public bool OperationSucceeded => _operationStage == "Succeeded";
        public int OperationProgress { get => _operationProgress; private set => SetProperty(ref _operationProgress, value); }
        public IReadOnlyList<OutputLine> OperationOutput => _operationOutput;
        public virtual Task<bool> VerifyCredentialsAsync() => Task.FromResult(true);

        internal void BeginOperation()
        {
            _operationCancellation?.Dispose(); _operationCancellation = new();
            _operationCancelRequested = false; _operationOutput.Clear(); _operationOutputLength = 0;
            _resumeAfterAuthentication = false;
            _partIndex = 0;
            _partCount = 1;
            OperationProgress = 0;
            OperationPanelVisible = false;
            OnPropertyChanged(nameof(OperationOutput));
            OnAuthenticationState("Connecting");
        }

        internal void ShowOperationPanel()
        {
            OperationPanelVisible = true;
            ReportPartProgress(0);
            OnAuthenticationState("Running");
        }

        internal void FinishOperation(bool success)
        {
            if (AuthenticationCancelled || !success && _operationCancelRequested)
            {
                OperationPanelVisible = false;
                OnAuthenticationState("Cancelled");
            }
            else
            {
                if (success) OperationProgress = 100;
                OnAuthenticationState(success ? "Succeeded" : "Failed");
            }
        }

        protected void BeginOperationPart(int index, int count)
        {
            _partIndex = index;
            _partCount = count;
            ReportPartProgress(0);
        }

        // Reserve the ends for connection/preflight and the final Git result. A
        // silent server leaves the bar at the last observed milestone.
        protected void ReportPartProgress(int percent)
        {
            var progress = 5 + (int)(94L * (100L * _partIndex + percent) / (100L * _partCount));
            if (progress > OperationProgress) OperationProgress = progress;
        }

        private void ReadTransferProgress(string line)
        {
            if (!InProgress || !OperationPanelVisible || _operationStage != "Running") return;
            var text = line.AsSpan().TrimStart();
            if (text.StartsWith("remote: ", StringComparison.Ordinal)) text = text[8..];
            var colon = text.IndexOf(':');
            if (colon <= 0) return;
            var value = text[(colon + 1)..].TrimStart();
            var sign = value.IndexOf('%');
            if (sign is < 1 or > 3 || !int.TryParse(value[..sign], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var percent) || percent > 100) return;

            // Each percentage from Git describes one phase, not the entire command.
            var (start, end) = text[..colon] switch
            {
                "Enumerating objects" or "Counting objects" => (0, 12),
                "Compressing objects" => (12, 25),
                "Receiving objects" or "Writing objects" or "Unpacking objects" or
                    "Downloading LFS objects" or "Uploading LFS objects" => (25, 82),
                "Resolving deltas" => (82, 94),
                "Updating files" or "Checking out files" or "Filtering content" => (94, 100),
                _ => (-1, -1),
            };
            if (start >= 0) ReportPartProgress(start + (end - start) * percent / 100);
        }

        internal void CancelOperation()
        {
            if (!CanCancelOperation) return;
            _operationCancelRequested = true;
            OnAuthenticationState("Cancelling");
            OnPropertyChanged(nameof(CanCancelOperation));
            _operationCancellation?.Cancel();
            Terminate();
        }

        public void OnAuthenticationState(string state)
        {
            var previousStage = _operationStage;
            _operationStage = state;
            if (OperationPanelVisible && state is "AuthenticationFailed" or "VerificationTimeout" or "ConnectionFailed")
            {
                _resumeAfterAuthentication = true; OperationPanelVisible = false;
            }
            else if (state == "Authenticated" && _resumeAfterAuthentication)
            {
                _resumeAfterAuthentication = false; OperationPanelVisible = true; _operationStage = "Running";
            }
            OnPropertyChanged(nameof(OperationStage)); OnPropertyChanged(nameof(OperationPhaseFailed));
            OnPropertyChanged(nameof(StatusDescription));
            OnPropertyChanged(nameof(ShowStatus));
            OnPropertyChanged(nameof(IsOperationActivityRunning));
            if (previousStage != _operationStage || _operationOutput.Count == 0) AppendOperationOutput(OperationStage, OperationPhaseFailed);
        }

        public void OnCommandFailed(string message) => AppendOperationOutput(message, true);
        internal void AppendOperationOutput(string text, bool error = false)
        {
            if (string.IsNullOrEmpty(text)) return;
            text = Native.RepositoryCredentials.RedactLog(text);
            error |= text.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("error:", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("[rejected]", StringComparison.OrdinalIgnoreCase) || text.StartsWith("remote: error:", StringComparison.OrdinalIgnoreCase);
            if (text.Length > 65536) text = text[^65536..];
            _operationOutput.Add(new(text, error)); _operationOutputLength += text.Length;
            while (_operationOutputLength > 512 * 1024 || _operationOutput.Count > 5000)
            {
                _operationOutputLength -= _operationOutput[0].Text.Length; _operationOutput.RemoveAt(0);
            }
            OnPropertyChanged(nameof(OperationOutput));
        }

        protected async Task<bool> VerifyRemoteAsync(string repo, Models.Remote remote, bool push = false, string scope = null, bool? useManager = null)
        {
            if (remote == null) return false;
            var owner = App.GetLauncher()?.Pages.Select(p => p.Data).OfType<Repository>().FirstOrDefault(r => string.Equals(r.FullPath, repo, StringComparison.OrdinalIgnoreCase));
            var log = owner?.CreateLog("Connection") ?? new CommandLog("Connection"); Use(log);
            try
            {
                Commands.Command command = push ? new Commands.VerifyPushCredentials(repo, remote) : new Commands.VerifyRemoteCredentials(repo, remote, scope);
                command.Log = log;
                command.UseCredentialManager = useManager;
                var success = command is Commands.VerifyPushCredentials write ? await write.VerifyAsync() : await command.ExecAsync();
                if (!success && !command.WasAuthenticationCancelled)
                {
                    var stage = command.LastExitCode.HasValue ? "ConnectionFailed" : "Failed";
                    if (!command.LastExitCode.HasValue && OperatingSystem.IsWindows() && string.IsNullOrEmpty(Native.OS.GitExecutable))
                        stage = "GitUnavailable";
                    OnAuthenticationState(_operationCancelRequested ? "Cancelled" : stage);
                }
                return success && !_operationCancelRequested;
            }
            finally { log.Complete(); }
        }

        protected async Task<bool> VerifyRemotesAsync(string repo, IEnumerable<Models.Remote> remotes, bool push = false)
        {
            foreach (var remote in remotes)
                if (_operationCancelRequested || !await VerifyRemoteAsync(repo, remote, push)) return false;
            return true;
        }

        private bool _operationPanelVisible, _showFullOutput = true, _operationCancelRequested;
        private bool _resumeAfterAuthentication;
        private string _operationStage = "Connecting";
        private int _operationOutputLength;
        private int _operationProgress, _partIndex, _partCount = 1;
        private CancellationTokenSource _operationCancellation;
        private readonly List<OutputLine> _operationOutput = new();
    }
}
