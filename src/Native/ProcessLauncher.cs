using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;

namespace SourceGit.Native
{
    internal static class ProcessLauncher
    {
#if INTEGRATION_TESTS
        internal static Action<Process, int> BeforeStart;
#endif

        internal static void Start(Process process)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
#if INTEGRATION_TESTS
                    BeforeStart?.Invoke(process, attempt + 1);
#endif
                    process.Start();
                    return;
                }
                catch (Win32Exception error) when (attempt < 2 && OperatingSystem.IsWindows() &&
                    error.NativeErrorCode == 5 && !process.StartInfo.UseShellExecute && !HasProcess(process))
                {
                    // CreateProcess failed before an operation could run. Do not
                    // replay an existing process or retry a Git exit failure.
                    Thread.Sleep(200 * (attempt + 1));
                }
            }
        }

        internal static Process Start(ProcessStartInfo info)
        {
            var process = new Process { StartInfo = info };
            try { Start(process); return process; }
            catch { process.Dispose(); throw; }
        }

        private static bool HasProcess(Process process)
        {
            try { return process.Id > 0; }
            catch (InvalidOperationException) { return false; }
        }
    }
}
