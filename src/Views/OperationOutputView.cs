using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;

namespace SourceGit.Views
{
    // One selectable text surface allows copying across lines and across error
    // highlights. Updates are coalesced so verbose Git output stays responsive.
    public sealed class OperationOutputView : SelectableTextBlock
    {
        public event EventHandler OutputUpdated;
        private ViewModels.Popup _model;
        private ViewModels.Popup.OutputLine _firstRendered;
        private int _renderedCount;
        private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(100) };
        protected override Type StyleKeyOverride => typeof(SelectableTextBlock);
        public OperationOutputView()
        {
            _refresh.Tick += (_, _) => { _refresh.Stop(); RenderOutput(); };
            DataContextChanged += (_, _) => Track();
            AttachedToVisualTree += (_, _) => Track();
            DetachedFromVisualTree += (_, _) => { _refresh.Stop(); if (_model != null) _model.PropertyChanged -= Changed; _model = null; };
        }
        private void Track()
        {
            if (_model != null) _model.PropertyChanged -= Changed;
            _model = DataContext as ViewModels.Popup;
            if (_model != null) _model.PropertyChanged += Changed;
            RenderOutput();
        }
        private void Changed(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModels.Popup.OperationOutput)) _refresh.Start();
        }
        private void RenderOutput()
        {
            var start = SelectionStart; var end = SelectionEnd;
            var lines = _model?.OperationOutput;
            if (lines == null || lines.Count == 0 || !ReferenceEquals(_firstRendered, lines[0]) || lines.Count < _renderedCount)
            {
                Inlines.Clear();
                _renderedCount = 0;
                _firstRendered = lines is { Count: > 0 } ? lines[0] : null;
            }
            if (lines == null) return;
            while (_renderedCount < lines.Count)
            {
                var line = lines[_renderedCount++];
                var run = new Run(line.Text + Environment.NewLine);
                if (line.IsError) run.Foreground = Brushes.IndianRed;
                Inlines.Add(run);
            }
            if (start != SelectionStart || end != SelectionEnd)
            {
                var length = Inlines.Text?.Length ?? 0;
                SelectionStart = Math.Min(start, length);
                SelectionEnd = Math.Min(end, length);
            }
            OutputUpdated?.Invoke(this, EventArgs.Empty);
        }
    }
}
