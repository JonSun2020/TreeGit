using System;
using System.Collections.Generic;
using System.Globalization;

using Avalonia;
using Avalonia.Data.Converters;

namespace SourceGit.Converters
{
    public static class DoubleConverters
    {
        public sealed class NotificationAreaHeightConverter : IMultiValueConverter
        {
            public object Convert(IList<object> values, Type targetType, object parameter, CultureInfo culture)
            {
                if (values is not [double height, bool hasPopup])
                    return 0.0;

                // Retain space for the modal form and its fixed footer even when errors accumulate.
                return Math.Max(0, hasPopup ? Math.Min(240, height * .35) : height - 64);
            }
        }

        public static readonly NotificationAreaHeightConverter NotificationAreaHeight = new();

        public static readonly FuncValueConverter<double, double> CommitSummaryHeight = new(v => System.Math.Min(240, System.Math.Max(0, v * .65)));

        public static readonly FuncValueConverter<double, double> Increase =
            new FuncValueConverter<double, double>(v => v + 1.0);

        public static readonly FuncValueConverter<double, double> Decrease =
            new FuncValueConverter<double, double>(v => v - 1.0);

        public static readonly FuncValueConverter<double, string> ToPercentage =
            new FuncValueConverter<double, string>(v => (v * 100).ToString("F0") + "%");

        public static readonly FuncValueConverter<double, string> OneMinusToPercentage =
            new FuncValueConverter<double, string>(v => ((1.0 - v) * 100).ToString("F0") + "%");

        public static readonly FuncValueConverter<double, Thickness> ToLeftMargin =
            new FuncValueConverter<double, Thickness>(v => new Thickness(v, 0, 0, 0));
    }
}
