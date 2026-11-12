using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using SourceGit.Automation;

namespace SourceGit.Tests
{
    internal static class UpdateUiProbe
    {
        public static void Attach(IClassicDesktopStyleApplicationLifetime desktop, string archive, string output)
        {
            Directory.CreateDirectory(output);
            desktop.MainWindow = new Window { Width = 900, Height = 650, Title = "TreeGit update UI verification" };
            desktop.MainWindow.Opened += async (_, _) =>
            {
                var checks = new List<string>();
                string failure = null;
                void Check(string label, bool pass) { if (!pass) throw new Exception(label); checks.Add(label); }
                var root = Path.Combine(output, "installation");
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "TreeGit.exe"), "unchanged old program");
                File.WriteAllText(Path.Combine(root, "build-info.json"), "{\"product\":\"TreeGit\",\"version\":\"2026.23.10\",\"integrationTests\":false}");
                var server = new Server(archive);
                ViewModels.SelfUpdate NewModel(string current = "2026.23.10") => new(root, current, () => new Native.TreeGitUpdater(root, new HttpClient(server, false)));
                async Task Capture(Window window, string name)
                {
                    await Task.Delay(120);
                    var scale = window.RenderScaling;
                    using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width * scale), (int)Math.Ceiling(window.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(output, name + ".png"));
                    foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("flat") && button.IsEffectivelyVisible))
                    {
                        var point = button.TranslatePoint(default, window);
                        var end = button.TranslatePoint(new Point(button.Bounds.Width, button.Bounds.Height), window);
                        Check(name + " visible action stays within the window", point.HasValue && end.HasValue && point.Value.X >= 0 && point.Value.Y >= 0 && end.Value.X <= window.Bounds.Width + 1 && end.Value.Y <= window.Bounds.Height + 1);
                    }
                    foreach (var text in window.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible && window.DataContext is ViewModels.SelfUpdate vm && (text.Text == vm.CurrentVersion || text.Text == vm.NewVersion) && !string.IsNullOrEmpty(text.Text)))
                    {
                        var point = text.TranslatePoint(default, window);
                        var end = text.TranslatePoint(new Point(text.Bounds.Width, text.Bounds.Height), window);
                        Check(name + " version label is not horizontally clipped", point.HasValue && end.HasValue && point.Value.X >= 0 && end.Value.X <= window.Bounds.Width + 1);
                    }
                }
                try
                {
                    var model = NewModel();
                    var window = new Views.SelfUpdate { DataContext = model };
                    window.Present(desktop.MainWindow);
                    await model.CheckAsync();
                    Check("a newer release shows a clear download action (state=" + model.State + ", message=" + model.Message + ")", model.State == "available" && model.CanAct && model.ActionText == App.Text("SelfUpdate.Flow.Download"));
                    await Capture(window, "available");
                    server.Slow = true;
                    var download = model.ActAsync();
                    await Task.Delay(350);
                    Check("download progress is shown without blocking the window", model.State == "download" && model.Progress > 0 && model.CanDismiss);
                    model.Dismiss();
                    await download;
                    Check("cancel keeps a resumable download", model.State == "cancelled" && Directory.EnumerateFiles(Path.Combine(root, ".treegit-update/downloads"), "*.partial").Any());
                    await Capture(window, "cancelled");
                    server.Slow = false;
                    await model.ActAsync();
                    Check("resuming a download prepares the real verified package", model.State == "ready" && server.Offset > 0);
                    Check("download does not replace files before restart is requested", File.ReadAllText(Path.Combine(root, "TreeGit.exe")) == "unchanged old program");
                    await Capture(window, "ready");
                    window.Close();
                    model = NewModel(); window = new Views.SelfUpdate { DataContext = model };
                    window.Present(desktop.MainWindow);
                    await model.CheckAsync();
                    Check("a downloaded update is ready again after reopening the window", model.State == "ready" && model.CanRestart);
                    Check("maintenance lock acquired for busy-case verification", Automation.GuiBridge.TryBeginUpdate());
                    try { await model.ActAsync(); }
                    finally { Automation.GuiBridge.EndUpdate(); }
                    Check("an active operation keeps the app open with useful guidance", model.State == "ready" && model.Message == App.Text("SelfUpdate.Flow.BusyHint"));
                    var pendingSettings = new Window { Width = 400, Height = 250 }; pendingSettings.Show(window);
                    await model.ActAsync();
                    Check("a pending settings window prevents restart without discarding its input", model.State == "ready" && !model.Restarting && pendingSettings.IsVisible);
                    pendingSettings.Close();
                    await Capture(window, "busy");
                    ViewModels.Preferences.Instance.Zoom = 2;
                    await Capture(window, "ready-zoom2");
                    ViewModels.Preferences.Instance.Zoom = 1;
                    var preferences = ViewModels.Preferences.Instance;
                    preferences.SetCanModify(); preferences.Save();
                    var preferencePath = Path.Combine(Native.OS.BasicDirectories.ConfigDir, "preference.json");
                    using (var heldPreference = new FileStream(preferencePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        await model.ActAsync();
                        Check("an unsavable preference keeps the program open before any update worker starts", model.State == "failed" && model.Message == App.Text("SelfUpdate.Flow.Error.settings") && model.CanRestart && !model.Restarting && !File.Exists(Path.Combine(root, ".treegit-update/active.json")));
                        Check("failed save releases update maintenance state", !Native.TreeGitUpdateInstaller.IsApplying && Automation.GuiBridge.TryBeginUpdate());
                        Automation.GuiBridge.EndUpdate();
                        await Capture(window, "settings-locked");
                    }
                    var stateRoot = Path.Combine(output, "repository-state"); Directory.CreateDirectory(stateRoot);
                    var draftState = Models.RepositoryUIStates.Load(stateRoot);
                    draftState.LastCommitMessage = "Keep the pending commit draft";
                    draftState.Save(requireSuccess: true);
                    using (var heldDraft = new FileStream(Path.Combine(stateRoot, "sourcegit.uistates"), FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        var rejected = false;
                        try { draftState.Save(requireSuccess: true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rejected = true; }
                        Check("restart cannot silently ignore a locked draft file", rejected);
                    }
                    draftState.Save(requireSuccess: true);
                    Check("the draft can be saved again when the file is available", Models.RepositoryUIStates.Load(stateRoot).LastCommitMessage == "Keep the pending commit draft");
                    window.Close();

                    server.Offline = true;
                    model = NewModel(); window = new Views.SelfUpdate { DataContext = model };
                    window.Present(desktop.MainWindow);
                    await model.CheckAsync();
                    Check("offline checking offers a retry and manual download", model.State == "failed" && model.CanAct && model.ShowManualDownload);
                    Check("the error contains no stack trace URL or checksum", !model.Message.Contains("https:") && !model.Message.Contains("sha256", StringComparison.OrdinalIgnoreCase) && !model.Message.Contains("Exception"));
                    await Capture(window, "offline"); window.Close();

                    server.Offline = false;
                    model = NewModel(server.Version); window = new Views.SelfUpdate { DataContext = model };
                    window.Present(desktop.MainWindow);
                    await model.CheckAsync();
                    Check("up-to-date has no misleading download or older-version label", model.State == "current" && !model.CanAct && model.NewVersion == string.Empty);
                    await Capture(window, "current"); window.Close();

                    async Task Until(Func<bool> predicate)
                    {
                        for (var i = 0; i < 300; i++) { if (predicate()) return; await Task.Delay(50); }
                        throw new Exception("Background update UI did not reach its expected state.");
                    }
                    void ResetRoot()
                    {
                        if (Directory.Exists(root)) Directory.Delete(root, true);
                        Directory.CreateDirectory(root);
                        File.WriteAllText(Path.Combine(root, "TreeGit.exe"), "unchanged old program");
                        File.WriteAllText(Path.Combine(root, "build-info.json"), "{\"product\":\"TreeGit\",\"version\":\"2026.23.10\",\"integrationTests\":false}");
                        server.Slow = true; server.FailDownload = false; server.Offline = false;
                    }
                    Views.UpdateCloseChoice Choice() => desktop.Windows.OfType<Views.UpdateCloseChoice>().FirstOrDefault(w => w.IsVisible);

                    ResetRoot(); model = NewModel(); window = new Views.SelfUpdate { DataContext = model }; window.Present(desktop.MainWindow);
                    await model.CheckAsync(); await Capture(window, "background-available");
                    UiLayoutProbe.ClickButton(window, "BackgroundUpdate");
                    await Until(() => model.State == "download" && model.Progress > 0 && !window.IsVisible);
                    Check("the actual background button hides the modal and enables the main window", model.IsBackground && desktop.MainWindow.IsEnabled);
                    var requests = server.RequestCount;
                    window.Present(desktop.MainWindow); await Task.Delay(100);
                    Check("reopening uses the same live download without another request", window.IsVisible && !model.IsBackground && model.Progress > 0 && server.RequestCount == requests);
                    UiLayoutProbe.ClickButton(window, "BackgroundUpdate"); await Until(() => !window.IsVisible);
                    server.Slow = false;
                    await Until(() => model.State == "ready" && window.IsVisible);
                    Check("background success returns one ready window without automatic installation", !model.IsBackground && model.CanRestart && !model.Restarting && File.ReadAllText(Path.Combine(root, "TreeGit.exe")) == "unchanged old program");
                    await Capture(window, "background-ready"); window.Close();

                    ResetRoot(); model = NewModel(); window = new Views.SelfUpdate { DataContext = model }; window.Present(desktop.MainWindow); await model.CheckAsync();
                    UiLayoutProbe.ClickButton(window, "BackgroundUpdate");
                    await Until(() => model.State == "download" && model.Progress > 0 && !window.IsVisible);
                    server.FailDownload = true;
                    await Until(() => model.State == "failed" && window.IsVisible);
                    Check("a background transfer failure returns a readable resumable window", !model.IsBackground && model.CanDownload && !model.Message.Contains("Exception"));
                    await Capture(window, "background-failed");
                    server.FailDownload = false; server.Slow = false;
                    UiLayoutProbe.ClickButton(window, "BackgroundUpdate");
                    await Until(() => model.State == "ready" && window.IsVisible);
                    Check("retrying a background failure resumes the partial file", server.Offset > 0 && model.CanRestart);
                    window.Close();

                    ResetRoot(); model = NewModel(); window = new Views.SelfUpdate { DataContext = model }; window.Present(desktop.MainWindow); await model.CheckAsync();
                    var closeDownload = model.ActAsync(); await Until(() => model.Progress > 0);
                    window.Close(); await Until(() => Choice() != null);
                    Check("X asks with explicit cancel-update and background-update choices", window.IsVisible && model.State == "download");
                    foreach (var zoom in new[] { 1.0, 1.5, 2.5 })
                    {
                        ViewModels.Preferences.Instance.Zoom = zoom; await Capture(Choice(), "update-close-choice-" + zoom);
                    }
                    ViewModels.Preferences.Instance.Zoom = 1;
                    UiLayoutProbe.ClickButton(Choice(), "ContinueBackgroundUpdate");
                    await Until(() => !window.IsVisible && Choice() == null);
                    Check("the X background choice releases the main window without cancelling", model.IsBackground && desktop.MainWindow.IsEnabled);
                    server.Slow = false; await closeDownload;
                    await Until(() => window.IsVisible && model.State == "ready"); window.Close();

                    ResetRoot(); model = NewModel(); window = new Views.SelfUpdate { DataContext = model }; window.Present(desktop.MainWindow); await model.CheckAsync();
                    var cancelDownload = model.ActAsync(); await Until(() => model.Progress > 0);
                    window.Close(); await Until(() => Choice() != null);
                    UiLayoutProbe.ClickButton(Choice(), "CancelUpdate"); await cancelDownload; await Task.Delay(150);
                    Check("the X cancel choice closes the update and never reopens on cancellation", !window.IsVisible && !model.IsBackground && desktop.MainWindow.IsEnabled);
                    Check("X cancellation preserves the resumable partial file", Directory.EnumerateFiles(Path.Combine(root, ".treegit-update/downloads"), "*.partial").Any());

                    ResetRoot(); model = NewModel(); window = new Views.SelfUpdate { DataContext = model }; window.Present(desktop.MainWindow); await model.CheckAsync();
                    var finishDownload = model.ActAsync(); await Until(() => model.Progress > 0);
                    window.Close(); await Until(() => Choice() != null);
                    Choice().Close(); await Until(() => Choice() == null); await Task.Delay(100);
                    Check("closing the choice itself keeps the foreground download running", window.IsVisible && model.State == "download" && !model.IsBackground);
                    window.Close(); await Until(() => Choice() != null);
                    server.Slow = false; await finishDownload; await Until(() => Choice() == null);
                    Check("completion during a close choice dismisses that obsolete choice", window.IsVisible && model.State == "ready" && !model.Restarting); window.Close();

                    ResetRoot(); model = NewModel(); window = new Views.SelfUpdate { DataContext = model }; window.Present(desktop.MainWindow); await model.CheckAsync();
                    var aiDownload = model.ActAsync(); await Until(() => model.Progress > 0);
                    window.Close(); await Until(() => Choice() != null);
                    await model.RunInBackgroundAsync(); await Task.Delay(100);
                    Check("a direct background action closes the outstanding X choice instead of hiding it", !window.IsVisible && !desktop.Windows.OfType<Views.UpdateCloseChoice>().Any());
                    server.Slow = false; await aiDownload; await Until(() => window.IsVisible && model.State == "ready"); window.Close();

                    ResetRoot();
                    var temporaryOwner = new Window { Width = 800, Height = 600 }; temporaryOwner.Show(desktop.MainWindow);
                    model = NewModel(); window = new Views.SelfUpdate { DataContext = model }; window.Present(temporaryOwner); await model.CheckAsync();
                    var ownerDownload = model.RunInBackgroundAsync(); await Until(() => model.Progress > 0 && !window.IsVisible);
                    temporaryOwner.Close(); await ownerDownload; await Task.Delay(100);
                    Check("closing the application owner cancels a hidden update without reopening it", !window.IsVisible && !model.IsBackground && desktop.MainWindow.IsVisible);
                }
                catch (Exception error) { failure = error.ToString(); }
                finally
                {
                    File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { passed = checks.Count, checks, failure }));
                    desktop.Shutdown(failure == null ? 0 : 1);
                }
            };
        }

        private sealed class Server : HttpMessageHandler
        {
            private readonly string _archive;
            private readonly Models.Version _release;
            public bool Offline { get; set; }
            public bool Slow { get; set; }
            public bool FailDownload { get; set; }
            public int RequestCount { get; private set; }
            public long? Offset { get; private set; }
            public string Version { get; }

            public Server(string archive)
            {
                _archive = archive;
                using var zip = System.IO.Compression.ZipFile.OpenRead(archive);
                using var info = JsonDocument.Parse(zip.GetEntry("TreeGit/build-info.json")!.Open());
                Version = info.RootElement.GetProperty("version").GetString();
                _release = new Models.Version { TagName = "treegit-v" + Version, Body = "更快打开仓库\n可选择本机字体\n内置二进制对比，无需外部工具", Assets = [new() { Name = "TreeGit-" + Version + "-Windows-x64.zip", DownloadUrl = "https://github.com/JonSun2020/TreeGit/releases/download/treegit-v" + Version + "/TreeGit-" + Version + "-Windows-x64.zip", Size = new FileInfo(archive).Length, Digest = "sha256:" + Native.TreeGitUpdater.Hash(archive) }] };
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                RequestCount++;
                if (Offline) throw new HttpRequestException("Test-only detailed error must never be shown to the user.");
                if (request.RequestUri!.AbsoluteUri == Native.TreeGitUpdater.LatestUrl)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(_release, JsonCodeGen.Default.Version), Encoding.UTF8, "application/json") });
                var file = File.OpenRead(_archive);
                Offset = request.Headers.Range?.Ranges.Single().From;
                if (Offset.HasValue) file.Position = Offset.Value;
                var content = new StreamContent(new DownloadStream(file, this));
                content.Headers.ContentLength = file.Length - file.Position;
                if (Offset.HasValue) content.Headers.ContentRange = new ContentRangeHeaderValue(Offset.Value, file.Length - 1, file.Length);
                return Task.FromResult(new HttpResponseMessage(Offset.HasValue ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content });
            }
        }

        private sealed class DownloadStream(FileStream file, Server server) : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => file.Length;
            public override long Position { get => file.Position; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => file.Read(buffer, offset, count);
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            {
                if (server.Slow) await Task.Delay(15, token);
                if (server.FailDownload) throw new HttpRequestException("Test-only interrupted download.");
                return await file.ReadAsync(buffer[..Math.Min(buffer.Length, server.Slow ? 16384 : buffer.Length)], token);
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) file.Dispose(); base.Dispose(disposing); }
        }
    }
}
