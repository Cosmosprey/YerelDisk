using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YerelDisk;
using YerelDisk.Presentation;
using YerelDisk.Wpf;

// Entirely synthetic metadata: no actual disk or registry inventory is collected.
static class Screenshots
{
    [STAThread] static int Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/YerelDisk;component/Theme.xaml") });
        app.Startup += async (_, _) =>
        {
            using var model = new WorkspaceViewModel(new ExampleScan());
            var window = new MainWindow(model) { Width = 1280, Height = 840, WindowStartupLocation = WindowStartupLocation.Manual, Left = -24000, Top = 0, ShowInTaskbar = false, ShowActivated = false };
            app.MainWindow = window;
            try
            {
                window.Show(); model.Roots.Add(@"D:\OrnekDisk"); await model.ScanAsync();
                await window.NavigateAsync(WorkspaceView.Folders); window.SetTheme(false);
                await Capture(window, output, "folders.png");
                await model.SetDistributionModeAsync(true); window.SetTheme(true);
                await Capture(window, output, "distribution.png");
                model.ReviewMinimum = 0; await window.NavigateAsync(WorkspaceView.Review); await model.RefreshAsync();
                if (model.Rows.Count == 0) throw new Exception("Example review fixture produced no rows.");
                model.SelectedRow = model.Rows[0];
                await Capture(window, output, "review.png");
                Console.WriteLine("Generated 3 screenshots from synthetic metadata only.");
                window.Close(); app.Shutdown(0);
            }
            catch (Exception error) { Console.Error.WriteLine(error); window.Close(); app.Shutdown(1); }
        };
        return app.Run();
    }
    static async Task Capture(Window window, string directory, string name)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        var content = (FrameworkElement)window.Content; var dpi = VisualTreeHelper.GetDpi(content);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
    }
    sealed class ExampleScan : IScanService
    {
        static long GiB(double value) => (long)(value * 1024 * 1024 * 1024);
        static FolderEntry Add(FolderEntry parent, string name, double gib, long count)
        { var node = new FolderEntry(Path.Combine(parent.Path, name), parent) { LogicalBytes = GiB(gib), AllocatedBytes = GiB(gib), FileCount = count }; parent.Children.Add(node); return node; }
        public string[] NormalizeRoots(IEnumerable<string> roots) => roots.ToArray();
        public Task<ScanOutcome> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken token)
        {
            var root = new FolderEntry(@"D:\OrnekDisk", null) { LogicalBytes = GiB(36), AllocatedBytes = GiB(36), FileCount = 24630 };
            var videos = Add(root, "Videolar", 18, 24); var projects = Add(root, "Projeler", 9, 18000);
            var user = Add(root, "Kullanici", 6, 6000); Add(root, "Arsiv", 3, 606);
            var gradle = Add(user, ".gradle", 4.5, 4500); var cache = Add(gradle, "caches", 4.5, 4500);
            var appData = Add(user, "AppData", 1.5, 1500); var local = Add(appData, "Local", 1.5, 1500); Add(local, "Temp", .8, 700);
            var web = Add(projects, "WebProjesi", 3, 15000); Add(web, "node_modules", 2.4, 14000);
            var scan = new ScanResult(root) { Folders = 11, Workers = 4, Elapsed = TimeSpan.FromSeconds(2.4) };
            scan.Files.Add(new(Path.Combine(videos.Path, "gezi-kurgusu.mp4"), GiB(8.2), GiB(8.2), new DateTime(2026, 5, 14), false, "Video dosyası"));
            scan.Files.Add(new(Path.Combine(videos.Path, "kayit-arsivi.mkv"), GiB(5.4), GiB(5.4), new DateTime(2025, 9, 20), false, "Video dosyası"));
            var rules = new ReviewRules(user.Path, local.Path, @"C:\ProgramData", @"C:\Windows");
            return Task.FromResult(new ScanOutcome(scan, rules.Analyze(scan)));
        }
    }
}
