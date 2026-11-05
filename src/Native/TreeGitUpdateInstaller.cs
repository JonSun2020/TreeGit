using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SourceGit.Native
{
    internal static class TreeGitUpdateInstaller
    {
        public static volatile bool IsApplying;
        public static Models.UpdatePlan BootstrapPlan { get; private set; }
#if TREEGIT_UPDATER_TESTS
        internal static Action<int> AfterReplacement;
#endif

        internal static string JobRoot(Models.UpdatePlan plan) => TreeGitUpdater.SafeChild(plan.InstallRoot, ".treegit-update/jobs/" + plan.Id);
        internal static string PlanPath(Models.UpdatePlan plan) => Path.Combine(JobRoot(plan), "plan.json");
        private static string MarkerPath(string root) => TreeGitUpdater.SafeChild(root, ".treegit-update/active.json");

        internal static void WriteJson(string path, object data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".new";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, data);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }

        internal static void SavePlan(Models.UpdatePlan plan)
        {
            WriteOwner(plan);
            WriteJson(PlanPath(plan), plan);
        }

        internal static void WriteOwner(Models.UpdatePlan plan)
        {
            var owner = Path.Combine(JobRoot(plan), "owner.json");
            if (!File.Exists(owner)) WriteJson(owner, new { product = "TreeGit", id = plan.Id, root = plan.InstallRoot, createdUtc = plan.CreatedUtc });
        }

        internal static Models.UpdatePlan LoadPlan(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 2 * 1024 * 1024) throw new Models.UpdateProblem("invalid");
            var plan = JsonSerializer.Deserialize<Models.UpdatePlan>(ReadText(path));
            if (plan == null || plan.Format != 1 || plan.Product != "TreeGit" || !Guid.TryParseExact(plan.Id, "N", out _) ||
                !TreeGitUpdater.TryVersion("treegit-v" + plan.BeforeVersion, out _) || !TreeGitUpdater.TryVersion("treegit-v" + plan.TargetVersion, out _) ||
                new System.Version(plan.TargetVersion) <= new System.Version(plan.BeforeVersion) || !TreeGitUpdater.IsHash(plan.BeforeExeSha256) ||
                !Path.GetFullPath(path).Equals(PlanPath(plan), StringComparison.OrdinalIgnoreCase) || plan.Files.Count is < 1 or > 6000 ||
                !File.Exists(Path.Combine(plan.InstallRoot, "TreeGit.exe")))
                throw new Models.UpdateProblem("invalid");
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in plan.Files)
            {
                if (!TreeGitUpdater.AllowedFile(file.Path) || !TreeGitUpdater.IsHash(file.Sha256) || (file.BeforeSha256 != null && !TreeGitUpdater.IsHash(file.BeforeSha256)) || !unique.Add(file.Path))
                    throw new Models.UpdateProblem("invalid");
                _ = TreeGitUpdater.SafeChild(plan.InstallRoot, file.Path);
            }
            if (!unique.Contains("TreeGit.exe")) throw new Models.UpdateProblem("invalid");
            using var installation = JsonDocument.Parse(ReadText(Path.Combine(plan.InstallRoot, "build-info.json")));
            if (installation.RootElement.GetProperty("product").GetString() != "TreeGit") throw new Models.UpdateProblem("invalid");
            return plan;
        }

        public static void Launch(Models.UpdatePlan plan, string workerSource = null, bool recover = false, int? parentId = null)
        {
            plan = LoadPlan(PlanPath(plan));
            if (!recover && (plan.State != "Prepared" || TreeGitUpdater.Hash(Path.Combine(plan.InstallRoot, "TreeGit.exe")) != plan.BeforeExeSha256))
                throw new Models.UpdateProblem("changed");
            TreeGitUpdater.EnsureWritable(plan.InstallRoot);
            if (!recover)
                TreeGitUpdater.EnsureSpace(plan.InstallRoot, plan.Files.Sum(file => new FileInfo(TreeGitUpdater.SafeChild(Path.Combine(JobRoot(plan), "stage/TreeGit"), file.Path)).Length) * 2 + 20L * 1024 * 1024);
            var workerRoot = TreeGitUpdater.SafeChild(JobRoot(plan), "worker");
            Directory.CreateDirectory(workerRoot);
            var worker = Path.Combine(workerRoot, "TreeGit.exe");
            if (!File.Exists(worker)) File.Copy(workerSource ?? Environment.ProcessPath!, worker);
            using var parent = parentId.HasValue ? Process.GetProcessById(parentId.Value) : Process.GetCurrentProcess();
            if (parentId.HasValue && !string.Equals(parent.MainModule?.FileName, Path.Combine(plan.InstallRoot, "TreeGit.exe"), StringComparison.OrdinalIgnoreCase))
                throw new Models.UpdateProblem("invalid");
            plan.ParentPid = parent.Id;
            plan.ParentStartTicks = parent.StartTime.ToUniversalTime().Ticks;
            plan.WorkerPid = 0;
            SavePlan(plan);
            WriteJson(MarkerPath(plan.InstallRoot), new { id = plan.Id });
            var start = new ProcessStartInfo(worker) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = workerRoot };
            start.ArgumentList.Add(recover ? "--recover-update" : "--apply-update");
            start.ArgumentList.Add(PlanPath(plan));
            Process process = null;
            try
            {
                process = ProcessLauncher.Start(start) ?? throw new Models.UpdateProblem("start");
                plan.WorkerPid = process.Id;
                SavePlan(plan);
            }
            catch
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit();
                }
                try { File.Delete(MarkerPath(plan.InstallRoot)); } catch (IOException) { }
                throw;
            }
            finally { process?.Dispose(); }
        }

        public static bool HasActiveUpdate(string root)
        {
            try
            {
                if (!File.Exists(Path.Combine(root, ".treegit-update", "active.json"))) return false;
                var path = MarkerPath(root);
                if (!File.Exists(path)) return false;
                using var marker = JsonDocument.Parse(ReadText(path));
                var id = marker.RootElement.GetProperty("id").GetString();
                if (!Guid.TryParseExact(id, "N", out _)) return true;
                var plan = LoadPlan(TreeGitUpdater.SafeChild(root, ".treegit-update/jobs/" + id + "/plan.json"));
                return plan.State is not ("Committed" or "RolledBack") || IsWorkerAlive(plan);
            }
            catch { return true; }
        }

        public static bool HandleBootstrap(string[] args)
        {
            var root = AppContext.BaseDirectory;
            try
            {
                if (args.Length == 2 && args[0] == "--updated" && Guid.TryParseExact(args[1], "N", out _))
                {
                    var plan = LoadPlan(TreeGitUpdater.SafeChild(root, ".treegit-update/jobs/" + args[1] + "/plan.json"));
                    if (plan.State == "Committed" && plan.TargetVersion == Models.TreeGitRelease.Version)
                    {
                        BootstrapPlan = plan;
                        IsApplying = true;
                        return false;
                    }
                }
                if (!File.Exists(Path.Combine(root, ".treegit-update", "active.json"))) return false;
                var marker = MarkerPath(root);
                if (!File.Exists(marker)) return false;
                using var document = JsonDocument.Parse(ReadText(marker));
                var id = document.RootElement.GetProperty("id").GetString();
                if (!Guid.TryParseExact(id, "N", out _)) throw new Models.UpdateProblem("invalid");
                var pending = LoadPlan(TreeGitUpdater.SafeChild(root, ".treegit-update/jobs/" + id + "/plan.json"));
                if (IsWorkerAlive(pending) || ParentIsAlive(pending)) return true;
                if (BeforeFilesMatch(pending))
                {
                    Finish(pending, "RolledBack", "interrupted");
                    return false;
                }
                if (pending.State == "Committed" && pending.TargetVersion == Models.TreeGitRelease.Version && TargetFilesMatch(pending))
                {
                    Finish(pending, "Committed", null);
                    return false;
                }
                if (pending.State == "RolledBack") { File.Delete(marker); return false; }
                Launch(pending, recover: true);
                return true;
            }
            catch
            {
                if (OperatingSystem.IsWindows()) MessageBox(IntPtr.Zero, "更新尚未完成。请重新打开 TreeGit；如仍无法打开，可从项目主页下载软件包解压到新文件夹。原有配置和仓库不会被删除。", "TreeGit", 0x10);
                return true;
            }
        }

        public static int Run(string path, bool recover)
        {
            Models.UpdatePlan plan = null;
            try
            {
                plan = LoadPlan(path);
                FileStream lockFile;
                try { lockFile = new FileStream(Path.Combine(JobRoot(plan), "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { return 3; }
                using var jobLock = lockFile;
                WaitForParent(plan);
                plan = LoadPlan(path);
                plan.WorkerPid = Environment.ProcessId;
                SavePlan(plan);
                if (recover)
                {
                    Restore(plan);
                    Finish(plan, "RolledBack", "interrupted");
                    Restart(plan, false);
                    return 1;
                }
                Apply(plan);
                plan.State = "Committed";
                SavePlan(plan);
                using var launched = Restart(plan, true);
                if (!WaitForReady(plan, launched))
                {
                    // This is the just-started, non-interactive update bootstrap, never an existing user process.
                    if (!launched.HasExited)
                    {
                        _ = launched.CloseMainWindow();
                        if (!launched.WaitForExit(5000)) { launched.Kill(entireProcessTree: true); launched.WaitForExit(); }
                    }
                    throw new Models.UpdateProblem("start");
                }
                Finish(plan, "Committed", null);
                return 0;
            }
            catch (Exception error)
            {
                if (plan == null) return 2;
                try
                {
                    Restore(plan);
                    Finish(plan, "RolledBack", ProblemCode(error));
                    Restart(plan, false)?.Dispose();
                }
                catch (Exception rollback)
                {
                    plan.State = "NeedsRecovery";
                    plan.Error = ProblemCode(rollback);
                    SavePlan(plan);
                    if (OperatingSystem.IsWindows()) MessageBox(IntPtr.Zero, "更新未完成，旧版文件已保留。请先关闭其他 TreeGit 窗口或相关终端，再重新打开 TreeGit，程序将继续恢复。", "TreeGit", 0x10);
                    return 2;
                }
                return 1;
            }
        }

        public static void FinishOrphanedBootstrap()
        {
            if (BootstrapPlan is { } plan && !IsWorkerAlive(plan) && TargetFilesMatch(plan))
                Finish(plan, "Committed", null);
        }

        private static bool TargetFilesMatch(Models.UpdatePlan plan)
        {
            try { return plan.Files.All(file => TreeGitUpdater.Hash(TreeGitUpdater.SafeChild(plan.InstallRoot, file.Path)) == file.Sha256); }
            catch { return false; }
        }

        private static bool BeforeFilesMatch(Models.UpdatePlan plan)
        {
            try
            {
                return plan.Files.All(file => file.BeforeSha256 == null
                    ? !File.Exists(TreeGitUpdater.SafeChild(plan.InstallRoot, file.Path))
                    : TreeGitUpdater.Hash(TreeGitUpdater.SafeChild(plan.InstallRoot, file.Path)) == file.BeforeSha256);
            }
            catch { return false; }
        }

        internal static void Apply(Models.UpdatePlan plan)
        {
            if (plan.State != "Prepared" || TreeGitUpdater.Hash(Path.Combine(plan.InstallRoot, "TreeGit.exe")) != plan.BeforeExeSha256)
                throw new Models.UpdateProblem("changed");
            var job = JobRoot(plan);
            var handles = new List<SafeFileHandle>();
            var temporaryHandles = new Dictionary<string, SafeFileHandle>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // Prepare and lock every destination before changing any program file.
                foreach (var file in plan.Files)
                {
                    var source = TreeGitUpdater.SafeChild(Path.Combine(job, "stage/TreeGit"), file.Path);
                    var target = TreeGitUpdater.SafeChild(plan.InstallRoot, file.Path);
                    if (!File.Exists(source) || TreeGitUpdater.Hash(source) != file.Sha256) throw new Models.UpdateProblem("damaged");
                    if (file.BeforeSha256 != null)
                    {
                        if (!File.Exists(target) || TreeGitUpdater.Hash(target) != file.BeforeSha256) throw new Models.UpdateProblem("changed");
                        handles.Add(LockForReplacement(target));
                    }
                    else if (File.Exists(target)) throw new Models.UpdateProblem("changed");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var temporary = TreeGitUpdater.SafeChild(plan.InstallRoot, file.Path + ".treegit-" + plan.Id + ".new");
                    File.Copy(source, temporary, true);
                    var temporaryHandle = LockForReplacement(temporary);
                    handles.Add(temporaryHandle);
                    temporaryHandles.Add(file.Path, temporaryHandle);
                }
                plan.State = "Applying";
                SavePlan(plan);
#if TREEGIT_UPDATER_TESTS
                var replaced = 0;
#endif
                foreach (var file in plan.Files)
                {
                    var target = TreeGitUpdater.SafeChild(plan.InstallRoot, file.Path);
                    var temporary = TreeGitUpdater.SafeChild(plan.InstallRoot, file.Path + ".treegit-" + plan.Id + ".new");
                    var backup = TreeGitUpdater.SafeChild(Path.Combine(job, "backup"), file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    // ReplaceFile needs exclusive access to the replacement, while the originals stay protected.
                    temporaryHandles[file.Path].Dispose();
                    ReplaceWithRetry(() =>
                    {
                        if (file.BeforeSha256 != null) File.Replace(temporary, target, backup, true);
                        else File.Move(temporary, target);
                    });
                    handles.Add(LockForReplacement(target));
#if TREEGIT_UPDATER_TESTS
                    AfterReplacement?.Invoke(++replaced);
#endif
                }
                foreach (var file in plan.Files)
                    if (TreeGitUpdater.Hash(TreeGitUpdater.SafeChild(plan.InstallRoot, file.Path)) != file.Sha256) throw new Models.UpdateProblem("damaged");
            }
            finally { foreach (var handle in handles) handle.Dispose(); }
        }

        internal static void Restore(Models.UpdatePlan plan)
        {
            if (plan.State == "Prepared")
            {
                foreach (var file in plan.Files)
                {
                    var temporary = TreeGitUpdater.SafeChild(plan.InstallRoot, file.Path + ".treegit-" + plan.Id + ".new");
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                return;
            }
            foreach (var file in plan.Files.AsEnumerable().Reverse())
            {
                var target = TreeGitUpdater.SafeChild(plan.InstallRoot, file.Path);
                var backup = TreeGitUpdater.SafeChild(Path.Combine(JobRoot(plan), "backup"), file.Path);
                if (file.BeforeSha256 != null)
                {
                    if (File.Exists(backup))
                    {
                        if (TreeGitUpdater.Hash(backup) != file.BeforeSha256) throw new Models.UpdateProblem("damaged");
                        var replacement = target + ".treegit-" + plan.Id + ".restore";
                        File.Copy(backup, replacement, true);
                        if (File.Exists(target)) File.Replace(replacement, target, null, true);
                        else File.Move(replacement, target);
                    }
                    if (!File.Exists(target) || TreeGitUpdater.Hash(target) != file.BeforeSha256) throw new Models.UpdateProblem("changed");
                }
                else if (File.Exists(target))
                {
                    if (TreeGitUpdater.Hash(target) != file.Sha256) throw new Models.UpdateProblem("changed");
                    File.Delete(target);
                }
                var temporary = TreeGitUpdater.SafeChild(plan.InstallRoot, file.Path + ".treegit-" + plan.Id + ".new");
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static SafeFileHandle LockForReplacement(string path)
        {
            for (var attempt = 0; ; attempt++)
            {
                var handle = CreateFile(path, 0xC0010000, 7, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
                if (!handle.IsInvalid) return handle;
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                if (attempt < 30 && error is 32 or 33) { Thread.Sleep(100); continue; }
                throw new Models.UpdateProblem(error == 5 ? "permission" : "locked", new Win32Exception(error));
            }
        }

        private static void ReplaceWithRetry(Action replace)
        {
            for (var attempt = 0; ; attempt++)
            {
                try { replace(); return; }
                catch (IOException error) when (attempt < 20 && (error.HResult & 0xFFFF) is 32 or 33 or 1175)
                {
                    Thread.Sleep(100);
                }
            }
        }

        private static bool ParentIsAlive(Models.UpdatePlan plan)
        {
            try { using var process = Process.GetProcessById(plan.ParentPid); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == plan.ParentStartTicks; }
            catch { return false; }
        }

        private static bool IsWorkerAlive(Models.UpdatePlan plan)
        {
            try
            {
                using var process = Process.GetProcessById(plan.WorkerPid);
                return !process.HasExited && string.Equals(process.MainModule?.FileName, Path.GetFullPath(Path.Combine(JobRoot(plan), "worker", "TreeGit.exe")), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void WaitForParent(Models.UpdatePlan plan)
        {
            try
            {
                using var parent = Process.GetProcessById(plan.ParentPid);
                if (parent.StartTime.ToUniversalTime().Ticks == plan.ParentStartTicks && !parent.WaitForExit(120000)) throw new Models.UpdateProblem("busy");
            }
            catch (ArgumentException) { }
        }

        private static Process Restart(Models.UpdatePlan plan, bool updated)
        {
            var start = new ProcessStartInfo(Path.Combine(plan.InstallRoot, "TreeGit.exe")) { UseShellExecute = false, WorkingDirectory = plan.InstallRoot };
            if (updated) { start.ArgumentList.Add("--updated"); start.ArgumentList.Add(plan.Id); }
            return ProcessLauncher.Start(start) ?? throw new Models.UpdateProblem("start");
        }

        private static bool WaitForReady(Models.UpdatePlan plan, Process process)
        {
            var root = Path.GetFullPath(plan.InstallRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var name = "TreeGit.MCP." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant() + "\n" + Environment.UserName)))[..24];
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(60) && !process.HasExited)
            {
                try
                {
                    using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    pipe.Connect(500);
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                    writer.WriteLine("{\"name\":\"ui_update_ping\",\"arguments\":{}}");
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var line = reader.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
                    using var document = JsonDocument.Parse(line!);
                    if (document.RootElement.GetProperty("version").GetString() == plan.TargetVersion && document.RootElement.GetProperty("processId").GetInt32() == process.Id) return true;
                }
                catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException) { }
                Thread.Sleep(200);
            }
            return false;
        }

        private static void Finish(Models.UpdatePlan plan, string state, string error)
        {
            plan.State = state;
            plan.Error = error;
            // Notification bookkeeping must never turn a healthy or restored program into a failed installation.
            try { SavePlan(plan); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { WriteJson(TreeGitUpdater.SafeChild(plan.InstallRoot, ".treegit-update/result.json"), new { id = plan.Id, state, version = state == "Committed" ? plan.TargetVersion : plan.BeforeVersion, error }); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { File.Delete(MarkerPath(plan.InstallRoot)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static string ReadText(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 2 * 1024 * 1024) throw new Models.UpdateProblem("invalid");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        public static (string State, string Version, string Error)? TakeResult(string root)
        {
            try
            {
                var file = TreeGitUpdater.SafeChild(root, ".treegit-update/result.json");
                if (!File.Exists(file)) return null;
                using var document = JsonDocument.Parse(ReadText(file));
                var data = document.RootElement;
                var state = data.GetProperty("state").GetString();
                var version = data.GetProperty("version").GetString();
                if (state is not ("Committed" or "RolledBack") || !TreeGitUpdater.TryVersion("treegit-v" + version, out _)) return null;
                var error = data.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                File.Move(file, TreeGitUpdater.SafeChild(root, ".treegit-update/last-result.json"), true);
                return (state, version, error);
            }
            catch { return null; }
        }

        internal static void DeleteOwnedJob(Models.UpdatePlan plan)
        {
            var job = JobRoot(plan);
            if (!OwnsJob(plan.InstallRoot, job)) throw new Models.UpdateProblem("invalid");
            DeleteDirectoryChecked(job, job);
        }

        private static bool OwnsJob(string root, string folder)
        {
            try
            {
                var id = Path.GetFileName(folder);
                if (!Guid.TryParseExact(id, "N", out _) || !folder.Equals(TreeGitUpdater.SafeChild(root, ".treegit-update/jobs/" + id), StringComparison.OrdinalIgnoreCase)) return false;
                using var owner = JsonDocument.Parse(ReadText(TreeGitUpdater.SafeChild(folder, "owner.json")));
                return owner.RootElement.GetProperty("product").GetString() == "TreeGit" && owner.RootElement.GetProperty("id").GetString() == id &&
                    string.Equals(Path.GetFullPath(owner.RootElement.GetProperty("root").GetString()!).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void DeleteDirectoryChecked(string ownerRoot, string folder)
        {
            if (!Directory.Exists(folder)) return;
            if (!Path.GetFullPath(folder).Equals(Path.GetFullPath(ownerRoot), StringComparison.OrdinalIgnoreCase))
                _ = TreeGitUpdater.SafeChild(ownerRoot, Path.GetRelativePath(ownerRoot, folder));
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new Models.UpdateProblem("invalid");
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories))
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new Models.UpdateProblem("invalid");
            Directory.Delete(folder, true);
        }

        public static void Cleanup(string root)
        {
            try
            {
                if (HasActiveUpdate(root)) return;
                var jobs = TreeGitUpdater.SafeChild(root, ".treegit-update/jobs");
                if (!Directory.Exists(jobs)) return;
                var completed = new List<Models.UpdatePlan>();
                foreach (var folder in Directory.EnumerateDirectories(jobs))
                {
                    if (!OwnsJob(root, folder)) continue;
                    Models.UpdatePlan plan;
                    try { plan = LoadPlan(Path.Combine(folder, "plan.json")); }
                    catch
                    {
                        if (Directory.GetLastWriteTimeUtc(folder) < DateTime.UtcNow.AddDays(-1)) DeleteDirectoryChecked(folder, folder);
                        continue;
                    }
                    if (IsWorkerAlive(plan)) continue;
                    if (plan.State is "Committed" or "RolledBack") completed.Add(plan);
                    else if (plan.State == "Prepared" && plan.CreatedUtc < DateTime.UtcNow.AddDays(-7)) DeleteOwnedJob(plan);
                }
                var keep = completed.Where(plan => plan.State == "Committed").OrderByDescending(plan => plan.CreatedUtc).FirstOrDefault()
                    ?? completed.OrderByDescending(plan => plan.CreatedUtc).FirstOrDefault();
                foreach (var plan in completed)
                {
                    if (plan == keep)
                    {
                        DeleteDirectoryChecked(JobRoot(plan), Path.Combine(JobRoot(plan), "stage"));
                        DeleteDirectoryChecked(JobRoot(plan), Path.Combine(JobRoot(plan), "worker"));
                    }
                    else DeleteOwnedJob(plan);
                }
                var downloads = TreeGitUpdater.SafeChild(root, ".treegit-update/downloads");
                var current = new System.Version(TreeGitUpdater.ReadInstalledVersion(root));
                if (Directory.Exists(downloads))
                    foreach (var file in Directory.EnumerateFiles(downloads))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(file), @"^([0-9]+\.[0-9]+\.[0-9]+)-[a-f0-9]{12}\.zip(?:\.partial)?\z");
                        if (match.Success && System.Version.TryParse(match.Groups[1].Value, out var version) && version <= current)
                            File.Delete(TreeGitUpdater.SafeChild(downloads, Path.GetFileName(file)));
                    }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Models.UpdateProblem or JsonException or InvalidOperationException) { }
        }

        internal static string ProblemCode(Exception error) => error switch
        {
            Models.UpdateProblem problem => problem.Code,
            UnauthorizedAccessException => "permission",
            System.Net.Http.HttpRequestException => "network",
            OperationCanceledException => "interrupted",
            InvalidDataException or JsonException => "damaged",
            IOException => "locked",
            _ => "failed",
        };

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint attributes, IntPtr template);

        [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
    }
}
