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

internal static class Tests
{
    static readonly List<string> log=[];
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);log.Add("PASS "+label);}
    [STAThread] static int Main(string[] args)
    {
        string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/YerelDisk;component/Theme.xaml")});
        using var errors=new StringWriter();PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(errors));PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        app.Startup+=async(_,_)=>
        {
            var service=new Fake();using var model=new WorkspaceViewModel(service);
            var main=new MainWindow(model){Left=-24000,Top=0,ShowInTaskbar=false,ShowActivated=false};app.MainWindow=main;main.Show();
            try
            {
                Check(!model.CanAnalyzePopulation && main.CreatePopulationWindow()==null,"No analysis entry before a scan");
                model.Roots.Add(service.Outcome.Scan.Root.Path);await model.ScanAsync();
                Check(model.CanAnalyzePopulation && main.PopulationButton.IsEnabled,"Completed scan enables the existing folder toolbar entry");
                var root=service.Outcome.Scan.Root;var child=root.Children[0];
                model.SelectedRow=child;
                Check(ReferenceEquals(model.PopulationTarget,child) && model.PopulationActionHint.Contains(child.Path),"Selected child is the analysis target and is named in the tooltip");
                Check(model.Details.Sections.Any(s=>s.Heading.Contains("Dosya yapısı") && s.Text.Contains("Alt klasörler")),"F6 short summary explicitly refers only to direct files");
                model.SelectedRow=null;
                Check(ReferenceEquals(model.PopulationTarget,root),"With no row selected the current browsed folder is analyzed");
                var dialog=main.CreatePopulationWindow()!;Position(dialog);dialog.Show();await dialog.PrepareAsync();
                Check(dialog.Analysis is{Complete:true,Count:82} && dialog.AnalysisTabs.IsEnabled,"Window displays all observations rather than retained top-K files");
                Check(dialog.Analysis is{Small:81,Empty:20} && dialog.TotalCount.Text=="82" && dialog.EmptyCount.Text=="20","Visible metrics distinguish small files from their empty-file subset");
                Check(dialog.TypesGrid.Items.Count>0 && dialog.AreasGrid.Items.Count==3,"Both extension and disjoint-area tables are populated");
                Check(dialog.Limits.Text.Contains("İçerikler okunmadı") && dialog.Limits.Text.Contains("silme önerisi değildir"),"Content and deletion limits are visible in the window");
                foreach(bool dark in new[]{false,true})
                {
                    main.SetTheme(dark);main.Width=dark?940:1480;main.Height=dark?680:940;
                    dialog.Width=dark?680:940;dialog.Height=dark?580:720;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);dialog.UpdateLayout();main.UpdateLayout();
                    Color Of(object brush)=>((SolidColorBrush)brush).Color;
                    Check(Of(dialog.Background)==Of(app.Resources["Canvas"]) && Of(dialog.Foreground)==Of(app.Resources["Ink"]),$"Analysis window follows {(dark?"dark":"light")} theme");
                    var content=(FrameworkElement)dialog.Content;
                    Check(Inside(dialog.CloseButton,content) && dialog.TypesGrid.ActualHeight>=104,$"Close action and two rows fit in {(dark?"compact":"wide")} layout");
                    Check(main.PopulationButton.ActualWidth>0 && Inside(main.PopulationButton,(FrameworkElement)main.Content),$"Main toolbar analysis action fits in {(dark?"compact":"wide")} layout");
                    Screenshot(dialog,Path.Combine(output,dark?"population-compact-dark.png":"population-wide-light.png"));
                    dialog.AnalysisTabs.SelectedIndex=1;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);dialog.UpdateLayout();
                    Check(dialog.AreasGrid.ActualHeight>=104 && dialog.AreasGrid.Items.Count==3,$"Area table remains usable in {(dark?"compact":"wide")} layout");
                    if(dark)Screenshot(dialog,Path.Combine(output,"areas-compact-dark.png"));
                    dialog.AnalysisTabs.SelectedIndex=0;
                }
                dialog.Close();
                await main.NavigateAsync(WorkspaceView.Files);
                Check(!model.CanAnalyzePopulation && main.CreatePopulationWindow()==null,"Other result views do not analyze an unrelated stale folder");
                await main.NavigateAsync(WorkspaceView.Folders);
                var missing=new ScanResult(new FolderEntry(@"C:\missing-population",null));
                var unavailable=new PopulationWindow(missing,missing.Root){Owner=main};Position(unavailable);unavailable.Show();await unavailable.PrepareAsync();
                Check(unavailable.Analysis is{Available:false} && unavailable.TotalCount.Text=="—" && !unavailable.AnalysisTabs.IsEnabled && unavailable.Coverage.Text.Contains("Yeni bir tarama"),"Missing summaries explain the need for a new scan without showing zero metrics");
                unavailable.Close();
                var closing=main.CreatePopulationWindow()!;Position(closing);closing.Show();var pending=closing.PrepareAsync();closing.Close();await pending;
                Check(!closing.IsVisible,"Closing an ongoing metadata analysis cancels without holding the window open");
                model.SelectedRow=child;child.Measurement=MeasurementState.Previous;
                var previous=main.CreatePopulationWindow()!;Position(previous);previous.Show();await previous.PrepareAsync();
                Check(previous.Analysis is{Available:false} && previous.TypesGrid.Items.Count==0,"Previous measurements do not produce confident extension rows");previous.Close();child.Measurement=MeasurementState.Measured;
                PresentationTraceSources.DataBindingSource.Flush();Check(errors.ToString().Length==0,"No WPF binding errors");
                log.Add($"ALL CHECKS PASSED ({log.Count})");app.Shutdown(0);
            }
            catch(Exception ex){log.Add("FAIL "+ex);app.Shutdown(1);}
            finally{File.WriteAllLines(Path.Combine(output,"results.txt"),log);}
        };
        return app.Run();
    }
    static void Position(Window window){window.WindowStartupLocation=WindowStartupLocation.Manual;window.Left=-24000;window.Top=0;window.ShowInTaskbar=false;window.ShowActivated=false;}
    static bool Inside(FrameworkElement element,FrameworkElement parent)
    {var rect=element.TransformToAncestor(parent).TransformBounds(new Rect(element.RenderSize));return rect.Left>=0 && rect.Top>=0 && rect.Right<=parent.ActualWidth && rect.Bottom<=parent.ActualHeight;}
    static void Screenshot(Window window,string path)
    {
        var content=(FrameworkElement)window.Content;var drawing=new DrawingVisual();using(var dc=drawing.RenderOpen()){dc.DrawRectangle(window.Background,null,new Rect(content.RenderSize));dc.DrawRectangle(new VisualBrush(content),null,new Rect(content.RenderSize));}
        var bitmap=new RenderTargetBitmap((int)content.ActualWidth,(int)content.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(drawing);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);png.Save(file);
    }
    sealed class Fake:IScanService
    {
        internal ScanOutcome Outcome{get;}=new(Snapshot(),[]);
        public string[] NormalizeRoots(IEnumerable<string> paths)=>paths.ToArray();
        public Task<ScanOutcome> ScanAsync(ScanRequest request,IProgress<ScanProgress>? progress,CancellationToken token)=>Task.FromResult(Outcome);
        static ScanResult Snapshot()
        {
            var root=new FolderEntry(@"C:\ornek-projeler",null);var a=new FolderEntry(root.Path+@"\test-ciktilari",root);var b=new FolderEntry(root.Path+@"\videolar",root);root.Children.AddRange([a,b]);
            var items=new List<(FolderEntry,FileObservation)>();
            for(int i=0;i<80;i++)items.Add((a,new("ornek-"+i+(i%4==0?".txt":".bin"),i%4==0?0:128,null,default,0,0,false,0,0,false,"")));
            items.Add((b,new("video.mp4",50*1024*1024,null,default,0,0,false,0,0,false,"")));items.Add((root,new("README",32,null,default,0,0,false,0,0,false,"")));
            foreach(var item in items)for(FolderEntry? owner=item.Item1;owner!=null;owner=owner.Parent){owner.FileCount++;owner.LogicalBytes+=item.Item2.Size;}
            var scan=new ScanResult(root){FileCandidates=items};scan.ObservedEvidence=ObservedFileEvidence.Capture([scan]);scan.FileCandidates=null;return scan;
        }
    }
}
