using System;
using System.IO;

namespace SourceGit.Native
{
    internal static class SettingsRecovery
    {
        // 1: exact original backed up; 2: keep this session read-only to protect the original.
        internal static int Preserve(string path, Exception error = null)
        {
            if (error is IOException or UnauthorizedAccessException) return 2;
            try
            {
                File.Copy(path, path + ".recovery-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8], false);
                return 1;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return 2; }
        }
    }
}
