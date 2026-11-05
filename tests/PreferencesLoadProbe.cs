using System;
using System.IO;
using System.Text.Json;

namespace SourceGit.Tests
{
    internal static class PreferencesLoadProbe
    {
        public static int Run(string output)
        {
            Directory.CreateDirectory(output);
            try
            {
                var preferences = ViewModels.Preferences.Instance;
                var workspace = preferences.GetActiveWorkspace();
                _ = preferences.Layout.LauncherWidth;
                _ = preferences.CustomActions.Count;
                _ = preferences.OpenAIServices.Count;
                foreach (var repository in workspace.Repositories) _ = repository.Replace('\\', '/');
                foreach (var node in preferences.RepositoryNodes) _ = node.SubNodes.Count;
                File.WriteAllText(Path.Combine(output, "preferences-load.json"), JsonSerializer.Serialize(new { opened = true, recovery = preferences.LoadRecovery, data = Native.OS.BasicDirectories.ConfigDir }));
                return 0;
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(output, "preferences-load.json"), JsonSerializer.Serialize(new { opened = false, error = error.ToString() }));
                return 1;
            }
        }
    }
}
