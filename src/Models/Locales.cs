using System.Collections.Generic;
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace SourceGit.Models
{
    public class Locale
    {
        public string Name { get; set; }
        public string Key { get; set; }

        public static readonly List<Locale> Supported = new List<Locale>() {
            new Locale("简体中文", "zh_CN"),
            new Locale("繁體中文", "zh_TW"),
            new Locale("English", "en_US"),
        };

        public static string Normalize(string key)
            => Supported.Find(locale => string.Equals(locale.Key, key, StringComparison.OrdinalIgnoreCase))?.Key ?? "en_US";

        // Bootstrap errors must respect the saved language before Avalonia and
        // its compiled dictionaries can be initialized.
        public static string BootstrapText(string key, string root = null)
        {
            var language = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? CultureInfo.CurrentUICulture.Name.Contains("TW", StringComparison.OrdinalIgnoreCase) || CultureInfo.CurrentUICulture.Name.Contains("HK", StringComparison.OrdinalIgnoreCase) ? "zh_TW" : "zh_CN"
                : "en_US";
            try
            {
                var directory = Path.Combine(root ?? AppContext.BaseDirectory, "data");
                if (!Directory.Exists(directory))
                {
                    if (OperatingSystem.IsWindows())
                        directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TreeGit");
                    else if (OperatingSystem.IsMacOS())
                        directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SourceGit");
                    else
                    {
                        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
                        var portable = !string.IsNullOrEmpty(appImage) && File.Exists(appImage) ? Path.Combine(Path.GetDirectoryName(appImage)!, "data") : null;
                        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                        directory = portable != null && Directory.Exists(portable) ? portable :
                            Path.Combine(!string.IsNullOrEmpty(config) && Directory.Exists(config) ? config :
                                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "SourceGit");
                    }
                }
                var file = Path.Combine(directory, "preference.json");
                if (File.Exists(file) && new FileInfo(file).Length < 1024 * 1024)
                {
                    using var saved = JsonDocument.Parse(File.ReadAllText(file));
                    if (saved.RootElement.ValueKind == JsonValueKind.Object && saved.RootElement.TryGetProperty("Locale", out var locale) && locale.ValueKind == JsonValueKind.String)
                        language = Normalize(locale.GetString());
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SourceGit.Resources.Locales.Bootstrap.json");
            if (stream == null) return key;
            using var catalog = JsonDocument.Parse(stream);
            return catalog.RootElement.GetProperty(language).TryGetProperty(key, out var text) ? text.GetString() : key;
        }

        public Locale(string name, string key)
        {
            Name = name;
            Key = key;
        }
    }
}
