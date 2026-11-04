using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class Config : Command
    {
        public Config(string repository, string file = null)
        {
            _file = file;
            if (string.IsNullOrEmpty(repository))
            {
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            else
            {
                WorkingDirectory = repository;
                Context = repository;
                _isLocal = true;
            }
        }

        public Dictionary<string, string> ReadAll() => ReadAll(false);

        public Dictionary<string, string> ReadAll(bool localOnly)
        {
            Args = _file != null ? $"config --file {_file.Quoted()} -l" : localOnly ? "config --local -l" : "config -l";

            var output = ReadToEnd();
            var rs = new Dictionary<string, string>();
            if (output.IsSuccess)
            {
                var lines = output.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length == 2)
                        rs[parts[0]] = parts[1];
                }
            }

            return rs;
        }

        public async Task<Dictionary<string, string>> ReadAllAsync(string globalOptions = null)
        {
            Args = _file != null ? $"config --file {_file.Quoted()} -l" : "config -l";
            if (!string.IsNullOrEmpty(globalOptions)) Args = globalOptions + " " + Args;

            var output = await ReadToEndAsync().ConfigureAwait(false);
            var rs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (output.IsSuccess)
            {
                var lines = output.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length == 2)
                    {
                        var key = parts[0];
                        var value = parts[1];
                        rs[key] = value;
                    }
                }
            }

            return rs;
        }

        public string Get(string key)
        {
            Args = _file != null ? $"config --file {_file.Quoted()} {key}" : $"config {key}";
            return ReadToEnd().StdOut.Trim();
        }

        public async Task<string> GetAsync(string key)
        {
            Args = _file != null ? $"config --file {_file.Quoted()} {key}" : $"config {key}";

            var rs = await ReadToEndAsync().ConfigureAwait(false);
            return rs.StdOut.Trim();
        }

        public async Task<bool> SetAsync(string key, string value, bool allowEmpty = false)
        {
            var scope = _file != null ? "--file " + _file.Quoted() : _isLocal ? "--local" : "--global";

            if (!allowEmpty && string.IsNullOrWhiteSpace(value))
                Args = $"config {scope} --unset {key}";
            else
                Args = $"config {scope} {key} {value.Quoted()}";

            return await ExecAsync().ConfigureAwait(false);
        }

        private bool _isLocal = false;
        private readonly string _file;
    }
}
