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
    static void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    [STAThread]static int Main(string[] args)
    {
        string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);string fixtures=Path.Combine(output,"own-fixtures");Directory.CreateDirectory(fixtures);
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/YerelDisk;component/Theme.xaml")});
        using var errors=new StringWriter();PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(errors));PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        app.Startup+=async(_,_)=>
        {
            var service=new Fake();using var model=new WorkspaceViewModel(service);var main=new MainWindow(model);Position(main);app.MainWindow=main;main.Show();
            try
            {
                model.Roots.Add(@"C:\existing-scan");await model.ScanAsync();var oldScan=model.Scan;
                string store=Path.Combine(fixtures,"profiles.json");var library=new ScanProfiles(store);
                string[] roots=[@"C:\projects",@"D:\games"];string[] excluded=[@"C:\projects\obj"];
                var scope=new RootsWindow(roots,MultiScan.NormalizeRootPaths,excluded,()=>library){Owner=main};Position(scope);scope.Show();
                var dialog=new ProfilesWindow(library,scope.Paths,scope.Exclusions){Owner=scope};Position(dialog);dialog.Show();
                Check(dialog.ProfileList.Items.Count==0 && !dialog.UseButton.IsEnabled && dialog.SaveButton.IsEnabled,"Empty profile library keeps creation available and application disabled");
                dialog.NameInput.Text="Work and games";await dialog.SaveAsync(false);
                var first=library.Profiles.Single();
                Check(dialog.ProfileList.Items.Count==1 && first.Roots.SequenceEqual(roots) && first.Exclusions.SequenceEqual(excluded),"Save action captures both current roots and exclusions");
                Check(!File.Exists(store) && dialog.Feedback.Text.Contains("oturum"),"Session save is explicitly explained without disk persistence");
                dialog.NameInput.Text="Work setup";await dialog.SaveAsync(true);
                Check(library.Profiles.Single().Id==first.Id && library.Profiles.Single().Name=="Work setup","Update action preserves selection identity");
                await dialog.SaveAsync(false);
                Check(library.Profiles.Count==1 && dialog.Feedback.Text.Contains("Bu adla"),"Duplicate name validation stays in the window without losing the profile");
                dialog.NameInput.Text="Temporary";await dialog.SaveAsync(false);Click(dialog.RemoveButton);await dialog.PendingOperation;
                Check(library.Profiles.Count==1 && !File.Exists(store),"Remove action only deletes the selected profile record");
                dialog.RememberOption.IsChecked=true;await dialog.PendingOperation;
                Check(File.Exists(store) && ScanProfiles.Load(store).Profiles.Single().Name=="Work setup","Remember checkbox persists only to the supplied local fixture store");
                foreach(bool dark in new[]{false,true})
                {
                    main.SetTheme(dark);dialog.Width=dark?660:820;dialog.Height=dark?600:720;scope.Width=dark?540:760;scope.Height=dark?480:600;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);dialog.UpdateLayout();scope.UpdateLayout();
                    var content=(FrameworkElement)dialog.Content;
                    Color Of(object brush)=>((SolidColorBrush)brush).Color;
                    Check(Of(dialog.Background)==Of(app.Resources["Canvas"]) && Of(dialog.Foreground)==Of(app.Resources["Ink"]),$"Profile window follows {(dark?"dark":"light")} theme");
                    log.Add($"Layout dark={dark}: rows={dialog.ProfileList.ActualHeight:F1}, name={dialog.NameInput.ActualWidth:F1}, applyInside={Inside(dialog.UseButton,content)}");
                    Screenshot(dialog,Path.Combine(output,dark?"profiles-compact-dark.png":"profiles-wide-light.png"));
                    Check(Inside(dialog.UseButton,content) && dialog.NameInput.ActualWidth>=120 && dialog.ProfileList.ActualHeight>=104,$"Profile list, name editor and application fit in {(dark?"compact":"wide")} layout");
                    Check(Inside(scope.ProfilesButton,(FrameworkElement)scope.Content) && scope.IncludedList.ActualHeight>=35,$"Scope profile entry preserves the {(dark?"compact":"wide")} folder list");
                }
                byte[] saved=File.ReadAllBytes(store);
                using(var handle=new FileStream(store,FileMode.Open,FileAccess.Read,FileShare.Read))
                {
                    dialog.NameInput.Text="Unsaved due to lock";var pending=dialog.SaveAsync(false);dialog.Close();
                    Check(dialog.IsVisible && (dialog.Feedback.Text.Contains("İşlem")||dialog.Feedback.Text.Contains("uygulanıyor")),"Closing waits for the explicit profile write to finish");
                    await pending;
                    Check(dialog.Feedback.Text.Contains("kaydedilemedi") && File.ReadAllBytes(store).SequenceEqual(saved),"Persistence failure is visible and preserves the previous saved library");
                }
                dialog.RememberOption.IsChecked=false;await dialog.PendingOperation;
                Check(!File.Exists(store) && library.Profiles.Count==2,"Opt-out removes the preference file while preserving session edits");
                dialog.Close();
                var offline=library.Save("Offline root",[@"R:\offline"],[]);
                var use=new ProfilesWindow(library,scope.Paths,scope.Exclusions){Owner=scope};Position(use);use.Show();
                use.ProfileList.SelectedItem=library.Profiles.Single(p=>p.Id==offline.Id);Click(use.UseButton);
                Check(!use.IsVisible && use.SelectedProfile?.Id==offline.Id,"Use action closes with the selected immutable profile");
                scope.ApplyProfile(use.SelectedProfile!);
                Check(scope.Paths.SequenceEqual([@"R:\offline"]) && scope.Exclusions.Length==0 && scope.Note.Text.Contains("Henüz tarama başlamadı"),"Applying replaces both editable lists and explains that scanning has not started");
                Check(service.ScanCalls==1 && ReferenceEquals(model.Scan,oldScan) && model.Scan!.RootPaths.SequenceEqual([@"C:\existing-scan"]),"Editing a profile never mutates the existing scan snapshot or starts a scan");
                scope.IncludedList.Items.Add(@"C:\extra");Check(offline.Roots.Count==1,"Editing an applied scope cannot mutate its stored profile");
                Check(scope.ValidateScope(),"Applied scope follows the same normal acceptance path");
                string conflictStore=Path.Combine(fixtures,"conflict.json");var seed=new ScanProfiles(conflictStore);var initial=seed.Save("Original",[@"C:\Original"],[]);seed.SetRemember(true);seed.Persist();
                var remote=ScanProfiles.Load(conflictStore);var local=ScanProfiles.Load(conflictStore);
                var conflictWindow=new ProfilesWindow(local,[@"D:\Local"],[]){Owner=scope};Position(conflictWindow);conflictWindow.Show();
                remote.Save("Remote",[@"C:\Remote"],[],initial.Id);remote.Persist();byte[] remoteBytes=File.ReadAllBytes(conflictStore);
                conflictWindow.NameInput.Text="Local";await conflictWindow.SaveAsync(true);
                Check(local.Conflict!=null && local.Profiles.Single().Name=="Local" && File.ReadAllBytes(conflictStore).SequenceEqual(remoteBytes),"UI stale update keeps the local draft and leaves the remote file byte-identical");
                Check(conflictWindow.ConflictActions.Visibility==Visibility.Visible && conflictWindow.NormalActions.Visibility==Visibility.Collapsed && !conflictWindow.UseButton.IsEnabled && !conflictWindow.ProfileList.IsEnabled,"Conflict mode exposes explicit resolution and blocks normal application");
                Click(conflictWindow.UseButton);await conflictWindow.SaveAsync(false);Click(conflictWindow.RemoveButton);await conflictWindow.PendingOperation;
                Check(conflictWindow.IsVisible && conflictWindow.SelectedProfile==null && local.Profiles.Count==1 && local.Conflict!=null,"Programmatic normal actions cannot bypass an unresolved conflict");
                conflictWindow.Width=660;conflictWindow.Height=600;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);conflictWindow.UpdateLayout();
                Screenshot(conflictWindow,Path.Combine(output,"profiles-conflict-compact-dark.png"));
                log.Add($"Conflict layout: list={conflictWindow.ProfileList.ActualHeight:F1}");
                Check(Inside(conflictWindow.CopyConflictButton,(FrameworkElement)conflictWindow.Content) && Inside(conflictWindow.UseSavedButton,(FrameworkElement)conflictWindow.Content) && Inside(conflictWindow.UseButton,(FrameworkElement)conflictWindow.Content) && conflictWindow.ProfileList.ActualHeight>=68,"Conflict resolution fits the minimum window size without losing the selected row");
                Check(conflictWindow.SelectedScope.Text.Contains(@"D:\Local") && conflictWindow.SelectedScope.Text.Contains(@"C:\Remote") && conflictWindow.Feedback.Text.Contains("korunuyor"),"Conflict details explain both scopes and preservation of unsaved work");
                conflictWindow.NameInput.Text="Remote";Click(conflictWindow.CopyConflictButton);await conflictWindow.PendingOperation;
                Check(local.Conflict!=null && conflictWindow.Feedback.Text.Contains("farklı") && File.ReadAllBytes(conflictStore).SequenceEqual(remoteBytes),"Invalid copy name stays editable without losing the conflict");
                conflictWindow.NameInput.Text="Local copy";Click(conflictWindow.CopyConflictButton);await conflictWindow.PendingOperation;
                var persisted=ScanProfiles.Load(conflictStore);
                Check(local.Conflict==null && persisted.Profiles.Count==2 && persisted.Profiles.Single(p=>p.Id==initial.Id).Name=="Remote" && persisted.Profiles.Single(p=>p.Name=="Local copy").Roots.Single()==@"D:\Local","Copy button saves a separate local scope while preserving the remote ID");
                Check(conflictWindow.NormalActions.Visibility==Visibility.Visible && conflictWindow.ConflictActions.Visibility==Visibility.Collapsed && conflictWindow.UseButton.IsEnabled,"Successful conflict resolution restores normal profile actions");
                conflictWindow.Close();

                remote=ScanProfiles.Load(conflictStore);local=ScanProfiles.Load(conflictStore);remote.Remove(initial.Id);remote.Persist();
                conflictWindow=new ProfilesWindow(local,[@"D:\Stale"],[]){Owner=scope};Position(conflictWindow);conflictWindow.Show();conflictWindow.ProfileList.SelectedItem=local.Profiles.Single(p=>p.Id==initial.Id);conflictWindow.NameInput.Text="Deleted draft";await conflictWindow.SaveAsync(true);
                Check(local.Conflict is{Saved:null} && conflictWindow.SelectedScope.Text.Contains("silinmiş"),"Deleted remote profile is distinguished from an edited one in the UI");
                remote.Save("Newest unrelated",[@"C:\Newest"],[]);remote.Persist();Click(conflictWindow.UseSavedButton);await conflictWindow.PendingOperation;
                Check(local.Conflict==null && local.Profiles.All(p=>p.Id!=initial.Id) && local.Profiles.Any(p=>p.Name=="Newest unrelated") && ScanProfiles.Load(conflictStore).Profiles.All(p=>p.Id!=initial.Id),"Using disk state reads the latest library without resurrecting the deleted profile");
                conflictWindow.Close();

                remote=ScanProfiles.Load(conflictStore);local=ScanProfiles.Load(conflictStore);var toDelete=local.Profiles.First();remote.Save("Updated before delete",[@"C:\Changed"],[],toDelete.Id);remote.Persist();
                conflictWindow=new ProfilesWindow(local,[@"D:\Unused"],[]){Owner=scope};Position(conflictWindow);conflictWindow.Show();conflictWindow.ProfileList.SelectedItem=local.Profiles.Single(p=>p.Id==toDelete.Id);Click(conflictWindow.RemoveButton);await conflictWindow.PendingOperation;
                Check(local.Conflict is{Local:null} && !conflictWindow.CopyConflictButton.IsEnabled && !conflictWindow.NameInput.IsEnabled && conflictWindow.UseSavedButton.IsEnabled,"Stale removal offers only the valid disk-state resolution");
                Click(conflictWindow.UseSavedButton);await conflictWindow.PendingOperation;
                Check(local.Conflict==null && local.Profiles.Any(p=>p.Id==toDelete.Id && p.Name=="Updated before delete"),"Resolving a removal conflict restores the newer profile for explicit review");
                conflictWindow.Close();
                scope.Close();PresentationTraceSources.DataBindingSource.Flush();Check(errors.ToString().Length==0,"No WPF binding errors");
                log.Add($"ALL CHECKS PASSED ({log.Count(s=>s.StartsWith("PASS "))})");app.Shutdown(0);
            }
            catch(Exception ex){log.Add("FAIL "+ex);app.Shutdown(1);}
            finally{File.WriteAllLines(Path.Combine(output,"results.txt"),log);}
        };
        return app.Run();
    }
    static void Position(Window window){window.WindowStartupLocation=WindowStartupLocation.Manual;window.Left=-24000;window.Top=0;window.ShowInTaskbar=false;window.ShowActivated=false;}
    static bool Inside(FrameworkElement element,FrameworkElement parent){var b=element.TransformToAncestor(parent).TransformBounds(new Rect(element.RenderSize));return b.Left>=0&&b.Top>=0&&b.Right<=parent.ActualWidth&&b.Bottom<=parent.ActualHeight;}
    static void Screenshot(Window window,string path){var content=(FrameworkElement)window.Content;var visual=new DrawingVisual();using(var dc=visual.RenderOpen()){dc.DrawRectangle(window.Background,null,new Rect(content.RenderSize));dc.DrawRectangle(new VisualBrush(content),null,new Rect(content.RenderSize));}var bitmap=new RenderTargetBitmap((int)content.ActualWidth,(int)content.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);png.Save(stream);}
    sealed class Fake:IScanService
    {
        public int ScanCalls;
        public string[] NormalizeRoots(IEnumerable<string> paths)=>paths.ToArray();
        public Task<ScanOutcome> ScanAsync(ScanRequest request,IProgress<ScanProgress>? progress,CancellationToken token){ScanCalls++;return Task.FromResult(new ScanOutcome(new ScanResult(new FolderEntry(@"C:\existing-scan",null)),[]));}
    }
}
