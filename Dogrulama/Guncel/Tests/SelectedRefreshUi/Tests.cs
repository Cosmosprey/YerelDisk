using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YerelDisk;
using YerelDisk.Presentation;
using YerelDisk.Wpf;

static class Tests
{
    static readonly List<string> log = [];
    static void Check(bool value, string label) { if (!value) throw new Exception(label); log.Add("PASS " + label); }
    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    [STAThread] static int Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/YerelDisk;component/Theme.xaml") });
        using var errors = new StringWriter(); PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(errors)); PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        app.Startup += async (_, _) =>
        {
            var scan = new ScanFake(); var refresh = new RefreshFake(); using var model = new WorkspaceViewModel(scan, new InventoryFake(), refresh);
            var window = new MainWindow(model) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -24000, Top = 0, ShowInTaskbar = false, ShowActivated = false }; app.MainWindow = window; window.Show();
            try
            {
                Check(!window.RefreshSelectedButton.IsEnabled && ToolTipService.GetShowOnDisabled(window.RefreshSelectedButton), "New action starts disabled with an explanation available on hover");
                await window.NavigateAsync(WorkspaceView.Settings); window.RefreshCacheSelector.SelectedValue = 64; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(model.RefreshCacheMiB == 64 && window.RefreshCacheSelector.IsEnabled, "Actual settings selector enables a session-only cache budget");
                await window.NavigateAsync(WorkspaceView.Folders); model.Roots.Add(@"C:\scope"); await model.ScanAsync(); window.ConfigureColumns(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                model.SelectedRow = model.Rows.OfType<FolderEntry>().Single(f => f.Name == "a");
                Check(window.RefreshSelectedButton.IsEnabled && window.RefreshSelectedButton.ToolTip.ToString()!.Contains(@"C:\scope\a"), "Real folder action targets the selected folder with explicit scope help");
                var old = model.Scan; var rows = model.Rows; var time = model.MeasurementCompletedAt; refresh.Pending = Data.Gate(); Click(window.RefreshSelectedButton); var running = model.CurrentScan;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(!window.RefreshSelectedButton.IsEnabled && !window.RemeasureButton.IsEnabled && !window.ScanButton.IsEnabled && !window.RefreshCacheSelector.IsEnabled, "Native preparation disables all competing actions and settings");
                Check(ReferenceEquals(window.ResultsGrid.ItemsSource, rows) && ReferenceEquals(model.Scan, old) && model.Status.Contains("Eski sonuç"), "Actual window keeps old rows and a clear preparation status");
                var lease = new LeaseFake(Data.Outcome(12, true)); refresh.Pending.SetResult(lease); await running; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(model.HasMixedMeasurements && model.MeasurementCompletedAt == time && lease.Validations == 1 && lease.Disposals == 1, "Window publishes a mixed generation only after lease validation");
                Check(!window.ComparisonButton.IsEnabled && window.ComparisonButton.ToolTip.ToString()!.Contains("tüm kapsamı"), "Reference action is disabled with a full-measurement recovery hint");
                var column = window.ResultsGrid.Columns.OfType<DataGridTextColumn>().Single(c => c.SortMemberPath == "Measurement"); var binding = (Binding)column.Binding; var converter = binding.Converter;
                var a = model.Rows.OfType<FolderEntry>().Single(f => f.Name == "a"); var b = model.Rows.OfType<FolderEntry>().Single(f => f.Name == "b");
                Check(converter.Convert(a, typeof(string), "Measurement", CultureInfo.CurrentCulture).ToString()!.StartsWith("Yenilenen alan"), "Table measurement cell visibly identifies the newly measured area");
                Check(converter.Convert(b, typeof(string), "Measurement", CultureInfo.CurrentCulture).ToString()!.StartsWith("Önceki ölçüm"), "Table measurement cell identifies an older outside area");
                Check(converter.Convert(b, typeof(string), "Full", CultureInfo.CurrentCulture).ToString()!.Contains("yeniden taranmadı"), "Cell tooltip explains that outside folders were not rescanned");
                foreach (var size in new[] { (940d, 680d), (1480d, 940d) }) foreach (bool dark in new[] { false, true })
                {
                    window.Width = size.Item1; window.Height = size.Item2; window.SetTheme(dark); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout(); var content = (FrameworkElement)window.Content; string label = $"{size.Item1}-{(dark ? "dark" : "light")}";
                    Check(Inside(window.RefreshSelectedButton, content) && Inside(window.PopulationButton, content), "Folder actions fit " + label);
                    Check(window.ResultsGrid.ActualHeight >= window.ResultsGrid.ColumnHeaderHeight + 38, "Mixed notice keeps one complete result row " + label);
                    Check(window.ScopeNoticeCard.Visibility == Visibility.Visible && window.ScopeNoticeText.Text.Contains("Karma ölçüm"), "Mixed provenance remains visible " + label);
                    Screenshot(window, Path.Combine(output, "mixed-" + label + ".png"));
                }
                model.SelectedRow = b; var details = window.CreateDetailsWindow();
                Check(model.FullDetails.Contains("yeniden taranmadı") && model.Details.Sections[0].Heading == "Ölçüm zamanı", "The actual F6 model preserves the older-area explanation"); details.Close();
                refresh.Pending = Data.Gate(); Click(window.RefreshSelectedButton); running = model.CurrentScan; model.CancelScan(); var prior = model.Scan; var rejected = new LeaseFake(Data.Outcome(99, true)); refresh.Pending.SetResult(rejected); await running; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(ReferenceEquals(model.Scan, prior) && rejected.Disposals == 1 && rejected.Validations == 0, "Stopping actual selected action rejects a late service result");
                Click(window.RemeasureButton); await model.CurrentScan; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(!model.HasMixedMeasurements && window.ComparisonButton.IsEnabled && model.MeasurementCompletedAt > time, "Existing full-scope button clears mixed state and re-enables references");
                await window.NavigateAsync(WorkspaceView.Settings); window.RefreshCacheSelector.SelectedValue = 0; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(model.RefreshCacheMiB == 0 && !model.CanRefreshSelected, "Actual selector can disable cache retention again");
                Screenshot(window, Path.Combine(output, "settings.png"));
                await ActionClarity(window, model, scan, refresh, output);
                await ReviewTimes(window, model, refresh, output);
                PresentationTraceSources.DataBindingSource.Flush(); Check(errors.ToString().Length == 0, "No WPF binding errors in selected refresh and settings flow");
                window.Close(); log.Add($"ALL CHECKS PASSED ({log.Count})"); app.Shutdown(0);
            }
            catch (Exception ex) { log.Add("FAIL " + ex); if (window.IsVisible) Screenshot(window, Path.Combine(output, "failure.png")); app.Shutdown(1); }
            finally { File.WriteAllLines(Path.Combine(output, "results.txt"), log); foreach (string line in log) Console.WriteLine(line); }
        };
        return app.Run();
    }
    static async Task ActionClarity(MainWindow window, WorkspaceViewModel model, ScanFake scan, RefreshFake refresh, string output)
    {
        await window.NavigateAsync(WorkspaceView.Folders); model.RefreshCacheMiB = 64; model.SelectedRow = null;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(window.ScanButton.Content.ToString() == "Yeni tam tarama" && window.ScanButton.ToolTip.ToString()!.Contains("yalnız doğrulama yapmaz"), "Actual primary button openly starts a new full scan");
        Check(window.RemeasureButton.Content.ToString() == "Tümünü yeniden ölç", "Existing-result replacement button explicitly describes complete scope");
        Check(window.RefreshSelectedButton.Content.ToString() == "Tüm kapsamı yenile" && window.RefreshTargetText.Text.Contains(@"C:\scope") && window.RefreshTargetText.Text.Contains("tüm kapsam"), "Root refresh and its visible target announce complete scope before clicking");
        model.SelectedRow = model.Rows.OfType<FolderEntry>().Single(f => f.Name == "a"); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(window.RefreshSelectedButton.Content.ToString() == "Klasörü yenile" && window.RefreshTargetText.Text.Contains(@"C:\scope\a") && window.RefreshTargetText.Text.Contains("seçili klasör"), "Changing selection immediately updates the visible refresh target");
        refresh.Pending = Data.Gate(); Click(window.RefreshSelectedButton); var running = model.CurrentScan;
        refresh.Progress!.Report(new(0, 0, 0, 0, "full boundary", TimeSpan.Zero, ScanPhase.Refresh, RefreshReason.SharedOutside));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(model.Status.Contains("Neden:") && model.Status.Contains("ortak dosya kimliği"), "Live full fallback identifies the actual shared-identity reason");
        refresh.Progress.Report(new(12, 3, 40, 0, "next file", TimeSpan.FromSeconds(1)));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(model.Status.Contains("ortak dosya kimliği") && model.Status.Contains("next file"), "Ordinary scan progress retains the full fallback reason");
        refresh.Pending.SetResult(new LeaseFake(Data.Outcome(20), reason: RefreshReason.SharedOutside)); await running;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(model.Status.Contains("ortak dosya kimliği") && model.RefreshDecisionNotice.Contains("Tüm mevcut kapsam"), "Completed full fallback retains its real reason instead of a generic fast-path message");
        Check(window.RefreshSelectedButton.ToolTip.ToString()!.Contains("Son tamamlanan") && window.RefreshSelectedButton.ToolTip.ToString()!.Contains("ortak dosya kimliği"), "Last completed decision is available in the actual refresh tooltip");
        var saved = model.RefreshDecisionNotice; var previous = model.Scan;
        refresh.Pending = Data.Gate(); Click(window.RefreshSelectedButton); running = model.CurrentScan;
        refresh.Progress!.Report(new(0, 0, 0, 0, "budget", TimeSpan.Zero, ScanPhase.Refresh, RefreshReason.MetadataBudget));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); model.CancelScan();
        refresh.Pending.SetResult(new LeaseFake(Data.Outcome(30), reason: RefreshReason.MetadataBudget)); await running;
        Check(ReferenceEquals(model.Scan, previous) && model.RefreshDecisionNotice == saved, "Cancelled replacement cannot overwrite the previous completed decision");
        model.SelectedRow = null; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        foreach (bool dark in new[] { false, true })
        {
            window.Width = 940; window.Height = 680; window.SetTheme(dark); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout(); var content = (FrameworkElement)window.Content;
            Check(Inside(window.ScanButton, content) && Inside(window.RemeasureButton, content) && Inside(window.RefreshSelectedButton, content), "Explicit complete-scope actions fit the compact window dark=" + dark);
            Check(window.RefreshTargetText.Visibility == Visibility.Visible && window.RefreshTargetText.ActualWidth > 80 && Inside(window.RefreshTargetText, content), "Visible refresh target retains usable space in the compact window dark=" + dark);
            Screenshot(window, Path.Combine(output, "refresh-clarity-" + (dark ? "dark" : "light") + ".png"));
        }
        Check(WorkspaceViewModel.FullRefreshExplanation(RefreshReason.MissingIndex, RefreshIndexState.BudgetExceeded).Contains("bütçesine sığmadı") && !WorkspaceViewModel.FullRefreshExplanation(RefreshReason.MissingIndex, null).Contains("bütçesine sığmadı"), "An oversized old index is distinguished from an index that was not retained");
        Click(window.RemeasureButton); await model.CurrentScan;
        Check(model.RefreshDecisionNotice.Length == 0, "Explicit full remeasurement clears stale selected-refresh decisions");
        refresh.Pending = null; refresh.Next = () => new LeaseFake(Data.Outcome(20), reason: RefreshReason.MissingIndex);
        model.SelectedRow = model.Rows.OfType<FolderEntry>().Single(f => f.Name == "a"); await model.RefreshSelectedAsync();
        scan.Next = () => { var outcome = Data.Outcome(); outcome.Scan.UnverifiedPaths.Add(@"C:\scope\a"); return outcome; };
        int calls = scan.Calls; Click(window.ScanButton); await model.CurrentScan; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(scan.Calls == calls + 1 && model.RefreshDecisionNotice.Length == 0 && model.Scan!.HasUnverified && window.ScanButton.Content.ToString() == "Yeni tam tarama", "Primary click starts a complete new scan and unverified results never relabel it as verification");
        scan.Next = () => Data.Outcome();
    }
    static async Task ReviewTimes(MainWindow window, WorkspaceViewModel model, RefreshFake refresh, string output)
    {
        model.RefreshCacheMiB = 64; model.ReviewMinimum = 0; refresh.Pending = null;
        async Task Show(ScanOutcome outcome, bool unknown = false)
        {
            await window.NavigateAsync(WorkspaceView.Folders);
            model.SelectedRow = model.Rows.OfType<FolderEntry>().Single(f => f.Name == "a");
            refresh.Next = () => new LeaseFake(outcome); await model.RefreshSelectedAsync();
            await window.NavigateAsync(WorkspaceView.Review);
            window.ReviewCollectionSelector.SelectedValue = unknown ? 1 : 0; await window.PendingView;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        }
        ReviewCandidate Candidate(string path, long bytes, bool partial = false) => new(path, bytes, bytes, 1,
            "fixture", "fixture", "fixture", "fixture", "fixture", partial, ReviewKind.Conditional, ExcludedSubtrees: partial ? 1 : 0);

        var aggregate = Data.Outcome(12, true); var parent = Candidate(aggregate.Scan.Root.Path, 32, true); aggregate.Candidates.Add(parent);
        await Show(aggregate); var parentCell = await ReviewCell(window, parent);
        Check(parentCell.Text.StartsWith("Karma zaman") && parentCell.Text.Contains("Kısmi / uyarılı") && parentCell.Text.Contains("hariç tutma"), "Actual rule row identifies mixed parent time without dropping partial or exclusion warnings");
        Check(parentCell.ToolTip?.ToString()?.Contains("farklı zamanlarda") == true, "Actual rule-cell tooltip explains mixed aggregate measurement times");
        var separate = Data.Outcome(12, true); var selected = Candidate(@"C:\scope\a", 12); var outside = Candidate(@"C:\scope\b", 20, true); separate.Candidates.AddRange([selected, outside]);
        await Show(separate);
        Check((await ReviewCell(window, selected)).Text.StartsWith("Yenilenen alan"), "Actual refreshed rule row shows its new-area provenance");
        var outsideCell = await ReviewCell(window, outside);
        Check(outsideCell.Text.StartsWith("Önceki ölçüm") && outsideCell.Text.Contains("Kısmi / uyarılı"), "Actual outside rule row retains both old-time and partial-measurement signals");
        Check(outsideCell.ToolTip?.ToString()?.Contains("yeniden taranmadı") == true, "Actual outside rule tooltip identifies the area that was not rescanned");
        foreach (bool dark in new[] { false, true })
        {
            window.Width = 940; window.Height = 680; window.SetTheme(dark); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout(); outsideCell = await ReviewCell(window, outside);
            Check(outsideCell.ActualHeight + outsideCell.Margin.Top + outsideCell.Margin.Bottom + 1 >= outsideCell.DesiredSize.Height, "Review time and warning text fits compact theme dark=" + dark);
            Screenshot(window, Path.Combine(output, "review-time-" + (dark ? "dark" : "light") + ".png"));
        }
        await Show(Data.Outcome(12, true), true);
        var freshFolder = model.Rows.OfType<FolderEntry>().Single(f => f.Name == "a"); var oldFolder = model.Rows.OfType<FolderEntry>().Single(f => f.Name == "b");
        Check((await ReviewCell(window, freshFolder)).Text.StartsWith("Yenilenen alan"), "Actual unknown-area row shares the refreshed measurement formatter");
        Check((await ReviewCell(window, oldFolder)).Text.StartsWith("Önceki ölçüm"), "Actual unknown-area row shares the older measurement formatter");
        Click(window.RemeasureButton); await model.CurrentScan; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        foreach (var row in model.Rows) Check((await ReviewCell(window, row)).Text == RowValues.MeasurementOf(row), "Full-result review measurement text remains unchanged for " + RowValues.PathOf(row));
    }
    static async Task<TextBlock> ReviewCell(MainWindow window, object row)
    {
        window.ResultsGrid.ScrollIntoView(row); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        var column = window.ResultsGrid.Columns.OfType<DataGridTemplateColumn>().Single(c => c.SortMemberPath == "Measurement");
        var content = column.GetCellContent(row) ?? throw new Exception("Actual measurement cell was not generated");
        return Descendants<TextBlock>(content).Single();
    }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    { if (root is T match) yield return match; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child; }
    static bool Inside(FrameworkElement child, FrameworkElement parent) { var bounds = child.TransformToAncestor(parent).TransformBounds(new Rect(child.RenderSize)); return bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= parent.ActualWidth && bounds.Bottom <= parent.ActualHeight; }
    static void Screenshot(Window window, string path) { var content = (FrameworkElement)window.Content; var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream); }
}
