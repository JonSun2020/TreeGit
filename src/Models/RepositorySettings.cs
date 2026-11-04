using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Avalonia.Collections;

namespace SourceGit.Models
{
    public class RepositorySettings
    {
        public bool AutoRefreshEnabled { get; set; } = true;
        public bool BackgroundFetchEnabled { get; set; } = true;

        public string DefaultRemote
        {
            get;
            set;
        } = string.Empty;

        public int PreferredMergeMode
        {
            get;
            set;
        } = 0;

        public string ConventionalTypesOverride
        {
            get;
            set;
        } = string.Empty;

        public bool EnableRecursiveWhenAutoUpdatingSubmodules
        {
            get;
            set;
        } = true;

        public bool AskBeforeAutoUpdatingSubmodules
        {
            get;
            set;
        } = false;

        public string PreferredOpenAIService
        {
            get;
            set;
        } = "---";

        public AvaloniaList<CommitTemplate> CommitTemplates
        {
            get;
            set;
        } = [];

        public AvaloniaList<CustomAction> CustomActions
        {
            get;
            set;
        } = [];

        internal string StoragePath => _file;

        internal RepositorySettings Copy() => JsonSerializer.Deserialize(JsonSerializer.Serialize(this, JsonCodeGen.Default.RepositorySettings), JsonCodeGen.Default.RepositorySettings);
        internal string Serialize() => JsonSerializer.Serialize(this, JsonCodeGen.Default.RepositorySettings);
        internal void MarkSaved(string content) => _orgHash = HashContent(content);

        public static RepositorySettings Get(string gitCommonDir)
        {
            var fileInfo = new FileInfo(Path.Combine(gitCommonDir, "sourcegit.settings"));
            var fullpath = fileInfo.FullName;
            if (_cache.TryGetValue(fullpath, out var setting))
                return setting;

            if (!File.Exists(fullpath))
            {
                setting = new();
            }
            else
            {
                try
                {
                    using var stream = File.OpenRead(fullpath);
                    setting = JsonSerializer.Deserialize(stream, JsonCodeGen.Default.RepositorySettings);
                }
                catch
                {
                    setting = new();
                }
            }

            // Hash the loaded value before exposing the mutable settings object.
            setting._orgHash = HashContent(setting.Serialize());

            setting._file = fullpath;
            _cache.Add(fullpath, setting);
            return setting;
        }

        public void Save()
        {
            try
            {
                SaveChecked();
            }
            catch
            {
                // Ignore save errors
            }
        }

        public void SaveChecked()
        {
            var content = JsonSerializer.Serialize(this, JsonCodeGen.Default.RepositorySettings);
            var hash = HashContent(content);
            if (hash.Equals(_orgHash, StringComparison.Ordinal)) return;
            using var reservation = new FileStream(_file + ".lock", FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1, FileOptions.DeleteOnClose);
            var tmpfile = $"{_file}.tmp";
            File.WriteAllText(tmpfile, content);
            File.Move(tmpfile, _file, true);
            _orgHash = hash;
        }

        public CustomAction AddNewCustomAction()
        {
            var act = new CustomAction() { Name = "Unnamed Action" };
            CustomActions.Add(act);
            return act;
        }

        public void RemoveCustomAction(CustomAction act)
        {
            if (act != null)
                CustomActions.Remove(act);
        }

        public void MoveCustomActionUp(CustomAction act)
        {
            var idx = CustomActions.IndexOf(act);
            if (idx > 0)
                CustomActions.Move(idx - 1, idx);
        }

        public void MoveCustomActionDown(CustomAction act)
        {
            var idx = CustomActions.IndexOf(act);
            if (idx < CustomActions.Count - 1)
                CustomActions.Move(idx + 1, idx);
        }

        private static string HashContent(string source)
        {
            var hash = MD5.HashData(Encoding.Default.GetBytes(source));
            return Convert.ToHexStringLower(hash);
        }

        private static Dictionary<string, RepositorySettings> _cache = new();
        private string _file = string.Empty;
        private string _orgHash = string.Empty;
    }
}
