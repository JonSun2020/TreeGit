using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public static class SaveRevisionFile
    {
        public static async Task RunAsync(string repo, string revision, string file, string saveTo)
        {
            try
            {
                var isLFSFiltered = await new IsLFSFiltered(repo, revision, file).GetResultAsync().ConfigureAwait(false);
                if (isLFSFiltered)
                {
                    await using var pointerStream = await QueryFileContent.RunAsync(repo, revision, file, requireSuccess: true).ConfigureAwait(false);
                    await ExecCmdAsync(repo, "lfs smudge", saveTo, pointerStream).ConfigureAwait(false);
                }
                else
                {
                    await ExecCmdAsync(repo, $"show {revision}:{file.Quoted()}", saveTo).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                ReportFailure(repo);
            }
        }

        private static async Task ExecCmdAsync(string repo, string args, string outputFile, Stream input = null)
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
                    proc.BeginErrorReadLine();

                    if (input != null)
                    {
                        var inputString = await new StreamReader(input).ReadToEndAsync().ConfigureAwait(false);
                        await proc.StandardInput.WriteAsync(inputString).ConfigureAwait(false);
                    }
                    proc.StandardInput.Close();

                    await proc.StandardOutput.BaseStream.CopyToAsync(sw).ConfigureAwait(false);
                    await proc.WaitForExitAsync().ConfigureAwait(false);
                    if (proc.ExitCode != 0) throw new IOException("The selected revision file could not be read.");
                }
                File.Move(partial, outputFile, true);
            }
            catch (Exception)
            {
                ReportFailure(repo);
            }
            finally
            {
                try { if (File.Exists(partial)) File.Delete(partial); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }

        private static void ReportFailure(string repo) => Models.Notification.Send(repo,
            App.Current == null ? "The file could not be saved. The existing target file was kept." : App.Text("File.ExportFailed"), true);
    }
}
