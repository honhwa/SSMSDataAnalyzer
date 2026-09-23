using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SsmsDataAnalyzer.Vsix.Pivot
{
    /// <summary>docs/pivot-plan.md §12: bool -&gt; Visibility for the 🔗 column marker and the
    /// per-cell "Go to source" icon. One-way only; these cells are never edited.</summary>
    internal sealed class PivotFkVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is bool b && b ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>docs/pivot-plan.md §12: builds the value-cell icon's tooltip, "Go to " +
    /// TargetText + " = " + value, from two bindings (PivotRowItem.TargetText, Values[i]).
    /// UI-only text — the cell value passes through a binding, never a log or exception.</summary>
    internal sealed class PivotFkGoTooltipConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2) return null;
            var targetText = values[0] as string;
            var cellValue = values[1] as string;
            if (string.IsNullOrEmpty(targetText)) return null;

            // "peek" = the peek window, where a click follows the link IN PLACE rather than
            // opening a query tab, so the wording has to promise something different.
            bool inPlace = string.Equals(parameter as string, "peek", StringComparison.Ordinal);
            return inPlace
                ? "Peek " + targetText + " = " + cellValue + " here (Back returns)"
                : "Go to " + targetText + " = " + cellValue;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
