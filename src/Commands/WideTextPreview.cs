using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    internal static class WideTextPreview
    {
        private const int MaxBytes = 8 * 1024 * 1024;

        internal static async Task<byte[]> ReadAsync(string repo, string revision, string path, string blob, int codePage)
        {
            if (path == "/dev/null" || (!string.IsNullOrEmpty(blob) && blob.All(c => c == '0'))) return [];
            if (revision == null)
            {
                var file = new FileInfo(Path.Combine(repo, path));
                if (!file.Exists || file.Length > MaxBytes || (file.Attributes & FileAttributes.ReparsePoint) != 0) return null;
                await using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return await ReadTextBytesAsync(stream, codePage).ConfigureAwait(false);
            }

            var start = new ProcessStartInfo(Native.OS.GitExecutable)
            {
                WorkingDirectory = repo, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.ArgumentList.Add("cat-file");
            start.ArgumentList.Add("blob");
            start.ArgumentList.Add(string.IsNullOrEmpty(blob) ? revision + ":" + path : blob);
            using var process = Native.ProcessLauncher.Start(start);
            var error = process.StandardError.ReadToEndAsync();
            try
            {
                var bytes = await ReadTextBytesAsync(process.StandardOutput.BaseStream, codePage).ConfigureAwait(false);
                if (bytes == null) return null;
                await process.WaitForExitAsync().ConfigureAwait(false);
                return process.ExitCode == 0 ? bytes : null;
            }
            finally
            {
                // Git for Windows can run cat-file through a launcher. Close the
                // read end first so its child cannot remain blocked writing a
                // binary blob after we have rejected the sample.
                process.StandardOutput.Close();
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync().ConfigureAwait(false);
                await error.ConfigureAwait(false);
            }
        }

        private static async Task<byte[]> ReadTextBytesAsync(Stream input, int codePage)
        {
            var block = new byte[8192];
            var n = await input.ReadAtLeastAsync(block, block.Length, false).ConfigureAwait(false);
            var sample = block.AsSpan(0, n);
            var wide = Models.DisplayEncoding.DetectWide(sample);
            if (!Models.DisplayEncoding.IsWide(codePage) && wide == 0 && sample.Contains((byte)0)) return null;
            using var content = new MemoryStream();
            while (n > 0)
            {
                if (content.Length + n > MaxBytes) return null;
                content.Write(block, 0, n);
                n = await input.ReadAsync(block).ConfigureAwait(false);
            }
            return content.ToArray();
        }
    }
}
