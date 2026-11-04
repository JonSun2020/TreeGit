using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

EnableControlC();
if (args[0] is "worker" or "parent")
{
    if (args[0] == "parent")
    {
        var child = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = false, WindowStyle = ProcessWindowStyle.Hidden };
        child.ArgumentList.Add("worker");child.ArgumentList.Add(args[2]);
        Process.Start(child);
    }
    WriteAtomic(args[1], Environment.ProcessId.ToString());
    await Task.Delay(TimeSpan.FromMinutes(20));
    return;
}
var root = Path.GetFullPath(args[1]);
Process StartWorker(string role)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = false, WindowStyle = ProcessWindowStyle.Hidden };
    start.ArgumentList.Add(role == "target" ? "parent" : "worker");
    start.ArgumentList.Add(Path.Combine(root, role + ".ready"));
    if (role == "target") start.ArgumentList.Add(Path.Combine(root, "descendant.ready"));
    return Process.Start(start)!;
}
using var target = StartWorker("target");
using var sentinel = StartWorker("sentinel");
while (!File.Exists(Path.Combine(root, "target.ready")) || !File.Exists(Path.Combine(root, "sentinel.ready")) || !File.Exists(Path.Combine(root, "descendant.ready"))) await Task.Delay(20);
var attached = new uint[32];
var count = GetConsoleProcessList(attached, (uint)attached.Length);
WriteAtomic(Path.Combine(root, "processes.json"), JsonSerializer.Serialize(new { coordinator = Environment.ProcessId, target = target.Id, sentinel = sentinel.Id, descendant = int.Parse(File.ReadAllText(Path.Combine(root, "descendant.ready"))), consoleProcesses = attached.AsSpan(0, checked((int)count)).ToArray() }));
await Task.Delay(TimeSpan.FromMinutes(20));

static void EnableControlC() => SetConsoleCtrlHandler(IntPtr.Zero, false);
static void WriteAtomic(string path, string value) { File.WriteAllText(path + ".tmp", value); File.Move(path + ".tmp", path, true); }
[DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
[DllImport("kernel32.dll")] static extern uint GetConsoleProcessList([Out] uint[] ids, uint count);
