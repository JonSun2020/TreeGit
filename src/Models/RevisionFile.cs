using System.Globalization;
using System;
using System.IO;
using Avalonia.Media.Imaging;

namespace SourceGit.Models
{
    public class RevisionBinaryFile
    {
        public string Repository { get; set; } = string.Empty;
        public string File { get; set; } = string.Empty;
        public string Revision { get; set; } = string.Empty;
        public long Size { get; set; } = 0;

        public RevisionBinaryFile(string repo, string file, string revision, long size)
        {
            Repository = repo;
            File = file;
            Revision = revision;
            Size = size;
        }
    }

    public class RevisionImageFile
    {
        public Bitmap Image { get; }
        public long FileSize { get; }
        public string ImageType { get; }
        public string ImageSize => Image != null ? $"{Image.PixelSize.Width} x {Image.PixelSize.Height}" : "0 x 0";

        public RevisionImageFile(string file, Bitmap img, long size)
        {
            Image = img;
            FileSize = size;
            ImageType = Path.GetExtension(file)!.Substring(1).ToUpper(CultureInfo.CurrentCulture);
        }
    }

    public class RevisionTextFile : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        public string FileName { get; }
        public string Content { get; private set; }

        public RevisionTextFile(string repository, string fileName, ArraySegment<byte> bytes)
        {
            _repository = repository;
            _bytes = bytes;
            FileName = fileName;
            _encodingIndex = ViewModels.Preferences.Instance.GetFileEncodingIndex(repository, fileName);
            Content = DisplayEncoding.Decode(bytes.AsSpan(), DisplayEncoding.CodePage(_encodingIndex));
        }

        public int DisplayEncodingIndex
        {
            get => _encodingIndex;
            set
            {
                ViewModels.Preferences.Instance.SetFileEncodingIndex(_repository, FileName, value);
                RefreshDisplayEncoding();
            }
        }

        internal void RefreshDisplayEncoding()
        {
            var index = ViewModels.Preferences.Instance.GetFileEncodingIndex(_repository, FileName);
            if (!SetProperty(ref _encodingIndex, index, nameof(DisplayEncodingIndex))) return;
            Content = DisplayEncoding.Decode(_bytes.AsSpan(), DisplayEncoding.CodePage(index));
            OnPropertyChanged(nameof(Content));
        }

        private readonly string _repository;
        private readonly ArraySegment<byte> _bytes;
        private int _encodingIndex;
    }

    public class RevisionLFSObject
    {
        public LFSObject Object { get; set; }
    }

    public class RevisionSubmodule
    {
        public Commit Commit { get; set; } = null;
        public CommitFullMessage FullMessage { get; set; } = null;
        public int UncommittedChanges { get; set; } = 0;

        public RevisionSubmodule(string hash)
        {
            Commit = new Commit() { SHA = hash };
            FullMessage = new CommitFullMessage() { Message = string.Empty };
        }

        public RevisionSubmodule(Commit commit, string message, int uncommittedChanges)
        {
            Commit = commit;
            FullMessage = new CommitFullMessage() { Message = message };
            UncommittedChanges = uncommittedChanges;
        }
    }
}
