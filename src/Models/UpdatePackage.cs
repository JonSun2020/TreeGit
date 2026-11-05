using System;
using System.Collections.Generic;

namespace SourceGit.Models
{
    public sealed class UpdateProblem(string code, Exception inner = null, long requiredBytes = 0) : Exception(code, inner)
    {
        public string Code { get; } = code;
        public long RequiredBytes { get; } = requiredBytes;
    }

    public sealed record UpdateProgress(string Phase, long Completed = 0, long Total = 0);

    public sealed class UpdateFile
    {
        public string Path { get; set; }
        public string Sha256 { get; set; }
        public string BeforeSha256 { get; set; }
    }

    public sealed class UpdatePlan
    {
        public int Format { get; set; } = 1;
        public string Product { get; set; } = "TreeGit";
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string InstallRoot { get; set; }
        public string BeforeVersion { get; set; }
        public string TargetVersion { get; set; }
        public string BeforeExeSha256 { get; set; }
        public string PackageSha256 { get; set; }
        public int ParentPid { get; set; }
        public long ParentStartTicks { get; set; }
        public int WorkerPid { get; set; }
        public string State { get; set; } = "Prepared";
        public string Error { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public List<UpdateFile> Files { get; set; } = [];
    }
}
