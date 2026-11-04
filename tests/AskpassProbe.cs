using System;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Media.Imaging;
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
            if (!view.Topmost || !view.ShowActivated)
                throw new InvalidOperationException("The authentication prompt must stay on top and activate when shown.");
            if (view.IsCredentialPrompt)
            {
                if (!view.UsernameSection.IsVisible || !view.TxtUsername.IsEffectivelyVisible || !view.TxtPassphrase.IsEffectivelyVisible)
                    throw new InvalidOperationException("Username and password must be visible in the same authentication window.");
                var cancel = value.TryGetProperty("cancel", out var cancelValue) && cancelValue.GetBoolean();
                view.TxtUsername.Text = value.GetProperty("username").GetString();
                view.TxtPassphrase.Text = value.GetProperty("password").GetString();
                File.AppendAllText(fixture + ".events", "combined-dialog\ntopmost-combined-dialog\n");
                Dispatcher.UIThread.Post(() =>
                {
                    if (!File.Exists(fixture + ".png"))
                    {
                        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(view.Bounds.Width), (int)Math.Ceiling(view.Bounds.Height)), new Vector(96, 96));
                        bitmap.Render(view);
                        bitmap.Save(fixture + ".png");
                    }
                    view.ToggleShowPassword.IsChecked = true;
                    if (!view.TxtPassphrase.RevealPassword) throw new InvalidOperationException("The eye toggle did not reveal the password.");
                    view.ToggleShowPassword.IsChecked = false;
                    if (view.TxtPassphrase.RevealPassword || view.TxtPassphrase.PasswordChar != '*')
                        throw new InvalidOperationException("The eye toggle did not restore star masking.");
                    File.AppendAllText(fixture + ".events", "visibility-toggle\n");
                    if (cancel) view.Close();
                    else submit();
                }, DispatcherPriority.Background);
                return;
            }
            var username = view.TxtDescription.Text.StartsWith("Username", StringComparison.OrdinalIgnoreCase);
            File.AppendAllText(fixture + ".events", username ? "username-dialog\n" : "password-dialog\n");
            File.AppendAllText(fixture + ".events", username ? "topmost-username-dialog\n" : "topmost-password-dialog\n");
            view.TxtPassphrase.Text = value.GetProperty(username ? "username" : "password").GetString();
            Dispatcher.UIThread.Post(submit, DispatcherPriority.Background);
        }
    }
}
