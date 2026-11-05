using System.Text;

namespace SourceGit.Commands
{
    public class Clone : Command
    {
        protected override bool SupportsAuthenticationRetry => true;

        public Clone(string ctx, string path, string url, string localName, string sshKey, string extraArgs)
        {
            Context = ctx;
            WorkingDirectory = path;
            SSHKey = sshKey;

            // Keep credentials for a new clone scoped to its destination, rather than
            // sharing the parent folder's account with every clone made there.
            var destination = localName;
            if (string.IsNullOrEmpty(destination))
            {
                var cleanURL = url.TrimEnd('/', '\\').Replace('\\', '/');
                if (System.Uri.TryCreate(cleanURL, System.UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ssh")
                    cleanURL = uri.AbsolutePath.TrimEnd('/');
                destination = cleanURL[(System.Math.Max(cleanURL.LastIndexOf('/'), cleanURL.LastIndexOf(':')) + 1)..];
                if (destination.EndsWith(".git", System.StringComparison.OrdinalIgnoreCase))
                    destination = destination[..^4];
                else if (destination.EndsWith(".bundle", System.StringComparison.OrdinalIgnoreCase))
                    destination = destination[..^7];
            }
            if (!string.IsNullOrEmpty(destination))
                CredentialScope = System.IO.Path.GetFullPath(System.IO.Path.Combine(path, destination));

            var builder = new StringBuilder(1024);
            builder.Append("clone --progress --verbose ");
            if (!string.IsNullOrEmpty(extraArgs))
                builder.Append(extraArgs).Append(' ');
            builder.Append(url.Quoted()).Append(' ');
            if (!string.IsNullOrEmpty(localName))
                builder.Append(localName.Quoted());

            Args = builder.ToString();
        }
    }
}
