using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using YerelDisk.Presentation;

namespace YerelDisk.Wpf;

public partial class DistributionView : UserControl
{
    internal event Action<DistributionSegment>? SelectionRequested;
    internal event Action<DistributionSegment>? NavigationRequested;
    public DistributionView() => InitializeComponent();
    void SelectFolder(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DistributionSegment segment }) SelectionRequested?.Invoke(segment);
    }
    void EnterFolder(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DistributionSegment segment }) NavigationRequested?.Invoke(segment);
    }
}
public sealed class DistributionSelectionConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length == 2 && values[0] is FolderEntry && ReferenceEquals(values[0], values[1]);
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
