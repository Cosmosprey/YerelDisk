using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
                Check(window.FindName("SummaryExpander") == null, "The scan summary has no UI element");
                model.Roots.Add(@"C:\example"); await model.ScanAsync(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(model.Rows[0] is FolderEntry { Name: "largest" } && model.Rows[^1] is FolderEntry { Measurement: MeasurementState.Unknown },
                    "Default order shows largest measured folders first and unknown size last");
                await model.SortAsync("Size");
                Check(model.Rows[0] is FolderEntry { Name: "zero" } && model.Rows[^1] is FolderEntry { Measurement: MeasurementState.Unknown },
                    "Ascending size keeps unknown after the measured zero");
                await model.SortAsync("Allocated");
                Check(model.Rows[0] is FolderEntry { Name: "largest" } && model.Rows[^1] is FolderEntry { Measurement: MeasurementState.Unknown },
                    "Descending allocated space keeps unavailable values last");
                await model.SortAsync("Allocated");
                Check(model.Rows[0] is FolderEntry { Name: "zero" } && model.Rows[^1] is FolderEntry { Measurement: MeasurementState.Unknown },
                    "Ascending allocated space keeps unavailable values last");
                await model.ScanAsync(); window.ConfigureColumns();
                Check(model.SortKey == "Size" && model.SortDescending && model.Rows[0] is FolderEntry { Name: "largest" },
                    "A new scan returns to descending size");
                foreach (var size in new[] { (940d, 680d), (1480d, 940d) })
                    foreach (bool dark in new[] { false, true })
                    {
                        window.Width = size.Item1; window.Height = size.Item2; window.SetTheme(dark);
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                        string label = $"{size.Item1}x{size.Item2}-{(dark ? "dark" : "light")}";
                        Check(window.ResultsGrid.RowHeight == 38 && window.ResultsGrid.ColumnHeaderHeight == 40
                            && window.ResultsGrid.ActualHeight >= window.ResultsGrid.ColumnHeaderHeight + 24,
                            $"Usable table: {label} (table {window.ResultsGrid.ActualHeight:N0}, window {window.ActualHeight:N0}, metrics {window.Metrics.ActualHeight:N0}, warning {window.ScopeNoticeCard.ActualHeight:N0}, visible {window.ResultsGrid.IsVisible})");
                        Check(window.ScopeNoticeCard.IsVisible, "Scope warnings stay visible: " + label);
                        Check(window.Metrics.IsVisible, "Existing metrics stay visible: " + label);
                        Check(VirtualizingPanel.GetIsVirtualizing(window.ResultsGrid)
                            && VirtualizingPanel.GetScrollUnit(window.ResultsGrid) == ScrollUnit.Pixel, "Virtualized pixel scrolling: " + label);
                        var area = (FrameworkElement)window.Content; var dpi = VisualTreeHelper.GetDpi(area);
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(area.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(area.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                        bitmap.Render(area); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var stream = File.Create(Path.Combine(output, label + ".png")); encoder.Save(stream);
                    }
                window.Width = 940; window.Height = 680;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                var viewer = (ScrollViewer)window.ResultsGrid.Template.FindName("DG_ScrollViewer", window.ResultsGrid);
                viewer.ScrollToTop();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var bar = Descendants<ScrollBar>(viewer).Single(item => item.Orientation == Orientation.Vertical && item.IsVisible);
                var up = (RepeatButton)bar.Template.FindName("ScrollUp", bar);
                var down = (RepeatButton)bar.Template.FindName("ScrollDown", bar);
                Check(up.IsVisible && down.IsVisible, "Both vertical arrow buttons are visible");
                ((RoutedCommand)down.Command).Execute(null, down);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(viewer.VerticalOffset > 0 && viewer.VerticalOffset <= window.ResultsGrid.RowHeight, "Down arrow moves a small single step");
                ((RoutedCommand)up.Command).Execute(null, up);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(viewer.VerticalOffset == 0, "Up arrow returns one step");
                for (int i = 0; i < 4; i++)
                    window.ResultsGrid.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -30) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                // Claude 1.21.1: the step follows Windows' lines-per-notch setting (3 lines = 24 px).
                double notch = SystemParameters.WheelScrollLines < 0 ? viewer.ViewportHeight : 8d * SystemParameters.WheelScrollLines;
                Check(Math.Abs(viewer.VerticalOffset - Math.Min(notch, viewer.ScrollableHeight)) < 1,
                    $"Four small wheel events accumulate to one notch ({notch:N0} px for {SystemParameters.WheelScrollLines} lines)");
                await window.NavigateAsync(WorkspaceView.Settings); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(!window.ScopeNoticeCard.IsVisible, "Scope warning stays out of settings");
                await window.NavigateAsync(WorkspaceView.Issues); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(window.ScopeNoticeCard.IsVisible, "Warning remains available in issues");
                fake.Outcome.Scan.Cancelled = true; await model.ScanAsync(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(window.ScopeNoticeText.Text.Contains("Durduruldu"), "Cancelled status reaches the scope warning");
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
            root.Children.Add(new(@"C:\example\unknown", root) { Measurement = MeasurementState.Unknown, LogicalBytes = 2000, AllocatedBytes = 8000 });
            root.Children.Add(new(@"C:\example\zero", root));
            root.Children.Add(new(@"C:\example\largest", root) { LogicalBytes = 1024, AllocatedBytes = 4096, FileCount = 1 });
            for (int i = 0; i < 100; i++)
                root.Children.Add(new($@"C:\example\folder-{i:D3}", root) { LogicalBytes = i + 1, AllocatedBytes = 512, FileCount = 1 });
            return new(new(root) { Folders = 3, Elapsed = TimeSpan.FromSeconds(10), Timings = new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(2)), IssueCount = 1, UnknownAllocation = 1 }, []);
        }
        public string[] NormalizeRoots(IEnumerable<string> paths) => paths.ToArray();
        public Task<ScanOutcome> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken token) => Task.FromResult(Outcome);
    }
}
