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

static class Tests
{
    static readonly List<string> log=[];
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);log.Add("PASS "+label);}
    static void Click(Button b)=>b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static TaskCompletionSource<ScanOutcome> Gate()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    [STAThread]static int Main(string[] args)
    {
        string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/YerelDisk;component/Theme.xaml")});
        using var errors=new StringWriter();PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(errors));PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        app.Startup+=async(_,_)=>
        {
            var service=new Fake();using var model=new WorkspaceViewModel(service,new Inventory());var window=new MainWindow(model){WindowStartupLocation=WindowStartupLocation.Manual,Left=-24000,Top=0,ShowInTaskbar=false,ShowActivated=false};app.MainWindow=window;window.Show();
            try
            {
                Check(!window.RemeasureButton.IsEnabled,"Remeasure action starts disabled without a scan");model.Roots.Add(@"C:\scan");await model.ScanAsync();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(window.RemeasureButton.IsEnabled&&window.RemeasureButton.ToolTip.ToString()!.Contains("kök"),"Existing scan enables the scope-preserving action with an explanation");
                foreach(var size in new[]{(940d,680d),(1480d,940d)})foreach(bool dark in new[]{false,true})
                {
                    window.Width=size.Item1;window.Height=size.Item2;window.SetTheme(dark);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);window.UpdateLayout();var content=(FrameworkElement)window.Content;string label=$"{size.Item1}-{(dark?"dark":"light")}";
                    Check(Inside(window.RemeasureButton,content)&&Inside(window.ScanButton,content)&&Inside(window.ChooseButton,content),"Scope actions fit "+label);
                    Check(window.ResultsGrid.ActualHeight>=window.ResultsGrid.ColumnHeaderHeight+38,"Results keep at least one complete row "+label);
                    Screenshot(window,Path.Combine(output,"ready-"+label+".png"));
                }
                window.Width=940;window.Height=680;window.SetTheme(true);var old=model.Scan;var rows=model.Rows;var time=model.MeasurementCompletedAt;string sizeText=model.LogicalSize;
                model.Roots.Clear();model.Roots.Add(@"D:\future");var gate=Gate();service.Pending=gate;Click(window.RemeasureButton);var running=model.CurrentScan;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(service.Request!.Roots.SequenceEqual([@"C:\scan"]),"Actual button uses the result scope despite editable future roots");
                Check(model.IsRemeasuring&&!window.RemeasureButton.IsEnabled&&!window.ScanButton.IsEnabled&&!window.ChooseButton.IsEnabled,"Preparing replacement disables conflicting scope actions");
                Check(ReferenceEquals(window.ResultsGrid.ItemsSource,rows)&&model.LogicalSize==sizeText&&model.Status.Contains("Eski sonuç"),"Window retains old rows and metric cards with a clear preparation status");
                var progress=service.Progress!;progress.Report(new(999,99,999999,0,"PROGRESS-CANARY",TimeSpan.FromSeconds(1)));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(model.Status.Contains("PROGRESS-CANARY")&&model.LogicalSize==sizeText&&ReferenceEquals(model.Scan,old),"Live replacement progress updates status without replacing old metrics");
                Check(!window.ResultsGrid.IsEnabled&&!window.SearchBox.IsEnabled,"Old results remain visible without selection or search edits during replacement");
                window.FilesNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await window.NavigateAsync(WorkspaceView.Files);
                Check(model.View==WorkspaceView.Folders&&window.FoldersNav.IsChecked==true,"Navigation input cannot detach the frozen old view");
                Click(window.ScanButton);Click(window.RemeasureButton);Check(service.Calls==2,"Programmatic disabled scan actions do not start competing scans");
                Screenshot(window,Path.Combine(output,"preparing-compact-dark.png"));
                model.CancelScan();gate.SetResult(Fake.Outcome(200));await running;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(ReferenceEquals(model.Scan,old)&&ReferenceEquals(model.Rows,rows)&&model.MeasurementCompletedAt==time&&window.RemeasureButton.IsEnabled,"Cancellation keeps the old result and restores the action in the real window");
                Check(model.Status.Contains("durduruldu"),"Cancelled replacement is explicitly identified in the status");
                string cancelledStatus=model.Status;progress.Report(new(1,1,1,0,"LATE-CANARY",TimeSpan.Zero));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(model.Status==cancelledStatus,"Late progress cannot overwrite the cancellation status");
                service.Pending=null;service.Bytes=300;Click(window.RemeasureButton);await model.CurrentScan;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(!ReferenceEquals(model.Scan,old)&&model.Scan!.Root.LogicalBytes==300&&model.MeasurementCompletedAt>time,"Successful button operation installs a new scan and timestamp");
                Check(ReferenceEquals(window.ResultsGrid.ItemsSource,model.Rows)&&window.ResultsGrid.IsEnabled&&window.SearchBox.IsEnabled,"New rows and controls publish together after commit");
                Check(model.Roots.Single()==@"D:\future"&&model.Scan!.RootPaths.Single()==@"C:\scan","Successful window replacement keeps future root edits separate");
                await window.NavigateAsync(WorkspaceView.Review);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                foreach(bool cancel in new[]{false,true})
                {
                    int minimum=model.ReviewMinimum,kind=model.ReviewKindFilter;service.Pending=Gate();Click(window.RemeasureButton);running=model.CurrentScan;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Check(!window.ReviewMinimumSelector.IsEnabled&&!window.ReviewKindSelector.IsEnabled,"Review filter controls are disabled during replacement: cancel="+cancel);
                    window.ReviewMinimumSelector.SelectedValue=0;window.ReviewKindSelector.SelectedValue=3;
                    model.ReviewMinimum=1073741824;model.ReviewKindFilter=2;
                    Check(model.ReviewMinimum==minimum&&model.ReviewKindFilter==kind,"Binding events and direct setters cannot change frozen review filters: cancel="+cancel);
                    if(cancel)model.CancelScan();service.Pending.SetResult(Fake.Outcome(cancel?500:450));await running;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Check(model.AppliedQuery?.ReviewMinimum==minimum&&model.AppliedQuery?.ReviewKind==kind
                        && Equals(window.ReviewMinimumSelector.SelectedValue,minimum)&&Equals(window.ReviewKindSelector.SelectedValue,kind),"Visible review filters match applied query after replacement: cancel="+cancel);
                    Check(window.ReviewMinimumSelector.IsEnabled&&window.ReviewKindSelector.IsEnabled,"Review filter controls become editable after replacement: cancel="+cancel);
                }
                model.ReviewMinimum=0;model.ReviewKindFilter=0;await model.RefreshAsync();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(model.AppliedQuery?.ReviewMinimum==0&&Equals(window.ReviewMinimumSelector.SelectedValue,0),"Idle review filter edits still update the applied query");
                await window.NavigateAsync(WorkspaceView.Settings);service.Pending=Gate();Click(window.RemeasureButton);
                for(int wait=0;!model.IsRemeasuring&&wait<200;wait++)await Task.Delay(10);
                running=model.CurrentScan;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(model.IsRemeasuring && model.View==WorkspaceView.Folders && model.Rows.Count>0,
                    "Remeasure launched from Settings shows the previous result table before completion");
                Check(!window.ViewOptionsPanel.IsEnabled,"View and search settings are disabled while a replacement is pending");
                bool beforePath=model.FullFolderPath;model.FullFolderPath=!beforePath;
                Check(model.FullFolderPath==beforePath,"Programmatic full-path edit cannot bypass the pending-query guard");
                model.CancelScan();service.Pending.SetResult(Fake.Outcome(470));await running;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(model.AppliedQuery!.FullFolderPath==model.FullFolderPath && window.ViewOptionsPanel.IsEnabled,
                    "Cancellation restores editable settings with a matching applied query");
                service.Pending=Gate();Click(window.RemeasureButton);running=model.CurrentScan;var saved=model.Scan;window.Close();
                Check(window.IsVisible&&!window.IsEnabled,"Closing waits for cancellation and completion of the running replacement");
                service.Pending.SetResult(Fake.Outcome(400));await running;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(!window.IsVisible&&ReferenceEquals(model.Scan,saved),"Late result cannot replace data in a closing window");
                PresentationTraceSources.DataBindingSource.Flush();Check(errors.ToString().Length==0,"No WPF binding errors");
                log.Add($"ALL CHECKS PASSED ({log.Count})");app.Shutdown(0);
            }
            catch(Exception ex){log.Add("FAIL "+ex);if(window.IsVisible)Screenshot(window,Path.Combine(output,"failure.png"));app.Shutdown(1);}
            finally{File.WriteAllLines(Path.Combine(output,"results.txt"),log);foreach(var line in log)Console.WriteLine(line);}
        };return app.Run();
    }
    static bool Inside(FrameworkElement element,FrameworkElement parent){var b=element.TransformToAncestor(parent).TransformBounds(new Rect(element.RenderSize));return b.Left>=0&&b.Top>=0&&b.Right<=parent.ActualWidth&&b.Bottom<=parent.ActualHeight;}
    static void Screenshot(Window window,string path){var content=(FrameworkElement)window.Content;var bitmap=new RenderTargetBitmap((int)content.ActualWidth,(int)content.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(content);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);png.Save(stream);}
    sealed class Fake:IScanService
    {
        internal int Calls;internal long Bytes=100;internal TaskCompletionSource<ScanOutcome>? Pending;internal ScanRequest? Request;internal IProgress<ScanProgress>? Progress;
        public string[] NormalizeRoots(IEnumerable<string> roots)=>roots.ToArray();public Task<ScanOutcome> ScanAsync(ScanRequest r,IProgress<ScanProgress>? p,CancellationToken token){Calls++;Request=r;Progress=p;return Pending?.Task??Task.FromResult(Outcome(Bytes));}
        internal static ScanOutcome Outcome(long bytes){var root=new FolderEntry(@"C:\scan",null){LogicalBytes=bytes,AllocatedBytes=4096,FileCount=1};root.Children.Add(new(root.Path+@"\child",root){LogicalBytes=bytes,AllocatedBytes=4096,FileCount=1});return new(new(root),[]);}
    }
    sealed class Inventory:IInventoryProvider{public Task<InventorySnapshot> ReadAsync(CancellationToken token)=>throw new Exception("UI tests must never read registry");}
}
