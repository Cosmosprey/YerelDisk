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

internal static class ReviewUiTests
{
    static readonly List<string> log=[];
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);log.Add("PASS "+message);}
    [STAThread] static int Main(string[] args)
    {
        string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
        CultureInfo.DefaultThreadCurrentCulture=CultureInfo.GetCultureInfo("tr-TR");
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/YerelDisk;component/Theme.xaml")});
        var bindingErrors=new StringWriter();PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(bindingErrors));PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        app.Startup+=async(_,_)=>
        {
            using var model=new WorkspaceViewModel(new FakeService());
            var window=new MainWindow(model){WindowStartupLocation=WindowStartupLocation.Manual,Left=-24000,Top=0,ShowInTaskbar=false,ShowActivated=false};app.MainWindow=window;window.Show();
            try
            {
                model.Roots.Add(FakeService.Outcome.Scan.Root.Path);await model.ScanAsync();model.ReviewMinimum=0;await window.NavigateAsync(WorkspaceView.Review);model.SelectedRow=model.Rows.Single();
                Check(model.CanUseSelection && model.Details.Sections[0].Caution && model.Details.Summary.Contains("Kullanımına bağlı"),"Evidence-adjusted review selection exposes caution before actions");
                foreach(var size in new[]{(940d,680d),(1480d,940d)})foreach(bool dark in new[]{false,true})
                {
                    string label=$"{size.Item1}-{(dark?"dark":"light")}";window.SetTheme(dark);window.Width=size.Item1;window.Height=size.Item2;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);window.UpdateLayout();
                    Check(window.ResultsGrid.ActualHeight>80 && model.RowCount==1,"Review table remains usable: "+label);
                    if(size.Item1>=1220)
                        Check(Descendants<TextBox>(window.SelectionContent).Any(t=>t.Text.Contains("local-mail.pst")),"Wide side pane renders file clue: "+label);
                    else Check(window.CompactSelectionActions.Visibility==Visibility.Visible,"Narrow review keeps actions accessible: "+label);
                    await Capture(window,output,"review-"+label+".png");
                    var details=window.CreateDetailsWindow();details.Owner=window;details.WindowStartupLocation=WindowStartupLocation.Manual;details.Left=-24000;details.Top=0;details.Width=540;details.Height=400;details.ShowActivated=false;details.ShowInTaskbar=false;details.Show();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);details.UpdateLayout();
                    Check(details.DetailScroll.ViewportHeight>50 && details.DetailScroll.ScrollableHeight>0 && details.DetailPath.Text==model.SelectedPath,"Minimum F6 keeps full path and scrollable risks: "+label);
                    Check(Descendants<TextBox>(details.ContentView).Any(t=>t.Text.Contains("local-mail.pst")&&t.Text.Contains("yalnız tutulan")),"F6 contains concrete clue and coverage limit: "+label);
                    await Capture(details,output,"details-"+label+".png");details.Close();
                }
                using var csv=new StringWriter();ReportSnapshot.Capture(model).Write(csv);
                Check(csv.ToString().Contains("Dosya adı ipuçları")&&csv.ToString().Contains("local-mail.pst")&&csv.ToString().Contains("Kullanımına bağlı"),"Applied review CSV retains type and file clues");
                PresentationTraceSources.DataBindingSource.Flush();Check(bindingErrors.ToString().Length==0,"No new WPF binding errors");
                log.Add($"ALL CHECKS PASSED ({log.Count})");File.WriteAllLines(Path.Combine(output,"results.txt"),log);Console.WriteLine(log[^1]);app.Shutdown(0);
            }
            catch(Exception ex){log.Add("FAIL "+ex);File.WriteAllLines(Path.Combine(output,"results.txt"),log);Console.WriteLine(ex);app.Shutdown(1);}
        };
        return app.Run();
    }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var c=VisualTreeHelper.GetChild(root,i);if(c is T value)yield return value;foreach(var child in Descendants<T>(c))yield return child;}}
    static async Task Capture(Window window,string directory,string name)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);window.UpdateLayout();var content=(FrameworkElement)window.Content;var dpi=VisualTreeHelper.GetDpi(content);
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(content.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
        bitmap.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=System.IO.File.Create(Path.Combine(directory,name));encoder.Save(stream);
    }
    sealed class FakeService:IScanService
    {
        internal static ScanOutcome Outcome{get;}=Create();
        static ScanOutcome Create()
        {
            string home=@"C:\Review-Test\User";string path=home+@"\.gradle\caches\"+new string('x',90)+"\\"+new string('y',90);
            var root=new FolderEntry(path,null){LogicalBytes=9_000_000,AllocatedBytes=10_000_000,FileCount=3};var scan=new ScanResult(root){Folders=1};
            foreach(string file in new[]{"working-disk.vhdx","local-mail.pst","custom-model.gguf"})scan.Files.Add(new(Path.Combine(path,file),1_000_000,1_100_000,DateTime.MinValue,false,""));
            var rules=new ReviewRules(home,home+@"\AppData\Local",@"C:\ProgramData",@"C:\Windows");return new(scan,rules.Analyze(scan));
        }
        public string[] NormalizeRoots(IEnumerable<string> paths)=>paths.ToArray();
        public Task<ScanOutcome> ScanAsync(ScanRequest request,IProgress<ScanProgress>? progress,CancellationToken token)=>Task.FromResult(Outcome);
    }
}
