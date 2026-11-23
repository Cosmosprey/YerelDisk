using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using YerelDisk;
using YerelDisk.Presentation;
using YerelDisk.Wpf;

internal static class Tests
{
    static readonly List<string> log = [];
    const string Root = @"C:\comparison";
    const string Child = Root + @"\changes";
    const string Grandchild = Child + @"\nested";
    static void Check(bool passed, string name)
    {
        if (!passed) throw new InvalidOperationException(name);
        log.Add("PASS " + name);
    }

    [STAThread] static int Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/YerelDisk;component/Theme.xaml") });
        using var errors = new StringWriter();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(errors));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        app.Startup += async (_, _) =>
        {
            var fake = new FakeService();
            using var model = new WorkspaceViewModel(fake);
            var main = new MainWindow(model) { Left = -24000, Top = 0, ShowInTaskbar = false, ShowActivated = false };
            app.MainWindow = main;
            main.Show();
            try
            {
                Check(!main.ComparisonButton.IsEnabled, "Comparison requires a scan");
                model.Roots.Add(Root);
                await model.ScanAsync();
                Check(main.ComparisonButton.IsEnabled && model.MeasurementCompletedAt != default, "Completed scan enables entry and records a timestamp");
                string filename = Path.Combine(output, "reference.yereldisk.json");
                FolderMeasurements.Capture(Example(100), model.MeasurementCompletedAt.AddDays(-1)).Write(filename);
                var library = new ReferenceLibrary(Path.Combine(output, "referanslar.json"));
                var dialog = new ComparisonWindow(fake.Outcome.Scan, model.MeasurementCompletedAt, library)
                {
                    Owner = main, WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -24000, Top = 0, ShowInTaskbar = false, ShowActivated = false
                };
                dialog.Show();
                Check(dialog.Changes.Visibility == Visibility.Collapsed && dialog.Feedback.Text.Contains("İlk kullanımda"), "Empty view explains first use");
                string compact = Path.Combine(output, "saved.yereldisk");
                await dialog.SaveReferenceAsync(compact);
                Check(dialog.Feedback.Text.Contains("Referans kaydedildi") && File.ReadAllBytes(compact).AsSpan(0, 8).SequenceEqual("YDKREF01"u8),
                    "Save action creates the compact reference");
                Check(library.Recent is [{ Path: var savedPath, FileBytes: var savedBytes }] && savedPath == compact
                    && savedBytes == new FileInfo(compact).Length, "Save action records the exact size of the saved file");
                await dialog.OpenReferenceAsync(compact);
                Check(dialog.Changes.Items.Count == 0 && dialog.Feedback.Text.Contains("boyut farkı bulunmadı"),
                    "Saved compact reference opens through the same comparison action");
                await dialog.OpenReferenceAsync(filename);
                Check(dialog.Changes.Items.Count == 3 && dialog.Dates.Text.Contains("Referans"), "Reference opens and displays ranked changes and dates");
                Check(library.Recent.Count == 2 && library.Recent[0].Path == filename
                    && library.Recent[0].FileBytes == new FileInfo(filename).Length, "Successful comparison promotes the reference without duplicating entries");
                var recent = new ReferenceLibraryWindow(library)
                {
                    Owner = dialog, WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -24000, Top = 0, ShowActivated = false
                };
                Check(recent.References.Items.Count == 2 && recent.CompareButton.IsEnabled, "Recent reference list starts with an actionable selection");
                recent.UpdateFolder(output);
                Check(recent.FolderLabel.Text.Contains(output) && library.PickerDirectory() == output, "Folder selection is shown and used for dialogs");
                recent.References.SelectedIndex = 1;
                recent.RemoveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(recent.References.Items.Count == 1 && File.Exists(compact), "Remove action only takes the reference off the list");
                _ = recent.Dispatcher.InvokeAsync(() => recent.CompareButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                Check(recent.ShowDialog() == true && recent.SelectedPath == filename, "Compare action returns the selected path");
                foreach (bool dark in new[] { false, true })
                {
                    main.SetTheme(dark);
                    dialog.Width = dark ? 670 : 1050;
                    dialog.Height = dark ? 500 : 680;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    dialog.UpdateLayout();
                    double twoRows = dialog.Changes.ColumnHeaderHeight + 2 * dialog.Changes.RowHeight;
                    Check(dialog.Changes.ActualHeight >= twoRows && dialog.Navigation.ActualWidth > 0,
                        $"Usable grid in {(dark ? "compact dark" : "wide light")} ({dialog.Changes.ActualHeight:N0} px)");
                    // Claude 1.21.1: implicit Window styles skip derived windows; the dialog sets its own theme brushes.
                    var themed = new ReferenceLibraryWindow(library)
                    {
                        Owner = dialog, WindowStartupLocation = WindowStartupLocation.Manual, Left = -24000, Top = 0, ShowActivated = false
                    };
                    themed.Show();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    static System.Windows.Media.Color Of(object brush) => ((System.Windows.Media.SolidColorBrush)brush).Color;
                    Check(Of(themed.Background) == Of(Application.Current.Resources["Canvas"])
                        && Of(themed.FolderLabel.Foreground) == Of(Application.Current.Resources["Ink"])
                        && Of(themed.LibraryFeedback.Foreground) == Of(Application.Current.Resources["Ink"]) && themed.FontSize == 13,
                        $"Recent references window follows the {(dark ? "dark" : "light")} theme");
                    themed.Close();
                }
                dialog.RootsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitFor(() => dialog.Location.Text == "Seçili kökler");
                Check(dialog.Changes.Items is [{ }] && dialog.Changes.Items[0] is FolderChange { Path: Root }, "Root navigation shows selected scope");
                dialog.Changes.SelectedIndex = 0;
                Check(dialog.EnterButton.IsEnabled, "Selection enables drilling down");
                dialog.EnterButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitFor(() => dialog.Location.Text == Root);
                Check(dialog.Changes.Items[0] is FolderChange { Path: Child, Difference: 800 }, "Drill-down exposes the matching child delta");
                dialog.UpButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitFor(() => dialog.Location.Text == "Seçili kökler");
                Check(!dialog.UpButton.IsEnabled, "Up returns to selected roots");
                string legacy = Path.Combine(output, "legacy.yereldisk.json");
                (FolderMeasurements.Capture(Example(100), model.MeasurementCompletedAt.AddDays(-1))
                    with { Format = 1, RootVolumes = null }).Write(legacy);
                await dialog.OpenReferenceAsync(legacy);
                Check(dialog.Changes.Items.Count == 3 && dialog.Dates.Text.Contains("Disk kimliği doğrulanamadı"),
                    "Legacy reference opens with an explicit volume warning");
                string differentVolume = Path.Combine(output, "different-volume.yereldisk.json");
                (FolderMeasurements.Capture(Example(100), model.MeasurementCompletedAt.AddDays(-1))
                    with { RootVolumes = [new(Root, Guid.NewGuid())] }).Write(differentVolume);
                await dialog.OpenReferenceAsync(differentVolume);
                Check(dialog.Changes.Items.Count == 3 && dialog.Feedback.Text.Contains("Sürücü birimi değişmiş")
                    && dialog.Dates.Text.Contains("Disk kimliği doğrulanamadı"), "Volume mismatch is rejected and the previous warning stays visible");
                string corrupt = Path.Combine(output, "corrupt.yereldisk.json");
                File.WriteAllText(corrupt, "{");
                await dialog.OpenReferenceAsync(corrupt);
                Check(dialog.Feedback.Text.Contains("okunamadı") && dialog.Changes.Items.Count == 3, "Malformed reference leaves previous results visible");
                Check(library.Recent.All(item => item.Path != corrupt && item.Path != differentVolume), "Failed comparisons do not enter the history");
                // Claude 1.20.1: Windows file errors, gzip damage and busy closing show Turkish guidance.
                string locked = Path.Combine(output, "locked.yereldisk");
                File.Copy(compact, locked, true);
                byte[] lockedBytes = File.ReadAllBytes(locked);
                using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
                    await dialog.SaveReferenceAsync(locked);
                Check(dialog.Feedback.Text.StartsWith("Dosyaya erişilemedi") && File.ReadAllBytes(locked).SequenceEqual(lockedBytes)
                    && dialog.Changes.Items.Count == 3, "Locked save target is kept and explained in Turkish");
                byte[] gzip = File.ReadAllBytes(compact)[40..];
                gzip[^8] ^= 0x55;
                string rehashed = Path.Combine(output, "rehashed-crc.yereldisk");
                File.WriteAllBytes(rehashed, [.. "YDKREF01"u8, .. System.Security.Cryptography.SHA256.HashData(gzip), .. gzip]);
                await dialog.OpenReferenceAsync(rehashed);
                Check(dialog.Feedback.Text.Contains("bozuk veya eksik") && dialog.Changes.Items.Count == 3,
                    "Gzip damage behind a valid checksum uses the Turkish container message");
                var pending = dialog.OpenReferenceAsync(compact);
                dialog.Close();
                Check(dialog.IsVisible && dialog.Feedback.Text.Contains("İşlem sürüyor"), "Closing while busy explains why the window stays open");
                await pending;
                dialog.Close();
                await InspectionChecks(main, model, fake, filename, output);
                fake.Outcome.Scan.Cancelled = true;
                await model.ScanAsync();
                Check(!main.ComparisonButton.IsEnabled, "Cancelled scan disables entry again");
                PresentationTraceSources.DataBindingSource.Flush();
                Check(errors.ToString().Length == 0, "No WPF binding errors");
                log.Add($"ALL CHECKS PASSED ({log.Count})");
                app.Shutdown(0);
            }
            catch (Exception ex) { log.Add("FAIL " + ex); app.Shutdown(1); }
            finally { File.WriteAllLines(Path.Combine(output, "results.txt"), log); }
        };
        return app.Run();
    }

    static async Task WaitFor(Func<bool> ready)
    {
        for (int i = 0; i < 200 && !ready(); i++) await Task.Delay(10);
        if (!ready()) throw new TimeoutException("Comparison navigation did not finish.");
    }

    static async Task InspectionChecks(MainWindow main, WorkspaceViewModel model, FakeService fake, string reference, string output)
    {
        var scan = fake.Outcome.Scan;
        var leaf = scan.Root.Children[0].Children[0];
        Check(ReferenceEquals(ComparisonInspection.Resolve(scan, new(Grandchild, 100, 900, false)), leaf),
            "Inspection resolves a leaf even when there are no comparison children");
        Check(ComparisonInspection.Resolve(scan, new(Root + @"\removed", 100, 0, false)) == null,
            "Removed folders cannot be mistaken for an existing zero-byte folder");
        Check(ComparisonInspection.Resolve(scan, new(Grandchild, 100, null, false)) == null,
            "Unverified current values cannot enable inspection");
        Check(ComparisonInspection.FindMeasuredFolder(scan, Grandchild.ToUpperInvariant()) == null
            && ComparisonInspection.FindMeasuredFolder(scan, Root + @"\changes-other\nested") == null,
            "Inspection preserves exact path spelling and component boundaries");
        var zero = new FolderEntry(Root + @"\empty", scan.Root);
        scan.Root.Children.Add(zero);
        Check(ReferenceEquals(ComparisonInspection.Resolve(scan, new(zero.Path, 10, 0, false)), zero),
            "Existing measured zero-byte folders remain actionable");
        scan.Root.Children.Remove(zero);
        scan.UnverifiedPaths.Add(Child);
        Check(ComparisonInspection.FindMeasuredFolder(scan, Grandchild) == null, "Unverified ancestor blocks inspection");
        scan.UnverifiedPaths.Clear();
        scan.ExcludedPaths = [Child];
        Check(ComparisonInspection.FindMeasuredFolder(scan, Grandchild) == null, "Excluded paths cannot be inspected");
        scan.ExcludedPaths = [];
        leaf.Measurement = MeasurementState.Previous;
        Check(ComparisonInspection.FindMeasuredFolder(scan, Grandchild) == null, "Previous measurements cannot be inspected");
        leaf.Measurement = MeasurementState.Measured;
        scan.Root.Children[0].Measurement = MeasurementState.Unknown;
        Check(ComparisonInspection.FindMeasuredFolder(scan, Grandchild) == null, "Unknown ancestor blocks a nominally measured descendant");
        scan.Root.Children[0].Measurement = MeasurementState.Partial;
        Check(ReferenceEquals(ComparisonInspection.FindMeasuredFolder(scan, Grandchild), leaf), "Measured descendants of a partial parent remain reachable");
        scan.Root.Children[0].Measurement = MeasurementState.Measured;
        var second = new FolderEntry(@"D:\second", null);
        var combined = new ScanResult(new("Seçilen klasörler (2)", null));
        combined.Root.Children.AddRange([scan.Root, second]); combined.RootPaths.Clear(); combined.RootPaths.AddRange([Root, second.Path]);
        Check(ReferenceEquals(ComparisonInspection.FindMeasuredFolder(combined, second.Path), second), "Multiple selected roots resolve without treating the synthetic root as a disk path");

        var dialog = new ComparisonWindow(scan, model.MeasurementCompletedAt, new ReferenceLibrary(Path.Combine(output, "inspection-library.json")))
        { Owner = main, WindowStartupLocation = WindowStartupLocation.Manual, Left = -24000, Top = 0, ShowInTaskbar = false, ShowActivated = false };
        dialog.Show();
        await dialog.OpenReferenceAsync(reference);
        Check(!dialog.InspectButton.IsEnabled && dialog.InspectionHint.Text.Contains("bir fark satırı"), "Inspection starts disabled with a visible selection hint");
        var rows = new FolderChange[] { new(Root + @"\removed", 100, 0, false), new(Child, 100, null, true), new(Grandchild, 100, 900, false) };
        dialog.Changes.ItemsSource = rows;
        dialog.Changes.SelectedItem = rows[0];
        Check(!dialog.InspectButton.IsEnabled && dialog.InspectionHint.Text.Contains("son ölçümde yok"), "Removed row explains why inspection is unavailable");
        dialog.Changes.SelectedItem = rows[1];
        Check(!dialog.InspectButton.IsEnabled && dialog.InspectionHint.Text.Contains("doğrulanamadı"), "Unverified row explains why inspection is unavailable");
        dialog.Changes.SelectedItem = rows[2];
        Check(dialog.InspectButton.IsEnabled && !dialog.EnterButton.IsEnabled, "Leaf inspection is independent from comparison drill-down");
        foreach (bool dark in new[] { false, true })
        {
            main.SetTheme(dark); dialog.Width = dark ? 670 : 1050; dialog.Height = dark ? 500 : 680;
            dialog.Changes.ScrollIntoView(dialog.Changes.SelectedItem);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); dialog.UpdateLayout();
            var content = (FrameworkElement)dialog.Content;
            Rect bounds = dialog.InspectButton.TransformToAncestor(content).TransformBounds(new Rect(dialog.InspectButton.RenderSize));
            Check(dialog.Changes.ActualHeight >= dialog.Changes.ColumnHeaderHeight + 2 * dialog.Changes.RowHeight
                && bounds.Left >= 0 && bounds.Right <= content.ActualWidth && bounds.Bottom <= content.ActualHeight,
                $"Inspection button and two result rows fit in {(dark ? "compact dark" : "wide light")}");
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            var drawing = new System.Windows.Media.DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                context.DrawRectangle(dialog.Background, null, new Rect(content.RenderSize));
                context.DrawRectangle(new System.Windows.Media.VisualBrush(content), null, new Rect(content.RenderSize));
            }
            bitmap.Render(drawing);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var image = File.Create(Path.Combine(output, dark ? "inspection-compact-dark.png" : "inspection-wide-light.png")); png.Save(image);
        }
        dialog.InspectButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!dialog.IsVisible && dialog.InspectionPath == Grandchild, "Inspection button closes comparison and returns the exact selected path");
        await main.NavigateAsync(WorkspaceView.Files);
        model.Search = "filter-that-must-not-hide-the-target";
        var oldQuery = model.RefreshAsync(true);
        await main.InspectComparisonFolderAsync(scan, dialog.InspectionPath!);
        await oldQuery;
        Check(model.View == WorkspaceView.Folders && model.FolderPath == Grandchild && main.FoldersNav.IsChecked == true,
            "Inspection switches the real main view and navigation to the selected folder");
        Check(model.Search.Length == 0 && model.SortKey == "Size" && model.SortDescending && model.ShowTable && !model.IsRefreshing,
            "Inspection clears old filters, uses size order and supersedes a pending query");
        Check(!await model.InspectComparisonFolderAsync(new ScanResult(scan.Root), Root) && model.FolderPath == Grandchild,
            "A different scan object cannot navigate the current workspace");
        Check(!await model.InspectComparisonFolderAsync(scan, Root + @"\removed") && model.FolderPath == Grandchild,
            "Unavailable targets leave the current main folder unchanged");
        await main.InspectComparisonFolderAsync(scan, Root);
        Check(model.FolderPath == Root && model.Rows.Any(row => ReferenceEquals(row, scan.Root.Children[0])), "Selected roots open their actual child list");
    }

    static ScanResult Example(long size)
    {
        var root = new FolderEntry(Root, null) { LogicalBytes = size };
        var child = new FolderEntry(Child, root) { LogicalBytes = size };
        root.Children.Add(child);
        child.Children.Add(new(Grandchild, child) { LogicalBytes = size });
        return new(root) { RootVolumeIds = new Dictionary<string, Guid> { [Root] = new("CA2850BD-9DD1-484C-B6C3-E4BF7DC94BE0") } };
    }
    sealed class FakeService : IScanService
    {
        internal ScanOutcome Outcome { get; } = new(Example(900), []);
        public string[] NormalizeRoots(IEnumerable<string> paths) => paths.ToArray();
        public Task<ScanOutcome> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken token) => Task.FromResult(Outcome);
    }
}
