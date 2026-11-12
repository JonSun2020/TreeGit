using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryRemotes
    {
        public bool Succeeded { get; private set; }
        public System.Threading.CancellationToken CancellationToken { get; set; }
        public QueryRemotes(string repo)
        {
            _repo = repo;
        }

        public async Task<List<Models.Remote>> GetResultAsync()
        {
            Succeeded = false;
            var query = new Config(_repo) { CancellationToken = CancellationToken };
            var config = await query.ReadAllAsync().ConfigureAwait(false);
            Succeeded = query.ReadSucceeded;
            return Parse(config);
        }

        public List<Models.Remote> GetResult()
        {
            Succeeded = false;
            var query = new Config(_repo);
            var config = query.ReadAll();
            Succeeded = query.ReadSucceeded;
            return Parse(config);
        }

        internal static List<Models.Remote> Parse(Dictionary<string, string> config)
        {
            var names = new HashSet<string>();
            var urls = new Dictionary<string, string>();
            var pushUrls = new Dictionary<string, string>();
            var privateSSHKeys = new Dictionary<string, string>();
            var disableAutoFetchRemotes = new HashSet<string>();

            foreach (var (k, v) in config)
            {
                if (!k.StartsWith("remote.", StringComparison.Ordinal))
                    continue;

                if (k.EndsWith(".url", StringComparison.Ordinal))
                {
                    var name = k.Substring(7, k.Length - 11).Trim('"');
                    names.Add(name);
                    urls[name] = v;
                }
                else if (k.EndsWith(".pushurl", StringComparison.OrdinalIgnoreCase))
                {
                    var name = k.Substring(7, k.Length - 15).Trim('"');
                    names.Add(name);
                    pushUrls[name] = v;
                }
                else if (k.EndsWith(".sshkey", StringComparison.OrdinalIgnoreCase))
                {
                    var name = k.Substring(7, k.Length - 14).Trim('"');
                    names.Add(name);
                    privateSSHKeys[name] = v;
                }
                else if (k.EndsWith(".disableautofetch", StringComparison.OrdinalIgnoreCase) &&
                         v.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    var name = k.Substring(7, k.Length - 24).Trim('"');
                    disableAutoFetchRemotes.Add(name);
                }
            }

            var remotes = new List<Models.Remote>();
            foreach (var name in names)
            {
                if (!urls.TryGetValue(name, out var url))
                    continue;

                var r = new Models.Remote()
                {
                    Name = name,
                    URL = url,
                    PushURL = pushUrls.TryGetValue(name, out var pushURL) ? pushURL : null,
                    PrivateSSHKey = privateSSHKeys.TryGetValue(name, out var privateSSHKey) ? privateSSHKey : null,
                    DisableAutoFetch = disableAutoFetchRemotes.Contains(name)
                };

                remotes.Add(r);
            }

            remotes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
            return remotes;
        }

        private readonly string _repo;
    }
}
