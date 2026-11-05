using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YerelDisk;
using YerelDisk.Presentation;
using YerelDisk.Wpf;

internal static class OtherAreaUiTests
{
    static readonly List<string> log = [];
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); log.Add("PASS " + label); }
    static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T item) yield return item; foreach (var nested in Children<T>(child)) yield return nested; } }
    static void Screenshot(FrameworkElement area, string path)
    { var dpi = VisualTreeHelper.GetDpi(area); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(area.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(area.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32); bitmap.Render(area); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); png.Save(stream); }
    [STAThread] static int Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/YerelDisk;component/Theme.xaml") });
        var errors = new StringWriter(); PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(errors)); PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        app.Startup += async (_, _) =>
        {
            var fake = new FakeService(); using var model = new WorkspaceViewModel(fake); var window = new MainWindow(model) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -24000, Top = -24000, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                window.Show(); model.Roots.Add(fake.Root.Path); await model.ScanAsync(); model.ReviewMinimum = 0; await window.NavigateAsync(WorkspaceView.Review);
                window.ReviewCollectionSelector.SelectedValue = 1; await window.PendingView;
                Check(model.IsUnrecognized && model.Rows.Count == 2002 && model.Rows.All(r => r is FolderEntry), "UI selector switches to independent unknown-folder list");
                Check(window.ResultsGrid.Columns.Count == 4 && !window.ResultsGrid.Columns[2].CanUserSort, "Discovery has four compact columns without sorting a constant instruction");
                foreach (bool dark in new[] { false, true }) foreach (var size in new[] { (940d, 680d), (1480d, 940d) })
                {
                    window.SetTheme(dark); window.Width = size.Item1; window.Height = size.Item2; model.SelectedRow = fake.Partial; window.ResultsGrid.ScrollIntoView(fake.Partial); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout(); string label = $"other-areas-{size.Item1}-{(dark ? "dark" : "light")}";
                    Check(!window.ReviewKindSelector.IsVisible && !Children<TextBlock>(window).Any(t => t.IsVisible && t.Text == "Kişisel not"), "Old rule kind and feedback controls stay hidden: " + label);
                    var scroll = Children<ScrollViewer>(window.ResultsGrid).First(); Check(scroll.ScrollableWidth <= 1 && scroll.ViewportHeight >= 80, "Discovery fits narrow/big viewport without horizontal overflow: " + label + " viewport=" + scroll.ViewportHeight);
                    var row = (DataGridRow)window.ResultsGrid.ItemContainerGenerator.ContainerFromItem(fake.Partial); var text = Children<TextBlock>(row).Where(t => t.IsVisible).ToArray();
                    Check(text.Any(t => t.Text == "Amaç / kullanım bilinmiyor") && text.Any(t => t.Text.Contains("Sistem veya kişisel veri")) && text.Any(t => t.Text.Contains("hariç tutma")), "Unknown meaning and partial/excluded state are visible together: " + label);
                    Check(text.All(t => t.ActualHeight + t.Margin.Top + t.Margin.Bottom + 1 >= t.DesiredSize.Height), "Auto-height discovery row avoids clipped wrapped warnings: " + label);
                    Check(Children<DataGridRow>(window.ResultsGrid).Count() < 100, "2002 discovery folders remain UI-virtualized: " + label);
                    Screenshot((FrameworkElement)window.Content, Path.Combine(output, label + ".png"));
                }
                var detail = window.CreateDetailsWindow(); detail.WindowStartupLocation = WindowStartupLocation.Manual; detail.Left = -24000; detail.Top = -24000; detail.ShowInTaskbar = false; detail.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(Children<TextBox>(detail.ContentView).Any(t => t.Text.Contains("silme önerisi değildir")) && Children<TextBox>(detail.ContentView).Any(t => t.Text.Contains("doğrudan dosyaları")), "F6 renders both meaning and partition coverage, not just a model string"); detail.Close();
                var report = ReportSnapshot.Capture(model); var writer = new StringWriter(); report.Write(writer); Check(report.Unrecognized && report.Items.Length == 2002 && writer.ToString().Contains("İlk kontrol adımı"), "Discovery export captures all filtered folder rows and guidance");
                window.ReviewCollectionSelector.SelectedValue = 0; await window.PendingView;
                Check(model.IsKnownReview && model.Rows.Count == 1 && model.Rows[0] is ReviewCandidate && window.ResultsGrid.Columns.Any(c => c.SortMemberPath == "ReviewSignal"), "Switching back restores original recognized table and classification");
                Check(ReportSnapshot.Capture(model).Unrecognized == false && report.Unrecognized && report.Items.Length == 2002, "Captured export remains immutable after list mode changes");
                window.ReviewCollectionSelector.SelectedValue = 1; await window.PendingView; model.SelectedRow = fake.Apps; await model.EnterAsync(fake.Apps); await window.NavigateAsync(model.View);
                Check(model.IsFolderView && model.Rows.Single() is FolderEntry f && f.Name == "Nested" && window.ResultsGrid.RowHeight == 42, "Discovery folder opens in ordinary folder navigation with original layout");
                await window.NavigateAsync(WorkspaceView.Review); Check(model.IsUnrecognized && window.ResultsGrid.Columns.Count == 4, "Returning to review restores discovery list and template");
                model.Search = "no-such-folder"; await model.RefreshAsync(); Check(model.IsEmpty && model.EmptyMessage.Contains("filtre"), "Empty filtered discovery has a useful explanation");
                PresentationTraceSources.DataBindingSource.Flush(); Check(errors.ToString().Length == 0, "No binding errors during selection, filters, mode changes or navigation");
                log.Add($"ALL CHECKS PASSED ({log.Count})"); File.WriteAllLines(Path.Combine(output, "results.txt"), log); Console.WriteLine(log[^1]); window.Close(); app.Shutdown(0);
            }
            catch (Exception ex) { log.Add("FAIL " + ex); File.WriteAllLines(Path.Combine(output, "results.txt"), log); Screenshot((FrameworkElement)window.Content, Path.Combine(output, "failure.png")); Console.WriteLine(ex); window.Close(); app.Shutdown(1); }
        };
        return app.Run();
    }
    sealed class FakeService : IScanService
    {
        internal FolderEntry Root { get; } = new(@"C:\Other-Ui", null) { LogicalBytes = 9000, FileCount = 2005 };
        internal FolderEntry Apps { get; }
        internal FolderEntry Partial { get; }
        readonly ScanOutcome outcome;
        public FakeService()
        {
            Apps = new(Path.Combine(Root.Path, "Old-Application-" + new string('x', 90)), Root) { LogicalBytes = 8000, FileCount = 1 }; Apps.Children.Add(new(Path.Combine(Apps.Path, "Nested"), Apps) { LogicalBytes = 8000, FileCount = 1 });
            Partial = new(Path.Combine(Root.Path, "Partial-Data"), Root) { LogicalBytes = 2000, FileCount = 1, ExcludedSubtrees = 1, Measurement = MeasurementState.Partial, IssueCount = 1 };
            var known = new FolderEntry(Path.Combine(Root.Path, "known-cache"), Root) { LogicalBytes = 1000, FileCount = 1 }; Root.Children.AddRange([Apps, Partial, known]);
            for (int i = 0; i < 2000; i++) Root.Children.Add(new(Path.Combine(Root.Path, "folder-" + i.ToString("D4")), Root) { LogicalBytes = 100, FileCount = 1 });
            var scan = new ScanResult(Root) { ExcludedPaths = [Path.Combine(Partial.Path, "skip")] };
            outcome = new(scan, [new(known.Path, 1000, 1000, 1, "cache", "fixture", "GRADLE-CACHES", "fixture", "fixture", false, ReviewKind.Cache)]);
        }
        public string[] NormalizeRoots(IEnumerable<string> paths) => paths.ToArray();
        public Task<ScanOutcome> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken token) => Task.FromResult(outcome);
    }
}
