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

internal static class TableTests
{
    static readonly List<string> log=[];
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);log.Add("PASS "+label);}
    static IEnumerable<T> Children<T>(DependencyObject root)where T:DependencyObject
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T item)yield return item;foreach(var nested in Children<T>(child))yield return nested;}}
    static void Screenshot(FrameworkElement area,string path)
    {var dpi=VisualTreeHelper.GetDpi(area);var bitmap=new RenderTargetBitmap((int)Math.Ceiling(area.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(area.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);bitmap.Render(area);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var stream=System.IO.File.Create(path);png.Save(stream);}
    static ReviewCandidate Make(string name,string rule,ReviewKind kind,long bytes,FileDataRisk risk=FileDataRisk.None,bool partial=false,int excluded=0)
    {string path=@"C:\Table-Example\"+name;return new(path,bytes,bytes,1,"Purpose category","Konum eşleşmesi; kullanım bilinmiyor",rule,"Known fixture location","Inspect in relevant application",partial,kind,new(ReviewMatchKind.KnownLocation,path,risk,risk==FileDataRisk.None?0:2,risk==FileDataRisk.None?[]:[new(FileDataRisk.Mail,path+@"\personal.pst"),new(FileDataRisk.VirtualDisk,path+@"\disk.vhdx")],FileClueSource.FinalScanObservations),excluded);}
    [STAThread]static int Main(string[] args)
    {
        string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);CultureInfo.DefaultThreadCurrentCulture=CultureInfo.GetCultureInfo("tr-TR");
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/YerelDisk;component/Theme.xaml")});var errors=new StringWriter();PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(errors));PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        app.Startup+=async(_,_)=>
        {
            var examples=new List<ReviewCandidate>{Make("caches-"+new string('x',120),"GRADLE-CACHES",ReviewKind.Cache,9000),Make("Installer","WINDOWS-INSTALLER",ReviewKind.Protected,8000),Make("Temp-personal","USER-TEMP",ReviewKind.Conditional,7000,FileDataRisk.Mail|FileDataRisk.VirtualDisk),Make("models","OLLAMA-MODELS",ReviewKind.Conditional,6000),Make("Temp-partial","USER-TEMP",ReviewKind.Temporary,5000,partial:true,excluded:1),Make("unknown","UNKNOWN",ReviewKind.Conditional,4000)};
            var fake=new FakeService(examples);using var model=new WorkspaceViewModel(fake);var window=new MainWindow(model){WindowStartupLocation=WindowStartupLocation.Manual,Left=-24000,Top=-24000,ShowInTaskbar=false,ShowActivated=false};
            try
            {
                Check(ReviewTableText.Signal(examples[2]).Contains("Posta")&&ReviewTableText.Signal(examples[2]).Contains("Sanal disk")&&ReviewTableText.Signal(examples[2]).Contains("ipucu"),"Risk signal carries both types as name clues");
                Check(ReviewTableText.Signal(examples[1]).Contains("Elle silmeyin")&&ReviewTableText.Effect(examples[1]).Contains("bozulabilir"),"Protected signal and concrete removal effect remain explicit");
                Check(ReviewTableText.Context(examples[0]).Contains("Konum ipucu")&&ReviewTableText.Context(examples[0]).Contains("İncelenmedi"),"Purpose association never becomes active usage");
                Check(ReviewTableText.Signal(examples[4]).Contains("Kısmi")&&ReviewTableText.Attention(examples[4])&&RowValues.MeasurementOf(examples[4]).Contains("hariç"),"Partial and excluded scope remains visible alongside compact signals");
                Check(ReviewTableText.Purpose(examples[5]).Contains("belirlenemedi")&&ReviewTableText.Effect(examples[5]).Contains("belirsiz"),"Unknown rule has no invented purpose or reassuring effect");
                foreach(var c in examples)Check(!ReviewTableText.Signal(c).Contains("güvenle")&&!ReviewTableText.Effect(c).Contains("gereksiz"),"Compact row never declares safe deletion or uselessness: "+c.RuleId);
                foreach(string id in new[]{"GRADLE-CACHES","GRADLE-DISTS","NPM-CACHE","NUGET-PACKAGES","NUGET-HTTP","NUGET-PLUGINS","CARGO-ARCHIVES","CARGO-SOURCES","HF-HUB","OLLAMA-MODELS","ANDROID-AVD","ANDROID-SDK-STRUCTURE","WINDOWS-OLD","PIP-CACHE","CRASH-DUMPS","NVIDIA-DX","NVIDIA-GL","USER-TEMP","WINDOWS-TEMP","PACKAGE-CACHE","WINDOWS-INSTALLER","WINDOWS-COMPONENTS","GAME-Naughty Dog","GAME-Hogwarts Legacy","BROWSER-CACHE"})
                {var c=examples[0]with{RuleId=id};if(ReviewTableText.Purpose(c).Contains("belirlenemedi")||ReviewTableText.Effect(c).Contains("etkisi belirsiz"))throw new Exception("Missing current rule summary: "+id);}
                Check(true,"Every existing rule family has compact purpose and effect");
                window.Show();model.Roots.Add(fake.Root.Path);await model.ScanAsync();model.ReviewMinimum=0;await window.NavigateAsync(WorkspaceView.Review);
                Check(window.ResultsGrid.Columns.Count==4&&window.ResultsGrid.Columns.Any(c=>c.SortMemberPath=="ReviewSignal")&&VirtualizingPanel.GetScrollUnit(window.ResultsGrid)==ScrollUnit.Pixel,"Default table exposes compact assessment without many new columns");
                foreach(bool dark in new[]{false,true})foreach(var size in new[]{(940d,680d),(1480d,940d)})
                {
                    window.SetTheme(dark);window.Width=size.Item1;window.Height=size.Item2;model.SelectedRow=examples[2];window.ResultsGrid.ScrollIntoView(examples[2]);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);window.UpdateLayout();string label=$"review-table-{size.Item1}-{(dark?"dark":"light")}";
                    var scroll=Children<ScrollViewer>(window.ResultsGrid).First();Check(scroll.ScrollableWidth<=1,"Default signal columns fit without horizontal scrolling: "+label+" width="+scroll.ScrollableWidth);
                    var row=(DataGridRow)window.ResultsGrid.ItemContainerGenerator.ContainerFromItem(examples[2]);var text=Children<TextBlock>(row).Where(t=>t.IsVisible).ToArray();
                    Check(text.Any(t=>t.Text==ReviewTableText.Signal(examples[2]))&&text.Any(t=>t.Text==RowValues.Text(examples[2],"ReviewStep"))&&text.Any(t=>t.Text==ReviewTableText.Effect(examples[2])),"Risk, first step and removal effect render before F6: "+label);
                    Check(!text.Any(t=>t.Text==ReviewTableText.Context(examples[2]))&&Children<TextBlock>(window).Any(t=>t.IsVisible&&t.Text==model.Subtitle&&t.Text.Contains("Güncel kullanım incelenmedi")),"Unexamined usage remains visible once in the shared heading: "+label);
                    Check(row.ActualHeight>=80&&text.All(t=>t.ActualHeight+t.Margin.Top+t.Margin.Bottom+1>=t.DesiredSize.Height),"Auto row height avoids clipped assessment lines: "+label);
                    Check(text.Any(t=>t.Text==ReviewTableText.Purpose(examples[2])),"Purpose appears beneath folder name: "+label);
                    var cell=Children<DataGridCell>(row).First(c=>c.Column.SortMemberPath=="ReviewSignal");var end=cell.TranslatePoint(new Point(cell.ActualWidth,0),window.ResultsGrid);Check(end.X<=window.ResultsGrid.ActualWidth+1,"Whole assessment cell stays inside viewport: "+label);
                    Screenshot((FrameworkElement)window.Content,Path.Combine(output,label+".png"));
                    model.SelectedRow=examples[0];window.ResultsGrid.ScrollIntoView(examples[0]);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);window.UpdateLayout();
                    var plainRow=(DataGridRow)window.ResultsGrid.ItemContainerGenerator.ContainerFromItem(examples[0]);
                    Check(plainRow.ActualHeight<=110,"Ordinary candidate stays compact even with a long folder name: "+label+" height="+plainRow.ActualHeight);
                    Check(Children<TextBlock>(plainRow).Any(t=>t.Text==ReviewTableText.Purpose(examples[0])&&t.TextTrimming==TextTrimming.CharacterEllipsis),"Long purpose uses single-line preview with full tooltip: "+label);
                }
                string futurePath=@"C:\Table-Example\future-risk";
                var future=examples[2]with{Path=futurePath,Evidence=examples[2].Evidence!with{MatchedPath=futurePath,DataRisks=(FileDataRisk)31,ClueCount=5,Examples=[]}};
                examples.Add(future);await model.ScanAsync();window.Width=940;window.Height=680;model.SelectedRow=future;window.ResultsGrid.ScrollIntoView(future);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);window.UpdateLayout();
                var futureRow=(DataGridRow)window.ResultsGrid.ItemContainerGenerator.ContainerFromItem(future);
                Check(Children<TextBlock>(futureRow).Any(t=>t.IsVisible&&t.Text==ReviewTableText.Signal(future)&&t.Text.Contains("Ek tür")&&t.Text.Contains("Sanal disk")),"Unknown risk stays visible alongside known types in the narrow rendered table");
                Check(Children<ScrollViewer>(window.ResultsGrid).First().ViewportHeight>=futureRow.ActualHeight,"Full mixed-risk row fits the minimum-height review viewport");
                var futureDetail=window.CreateDetailsWindow();futureDetail.WindowStartupLocation=WindowStartupLocation.Manual;futureDetail.Left=-24000;futureDetail.Top=-24000;futureDetail.ShowInTaskbar=false;futureDetail.Show();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(Children<TextBox>(futureDetail.ContentView).Any(t=>t.Text.Contains("tanımlanamayan ek dosya türü"))&&Children<TextBox>(futureDetail.ContentView).Any(t=>t.Text.Contains("çözümlenemedi")),"Rendered F6 gives explicit unknown type and an uncertainty check");futureDetail.Close();
                Check(ReviewText.CsvFields(future,"").Length==19&&ReviewText.CsvFields(future,"")[15].Contains("tanımlanamayan"),"Unknown type survives the existing 19-field CSV contract");
                Screenshot((FrameworkElement)window.Content,Path.Combine(output,"unknown-risk-940.png"));examples.Remove(future);await model.ScanAsync();
                await model.SortAsync("ReviewSignal");Check(ReferenceEquals(model.Rows[0],examples[1])&&model.SortDescription.Contains("Uyarı önceliği"),"Assessment header sorts warnings, without a deletion-confidence score");
                await model.SortAsync("ReviewSignal");Check(ReferenceEquals(model.Rows[0],examples[0]),"Reverse warning order is deterministic");
                model.SelectedRow=examples[2];var detail=window.CreateDetailsWindow();detail.WindowStartupLocation=WindowStartupLocation.Manual;detail.Left=-24000;detail.Top=-24000;detail.ShowInTaskbar=false;detail.Show();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(model.Details.FullText.Contains("personal.pst")&&model.Details.FullText.Contains("Kullanım bilgisi")&&model.Details.Sections.Any(s=>s.Heading=="İlk kontrol adımı"&&s.Text.Contains("Posta dosyasının")),"F6 keeps full evidence and concrete first-step checks");detail.Close();
                var writer=new StringWriter();var report=ReportSnapshot.Capture(model);report.Write(writer);Check(report.Items.Length==examples.Count&&ReviewText.CsvHeader.Length==19&&writer.ToString().Contains("Kanıt kapsamı"),"CSV keeps full 19-field explanations rather than replacing them with short labels");
                model.TechnicalColumns=true;window.ConfigureColumns();Check(window.ResultsGrid.Columns.Count==7&&window.ResultsGrid.Columns.Any(c=>c.SortMemberPath=="Evidence")&&window.ResultsGrid.Columns.Any(c=>c.SortMemberPath=="Path"),"Technical view retains original type, full evidence label and path");
                model.TechnicalColumns=false;window.ConfigureColumns();await window.NavigateAsync(WorkspaceView.Files);Check(window.ResultsGrid.RowHeight==42&&VirtualizingPanel.GetScrollUnit(window.ResultsGrid)==ScrollUnit.Item,"Other tables keep original compact row height");
                await window.NavigateAsync(WorkspaceView.Review);Check(double.IsNaN(window.ResultsGrid.RowHeight)&&window.ResultsGrid.Columns.Count==4,"Returning to review restores automatic assessment row height");
                for(int i=0;i<2000;i++)examples.Add(examples[0]with{Path=@"C:\Table-Example\generated-"+i.ToString("D4")});
                await model.ScanAsync();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);window.UpdateLayout();
                Check(model.Rows.Count==2006&&Children<DataGridRow>(window.ResultsGrid).Count()<100,"Multiline assessment rows remain virtualized with 2006 candidates");
                PresentationTraceSources.DataBindingSource.Flush();Check(errors.ToString().Length==0,"No WPF binding errors in compact assessment rows");
                log.Add($"ALL CHECKS PASSED ({log.Count})");System.IO.File.WriteAllLines(Path.Combine(output,"results.txt"),log);Console.WriteLine(log[^1]);window.Close();app.Shutdown(0);
            }
            catch(Exception ex){log.Add("FAIL "+ex);System.IO.File.WriteAllLines(Path.Combine(output,"results.txt"),log);Screenshot((FrameworkElement)window.Content,Path.Combine(output,"failure.png"));Console.WriteLine(ex);window.Close();app.Shutdown(1);}
        };
        return app.Run();
    }
    sealed class FakeService(List<ReviewCandidate> candidates):IScanService
    {
        internal FolderEntry Root{get;}=new(@"C:\Table-Example",null){LogicalBytes=39000,FileCount=6};
        public string[] NormalizeRoots(IEnumerable<string> paths)=>paths.ToArray();
        public Task<ScanOutcome> ScanAsync(ScanRequest request,IProgress<ScanProgress>? progress,CancellationToken token)=>Task.FromResult(new ScanOutcome(new(Root),candidates));
    }
}
