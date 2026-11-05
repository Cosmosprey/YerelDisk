using System.Globalization;
using System.Windows.Data;
using YerelDisk.Presentation;

namespace YerelDisk.Wpf;

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

