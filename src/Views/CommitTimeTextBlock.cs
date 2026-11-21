using System;

using Avalonia;
using Avalonia.Controls;

namespace SourceGit.Views
{
    public class CommitTimeTextBlock : TextBlock
    {
        public static readonly DirectProperty<CommitTimeTextBlock, bool> Use24HoursProperty =
            AvaloniaProperty.RegisterDirect<CommitTimeTextBlock, bool>(
                nameof(Use24Hours),
                static o => o.Use24Hours,
                static (o, v) => o.Use24Hours = v);

        public bool Use24Hours
        {
            get => _use24Hours;
            set => SetAndRaise(Use24HoursProperty, ref _use24Hours, value);
        }

        public static readonly DirectProperty<CommitTimeTextBlock, int> DateTimeFormatProperty =
            AvaloniaProperty.RegisterDirect<CommitTimeTextBlock, int>(
                nameof(DateTimeFormat),
                static o => o.DateTimeFormat,
                static (o, v) => o.DateTimeFormat = v);

        public int DateTimeFormat
        {
            get => _dateTimeFormat;
            set => SetAndRaise(DateTimeFormatProperty, ref _dateTimeFormat, value);
        }

        public static readonly DirectProperty<CommitTimeTextBlock, ulong> TimestampProperty =
            AvaloniaProperty.RegisterDirect<CommitTimeTextBlock, ulong>(
                nameof(Timestamp),
                static o => o.Timestamp,
                static (o, v) => o.Timestamp = v);

        public ulong Timestamp
        {
            get => _timestamp;
            set => SetAndRaise(TimestampProperty, ref _timestamp, value);
        }

        protected override Type StyleKeyOverride => typeof(TextBlock);

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == TimestampProperty || change.Property == DateTimeFormatProperty || change.Property == Use24HoursProperty)
                RefreshText();
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            RefreshText();
        }

        private void RefreshText() => SetCurrentValue(TextProperty, Models.DateTimeFormat.Format(Timestamp));

        private bool _use24Hours = true;
        private int _dateTimeFormat = 0;
        private ulong _timestamp = 0;
    }
}
