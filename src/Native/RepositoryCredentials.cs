using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;

namespace SourceGit.Native
{
    internal enum RemoteAccessFailure
    {
        None,
        RejectedCredentials,
        PermissionDenied,
        RepositoryUnavailable,
        ServerError,
    }

    // Generic Windows credentials are encrypted and owned by the current Windows user.
    // No passwords are written into repository config, portable data, logs or MCP output.
    internal static class RepositoryCredentials
    {
        internal static bool UsesGitHubToken(string remote)
            => Uri.TryCreate(remote, UriKind.Absolute, out var uri) &&
                (uri.Scheme is "http" or "https") &&
                uri.IdnHost.TrimEnd('.').Equals("github.com", StringComparison.OrdinalIgnoreCase);

        // Git may authenticate against a canonical redirect (e.g. repo.git)
        // while the configured remote is repo. Only follow server-confirmed,
        // same-origin redirects; never guess that two paths share credentials.
        internal static async Task<IReadOnlyList<Uri>> ResolveCredentialUrisAsync(Uri configured, CancellationToken cancellation = default)
        {
            var result = new List<Uri> { configured };
            var clean = new UriBuilder(configured) { UserName = string.Empty, Password = string.Empty, Query = string.Empty, Fragment = string.Empty }.Uri;
            var endpoint = new Uri(clean.AbsoluteUri.TrimEnd('/') + "/info/refs?service=git-upload-pack");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false };
            using var client = new HttpClient(handler);
            try
            {
                for (var step = 0; step < 4; step++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect) || response.Headers.Location == null) break;
                    var next = new Uri(endpoint, response.Headers.Location);
                    if (next.Scheme != clean.Scheme || next.Authority != clean.Authority || !string.IsNullOrEmpty(next.UserInfo) ||
                        !next.AbsolutePath.EndsWith("/info/refs", StringComparison.Ordinal) || next.Query != "?service=git-upload-pack") break;
                    var canonical = new UriBuilder(next) { Path = next.AbsolutePath[..^10], Query = string.Empty, Fragment = string.Empty }.Uri;
                    if (!result.Exists(uri => uri.AbsoluteUri == canonical.AbsoluteUri)) result.Add(canonical);
                    endpoint = next;
                }
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
            cancellation.ThrowIfCancellationRequested();
            return result;
        }

        internal static string RedactLog(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = Regex.Replace(text, @"(https?://)[^/\s@]*@", "$1***@", RegexOptions.IgnoreCase);
            return Regex.Replace(text, @"(Authorization\s*:\s*(?:Basic|Bearer)\s+)[^\s\""']+", "$1***", RegexOptions.IgnoreCase);
        }
        public static bool IsAuthenticationFailure(string output)
            => ClassifyRemoteAccessFailure(output) is RemoteAccessFailure.RejectedCredentials or RemoteAccessFailure.PermissionDenied;

        internal static RemoteAccessFailure ClassifyRemoteAccessFailure(string output)
        {
            if (string.IsNullOrEmpty(output)) return RemoteAccessFailure.None;
            // A local reference hook can print an authentication-like message
            // after the fetch transport has succeeded. Never replay that hook.
            // Remove URLs before testing so a repository named "hook" still
            // receives the normal credential correction flow.
            var detail = Regex.Replace(output, @"\b(?:https?|ssh|git|file)://\S+", string.Empty, RegexOptions.IgnoreCase);
            if (Regex.IsMatch(detail, @"\bhooks?\b", RegexOptions.IgnoreCase) ||
                detail.Contains("unable to update local ref", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("cannot lock ref", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("failed to update ref", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("told us to quit", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("User canceled authentication", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("User cancelled authentication", StringComparison.OrdinalIgnoreCase))
                return RemoteAccessFailure.None;

            if (output.Contains("The requested URL returned error: 403", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("Write access to repository not granted", StringComparison.OrdinalIgnoreCase))
                return RemoteAccessFailure.PermissionDenied;

            if (output.Contains("The requested URL returned error: 404", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("remote: Repository not found", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(output, @"fatal: repository 'https?://[^']*' not found", RegexOptions.IgnoreCase))
                return RemoteAccessFailure.RepositoryUnavailable;

            if (Regex.IsMatch(output, @"The requested URL returned error: 5[0-9]{2}\b", RegexOptions.IgnoreCase))
                return RemoteAccessFailure.ServerError;

            return output.Contains("fatal: Authentication failed for 'http", StringComparison.OrdinalIgnoreCase) ||
                output.Contains(CredentialSession.RejectedMarker, StringComparison.Ordinal) ||
                output.Contains("The requested URL returned error: 401", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("Permission denied (publickey", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("Permission denied (password", StringComparison.OrdinalIgnoreCase)
                ? RemoteAccessFailure.RejectedCredentials : RemoteAccessFailure.None;
        }

        private static readonly ConcurrentDictionary<string, Lazy<bool>> _managerModes = new(StringComparer.OrdinalIgnoreCase);

        public static bool UsesCredentialManager(string repo)
        {
            if (string.IsNullOrEmpty(repo))
                return false;
            // Publish the entry before starting I/O so invalidation also removes in-flight reads.
            return _managerModes.GetOrAdd(Path.GetFullPath(repo), path => new Lazy<bool>(() =>
            {
                try
                {
                    var start = new ProcessStartInfo(OS.GitExecutable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    start.ArgumentList.Add("-C");
                    start.ArgumentList.Add(path);
                    start.ArgumentList.Add("config");
                    start.ArgumentList.Add("--bool");
                    start.ArgumentList.Add("sourcegit.credentialmanager");
                    using var process = ProcessLauncher.Start(start);
                    var output = process.StandardOutput.ReadToEndAsync();
                    var error = process.StandardError.ReadToEndAsync();
                    Task.WhenAll(output, error).GetAwaiter().GetResult();
                    process.WaitForExit();
                    return process.ExitCode == 0 && output.GetAwaiter().GetResult().Trim() == "true";
                }
                catch { return false; }
            })).Value;
        }

        public static void SetCredentialManagerMode(string repo, bool enabled)
            => _managerModes[Path.GetFullPath(repo)] = new Lazy<bool>(() => enabled);

        public static void InvalidateMode(string repo)
        {
            if (!string.IsNullOrEmpty(repo)) _managerModes.TryRemove(Path.GetFullPath(repo), out _);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Write(ref Credential credential, uint flags);
        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Read(string target, uint type, uint flags, out IntPtr credential);
        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Delete(string target, uint type, uint flags);
        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr buffer);

        private static string Target(string repo, string protocol, string host, string path)
        {
            if (Uri.TryCreate($"{protocol}://{host}/{path.TrimStart('/')}", UriKind.Absolute, out var uri))
            {
                host = uri.Authority;
                path = uri.AbsolutePath.TrimEnd('/');
            }
            return LegacyTarget(repo, protocol, host, path);
        }

        private static string LegacyTarget(string repo, string protocol, string host, string path)
        {
            var scope = Path.GetFullPath(repo).TrimEnd('\\', '/').ToUpperInvariant();
            var value = $"{scope}\n{protocol.ToLowerInvariant()}\n{host.ToLowerInvariant()}\n{path.TrimStart('/')}";
            return "TreeGit.Repository/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        }

        private static IEnumerable<string> Targets(string repo, Uri remote)
        {
            var canonical = Target(repo, remote.Scheme, remote.Authority, remote.AbsolutePath);
            yield return canonical;
            // Decode legacy Unicode spellings without equating an encoded path
            // separator or URI delimiter with a different remote repository.
            var paths = Regex.IsMatch(remote.AbsolutePath, @"%(?:2f|5c|3f|23|3a|40)", RegexOptions.IgnoreCase) ?
                new[] { remote.AbsolutePath } : new[] { remote.AbsolutePath, Uri.UnescapeDataString(remote.AbsolutePath) };
            var hosts = remote.IsDefaultPort ? new[] { remote.Authority, remote.Host + ":" + remote.Port } : new[] { remote.Authority };
            var seen = new HashSet<string>(StringComparer.Ordinal) { canonical };
            foreach (var host in hosts)
                foreach (var path in paths)
                    foreach (var variant in new[] { path, path.TrimEnd('/'), path.TrimEnd('/') + "/" })
                    {
                        var key = LegacyTarget(repo, remote.Scheme, host, variant);
                        if (seen.Add(key)) yield return key;
                    }
        }

        public static (string Username, string Password)? Get(string repo, Uri remote)
        {
            var canonical = Target(repo, remote.Scheme, remote.Authority, remote.AbsolutePath);
            foreach (var key in Targets(repo, remote))
            {
                var saved = Get(key);
                if (!saved.HasValue) continue;
                if (key != canonical) Save(canonical, saved.Value.Username, saved.Value.Password);
                return saved;
            }
            return null;
        }

        private static (string Username, string Password)? Get(string target)
        {
            if (!OperatingSystem.IsWindows())
                return null;
            var migrated = false;
            if (!Read(target, 1, 0, out var ptr))
            {
                if (!Read(target.Replace("TreeGit.Repository/", "SourceGit.Repository/", StringComparison.Ordinal), 1, 0, out ptr))
                    return null;
                migrated = true;
            }
            try
            {
                var c = Marshal.PtrToStructure<Credential>(ptr);
                var password = c.CredentialBlobSize == 0 ? string.Empty : Marshal.PtrToStringUni(c.CredentialBlob, checked((int)c.CredentialBlobSize / 2)) ?? string.Empty;
                if (migrated)
                    Save(target, c.UserName, password);
                return (c.UserName, password);
            }
            finally { CredFree(ptr); }
        }

        public static void Save(string repo, Uri remote, string username, string password)
            => Save(Target(repo, remote.Scheme, remote.Authority, remote.AbsolutePath), username, password);

        private static void Save(string target, string username, string password)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Repository credential storage requires Windows.");
            if (username.IndexOfAny(['\r', '\n', '\0']) >= 0 || password.IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new ArgumentException("Credentials cannot contain line breaks or NUL characters.");
            var bytes = Encoding.Unicode.GetBytes(password);
            if (bytes.Length > 5120)
                throw new ArgumentException("Password/token exceeds Windows credential storage size.");
            var blob = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                var c = new Credential
                {
                    Type = 1,
                    TargetName = target,
                    UserName = username,
                    Persist = 2,
                    CredentialBlob = blob,
                    CredentialBlobSize = (uint)bytes.Length,
                    Comment = "TreeGit repository-scoped HTTPS credential",
                };
                if (!Write(ref c, 0))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                for (var i = 0; i < bytes.Length; i++)
                    Marshal.WriteByte(blob, i, 0);
                Marshal.FreeHGlobal(blob);
            }
        }

        public static void Forget(string repo, Uri remote)
        {
            foreach (var target in Targets(repo, remote)) ForgetTarget(target);
        }

        private static void ForgetTarget(string target)
        {
            if (!OperatingSystem.IsWindows()) return;
            static void Remove(string name)
            {
                if (Delete(name, 1, 0)) return;
                var error = Marshal.GetLastWin32Error();
                // A missing entry is already cleared. Other failures must remain visible.
                if (error != 1168) throw new System.ComponentModel.Win32Exception(error);
            }
            Remove(target);
            Remove(target.Replace("TreeGit.Repository/", "SourceGit.Repository/", StringComparison.Ordinal));
        }

        public static int RunHelper(string operation, bool promptOnly = false)
        {
            try
            {
                var input = new Dictionary<string, string>(StringComparer.Ordinal);
                using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
                while (reader.ReadLine() is { Length: > 0 } line)
                {
                    var sep = line.IndexOf('=');
                    if (sep > 0)
                        input[line[..sep]] = line[(sep + 1)..];
                }
                var repo = Environment.GetEnvironmentVariable("SOURCEGIT_CREDENTIAL_SCOPE");
                if (string.IsNullOrEmpty(repo) || !input.TryGetValue("protocol", out var protocol) ||
                    protocol is not ("https" or "http") || !input.TryGetValue("host", out var host))
                    return 0;
                input.TryGetValue("path", out var path);
                var remote = new Uri($"{protocol}://{host}/{path?.TrimStart('/')}");
                if (operation == "get")
                {
                    var stored = promptOnly || Environment.GetEnvironmentVariable("SOURCEGIT_AUTHENTICATION_RETRY") == "TRUE"
                        ? null : Get(repo, remote);
                    input.TryGetValue("username", out var requested);
                    if (stored.HasValue && (requested == null || requested == stored.Value.Username))
                    {
                        using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                        writer.WriteLine("username=" + stored.Value.Username);
                        writer.WriteLine("password=" + stored.Value.Password);
                        writer.WriteLine();
                    }
                    else if (Environment.GetEnvironmentVariable("SOURCEGIT_LAUNCH_AS_ASKPASS") == "TRUE")
                    {
                        // Git accepts username and password together from a helper,
                        // including an explicitly empty password. Never split this
                        // into Git's subsequent username/password askpass calls.
                        string result;
                        if (CredentialSession.IsValid(CredentialSession.CurrentId))
                        {
                            var answer = CredentialSession.Request(new() { Action = "get", Repo = repo,
                                Remote = $"{protocol}://{host}/{path?.TrimStart('/')}", Username = requested,
                                Retry = Environment.GetEnvironmentVariable("SOURCEGIT_AUTHENTICATION_RETRY") == "TRUE" });
#if INTEGRATION_TESTS
                            Tests.AskpassProbe.Trace("helper-answer-" + (answer?.State ?? "null"));
#endif
                            result = answer?.State == "submitted" ? $"username={answer.Username}\npassword={answer.Password}\n\n" : null;
                        }
                        else result = Prompt(repo, $"{protocol}://{host}/{path?.TrimStart('/')}", requested);
                        if (result == null) Console.Error.WriteLine(CredentialSession.CancelMarker);
                        using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                        writer.Write(result ?? "quit=true\n\n");
                    }
                }
                else if (operation == "store" && input.TryGetValue("username", out var username) && input.TryGetValue("password", out var password))
                {
                    if (!promptOnly) Save(repo, remote, username, password);
                    CredentialSession.Publish("accepted");
                }
                else if (operation == "erase")
                {
                    if (!promptOnly) Forget(repo, remote);
                    Console.Error.WriteLine(CredentialSession.RejectedMarker);
                    CredentialSession.Publish("rejected");
                }
                return 0;
            }
            catch (Exception ex)
            {
#if INTEGRATION_TESTS
                // Preserve method names for isolated test diagnostics without logging input or paths.
                var frames = new System.Diagnostics.StackTrace(ex, false).GetFrames();
                var methods = Array.ConvertAll(frames, frame => frame.GetMethod()?.Name);
                Tests.AskpassProbe.Trace("helper-error-" + ex.GetType().Name + "-" + string.Join("/", methods));
#endif
                // Never include credential input or exception data in protocol output.
                Console.Error.WriteLine("TreeGit credential storage failed: " + ex.GetType().Name);
                return 1;
            }
        }

        private static string Prompt(string repo, string remote, string username)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            };
            start.ArgumentList.Add("--credential-prompt");
            using var process = ProcessLauncher.Start(start);
            if (process == null) return null;
            var error = process.StandardError.ReadToEndAsync();
            var retry = Environment.GetEnvironmentVariable("SOURCEGIT_AUTHENTICATION_RETRY") == "TRUE";
            process.StandardInput.Write(JsonSerializer.Serialize(new { repo, remote, username, retry }));
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            error.GetAwaiter().GetResult();
            if (process.ExitCode != 0) return null;
            var hasUsername = false;
            var hasPassword = false;
            using var lines = new StringReader(output);
            while (lines.ReadLine() is { Length: > 0 } line)
            {
                if (line.StartsWith("username=", StringComparison.Ordinal)) hasUsername = line.Length > 9;
                else if (line.StartsWith("password=", StringComparison.Ordinal)) hasPassword = true;
                else return null;
            }
            // quit=true tells Git to honour cancellation without asking again:
            // https://git-scm.com/docs/gitcredentials
            return hasUsername && hasPassword ? output : null;
        }
    }
}
