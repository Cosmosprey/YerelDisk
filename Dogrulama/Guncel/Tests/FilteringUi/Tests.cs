using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YerelDisk;
using YerelDisk.Presentation;
using YerelDisk.Wpf;

internal static class FilteringUiTests
{
    static readonly List<string> log=[];
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);log.Add("PASS "+label);}
    static IEnumerable<T> Children<T>(DependencyObject root)where T:DependencyObject
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T item)yield return item;foreach(var nested in Children<T>(child))yield return nested;}}
    static void Screenshot(FrameworkElement area,string path)
    {var dpi=VisualTreeHelper.GetDpi(area);var bitmap=new RenderTargetBitmap((int)Math.Ceiling(area.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(area.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);bitmap.Render(area);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);png.Save(stream);}
    static void PressKey(FrameworkElement element,Key key)
    {element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(element)!,Environment.TickCount,key){RoutedEvent=Keyboard.PreviewKeyDownEvent});}
    [STAThread]static int Main(string[] args)
    {
        string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/YerelDisk;component/Theme.xaml")});
        var errors=new StringWriter();PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(errors));PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        app.Startup+=async(_,_)=>
        {
            var fake=new Fake();using var model=new WorkspaceViewModel(fake);var window=new MainWindow(model){WindowStartupLocation=WindowStartupLocation.Manual,Left=-24000,Top=-24000,ShowInTaskbar=false,ShowActivated=false};
            try
            {
                window.Show();model.Roots.Add(fake.Root.Path);await model.ScanAsync();model.ReviewMinimum=0;await window.NavigateAsync(WorkspaceView.Review);
                foreach(bool dark in new[]{false,true})foreach(var size in new[]{(940d,680d),(1480d,940d)})
                {
                    window.SetTheme(dark);window.Width=size.Item1;window.Height=size.Item2;window.ReviewMinimumSelector.SelectedValue=10*1024*1024;await window.PendingView;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);window.UpdateLayout();string label=$"filters-{size.Item1}-{(dark?"dark":"light")}";
                    Check(model.Rows.Count==0&&model.ListCaption.Contains("0 / 3")&&model.ListCaptionDetail.Contains("boyut 3"),"Filtered-out candidates have visible available and reason counts: "+label);
                    var clear=window.ClearReviewFiltersButton;var position=clear.TranslatePoint(new Point(clear.ActualWidth,clear.ActualHeight),window);
                    Check(clear.IsVisible&&clear.IsEnabled&&clear.ActualWidth>50&&position.X<=window.ActualWidth&&position.Y<=window.ActualHeight,"Clear button remains reachable with warning band and empty list: "+label);
                    Screenshot((FrameworkElement)window.Content,Path.Combine(output,label+".png"));clear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));await window.PendingView;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);window.UpdateLayout();
                    Check(model.Rows.Count==3&&!clear.IsVisible&&model.ReviewMinimum==0&&model.AppliedCounts is{Hidden:0},"Actual clear-button event restores candidates in one refresh: "+label);
                    Check(Children<ScrollViewer>(window.ResultsGrid).First().ScrollableWidth<=1,"Footer controls do not force horizontal table scrolling: "+label);
                }
                window.ReviewKindSelector.SelectedValue=3;await window.PendingView;window.SearchBox.Text="no-match";await window.PendingView;
                Check(model.AppliedCounts is{ByKind:2,BySearch:1,Hidden:3}&&model.EmptyMessage.Contains("filtre"),"Combined type and search filters agree with the empty-state explanation");
                var report=ReportSnapshot.Capture(model);Check(report.Metadata.Any(r=>r[0]=="Filtrelenen kayıt"||r.Contains("Filtrelenen kayıt")),"Filtered report carries the same count explanation");
                window.ClearReviewFiltersButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));await window.PendingView;
                model.SelectedRow=model.Rows[0];window.ResultsGrid.ScrollIntoView(model.SelectedRow);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var knownRow=(DataGridRow)window.ResultsGrid.ItemContainerGenerator.ContainerFromItem(model.SelectedRow);PressKey(Children<DataGridCell>(knownRow).First(),Key.Enter);await window.PendingView;
                Check(model.IsKnownReview&&fake.Calls==1,"Enter on a recognized candidate never navigates or executes its path");
                window.ReviewCollectionSelector.SelectedValue=1;await window.PendingView;model.SelectedRow=fake.Apps;window.ResultsGrid.ScrollIntoView(fake.Apps);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var header=Children<DataGridColumnHeader>(window.ResultsGrid).First(h=>h.IsVisible&&h.Column!=null);PressKey(header,Key.Enter);await window.PendingView;Check(model.IsUnrecognized,"Enter on a header never acts on a previously selected folder");
                var row=(DataGridRow)window.ResultsGrid.ItemContainerGenerator.ContainerFromItem(fake.Apps);var cell=Children<DataGridCell>(row).First(c=>c.IsVisible);
                PressKey(cell,Key.Space);await window.PendingView;Check(model.IsUnrecognized,"Other keys leave folder navigation unchanged");
                PressKey(cell,Key.Enter);await window.PendingView;Check(model.IsFolderView&&model.Rows.Single() is FolderEntry nested&&nested.Name=="Nested","Routed Enter from a discovery row opens the ordinary folder view");
                await model.GoUpAsync();model.SelectedRow=fake.Unknown;window.ResultsGrid.ScrollIntoView(fake.Unknown);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var blockedRow=(DataGridRow)window.ResultsGrid.ItemContainerGenerator.ContainerFromItem(fake.Unknown);PressKey(Children<DataGridCell>(blockedRow).First(),Key.Enter);await window.PendingView;
                Check(!model.CanUseSelection&&model.Rows.Contains(fake.Apps),"Enter on an unknown measurement cannot enter that folder");
                model.SelectedRow=fake.Apps;window.ResultsGrid.ScrollIntoView(fake.Apps);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);var folderRow=(DataGridRow)window.ResultsGrid.ItemContainerGenerator.ContainerFromItem(fake.Apps);PressKey(Children<DataGridCell>(folderRow).First(),Key.Enter);await window.PendingView;
                Check(model.IsFolderView&&model.Rows.Single() is FolderEntry item&&item.Name=="Nested","Enter also works in the existing folder table");
                await window.NavigateAsync(WorkspaceView.Review);Check(model.IsUnrecognized&&model.AppliedCounts is{Available:3,Hidden:0},"Returning from keyboard navigation restores discovery counts");
                window.SearchBox.Text="no-match";await window.PendingView;window.ClearReviewFiltersButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));await window.PendingView;
                Check(model.Rows.Count==3&&model.AppliedCounts is{Hidden:0}&&fake.Calls==1,"Clearing discovery search does not scan again or alter scope");
                PresentationTraceSources.DataBindingSource.Flush();Check(errors.ToString().Length==0,"No WPF binding errors across filters, footer and keyboard routes");
                log.Add($"ALL CHECKS PASSED ({log.Count})");File.WriteAllLines(Path.Combine(output,"results.txt"),log);Console.WriteLine(log[^1]);window.Close();app.Shutdown(0);
            }
            catch(Exception ex){log.Add("FAIL "+ex);File.WriteAllLines(Path.Combine(output,"results.txt"),log);Screenshot((FrameworkElement)window.Content,Path.Combine(output,"failure.png"));Console.WriteLine(ex);window.Close();app.Shutdown(1);}
        };return app.Run();
    }
    sealed class Fake:IScanService
    {
        internal FolderEntry Root{get;}=new(@"C:\Filter-Ui",null);
        internal FolderEntry Apps{get;}internal FolderEntry Unknown{get;}internal int Calls;
        readonly ScanOutcome outcome;
        FolderEntry Add(string name,long bytes){var f=new FolderEntry(Path.Combine(Root.Path,name),Root){LogicalBytes=bytes,AllocatedBytes=bytes,FileCount=1};Root.Children.Add(f);return f;}
        public Fake(){Apps=Add("Apps",4000);Apps.Children.Add(new(Path.Combine(Apps.Path,"Nested"),Apps){LogicalBytes=4000,FileCount=1});var work=Add("Work",2500);work.ExcludedSubtrees=1;Add("Tiny",100);Unknown=Add("Unavailable",1000);Unknown.Measurement=MeasurementState.Unknown;var scan=new ScanResult(Root){ExcludedPaths=[Path.Combine(work.Path,"skip")]};var list=new List<ReviewCandidate>();foreach(var pair in new[]{("cache",1000L,ReviewKind.Cache),("Package",2000L,ReviewKind.Protected),("models",3000L,ReviewKind.Conditional)}){var f=Add(pair.Item1,pair.Item2);list.Add(new(f.Path,f.LogicalBytes,f.AllocatedBytes,1,"fixture","fixture","GRADLE-CACHES","fixture","fixture",false,pair.Item3,new(ReviewMatchKind.KnownLocation,f.Path)));}outcome=new(scan,list);}
        public string[] NormalizeRoots(IEnumerable<string> paths)=>paths.ToArray();public Task<ScanOutcome> ScanAsync(ScanRequest request,IProgress<ScanProgress>? progress,CancellationToken token){Calls++;return Task.FromResult(outcome);}
    }
}
