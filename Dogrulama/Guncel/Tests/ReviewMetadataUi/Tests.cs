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

internal static class MetadataUiTests
{
    static readonly List<string> log=[];
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);log.Add("PASS "+label);}
    static IEnumerable<T> Children<T>(DependencyObject root)where T:DependencyObject
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T item)yield return item;foreach(var nested in Children<T>(child))yield return nested;}}
    static void Screenshot(FrameworkElement area,string path)
    {var dpi=VisualTreeHelper.GetDpi(area);var bitmap=new RenderTargetBitmap((int)Math.Ceiling(area.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(area.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);bitmap.Render(area);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var stream=System.IO.File.Create(path);png.Save(stream);}
    [STAThread]static int Main(string[] args)
    {
        string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/YerelDisk;component/Theme.xaml")});
        var errors=new StringWriter();PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(errors));PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        app.Startup+=async(_,_)=>
        {
            var fake=new FakeService();using var model=new WorkspaceViewModel(fake);var window=new MainWindow(model){WindowStartupLocation=WindowStartupLocation.Manual,Left=-24000,Top=-24000,ShowInTaskbar=false,ShowActivated=false};
            try
            {
                window.Show();model.Roots.Add(fake.Outcome.Scan.Root.Path);await model.ScanAsync();model.ReviewMinimum=0;await window.NavigateAsync(WorkspaceView.Review);model.SelectedRow=model.Rows.Single();
                var candidate=(ReviewCandidate)model.SelectedRow!;
                Check(model.Details.Summary.Contains("Kullanım: İncelenmedi"),"Usage uncertainty is visible in selection header before scrolling");
                Check(candidate.Kind==ReviewKind.Conditional&&candidate.Evidence is{ClueSource:FileClueSource.FinalScanObservations,ClueCount:4},"Risk outside retained list reaches review selection");
                Check(model.Details.Sections[0].Caution&&model.Details.Sections.Any(s=>s.Heading=="İlişki")&&model.Details.Sections.Any(s=>s.Heading=="Kullanım bilgisi"),"Selection presents risk, association and independent usage section");
                foreach(bool dark in new[]{false,true})foreach(var size in new[]{(940d,680d),(1480d,940d)})
                {
                    window.SetTheme(dark);window.Width=size.Item1;window.Height=size.Item2;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);window.UpdateLayout();string label=$"metadata-{size.Item1}-{(dark?"dark":"light")}";
                    Check(window.ResultsGrid.ActualHeight>=80&&window.ResultsGrid.ActualWidth>=350,"Review table keeps usable area: "+label);
                    Check(model.CanUseSelection&&model.Details.FullText.Contains("small-mail.pst")&&!model.Details.FullText.Contains("Bu ipucu yalnız tutulan"),"Current metadata evidence and path actions stay consistent: "+label);
                    Screenshot((FrameworkElement)window.Content,Path.Combine(output,label+".png"));
                    var detail=window.CreateDetailsWindow();detail.WindowStartupLocation=WindowStartupLocation.Manual;detail.Left=-24000;detail.Top=-24000;detail.ShowInTaskbar=false;detail.Width=540;detail.Height=400;detail.Show();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);detail.UpdateLayout();
                    Check(Children<TextBox>(detail.ContentView).Any(t=>t.Text.Contains("İncelenmedi")&&t.Text.Contains("proje içerikleri")),"F6 retains explicit unexamined usage: "+label);
                    Check(Children<TextBox>(detail.ContentView).Any(t=>t.Text.Contains("sınırından bağımsız")&&t.Text.Contains("İpucu görülmemesi")),"F6 retains metadata scope and absence limitation: "+label);
                    var scroll=Children<ScrollViewer>(detail).First(s=>s.Content==detail.ContentView);scroll.ScrollToEnd();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);Check(scroll.ScrollableHeight>0&&scroll.VerticalOffset>0,"Narrow F6 can reach all assessment sections: "+label);
                    Screenshot((FrameworkElement)detail.Content,Path.Combine(output,label+"-details-end.png"));detail.Close();
                }
                model.Feedback="Tutacağım";
                Check(model.Details.Sections.Single(s=>s.Heading=="Kullanım bilgisi").Text.Contains("İncelenmedi"),"Personal note never becomes technical usage evidence");
                var report=ReportSnapshot.Capture(model);var writer=new StringWriter();report.Write(writer);string csv=writer.ToString();
                Check(csv.Contains("Kanıt kapsamı")&&csv.Contains("Kullanım bilgisi")&&csv.Contains("small-mail.pst")&&csv.Contains("Tutacağım"),"CSV preserves structured assessment and user note separately");
                PresentationTraceSources.DataBindingSource.Flush();Check(errors.ToString().Length==0,"No WPF binding errors in assessment views");
                log.Add($"ALL CHECKS PASSED ({log.Count})");System.IO.File.WriteAllLines(Path.Combine(output,"results.txt"),log);Console.WriteLine(log[^1]);window.Close();app.Shutdown(0);
            }
            catch(Exception ex){log.Add("FAIL "+ex);System.IO.File.WriteAllLines(Path.Combine(output,"results.txt"),log);Console.WriteLine(ex);window.Close();app.Shutdown(1);}
        };
        return app.Run();
    }
    sealed class FakeService:IScanService
    {
        internal ScanOutcome Outcome{get;}=Example();
        static ScanOutcome Example()
        {
            const string home=@"C:\Evidence-Test\User";string cache=Path.Combine(home,".gradle","caches");var root=new FolderEntry(cache,null){LogicalBytes=8196,FileCount=5};var scan=new ScanResult(root){RetainedFileLimit=1};
            scan.Files.Add(new(Path.Combine(cache,"big.bin"),8192,8192,DateTime.MinValue,false,""));
            scan.FileCandidates=new[]{"small-mail.pst","virtual.vhdx","model.gguf","diagnostic.dmp"}.Select(name=>(root,new FileObservation(name,1,4096,DateTime.MinValue,1,0,false,0,0,false,"")));
            scan.ObservedEvidence=ObservedFileEvidence.Capture([scan]);scan.FileCandidates=null;
            return new(scan,new ReviewRules(home,Path.Combine(home,"local"),Path.Combine(home,"data"),@"C:\Windows").Analyze(scan));
        }
        public string[] NormalizeRoots(IEnumerable<string> paths)=>paths.ToArray();
        public Task<ScanOutcome> ScanAsync(ScanRequest request,IProgress<ScanProgress>? progress,CancellationToken token)=>Task.FromResult(Outcome);
    }
}
