using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YerelDisk;
using YerelDisk.Presentation;
using YerelDisk.Wpf;

internal static class DistributionUiTests
{
    static readonly List<string> log=[];
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);log.Add("PASS "+label);}
    static void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static async Task Settle(WorkspaceViewModel model)
    {
        var watch=Stopwatch.StartNew();
        do{await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);if(watch.Elapsed>TimeSpan.FromSeconds(10))throw new TimeoutException("UI refresh");await Task.Delay(10);}while(model.IsRefreshing);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    static IEnumerable<T> Descendants<T>(DependencyObject root)where T:DependencyObject
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T result)yield return result;foreach(var nested in Descendants<T>(child))yield return nested;}}
    static void Screenshot(FrameworkElement area,string path)
    {
        var dpi=VisualTreeHelper.GetDpi(area);var visual=new DrawingVisual();using(var context=visual.RenderOpen()){context.DrawRectangle((Brush)area.FindResource("Canvas"),null,new Rect(0,0,area.ActualWidth,area.ActualHeight));context.DrawRectangle(new VisualBrush(area),null,new Rect(0,0,area.ActualWidth,area.ActualHeight));}
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(area.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(area.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);bitmap.Render(area);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);
    }
    [STAThread]static int Main(string[] args)
    {
        string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/YerelDisk;component/Theme.xaml")});
        var errors=new StringWriter();PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(errors));PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        app.Startup+=async(_,_)=>
        {
            var fake=new FakeService();using var model=new WorkspaceViewModel(fake);var window=new MainWindow(model){WindowStartupLocation=WindowStartupLocation.Manual,Left=-24000,Top=-24000,ShowInTaskbar=false,ShowActivated=false};
            try
            {
                window.Show();model.Roots.Add(fake.Result.Root.Path);await model.ScanAsync();
                Check(window.ResultsGrid.Visibility==Visibility.Visible&&window.DistributionChart.Visibility==Visibility.Collapsed,"Table remains initial presentation");
                Click(window.DistributionModeButton);await Settle(model);window.UpdateLayout();
                Check(window.ResultsGrid.Visibility==Visibility.Collapsed&&window.DistributionChart.Visibility==Visibility.Visible,"Mode button activates bounded graph and hides table");
                foreach(bool dark in new[]{false,true})foreach(var size in new[]{(940d,680d),(1480d,940d)})
                {
                    window.SetTheme(dark);window.Width=size.Item1;window.Height=size.Item2;window.SummaryExpander.IsExpanded=false;window.DistributionChart.ChartScroll.ScrollToTop();await Settle(model);window.UpdateLayout();string label=$"chart-{size.Item1}-{(dark?"dark":"light")}";
                    Check(window.DistributionChart.ActualHeight>=150&&window.DistributionChart.ActualWidth>=350,"Chart viewport stays usable: "+label+" height="+window.DistributionChart.ActualHeight+" width="+window.DistributionChart.ActualWidth);
                    Check(window.DistributionChart.Bars.Items.Count<=15&&Descendants<ProgressBar>(window.DistributionChart).All(p=>double.IsFinite(p.Value)&&p.Value>=0&&p.Value<=100),"Rendered bars stay bounded and proportional: "+label);
                    var area=(FrameworkElement)window.Content;Point end=window.DistributionModeButton.TranslatePoint(new Point(window.DistributionModeButton.ActualWidth,window.DistributionModeButton.ActualHeight),area);
                    Check(end.X<=area.ActualWidth+1&&end.Y<=area.ActualHeight+1,"Presentation controls remain inside window: "+label);
                    var firstBar=Descendants<ProgressBar>(window.DistributionChart).First();var firstEnd=firstBar.TranslatePoint(new Point(firstBar.ActualWidth,firstBar.ActualHeight),window.DistributionChart.ChartScroll);
                    Check(firstEnd.Y<=window.DistributionChart.ChartScroll.ActualHeight+1,"First complete bar is visible without scrolling: "+label+" bottom="+firstEnd.Y+" viewport="+window.DistributionChart.ChartScroll.ActualHeight);
                    Screenshot(area,Path.Combine(output,label+".png"));
                }
                window.DistributionChart.ChartScroll.ScrollToEnd();await Settle(model);window.UpdateLayout();
                Check(window.DistributionChart.ChartScroll.ScrollableHeight>0&&window.DistributionChart.ChartScroll.VerticalOffset>0,"Long chart and qualification footer remain scrollable");
                Screenshot((FrameworkElement)window.Content,Path.Combine(output,"chart-footer-dark.png"));
                window.DistributionChart.ChartScroll.ScrollToTop();await Settle(model);
                var segment=model.Distribution.Segments.First(s=>s.Folder!=null);var select=Descendants<Button>(window.DistributionChart).First(b=>Equals(b.Content,segment.Title));
                Check(select.Focusable&&select.IsTabStop,"Folder selection is available through normal keyboard button navigation");
                Click(select);await Settle(model);Check(ReferenceEquals(model.SelectedRow,segment.Folder)&&model.Details.Path==segment.Folder!.Path,"Graph name selection updates shared F6/side detail");
                var enter=Descendants<Button>(window.DistributionChart).First(b=>ReferenceEquals(b.DataContext,segment)&&Equals(b.Content,"→"));Click(enter);await Settle(model);
                Check(model.FolderPath==segment.Folder!.Path&&model.IsDistribution,"Arrow button enters the intended folder without switching presentation");
                Check(model.Distribution.Segments.Any(s=>s.Group==DistributionGroup.Direct),"Leaf/direct file data does not disappear when child list is small");
                await model.GoUpAsync();await Settle(model);model.Search="no-such-folder";await model.RefreshAsync();await Settle(model);
                Check(model.Rows.Count==0&&window.DistributionChart.Visibility==Visibility.Visible&&model.Distribution.Segments.All(s=>s.Folder==null),"No-match search keeps explicitly labeled context graph");
                model.Search="";await model.RefreshAsync();await Settle(model);var writer=new StringWriter();ReportSnapshot.Capture(model).Write(writer);
                Check(writer.ToString().Contains("çubuk grupları ayrı kayıt değildir"),"CSV states that graph grouping does not truncate table export");
                Click(window.TableModeButton);await Settle(model);Check(window.ResultsGrid.Visibility==Visibility.Visible&&window.DistributionChart.Visibility==Visibility.Collapsed,"Table button restores complete results table");
                await window.NavigateAsync(WorkspaceView.Files);await Settle(model);Check(window.ResultsGrid.Visibility==Visibility.Visible&&window.DistributionChart.Visibility==Visibility.Collapsed,"Files view always keeps its existing table");
                PresentationTraceSources.DataBindingSource.Flush();Check(errors.ToString().Length==0,"No WPF binding errors");
                log.Add($"ALL CHECKS PASSED ({log.Count})");File.WriteAllLines(Path.Combine(output,"results.txt"),log);Console.WriteLine(log[^1]);window.Close();app.Shutdown(0);
            }
            catch(Exception ex){Screenshot((FrameworkElement)window.Content,Path.Combine(output,"failure.png"));log.Add("FAIL "+ex);File.WriteAllLines(Path.Combine(output,"results.txt"),log);Console.WriteLine(ex);window.Close();app.Shutdown(1);}
        };
        return app.Run();
    }
    sealed class FakeService:IScanService
    {
        internal ScanResult Result{get;}=Example();
        static ScanResult Example()
        {
            var root=new FolderEntry(@"C:\Example\Scanned",null){FileCount=40,ExcludedSubtrees=1,Measurement=MeasurementState.Partial,LogicalBytes=80};
            for(int i=1;i<=30;i++){var f=new FolderEntry(Path.Combine(root.Path,$"Folder-{i:D2}-"+new string('x',i==30?140:8)),root){LogicalBytes=i*100,FileCount=1};root.Children.Add(f);root.LogicalBytes+=f.LogicalBytes;}
            var biggest=root.Children[^1];biggest.Children.Add(new(Path.Combine(biggest.Path,"Nested"),biggest){LogicalBytes=1000,FileCount=1});
            root.Children.Add(new(Path.Combine(root.Path,"Unavailable"),root){Measurement=MeasurementState.Unknown});root.Children.Add(new(Path.Combine(root.Path,"Empty"),root));
            return new(root){Folders=34,ExcludedPaths=Array.AsReadOnly(new[]{Path.Combine(root.Path,"Omitted")})};
        }
        public string[] NormalizeRoots(IEnumerable<string> roots)=>roots.ToArray();
        public Task<ScanOutcome> ScanAsync(ScanRequest request,IProgress<ScanProgress>? progress,CancellationToken token)=>Task.FromResult(new ScanOutcome(Result,[]));
    }
}
