using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class Submodule : Command
    {
        public Submodule(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
            PreserveSubmoduleArguments = true;
        }

        public async Task<bool> AddAsync(string url, string relativePath, bool recursive)
        {
            Args = $"--literal-pathspecs -c protocol.file.allow=always submodule add -- {NormalizeURL(url).Quoted()} {relativePath.Quoted()}";

            var succ = await ExecAsync().ConfigureAwait(false);
            if (!succ)
                return false;

            if (recursive)
                Args = $"submodule update --init --recursive -- {LiteralPath(relativePath)}";
            else
                Args = $"submodule update --init -- {LiteralPath(relativePath)}";
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> SetURLAsync(string path, string url)
        {
            Args = $"submodule set-url -- {path.Quoted()} {NormalizeURL(url).Quoted()}";
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> SetBranchAsync(string path, string branch)
        {
            if (string.IsNullOrEmpty(branch))
                Args = $"submodule set-branch -d -- {path.Quoted()}";
            else
                Args = $"submodule set-branch -b {branch.Quoted()} -- {path.Quoted()}";

            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> UpdateAsync(List<string> modules, bool init, bool recursive, bool useRemote)
        {
            var builder = new StringBuilder();
            builder.Append("submodule update");

            if (init)
                builder.Append(" --init");
            if (recursive)
                builder.Append(" --recursive");
            if (useRemote)
                builder.Append(" --remote");
            if (modules.Count > 0)
            {
                builder.Append(" --");
                foreach (var module in modules)
                    builder.Append(' ').Append(LiteralPath(module));
            }

            Args = builder.ToString();
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> DeinitAsync(string module, bool force)
        {
            Args = force ? $"submodule deinit -f -- {LiteralPath(module)}" : $"submodule deinit -- {LiteralPath(module)}";
            return await ExecAsync().ConfigureAwait(false);
        }

        // Explicit pathspec magic keeps each selected module literal across
        // git-submodule's shell wrapper and its helper commands.
        private static string LiteralPath(string path) => (":(top,literal)" + path).Quoted();

        private static string NormalizeURL(string url) => System.OperatingSystem.IsWindows() && System.IO.Path.IsPathRooted(url) ? url.Replace('\\', '/') : url;

        public async Task<bool> DeleteAsync(string module)
        {
            Args = $"--literal-pathspecs rm -rf -- {module.Quoted()}";
            return await ExecAsync().ConfigureAwait(false);
        }
    }
}
