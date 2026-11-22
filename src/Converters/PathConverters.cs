using System.IO;
using Avalonia.Data.Converters;

namespace SourceGit.Converters
{
    public static class PathConverters
    {
        public static readonly FuncValueConverter<string, string> Local =
            new(Native.OS.FormatLocalPath);

        public static readonly FuncValueConverter<string, string> RemoteAddress =
            new(Native.OS.FormatRemoteAddress);

        public static readonly FuncValueConverter<string, string> PureFileName =
            new(v => Path.GetFileName(v) ?? "");

        public static readonly FuncValueConverter<string, string> PureDirectoryName =
            new(v => Native.OS.FormatLocalPath(Path.GetDirectoryName(v)) ?? "");

        public static readonly FuncValueConverter<string, string> RelativeToHome =
            new(Native.OS.GetRelativePathToHome);
    }
}
