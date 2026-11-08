using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public static class SaveRevisionFile
    {
        public static async Task<bool> RunAsync(string repo, string revision, string file, string saveTo, CancellationToken token = default, bool reportFailure = true)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var isLFSFiltered = await new IsLFSFiltered(repo, revision, file).GetResultAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (isLFSFiltered)
                {
                    await using var pointerStream = await QueryFileContent.RunAsync(repo, revision, file, requireSuccess: true).ConfigureAwait(false);
                    await ExecCmdAsync(repo, "lfs smudge", saveTo, token, pointerStream).ConfigureAwait(false);
                }
                else
                {
                    await ExecCmdAsync(repo, $"show {(revision + ":" + file).Quoted()}", saveTo, token).ConfigureAwait(false);
                }
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                if (reportFailure) ReportFailure(repo);
                return false;
            }
        }

        private static async Task ExecCmdAsync(string repo, string args, string outputFile, CancellationToken token, Stream input = null)
        {
            var starter = new ProcessStartInfo();
            starter.WorkingDirectory = repo;
            starter.FileName = Native.OS.GitExecutable;
            starter.Arguments = args;
            starter.UseShellExecute = false;
            starter.CreateNoWindow = true;
            starter.WindowStyle = ProcessWindowStyle.Hidden;
            starter.RedirectStandardInput = true;
            starter.RedirectStandardOutput = true;
            starter.RedirectStandardError = true;

            var partial = outputFile + ".treegit-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var directory = Path.GetDirectoryName(outputFile);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                await using (var sw = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    using var proc = Native.ProcessLauncher.Start(starter)!;
                    using var cancel = token.Register(() => Stop(proc));
                    proc.BeginErrorReadLine();
                    try
                    {
                        if (input != null)
                            await input.CopyToAsync(proc.StandardInput.BaseStream, token).ConfigureAwait(false);
                        proc.StandardInput.Close();

                        await proc.StandardOutput.BaseStream.CopyToAsync(sw, token).ConfigureAwait(false);
                        await proc.WaitForExitAsync(token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (proc.ExitCode != 0) throw new IOException("The selected revision file could not be read.");
                    }
                    catch
                    {
                        Stop(proc);
                        try { await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                        catch (Exception error) when (error is TimeoutException or InvalidOperationException) { }
                        throw;
                    }
                }
                token.ThrowIfCancellationRequested();
                File.Move(partial, outputFile, true);
            }
            finally
            {
                try { if (File.Exists(partial)) File.Delete(partial); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }

        private static void Stop(Process process)
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        private static void ReportFailure(string repo) => Models.Notification.Send(repo,
            App.Current == null ? "The file could not be saved. The existing target file was kept." : App.Text("File.ExportFailed"), true);
    }
}
