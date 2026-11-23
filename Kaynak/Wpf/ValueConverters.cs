using System.Globalization;
using System.Windows.Data;
using YerelDisk.Presentation;

namespace YerelDisk.Wpf;

internal sealed class MeasurementCellConverter(WorkspaceViewModel model) : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => Format(model, value, parameter as string == "Full");
    internal static string Format(WorkspaceViewModel model, object value, bool full)
    {
        if (model.Scan is not { HasMixedMeasurementTimes: true } scan) return RowValues.MeasurementOf(value);
        string time = MeasurementProvenance.ForRow(scan, model.MeasurementCompletedAt, value, !full);
        return time + " · " + RowValues.MeasurementOf(value);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class MeasurementContextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length >= 2 && values[1] is WorkspaceViewModel model
            ? MeasurementCellConverter.Format(model, values[0], parameter as string == "Full")
            : values.Length > 0 ? RowValues.MeasurementOf(values[0]) : "";
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class CellTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value == null ? "" : RowValues.Text(value, parameter as string ?? "Name");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class InventoryHintConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => values.Length >= 2 && values[1] is InventoryController inventory ? inventory.RowSummary(values[0]) : "";
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ReviewAttentionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is ReviewCandidate c && ReviewTableText.Attention(c);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class SizeRatioConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => values is [var item, long maximum] && maximum > 0 ? Math.Clamp(RowValues.SizeOf(item) * 100d / maximum, 0, 100) : 0d;
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

