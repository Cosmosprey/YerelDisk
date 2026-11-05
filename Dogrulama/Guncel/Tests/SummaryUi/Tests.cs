using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YerelDisk;
using YerelDisk.Presentation;
using YerelDisk.Wpf;

internal static class UiTests
{
    static readonly List<string> log = [];
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); log.Add("PASS " + name); }
    [STAThread] static int Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/YerelDisk;component/Theme.xaml") });
        var errors = new StringWriter(); PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(errors));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        app.Startup += async (_, _) =>
        {
            var fake = new FakeService(); using var model = new WorkspaceViewModel(fake);
            var window = new MainWindow(model) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -24000, Top = 0, ShowInTaskbar = false, ShowActivated = false };
            app.MainWindow = window; window.Show();
            try
            {
                Check(window.SummaryCard.Visibility == Visibility.Collapsed, "Summary is hidden before first scan");
                model.Roots.Add(@"C:\example"); await model.ScanAsync(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(window.SummaryCard.Visibility == Visibility.Visible && !window.SummaryExpander.IsExpanded, "Completed summary starts collapsed");
                foreach (var size in new[] { (940d, 680d), (1480d, 940d) })
                    foreach (bool dark in new[] { false, true })
                    {
                        window.Width = size.Item1; window.Height = size.Item2; window.SetTheme(dark);
                        window.SummaryExpander.IsExpanded = true;
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                        string label = $"{size.Item1}x{size.Item2}-{(dark ? "dark" : "light")}";
                        Console.WriteLine($"Layout {label}: window={window.ActualWidth}x{window.ActualHeight}; summary={window.SummaryCard.ActualHeight}; metrics={window.Metrics.ActualHeight}; table={window.ResultsGrid.ActualHeight}");
                        Console.WriteLine("Rows: " + string.Join(", ", ((Grid)((StackPanel)window.SummaryCard.Parent).Parent).RowDefinitions.Select(row => row.ActualHeight)));
                        Check(window.ResultsGrid.ActualHeight >= 90, "Expanded summary leaves usable table: " + label + " height=" + window.ResultsGrid.ActualHeight);
                        Check(window.ScopeNoticeCard.Visibility == Visibility.Collapsed, "Expanded summary replaces duplicate warning band: " + label);
                        var header = (TextBlock)window.SummaryExpander.Header;
                        var point = header.TranslatePoint(new Point(header.ActualWidth, header.ActualHeight), (FrameworkElement)window.Content);
                        Check(header.ActualHeight > 0 && point.X <= ((FrameworkElement)window.Content).ActualWidth + 1, "Summary heading stays inside window: " + label);
                        Check(Descendants<TextBlock>(window.SummaryExpander).Any(text => text.Text.Contains("İlk tarama 3,00 sn")), "Phase timing is visibly bound: " + label);
                        var area = (FrameworkElement)window.Content; var dpi = VisualTreeHelper.GetDpi(area);
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(area.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(area.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                        bitmap.Render(area); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var stream = File.Create(Path.Combine(output, label + ".png")); encoder.Save(stream);
                    }
                window.Width = 940; window.Height = 680; window.SummaryExpander.IsExpanded = false;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                Check(window.Metrics.Visibility == Visibility.Visible && window.ScopeNoticeCard.Visibility == Visibility.Visible, "Collapsing summary restores metrics and warning band on short windows");
                await window.NavigateAsync(WorkspaceView.Settings); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(window.SummaryCard.Visibility == Visibility.Collapsed, "Summary does not consume settings space");
                await window.NavigateAsync(WorkspaceView.Issues); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(window.SummaryCard.Visibility == Visibility.Visible, "Summary remains available with warnings");
                fake.Outcome.Scan.Cancelled = true; await model.ScanAsync(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(((TextBlock)window.SummaryExpander.Header).Text.Contains("Durduruldu"), "Cancelled status reaches rendered header");
                PresentationTraceSources.DataBindingSource.Flush(); Check(errors.ToString().Length == 0, "No WPF binding errors");
                log.Add($"ALL CHECKS PASSED ({log.Count})"); Console.WriteLine(log[^1]); File.WriteAllLines(Path.Combine(output, "results.txt"), log); app.Shutdown(0);
            }
            catch (Exception ex) { log.Add("FAIL " + ex); File.WriteAllLines(Path.Combine(output, "results.txt"), log); Console.WriteLine(ex); app.Shutdown(1); }
        };
        return app.Run();
    }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var item in Descendants<T>(child)) yield return item;
        }
    }
    sealed class FakeService : IScanService
    {
        internal ScanOutcome Outcome { get; } = Example();
        static ScanOutcome Example()
        {
            var root = new FolderEntry(@"C:\example", null) { LogicalBytes = 1024, AllocatedBytes = 8192, FileCount = 2 };
            root.Children.Add(new(@"C:\example\first", root) { LogicalBytes = 512, AllocatedBytes = 4096, FileCount = 1 });
            root.Children.Add(new(@"C:\example\second", root) { LogicalBytes = 512, AllocatedBytes = 4096, FileCount = 1 });
            return new(new(root) { Folders = 3, Elapsed = TimeSpan.FromSeconds(10), Timings = new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(2)), IssueCount = 1, UnknownAllocation = 1 }, []);
        }
        public string[] NormalizeRoots(IEnumerable<string> paths) => paths.ToArray();
        public Task<ScanOutcome> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken token) => Task.FromResult(Outcome);
    }
}
