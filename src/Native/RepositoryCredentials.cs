using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SourceGit.Native
{
    // Generic Windows credentials are encrypted and owned by the current Windows user.
    // No passwords are written into repository config, portable data, logs or MCP output.
    internal static class RepositoryCredentials
    {
        public static bool IsAuthenticationFailure(string output)
            => output?.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) == true;

        private static readonly ConcurrentDictionary<string, bool> _managerModes = new(StringComparer.OrdinalIgnoreCase);

        public static bool UsesCredentialManager(string repo)
        {
            if (string.IsNullOrEmpty(repo))
                return false;
            return _managerModes.GetOrAdd(Path.GetFullPath(repo), path =>
            {
                try
                {
                    var start = new ProcessStartInfo(OS.GitExecutable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    start.ArgumentList.Add("-C");
                    start.ArgumentList.Add(path);
                    start.ArgumentList.Add("config");
                    start.ArgumentList.Add("--bool");
                    start.ArgumentList.Add("sourcegit.credentialmanager");
                    using var process = Process.Start(start);
                    var value = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    return process.ExitCode == 0 && value.Trim() == "true";
                }
                catch { return false; }
            });
        }

        public static void SetCredentialManagerMode(string repo, bool enabled)
            => _managerModes[Path.GetFullPath(repo)] = enabled;

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
            var scope = Path.GetFullPath(repo).TrimEnd('\\', '/').ToUpperInvariant();
            var value = $"{scope}\n{protocol.ToLowerInvariant()}\n{host.ToLowerInvariant()}\n{path.TrimStart('/')}";
            return "TreeGit.Repository/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        }

        public static (string Username, string Password)? Get(string repo, Uri remote)
            => Get(Target(repo, remote.Scheme, remote.Authority, remote.AbsolutePath));

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
                var password = Marshal.PtrToStringUni(c.CredentialBlob, checked((int)c.CredentialBlobSize / 2)) ?? string.Empty;
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
            => ForgetTarget(Target(repo, remote.Scheme, remote.Authority, remote.AbsolutePath));

        private static void ForgetTarget(string target)
        {
            Delete(target, 1, 0);
            Delete(target.Replace("TreeGit.Repository/", "SourceGit.Repository/", StringComparison.Ordinal), 1, 0);
        }

        public static int RunHelper(string operation)
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
                var target = Target(repo, protocol, host, path ?? string.Empty);
                if (operation == "get")
                {
                    var stored = Get(target);
                    if (stored.HasValue && (!input.TryGetValue("username", out var requested) || requested == stored.Value.Username))
                    {
                        using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                        writer.WriteLine("username=" + stored.Value.Username);
                        writer.WriteLine("password=" + stored.Value.Password);
                        writer.WriteLine();
                    }
                }
                else if (operation == "store" && input.TryGetValue("username", out var username) && input.TryGetValue("password", out var password))
                    Save(target, username, password);
                else if (operation == "erase")
                    ForgetTarget(target);
                return 0;
            }
            catch (Exception ex)
            {
                // Never include credential input or exception data in protocol output.
                Console.Error.WriteLine("TreeGit credential storage failed: " + ex.GetType().Name);
                return 1;
            }
        }
    }
}
