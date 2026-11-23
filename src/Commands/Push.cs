using System.Text;

namespace SourceGit.Commands
{
    public class Push : Command
    {
        protected override bool SupportsAuthenticationRetry => true;
        // A rejecting pre-push hook returns 1, even if it prints an authentication
        // failure from a nested command. Only retry Git's transport failure (128).
        protected override bool RequiresAuthenticationTransportExitCode => true;

        internal static string BranchRefspec(string source, string target) => $"{source}:refs/heads/{target}";

        internal static string DisableMirror(string remoteName) => "-c " + $"remote.{remoteName}.mirror=false".Quoted();

        public Push(string repo, Models.Branch local, Models.Remote remote, Models.Branch remoteBranch, bool withTags, bool checkSubmodules, bool track, bool force, bool noVerify)
        {
            WorkingDirectory = repo;
            Context = repo;
            HttpProxyTarget = remote.PushURL ?? remote.URL;
            SSHKey = remote.PrivateSSHKey;

            var builder = new StringBuilder(1024);
            builder.Append(DisableMirror(remote.Name)).Append(" push --progress --verbose --no-follow-tags ");
            if (withTags)
                builder.Append("--tags ");
            builder.Append(checkSubmodules ? "--recurse-submodules=check " : "--recurse-submodules=no ");
            if (track)
                builder.Append("-u ");
            if (force)
                builder.Append("--force-with-lease ");
            if (noVerify)
                builder.Append("--no-verify ");

            builder.Append("-- ").Append(remote.Name.Quoted()).Append(' ')
                .Append(BranchRefspec($"refs/heads/{local.Name}", remoteBranch.Name).Quoted());
            Args = builder.ToString();
        }

        public Push(string repo, Models.Commit revision, Models.Remote remote, Models.Branch remoteBranch, bool force)
        {
            WorkingDirectory = repo;
            Context = repo;
            HttpProxyTarget = remote.PushURL ?? remote.URL;
            SSHKey = remote.PrivateSSHKey;

            var builder = new StringBuilder(1024);
            builder.Append(DisableMirror(remote.Name)).Append(" push --progress --verbose --no-follow-tags --recurse-submodules=no ");
            if (force)
                builder.Append("--force-with-lease ");

            builder.Append("-- ").Append(remote.Name.Quoted()).Append(' ')
                .Append(BranchRefspec(revision.SHA, remoteBranch.Name).Quoted());
            Args = builder.ToString();
        }

        public Push(string repo, Models.Remote remote, string refname, bool isDelete)
        {
            WorkingDirectory = repo;
            Context = repo;
            HttpProxyTarget = remote.PushURL ?? remote.URL;
            SSHKey = remote.PrivateSSHKey;

            var builder = new StringBuilder(512);
            builder.Append("push --progress ");
            if (isDelete)
                builder.Append("--delete ");
            builder.Append(remote.Name).Append(' ').Append(refname);

            Args = builder.ToString();
        }
    }
}
