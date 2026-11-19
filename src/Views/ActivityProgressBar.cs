using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace SourceGit.Views
{
    public sealed class ActivityProgressBar : ProgressBar
    {
        public static readonly StyledProperty<double> ReportedValueProperty =
            AvaloniaProperty.Register<ActivityProgressBar, double>(nameof(ReportedValue));
        public static readonly StyledProperty<bool> IsRunningProperty =
            AvaloniaProperty.Register<ActivityProgressBar, bool>(nameof(IsRunning));

        public double ReportedValue { get => GetValue(ReportedValueProperty); set => SetValue(ReportedValueProperty, value); }
        public bool IsRunning { get => GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }
        protected override Type StyleKeyOverride => typeof(ProgressBar);

        public ActivityProgressBar()
        {
            Classes.Add("operation_progress");
            _timer.Tick += (_, _) => Advance();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            UpdateTimer();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _timer.Stop();
            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property != ReportedValueProperty && change.Property != IsRunningProperty) return;
            if (change.Property == IsRunningProperty && IsRunning) Value = 0;
            UpdateTimer();
        }

        private void UpdateTimer()
        {
            if (VisualRoot == null || (!IsRunning && (ReportedValue < 100 || Value >= 100)))
                _timer.Stop();
            else if (!_timer.IsEnabled)
            {
                _lastTick = Stopwatch.GetTimestamp();
                _timer.Start();
            }
        }

        private void Advance()
        {
            var now = Stopwatch.GetTimestamp();
            var seconds = Math.Min(0.08, Stopwatch.GetElapsedTime(_lastTick, now).TotalSeconds);
            _lastTick = now;
            if (!IsEffectivelyVisible)
            {
                if (!IsRunning) { Value = 100; _timer.Stop(); }
                return;
            }
            if (!IsRunning)
            {
                Value = Math.Min(100, Value + seconds * 180);
                if (Value >= 100) _timer.Stop();
                return;
            }

            // Catch up to measured phases quickly. Between reports, only borrow
            // a fraction of the remaining range; keep a visible gap even at 99%.
            var measured = Math.Clamp(ReportedValue, 5, 97);
            var estimate = measured + Math.Min(18, (97 - measured) * 0.4);
            var rate = Value < measured ? Math.Clamp((measured - Value) * 4, 5, 75) : (estimate - Value) * 0.11;
            Value += Math.Clamp(seconds * rate, 0, Math.Max(0, estimate - Value));
        }

        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(32) };
        private long _lastTick;
    }
}
