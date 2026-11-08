using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace SourceGit.Views
{
    public sealed class MergeResultEditor : TextEditor
    {
        public static readonly StyledProperty<string> FileNameProperty = AvaloniaProperty.Register<MergeResultEditor, string>(nameof(FileName));
        public string FileName { get => GetValue(FileNameProperty); set => SetValue(FileNameProperty, value); }
        protected override Type StyleKeyOverride => typeof(TextEditor);

        public MergeResultEditor() : base(new TextArea(), new TextDocument())
        {
            IsReadOnly = false;
            ShowLineNumbers = false;
            Options.EnableHyperlinks = false;
            Options.EnableEmailHyperlinks = false;
            Options.AllowScrollBelowDocument = false;
            TextArea.TextView.Margin = new Thickness(0);
            TextArea.LeftMargins.Add(new MergeConflictNumberMargin(this, false));
            TextArea.LeftMargins.Add(new MergeConflictSeparator(this, false));
            TextArea.TextView.BackgroundRenderers.Add(new MergeResultBackground(this));
            AddHandler(PointerPressedEvent, OnResultPointerPressed, RoutingStrategies.Tunnel, true);
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
            _textMate = Models.TextMateHelper.CreateForEditor(this);
            Models.TextMateHelper.SetGrammarByFileName(_textMate, FileName);
            TextArea.TextView.ContextRequested += OnContextRequested;
        }

        protected override void OnUnloaded(RoutedEventArgs e)
        {
            TextArea.TextView.ContextRequested -= OnContextRequested;
            _textMate?.Dispose();
            _textMate = null;
            base.OnUnloaded(e);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == FileNameProperty) Models.TextMateHelper.SetGrammarByFileName(_textMate, FileName);
            else if (change.Property.Name == nameof(ActualThemeVariant)) Models.TextMateHelper.SetThemeByApp(_textMate);
        }

        private void OnResultPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (DataContext is not ViewModels.MergeConflictEditor vm) return;
            vm.SelectedChunk = null;
            var point = e.GetPosition(this);
            var position = GetPositionFromPoint(point);
            if (position.HasValue)
            {
                var index = vm.RegionForOffset(Document.GetOffset(position.Value.Location));
                if (index >= 0) vm.ActiveConflictIndex = index;
            }
        }

        private void OnContextRequested(object sender, ContextRequestedEventArgs e)
        {
            var menu = new ContextMenu();
            Add("MergeConflictEditor.UndoEdit", () => Undo(), Document?.UndoStack.CanUndo == true);
            Add("MergeConflictEditor.RedoEdit", () => Redo(), Document?.UndoStack.CanRedo == true);
            menu.Items.Add(new Separator());
            Add("Cut", Cut, !string.IsNullOrEmpty(SelectedText));
            Add("Copy", Copy, !string.IsNullOrEmpty(SelectedText));
            Add("Paste", Paste, true);
            Add("MergeConflictEditor.SelectAll", SelectAll, Document?.TextLength > 0);
            menu.Open(TextArea.TextView);
            e.Handled = true;

            void Add(string key, Action action, bool enabled)
            {
                var item = new MenuItem { Header = App.Text(key), IsEnabled = enabled };
                item.Click += (_, _) => action();
                menu.Items.Add(item);
            }
        }

        private AvaloniaEdit.TextMate.TextMate.Installation _textMate;
    }
}
