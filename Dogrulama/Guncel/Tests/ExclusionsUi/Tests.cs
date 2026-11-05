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

internal static class ExclusionUiTests
{
    static readonly List<string> log = [];
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); log.Add("PASS " + label); }
    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static void Screenshot(FrameworkElement area, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(area); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(area.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(area.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual(); using (var context = visual.RenderOpen()) { context.DrawRectangle((Brush)area.FindResource("Canvas"), null, new Rect(0, 0, area.ActualWidth, area.ActualHeight)); context.DrawRectangle(new VisualBrush(area) { Stretch = Stretch.Fill }, null, new Rect(0, 0, area.ActualWidth, area.ActualHeight)); } bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    [STAThread] static int Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/YerelDisk;component/Theme.xaml") });
        var errors = new StringWriter(); PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(errors)); PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        app.Startup += async (_, _) => {
            using var model = new WorkspaceViewModel(new FakeService()); var main = new MainWindow(model) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -24000, Top = -24000, ShowInTaskbar = false };
            var dialog = new RootsWindow([@"C:\example"], MultiScan.NormalizeRootPaths, [@"C:\example\omit"]) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -24000, Top = -24000, ShowInTaskbar = false };
            try
            {
                main.Show(); dialog.Show();
                foreach (bool dark in new[] { false, true }) foreach (double width in new[] { 540d, 760d })
                {
                    main.SetTheme(dark); dialog.Width = width; dialog.Height = width == 540 ? 480 : 600;
                    foreach (bool excluded in new[] { false, true })
                    {
                        dialog.SelectExclusions(excluded); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); dialog.UpdateLayout();
                        string label = $"scope-{width}-{(dark ? "dark" : "light")}-{(excluded ? "excluded" : "included")}";
                        var area = (FrameworkElement)dialog.Content;
                        Point bottom = dialog.AcceptButton.TranslatePoint(new Point(dialog.AcceptButton.ActualWidth, dialog.AcceptButton.ActualHeight), area);
                        Check(bottom.Y <= area.ActualHeight + 1 && bottom.X <= area.ActualWidth + 1 && dialog.AcceptButton.ActualHeight >= 30, "Scope acceptance remains visible: " + label);
                        var list = excluded ? dialog.ExcludedList : dialog.IncludedList;
                        Check(list.Visibility == Visibility.Visible && list.ActualHeight >= 35 && dialog.PathInput.ActualWidth >= 120, "Active list and path editor have usable space: " + label);
                        Screenshot(area, Path.Combine(output, label + ".png"));
                    }
                }
                dialog.SelectExclusions(true); dialog.PathInput.Text = @"C:\example\second"; Click(dialog.AddPathButton);
                Check(dialog.Exclusions.Contains(@"C:\example\second") && dialog.Paths.SequenceEqual([@"C:\example"]), "Manual path goes only to selected exclusion list");
                dialog.PathInput.Text = @"C:\EXAMPLE\SECOND"; Click(dialog.AddPathButton);
                Check(dialog.Exclusions.Length == 2 && dialog.ExcludedTab.Content.ToString()!.Contains("2"), "Duplicate manual entry is ignored and count remains correct");
                dialog.ExcludedList.SelectedItems.Add(@"C:\example\second"); Click(dialog.RemoveButton);
                Check(dialog.Exclusions.SequenceEqual([@"C:\example\omit"]), "Remove operates only on selected list");
                dialog.ExcludedList.Items.Add(@"C:\example\omit\nested");
                Check(dialog.ValidateScope() && dialog.Exclusions.SequenceEqual([@"C:\example\omit"]), "Scope acceptance canonicalizes nested exclusions");
                dialog.ExcludedList.Items.Add(@"D:\outside");
                Check(!dialog.ValidateScope() && dialog.Note.Text.Contains("altında") && dialog.Paths.Length == 1 && dialog.Exclusions.Length == 2, "Invalid scope shows actionable error without losing either list");
                dialog.ExcludedList.Items.Remove(@"D:\outside"); dialog.IncludedList.Items.Clear();
                Check(!dialog.ValidateScope() && dialog.Exclusions.Length == 1, "Empty roots cannot silently apply stale exclusions");
                dialog.Close();
                model.Roots.Add(@"C:\example"); model.Exclusions.Add(@"C:\example\omit"); await model.ScanAsync();
                Check(model.RootsLabel.Contains("1 hariç") && model.Summary!.Coverage.Contains("hariç tutuldu"), "Pending scope and finished summary are visibly distinct");
                main.Width = 940; main.Height = 680; main.SummaryExpander.IsExpanded = false; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); main.UpdateLayout();
                Check(main.ScopeNoticeCard.Visibility == Visibility.Visible, "Intentional exclusions keep a notice even with zero access warnings");
                Screenshot((FrameworkElement)main.Content, Path.Combine(output, "result-940-dark.png"));
                model.SelectedRow = model.Rows.OfType<FolderEntry>().First();
                Check(model.Details.FullText.Contains(@"C:\example\omit"), "Selected result full detail preserves scan exclusion paths");
                PresentationTraceSources.DataBindingSource.Flush(); Check(errors.ToString().Length == 0, "No WPF binding errors");
                log.Add($"ALL CHECKS PASSED ({log.Count})"); File.WriteAllLines(Path.Combine(output, "results.txt"), log); Console.WriteLine(log[^1]); main.Close(); app.Shutdown(0);
            }
            catch(Exception ex) { log.Add("FAIL " + ex); File.WriteAllLines(Path.Combine(output, "results.txt"), log); Console.WriteLine(ex); dialog.Close(); main.Close(); app.Shutdown(1); }
        };
        return app.Run();
    }
    sealed class FakeService : IScanService
    {
        public string[] NormalizeRoots(IEnumerable<string> paths) => paths.ToArray();
        public Task<ScanOutcome> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken token)
        {
            var root = new FolderEntry(@"C:\example", null) { LogicalBytes = 1024, AllocatedBytes = 4096, FileCount = 1 };
            root.Children.Add(new(@"C:\example\kept", root) { LogicalBytes = 1024, AllocatedBytes = 4096, FileCount = 1 });
            var scan = new ScanResult(root) { Folders = 2 }; ScanExclusions.Create(request.Roots, request.Exclusions).ApplyTo(scan);
            return Task.FromResult(new ScanOutcome(scan, []));
        }
    }
}
