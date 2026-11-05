using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YerelDisk;
using YerelDisk.Presentation;
using YerelDisk.Wpf;

internal static class InventoryUiTests
{
    static readonly List<string> log=[];static void Check(bool ok,string name){if(!ok)throw new Exception(name);log.Add("PASS "+name);}
    static IEnumerable<T> Children<T>(DependencyObject root)where T:DependencyObject{for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T value)yield return value;foreach(var descendant in Children<T>(child))yield return descendant;}}
    static void Screenshot(FrameworkElement area,string path){var dpi=VisualTreeHelper.GetDpi(area);var bmp=new RenderTargetBitmap((int)Math.Ceiling(area.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(area.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);bmp.Render(area);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bmp));using var stream=File.Create(path);png.Save(stream);}
    [STAThread]static int Main(string[] args)
    {
        string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/YerelDisk;component/Theme.xaml")});var errors=new StringWriter();PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(errors));PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        app.Startup+=async(_,_)=>
        {
            var service=new FakeScan();var provider=new FakeProvider(service);using var model=new WorkspaceViewModel(service,provider);var window=new MainWindow(model){WindowStartupLocation=WindowStartupLocation.Manual,Left=-24000,Top=-24000,ShowInTaskbar=false,ShowActivated=false};
            try
            {
                window.Show();await window.NavigateAsync(WorkspaceView.Settings);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(provider.Calls==0&&!window.InspectInventoryButton.IsEnabled&&!window.ExportInventoryCheck.IsEnabled&&!model.Inventory.ExportHints,"Startup and settings visibility never read inventory or enable export");
                model.Roots.Add(service.Root.Path);await model.ScanAsync();await window.NavigateAsync(WorkspaceView.Settings);
                Check(provider.Calls==0&&window.InspectInventoryButton.IsEnabled,"Completed scan enables only the explicit read action");
                Check(Children<TextBlock>(window).Any(t=>t.Text.Contains("Seçilen disk klasörlerinin dışındaki kayıtlar")),"Disclosure explains source scope beyond scanned folders before reading");
                window.InspectInventoryButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));await model.Inventory.CurrentOperation;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(provider.Calls==1&&model.Inventory.HasSnapshot&&window.ExportInventoryCheck.IsEnabled&&window.ExportInventoryCheck.IsChecked==false,"Click reads once; CSV remains opt-in");
                model.ReviewMinimum=0;await window.NavigateAsync(WorkspaceView.Review);model.SelectedRow=service.Candidate;
                foreach(bool dark in new[]{false,true})foreach(var size in new[]{(940d,680d),(1480d,940d)})
                {
                    window.SetTheme(dark);window.Width=size.Item1;window.Height=size.Item2;window.ResultsGrid.ScrollIntoView(model.SelectedRow);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);window.UpdateLayout();string label=$"inventory-{size.Item1}-{(dark?"dark":"light")}";
                    var row=(DataGridRow)window.ResultsGrid.ItemContainerGenerator.ContainerFromItem(service.Candidate);var texts=Children<TextBlock>(row).Where(t=>t.IsVisible).ToArray();
                    Check(texts.Any(t=>t.Text.StartsWith("Yol kaydı: PRIVATE-EDITOR",StringComparison.Ordinal))&&texts.Any(t=>t.Text.Contains("Elle silmeyin")&&t.Text.Contains("Posta")),"Table shows registry hint without hiding protection/type risk: "+label);
                    Check(Children<ScrollViewer>(window.ResultsGrid).First().ScrollableWidth<=1&&texts.All(t=>t.ActualHeight+t.Margin.Top+t.Margin.Bottom+1>=t.DesiredSize.Height),"Optional hint fits narrow/big table without clipping: "+label);
                    Check(model.Details.Sections[0].Caution&&model.Details.FullText.Contains("PRIVATE-EDITOR")&&model.Details.FullText.Contains("etkin kullanım doğrulanmadı"),"Detail keeps first risk and independent usage caveat: "+label);
                    Screenshot((FrameworkElement)window.Content,Path.Combine(output,label+".png"));
                }
                var detail=window.CreateDetailsWindow();detail.WindowStartupLocation=WindowStartupLocation.Manual;detail.Left=-24000;detail.Top=-24000;detail.ShowInTaskbar=false;detail.Show();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(Children<TextBox>(detail.ContentView).Any(t=>t.Text.Contains("PRIVATE-EDITOR")&&t.Text.Contains("Bildirilen yol")),"Real F6 renders source path and application details");detail.Close();
                var off=new StringWriter();ReportSnapshot.Capture(model).Write(off);Check(!off.ToString().Contains("PRIVATE-EDITOR")&&!off.ToString().Contains("UNRELATED-PRIVATE"),"Default report omits inventory identities even after a successful read");
                await window.NavigateAsync(WorkspaceView.Settings);window.ExportInventoryCheck.IsChecked=true;await window.NavigateAsync(WorkspaceView.Review);
                var captured=ReportSnapshot.Capture(model);var exported=new StringWriter();captured.Write(exported);Check(exported.ToString().Contains("PRIVATE-EDITOR")&&!exported.ToString().Contains("UNRELATED-PRIVATE"),"Explicit export checkbox includes related identities only");
                window.ReviewCollectionSelector.SelectedValue=1;await window.PendingView;model.SelectedRow=service.Other;window.ResultsGrid.ScrollIntoView(service.Other);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(model.Details.FullText.Contains("PRIVATE-OTHER")&&model.Details.FullText.Contains("silme önerisi değildir"),"Unknown-area explanation remains unknown despite a registration hint");
                model.Inventory.Forget();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);window.UpdateLayout();
                Check(!model.Inventory.HasSnapshot&&!model.Inventory.ExportHints&&!model.Details.FullText.Contains("PRIVATE-OTHER")&&!Children<TextBlock>(window.ResultsGrid).Any(t=>t.IsVisible&&t.Text.StartsWith("Yol kaydı:")),"Forget removes live detail/table hints and resets export selection");
                var same=new StringWriter();captured.Write(same);Check(same.ToString()==exported.ToString(),"Previously captured opt-in report stays immutable after forgetting");
                await model.Inventory.InspectAsync();await model.ScanAsync();Check(!model.Inventory.HasSnapshot&&provider.Calls==2&&!model.Inventory.ExportHints,"New scan drops inventory and never performs an automatic reread");
                await window.NavigateAsync(WorkspaceView.Settings);window.Width=940;window.Height=680;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);Screenshot((FrameworkElement)window.Content,Path.Combine(output,"inventory-settings-940.png"));
                provider.Gate=new(TaskCreationOptions.RunContinuationsAsynchronously);window.InspectInventoryButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(model.Inventory.IsBusy&&!window.InspectInventoryButton.IsEnabled,"Read action is disabled during an in-flight inventory request");
                window.Close();Check(!window.IsEnabled&&!model.Inventory.HasSnapshot,"Closing cancels and drops in-flight inventory before waiting");provider.Gate.SetResult(provider.Snapshot);await model.Inventory.CurrentOperation;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(!window.IsVisible&&!model.Inventory.HasSnapshot,"Late provider completion cannot repopulate a closing window");
                PresentationTraceSources.DataBindingSource.Flush();Check(errors.ToString().Length==0,"No inventory WPF binding errors");
                log.Add($"ALL CHECKS PASSED ({log.Count})");File.WriteAllLines(Path.Combine(output,"results.txt"),log);Console.WriteLine(log[^1]);app.Shutdown(0);
            }
            catch(Exception ex){log.Add("FAIL "+ex);File.WriteAllLines(Path.Combine(output,"results.txt"),log);if(window.IsVisible)Screenshot((FrameworkElement)window.Content,Path.Combine(output,"failure.png"));Console.WriteLine(ex);window.Close();app.Shutdown(1);}
        };return app.Run();
    }
    sealed class FakeScan:IScanService
    {
        internal FolderEntry Root{get;}=new(@"D:\Apps",null){LogicalBytes=20000,FileCount=2};internal FolderEntry Editor{get;}internal FolderEntry Other{get;}internal ReviewCandidate Candidate{get;}
        internal FakeScan(){Editor=new(Path.Combine(Root.Path,"Editor"),Root){LogicalBytes=10000,FileCount=1,ExcludedSubtrees=1};Other=new(Path.Combine(Root.Path,"Other"),Root){LogicalBytes=10000,FileCount=1};Root.Children.AddRange([Editor,Other]);Candidate=new(Editor.Path,10000,10000,1,"fixture","fixture","PACKAGE-CACHE","fixture","fixture",false,ReviewKind.Protected,new(ReviewMatchKind.KnownLocation,Editor.Path,FileDataRisk.Mail,1,[new(FileDataRisk.Mail,Path.Combine(Editor.Path,"personal.pst"))]),1);}
        public string[] NormalizeRoots(IEnumerable<string> p)=>p.ToArray();public Task<ScanOutcome> ScanAsync(ScanRequest r,IProgress<ScanProgress>? p,CancellationToken t)=>Task.FromResult(new ScanOutcome(new(Root){ExcludedPaths=[Path.Combine(Editor.Path,"skip")]},[Candidate]));
    }
    sealed class FakeProvider(FakeScan scan):IInventoryProvider
    {internal int Calls;internal TaskCompletionSource<InventorySnapshot>? Gate;internal InventorySnapshot Snapshot{get;}=new(DateTimeOffset.Now,DateTimeOffset.Now,[new("PRIVATE-EDITOR-"+new string('x',200),"publisher","1",scan.Editor.Path,"fixture64"),new("PRIVATE-OTHER","publisher","1",scan.Other.Path,"fixture32"),new("UNRELATED-PRIVATE","private publisher","2",@"C:\Unrelated","fixture")],[new("fixture",InventoryReadState.Complete,3,0,0)]);public Task<InventorySnapshot> ReadAsync(CancellationToken token){Calls++;return Gate?.Task??Task.FromResult(Snapshot);}}
}
