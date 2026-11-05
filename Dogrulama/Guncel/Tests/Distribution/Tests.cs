using System.Diagnostics;
using System.Globalization;
using YerelDisk;
using YerelDisk.Presentation;

internal static class DistributionTests
{
    static readonly List<string> log=[];
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);log.Add("PASS "+label);}
    static FolderEntry Child(FolderEntry parent,string name,long bytes,MeasurementState state=MeasurementState.Measured)
    {var child=new FolderEntry(Path.Combine(parent.Path,name),parent){LogicalBytes=bytes,FileCount=1,Measurement=state};parent.Children.Add(child);return child;}
    static ScanResult Sample()
    {
        var root=new FolderEntry(@"C:\sample",null){LogicalBytes=100,FileCount=3};Child(root,"large",60);Child(root,"small",30);Child(root,"zero",0);Child(root,"unknown",0,MeasurementState.Unknown);return new(root);
    }
    static SpaceDistribution Chart(ScanResult scan,IReadOnlyList<object>? visible=null,bool filtered=false)
        =>SpaceDistribution.Create(scan,scan.Root,visible??scan.Root.Children.Cast<object>().ToArray(),filtered,CancellationToken.None);
    static async Task<int> Main(string[] args)
    {
        string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);CultureInfo.DefaultThreadCurrentCulture=CultureInfo.GetCultureInfo("tr-TR");
        try
        {
            var sample=Sample();var chart=Chart(sample);
            Check(chart.Segments.Count==3,"Only positive known children and direct data form bars");
            Check(chart.Segments[0].Folder==sample.Root.Children[0]&&chart.Segments[0].Bytes==60&&chart.Segments[0].Share==0.6,"Largest child keeps original folder reference and parent-based share");
            Check(chart.Segments.Single(s=>s.Group==DistributionGroup.Direct).Bytes==10,"Direct files and own streams are the measured parent remainder");
            Check(Math.Abs(chart.Segments.Sum(s=>s.Share??0)-1)<1e-12,"Represented shares sum to measured parent when known observations cover it");
            Check(chart.Notice.Contains("1 klasörün boyutu bilinmiyor")&&chart.Notice.Contains("1 klasör 0 bayt"),"Unknown and measured zero are counted separately");
            Check(chart.Notice.Contains("Silerek kazanılacak")&&chart.Caption.Contains("mantıksal"),"Logical chart does not imply reclaimable disk space");
            Check(chart.Segments[0].ShareLabel=="%60,0"&&chart.Segments[0].BarValue==60,"Turkish share label and proportional bar agree");
            var filtered=Chart(sample,[sample.Root.Children[1]],true);
            Check(filtered.Segments.Single(s=>s.Folder!=null).Share==0.3,"Search never renormalizes the denominator to matching rows");
            Check(filtered.Segments.Single(s=>s.Group==DistributionGroup.FilteredOut).Bytes==60,"Search-excluded child bytes form an explicit context group");
            Check(filtered.Segments.Single(s=>s.Group==DistributionGroup.Direct).Bytes==10,"Direct data remains separate from search-excluded folders");
            var noMatch=Chart(sample,[],true);
            Check(noMatch.Segments.Count==2&&noMatch.Segments.All(s=>s.Folder==null)&&noMatch.Notice.Contains("arama dışı"),"No matching folder retains explicitly labeled context instead of an empty-folder claim");
            var root=new FolderEntry(@"C:\many",null);for(int i=1;i<=25;i++){Child(root,"f"+i.ToString("D2"),i);root.LogicalBytes+=i;}
            var many=Chart(new(root));
            Check(many.Segments.Count==13&&many.Segments.Count(s=>s.Folder!=null)==12,"Top selection has a fixed 12-folder object bound");
            Check(many.Segments[0].Bytes==25&&many.Segments[11].Bytes==14,"Top folders are selected by size regardless of table order");
            Check(many.Segments.Single(s=>s.Group==DistributionGroup.Remaining).Bytes==91&&many.Segments.Sum(s=>s.Bytes)==325,"Remaining matched group preserves exact byte sum");
            var reverse=Chart(new(root),root.Children.AsEnumerable().Reverse().Cast<object>().ToArray());
            Check(reverse.Segments.Select(s=>s.Title).SequenceEqual(many.Segments.Select(s=>s.Title)),"Graph ordering is independent of table sort order");
            var ties=new FolderEntry(@"C:\ties",null){LogicalBytes=20};for(int i=19;i>=0;i--)Child(ties,"f"+i.ToString("D2"),1);
            Check(Chart(new(ties)).Segments.Take(12).Select(s=>s.Title).SequenceEqual(Enumerable.Range(0,12).Select(i=>"f"+i.ToString("D2"))),"Equal sizes use deterministic ordinal path ordering");
            var tiny=new DistributionSegment("tiny",1,0.0000001,"",DistributionGroup.Direct);
            Check(tiny.ShareLabel=="< %0,1"&&tiny.BarValue>0&&tiny.BarValue<0.001,"Tiny positive share is labeled below threshold without inventing a minimum-width share");
            var zero=Chart(new(new FolderEntry(@"C:\zero",null)));
            Check(zero.Segments.Count==0&&zero.Notice.Contains("pozitif boyut yok"),"All-zero result avoids divide-by-zero or a fabricated full bar");
            sample.Root.Measurement=MeasurementState.Unknown;var unknown=Chart(sample);
            Check(unknown.Segments.All(s=>s.Share==null)&&unknown.Segments.All(s=>s.Group!=DistributionGroup.Direct)&&unknown.Caption.Contains("bilinmiyor"),"Unknown parent suppresses percentages and direct-data inference");
            Check(unknown.Segments[0].ShareLabel=="Pay bilinmiyor"&&unknown.Segments[0].BarValue==0,"Unknown share is textual and has no bar");
            sample=Sample();sample.Root.LogicalBytes=110;sample.Root.Children[3].LogicalBytes=10;var unknownObservation=Chart(sample);
            Check(unknownObservation.Segments.Single(s=>s.Group==DistributionGroup.Direct).Bytes==10,"Unknown child's partial bytes never become parent-direct files");
            Check(unknownObservation.Notice.Contains("kısmi gözlemler")&&unknownObservation.Segments.Sum(s=>s.Bytes)==100,"Unplotted unknown observation is explicitly disclosed");
            sample.Root.Children[0].Measurement=MeasurementState.Previous;sample.Root.Measurement=MeasurementState.Partial;sample.Root.ExcludedSubtrees=2;sample.Cancelled=true;
            var partial=Chart(sample);
            Check(!partial.Segments[0].CanEnter&&partial.Segments[0].Note.Contains("Son ölçüm"),"Previous folder keeps qualification and disabled navigation");
            Check(partial.Notice.Contains("Kısmi / son")&&partial.Notice.Contains("2 yol hariç"),"Interrupted and intentionally excluded scope remains explicit");
            sample.UnverifiedPaths.Add(sample.Root.Children[1].Path);Check(!Chart(sample).Segments.Single(s=>s.Folder==sample.Root.Children[1]).CanEnter,"Unverified path cannot be entered through chart");
            var inconsistent=Sample();inconsistent.Root.LogicalBytes=20;var invalid=Chart(inconsistent);
            Check(invalid.Segments.All(s=>s.Share==null)&&invalid.Segments.All(s=>s.Group!=DistributionGroup.Direct)&&invalid.Notice.Contains("tutarsızlık"),"Inconsistent totals do not produce oversized percentages or negative direct bars");
            var overflow=new FolderEntry(@"C:\overflow",null){LogicalBytes=long.MaxValue};Child(overflow,"a",long.MaxValue);Child(overflow,"b",long.MaxValue);
            Check(Chart(new(overflow)).Segments.All(s=>s.Share==null),"Child summation does not wrap Int64 and invent valid shares");
            using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();bool threw=false;try{SpaceDistribution.Create(new(root),root,[],false,cancelled.Token);}catch(OperationCanceledException){threw=true;}Check(threw,"Distribution preparation honors cancellation");}
            var wide=new FolderEntry(@"C:\wide",null){LogicalBytes=100000};for(int i=0;i<100000;i++)Child(wide,"f"+i.ToString("D6"),1);
            var visible=wide.Children.Cast<object>().ToArray();Chart(new(wide),visible);long before=GC.GetAllocatedBytesForCurrentThread();var watch=Stopwatch.StartNew();var bounded=Chart(new(wide),visible);watch.Stop();long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
            Check(bounded.Segments.Count==13&&bounded.Segments.Sum(s=>s.Bytes)==100000,"100k child model preserves totals with 13 chart records");
            Check(allocated<65536,"100k preparation allocates bounded chart metadata, not per-folder wrappers");
            log.Add($"MEASURE chart-only 100k children: {watch.Elapsed.TotalMilliseconds:F2} ms; {allocated} managed bytes; not full-app or disk benchmark");
            sample=Sample();var fake=new FakeService(sample);using var model=new WorkspaceViewModel(fake);
            await model.SetDistributionModeAsync(true);Check(!model.IsDistribution,"Chart cannot be enabled before a result exists");
            model.Roots.Add(sample.Root.Path);await model.ScanAsync();Check(model.ShowTable&&model.Distribution.Segments.Count==0,"Table is default; chart model is not built until requested");
            await model.SetDistributionModeAsync(true);Check(model.IsDistribution&&model.CanUseDistribution&&model.Distribution.Segments.Count==3,"Toggle builds chart from completed query");
            var selected=model.Distribution.Segments[0];model.SelectDistributionSegment(selected);Check(ReferenceEquals(model.SelectedRow,selected.Folder),"Chart selection uses shared row/detail state");
            await model.EnterAsync(selected.Folder!);Check(model.FolderPath==selected.Folder!.Path&&model.IsDistribution&&model.Distribution.Segments.Single().Group==DistributionGroup.Direct,"Chart navigation preserves mode and shows leaf-direct data even with no child rows");
            model.SelectDistributionSegment(selected);Check(model.SelectedRow==null,"Old folder chart cannot select an absent current row");
            await model.GoUpAsync();Check(model.FolderPath==sample.Root.Path&&model.Distribution.Segments.Count==3,"Up navigation rebuilds the same parent distribution");
            model.Search="small";await model.RefreshAsync();Check(model.Rows.Count==1&&model.Distribution.Segments.Single(s=>s.Folder!=null).Share==0.3,"Chart uses applied search query");
            var report=ReportSnapshot.Capture(model);var writer=new StringWriter();report.Write(writer);
            Check(report.Items.Length==1&&writer.ToString().Contains("çubuk grupları ayrı kayıt değildir"),"CSV exports full filtered table rows with explicit chart semantics");
            await model.SetViewAsync(WorkspaceView.Files);Check(model.ShowTable&&!model.IsDistribution&&model.Distribution.Segments.Count==0,"Other views keep their tables and release chart model");
            await model.SetViewAsync(WorkspaceView.Folders);Check(model.IsDistribution&&model.Rows.Count==1,"Returning to folders restores chart preference and saved filter");
            model.SelectDistributionSegment(model.Distribution.Segments.Single(s=>s.Folder!=null));await model.SetDistributionModeAsync(false);
            Check(model.ShowTable&&model.SelectedRow==sample.Root.Children[1]&&model.Distribution.Segments.Count==0,"Switching to table preserves folder selection and releases chart records");
            await model.SetDistributionModeAsync(true);await model.ScanAsync();Check(model.Distribution.Segments.All(s=>s.Folder==null||sample.Root.Children.Contains(s.Folder)),"New scan replaces chart from new applied result, not stale rows");
            log.Add($"ALL CHECKS PASSED ({log.Count(s=>s.StartsWith("PASS "))})");File.WriteAllLines(Path.Combine(output,"results.txt"),log);Console.WriteLine(log[^1]);return 0;
        }
        catch(Exception ex){log.Add("FAIL "+ex);File.WriteAllLines(Path.Combine(output,"results.txt"),log);Console.WriteLine(ex);return 1;}
    }
    sealed class FakeService(ScanResult scan):IScanService
    {
        public string[] NormalizeRoots(IEnumerable<string> roots)=>roots.ToArray();
        public Task<ScanOutcome> ScanAsync(ScanRequest request,IProgress<ScanProgress>? progress,CancellationToken token)=>Task.FromResult(new ScanOutcome(scan,[]));
    }
}
