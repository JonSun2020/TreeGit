using System;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    // Authenticate against receive-pack without sending an update, invoking a
    // pre-push hook, setting upstreams, or following additional refs. This runs
    // before a commit-and-push can change the index or create a local commit.
    internal sealed class VerifyPushCredentials : Command
    {
        protected override bool SupportsAuthenticationRetry => true;
        protected override bool RequiresAuthenticationTransportExitCode => true;
        protected override bool RequiresInvariantOutput => true;

        internal VerifyPushCredentials(string repo, Models.Remote remote, string refspec = null)
        {
            WorkingDirectory = Context = repo;
            HttpProxyTarget = remote.PushURL ?? remote.URL;
            SSHKey = remote.PrivateSSHKey;
            RaiseError = false;
            Editor = EditorType.None;
            Args = "-c push.autoSetupRemote=false -c push.followTags=false -c push.recurseSubmodules=no " +
                Push.DisableMirror(remote.Name) +
                " push --dry-run --no-verify --porcelain" + (string.IsNullOrEmpty(refspec) ? " --all" : "") +
                " -- " + remote.Name.Quoted();
            if (!string.IsNullOrEmpty(refspec))
                Args += " " + refspec.Quoted();
        }

        internal async Task<bool> VerifyAsync()
        {
            var success = await ExecAsync().ConfigureAwait(false);
            return !WasAuthenticationCancelled &&
                (success || LastExitCode == 1 && WasCredentialAccepted || ReachedReceivePack());
        }

        private bool ReachedReceivePack()
        {
            if (LastExitCode != 1)
                return false;

            if (FailureMessage.Contains("No refs in common and none specified", StringComparison.OrdinalIgnoreCase))
                return true;

            // A ref rejected by the client (such as a non-fast-forward) still
            // reached receive-pack. Leave the actual push to report its outcome.
            var rejected = false;
            foreach (var line in FailureMessage.AsSpan().EnumerateLines())
            {
                if (line.StartsWith("fatal:", StringComparison.Ordinal) ||
                    line.StartsWith("error:", StringComparison.Ordinal) &&
                    !line.StartsWith("error: failed to push some refs to ", StringComparison.Ordinal))
                    return false;

                if (line.StartsWith("!\t", StringComparison.Ordinal))
                {
                    if (!line.Contains("\t[rejected]", StringComparison.Ordinal))
                        return false;
                    rejected = true;
                }
            }

            return rejected;
        }
    }
}
