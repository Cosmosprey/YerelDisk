using System.Windows;
using System.Windows.Input;
using YerelDisk.Presentation;

namespace YerelDisk.Wpf;

public partial class DetailsWindow : Window
{
    readonly SelectionDetails details;
    internal DetailsWindow(SelectionDetails details)
    {
        this.details = details;
        InitializeComponent(); DataContext = details;
        WindowPlacement.Attach(this);
    }
    void CopyText(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(details.FullText); CopyState.Text = "Tam metin kopyalandı."; }
        catch (Exception ex) { CopyState.Text = ex.Message; }
    }
    void CloseWindow(object sender, RoutedEventArgs e) => Close();
    void WindowKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { Close(); e.Handled = true; } }
}
