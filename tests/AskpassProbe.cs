using System;
using System.IO;
using System.Text.Json;
using Avalonia.Threading;

namespace SourceGit.Tests
{
    internal static class AskpassProbe
    {
        public static void OnLoaded(Views.Askpass view, Action submit)
        {
            var fixture = Environment.GetEnvironmentVariable("SOURCEGIT_TEST_ASKPASS_FIXTURE");
            if (string.IsNullOrEmpty(fixture))
                return;
            using var document = JsonDocument.Parse(File.ReadAllText(fixture));
            var value = document.RootElement;
            var username = view.TxtDescription.Text.StartsWith("Username", StringComparison.OrdinalIgnoreCase);
            File.AppendAllText(fixture + ".events", username ? "username-dialog\n" : "password-dialog\n");
            view.TxtPassphrase.Text = value.GetProperty(username ? "username" : "password").GetString();
            Dispatcher.UIThread.Post(submit, DispatcherPriority.Background);
        }
    }
}
