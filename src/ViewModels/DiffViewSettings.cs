using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public interface IDiffViewSettings : INotifyPropertyChanged
    {
        int DisplayEncodingIndex { get; set; }
        bool UseSideBySideDiff { get; set; }
        bool UseSyntaxHighlighting { get; set; }
        bool EnableDiffViewWordWrap { get; set; }
        bool IgnoreWhitespaceChangesInDiff { get; set; }
        bool IgnoreCRAtEOLInDiff { get; set; }
        bool ShowHiddenSymbolsInDiffView { get; set; }
    }

    public sealed class DiffViewSettings : ObservableObject, IDiffViewSettings
    {
        public int DisplayEncodingIndex { get => _encodingIndex; set => SetProperty(ref _encodingIndex, Math.Max(0, value)); }
        private int _encodingIndex;
        public bool UseSideBySideDiff { get => _columns; set => SetProperty(ref _columns, value); }
        public bool UseSyntaxHighlighting { get => _syntax; set => SetProperty(ref _syntax, value); }
        public bool EnableDiffViewWordWrap { get => _wrap; set => SetProperty(ref _wrap, value); }
        public bool IgnoreWhitespaceChangesInDiff { get => _whitespace; set => SetProperty(ref _whitespace, value); }
        public bool IgnoreCRAtEOLInDiff { get => _cr; set => SetProperty(ref _cr, value); }
        public bool ShowHiddenSymbolsInDiffView { get => _symbols; set => SetProperty(ref _symbols, value); }
        private bool _columns = true;
        private bool _syntax = true;
        private bool _wrap;
        private bool _whitespace;
        private bool _cr = true;
        private bool _symbols;
    }
}
