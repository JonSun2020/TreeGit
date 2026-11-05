using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SourceGit.Native
{
    internal static class StartupTrace
    {
        public static void Mark(string phase, bool complete = false)
        {
            if (string.IsNullOrEmpty(_path))
                return;
            _phases.Add(new { phase, elapsedMs = Math.Round(_watch.Elapsed.TotalMilliseconds, 2) });
            if (!complete)
                return;
            try { File.WriteAllText(_path, JsonSerializer.Serialize(_phases)); }
            catch (Exception) { /* Optional diagnostics must not affect startup. */ }
        }

        private static readonly string _path = Environment.GetEnvironmentVariable("TREEGIT_STARTUP_TRACE");
        private static readonly Stopwatch _watch = Stopwatch.StartNew();
        private static readonly List<object> _phases = [];
    }
}
