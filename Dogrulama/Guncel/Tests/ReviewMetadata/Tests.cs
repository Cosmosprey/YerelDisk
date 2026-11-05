using System.Diagnostics;
using YerelDisk;
using YerelDisk.Presentation;

internal static class MetadataTests
{
    static readonly List<string> log=[];
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);log.Add("PASS "+label);}
    static string fixtures="";
    static FileObservation File(string name,bool unsafeFile=false)=>new(name,1,4096,DateTime.MinValue,1,0,false,0,0,unsafeFile,"");
    static ScanResult Synthetic(string path,IEnumerable<(FolderEntry Owner,FileObservation File)>? files=null)
    {var root=new FolderEntry(path,null){LogicalBytes=100,FileCount=2};return new(root){FileCandidates=files};}
    static void Seal(ScanResult scan,ScanExclusions? exclusions=null){scan.ObservedEvidence=ObservedFileEvidence.Capture([scan],exclusions);scan.FileCandidates=null;}
    static ReviewRules Rules(string home)=>new(home,Path.Combine(home,"local"),Path.Combine(home,"data"),@"C:\Windows");
    static ReviewCandidate Candidate(ScanResult scan,string home)=>Rules(home).Analyze(scan).Single();
    sealed class Callback(Action<ScanProgress> action):IProgress<ScanProgress>{public void Report(ScanProgress value)=>action(value);}
    static ScanResult Scan(string[] roots,int limit,int workers,Action<ScanProgress>? action=null,string[]? excluded=null)
        =>VerifiedScan.Scan(roots,action==null?null:new Callback(action),CancellationToken.None,limit,workers,excluded);
    static void Data(string path,int bytes){Directory.CreateDirectory(Path.GetDirectoryName(path)!);System.IO.File.WriteAllBytes(path,new byte[bytes]);}
    static int Main(string[] args)
    {
        string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);fixtures=Path.Combine(output,"own-fixtures");Directory.CreateDirectory(fixtures);
        try
        {
            string home=Path.Combine(fixtures,"user"),cache=Path.Combine(home,".gradle","caches"),nested=Path.Combine(cache,"nested");
            Data(Path.Combine(cache,"big.bin"),8192);foreach(string ext in new[]{".pst",".vhdx",".gguf",".dmp"})Data(Path.Combine(nested,"small"+ext),1);
            ScanResult? first=null;
            foreach(int workers in new[]{1,4,8,16})
            {
                var scan=Scan([cache],1,workers);first??=scan;var c=Candidate(scan,home);var e=ReviewText.Explain(c);
                Check(scan.Files.Single().Name=="big.bin"&&c.Evidence is{ClueCount:4,Examples.Count:4,ClueSource:FileClueSource.FinalScanObservations},"Small four-type risks survive K=1: workers "+workers);
                Check(c.Kind==ReviewKind.Conditional&&scan.Root.LogicalBytes==8196&&scan.Root.FileCount==5,"Evidence does not alter byte/count totals: workers "+workers);
                Check(e.Coverage.Contains("sınırından bağımsız")&&!e.FileClues.Contains("yalnız tutulan")&&e.FileClues.Contains("small.pst"),"Observed scope is distinct from retained-file scope: workers "+workers);
                Check(scan.FileCandidates==null&&scan.ObservedEvidence!.Owners.Count==1&&scan.ObservedEvidence.VisitedNames==5,"Full snapshots are released; only sparse owner summary survives: workers "+workers);
            }
            var small=Candidate(first!,home);var expanded=Candidate(Scan([cache],100,4),home);
            Check(small.Evidence!.DataRisks==expanded.Evidence!.DataRisks&&small.Evidence.ClueCount==expanded.Evidence.ClueCount,"Candidate evidence is invariant under list retention setting");
            var explanation=ReviewText.Explain(small);
            Check(explanation.Association.Contains("amaç ilişkisi")&&explanation.Association.Contains("doğrulanmadı"),"Known location is explicitly a purpose association, not verified dependency");
            Check(small.Evidence.Usage==ReviewUsageState.NotExamined&&explanation.Usage.Contains("İncelenmedi")&&explanation.Usage.Contains("proje içerikleri incelenmedi"),"Usage stays unexamined; no inventory or content inference");
            var fields=ReviewText.CsvFields(small,"Tutacağım");
            Check(fields.Length==19&&fields[14]=="Tutacağım"&&fields[15].Contains("small.pst")&&fields[16]==explanation.Association&&fields[17]==explanation.Usage&&fields[18]==explanation.Coverage,"CSV preserves prior positions and appends independent relation/usage/coverage");
            var details=SelectionDetails.Create(first,small,true,"Tutacağım","scope");
            Check(details.Sections.Any(s=>s.Heading=="Amaç")&&details.Sections.Any(s=>s.Heading=="İlişki")&&details.Sections.Any(s=>s.Heading=="Kullanım bilgisi")&&details.Sections.Any(s=>s.Heading=="Kaldırma etkisi"&&s.Caution),"Details separate purpose, association, usage and removal effect");
            Check(details.FullText.Contains(explanation.Usage)&&details.Sections[0].Caution&&ReviewText.Preview(small).Contains("KULLANIM BİLGİSİ"),"Full and preview details retain unexamined usage and first risk caution");
            var omitted=Scan([cache],1,4,excluded:[nested]);var noClue=Candidate(omitted,home);
            Check(noClue.Kind==ReviewKind.Cache&&noClue.Evidence!.DataRisks==FileDataRisk.None&&noClue.ExcludedSubtrees==1,"Excluded subtree never contributes observed evidence");
            Check(ReviewText.Explain(noClue).Coverage.Contains("1 yol bilerek")&&ReviewText.Explain(noClue).Unknown.Contains("İpucu görülmemesi"),"No clue in narrowed scope still makes no personal-data absence claim");
            bool changed=false;
            var deleted=Scan([cache],1,4,p=>{if(!changed&&p.Current=="Doğrulama: "+cache){changed=true;foreach(string file in Directory.GetFiles(nested))System.IO.File.Delete(file);}});
            Check(changed&&deleted.RetryCount==1&&Candidate(deleted,home).Evidence!.ClueCount==0&&deleted.ObservedEvidence!.Owners.Count==0,"Deleted risk names do not survive targeted repair");
            bool added=false;
            var addedRisk=Scan([cache],1,4,p=>{if(!added&&p.Current=="Doğrulama: "+cache){added=true;Data(Path.Combine(nested,"late.pst"),1);}});
            Check(added&&addedRisk.RetryCount==1&&addedRisk.Files.Single().Name=="big.bin"&&Candidate(addedRisk,home).Evidence!.ClueCount==1,"New small risk discovered by repair survives K=1");
            string a=Path.Combine(cache,"a"),b=Path.Combine(cache,"b");Data(Path.Combine(a,"moves.pst"),3);Data(Path.Combine(a,"large.bin"),1024);Data(Path.Combine(b,"large.bin"),2048);bool moved=false;
            var cross=Scan([a,b],1,4,p=>{if(!moved&&p.Current==b){moved=true;System.IO.File.Move(Path.Combine(a,"moves.pst"),Path.Combine(b,"moves.pst"));}});
            var crossCandidates=Rules(home).Analyze(cross);var movedCandidate=crossCandidates.Single(c=>c.Path==b);
            Check(moved&&cross.Root.LogicalBytes==3075&&cross.Root.FileCount==3&&movedCandidate.Evidence is{ClueCount:1},"Cross-root move evidence uses final destination once after merge");
            Check(movedCandidate.Evidence!.Examples!.Single().Path==Path.Combine(b,"moves.pst")&&cross.ObservedEvidence!.Owners.Keys.All(owner=>owner.Path!=a),"Stale source owner is absent from observed evidence");
            var synthetic=Synthetic(Path.Combine(home,".gradle","caches"));
            synthetic.FileCandidates=[(synthetic.Root,File("a.pst",true)),(synthetic.Root,File("safe.gguf"))];Seal(synthetic);
            Check(Candidate(synthetic,home).Evidence!.DataRisks==FileDataRisk.Model,"Unverified individual observation is excluded without losing healthy clue");
            synthetic=Synthetic(cache);synthetic.FileCandidates=[(synthetic.Root,File("a.pst")),(synthetic.Root,File("safe.gguf"))];synthetic.UnverifiedPaths.Add(Path.Combine(cache,"a.pst"));Seal(synthetic);
            Check(Candidate(synthetic,home).Evidence!.DataRisks==FileDataRisk.Model,"Late exact-file uncertainty blocks a previously healthy observation");
            foreach(var state in new[]{MeasurementState.Unknown,MeasurementState.Previous})
            {var s=Synthetic(cache);s.Root.Measurement=state;s.FileCandidates=[(s.Root,File("a.pst"))];Seal(s);Check(s.ObservedEvidence!.Owners.Count==0,"Unusable owner suppressed: "+state);}
            synthetic=Synthetic(cache);synthetic.FileCandidates=[(synthetic.Root,File("a.pst"))];synthetic.ScopeChanged=true;Seal(synthetic);
            Check(synthetic.ObservedEvidence!.Owners.Count==0&&Rules(home).Analyze(synthetic).Count==0,"Root scope loss without path detail yields no actionable evidence");
            synthetic=Synthetic(cache);synthetic.FileCandidates=[(synthetic.Root,File("a.pst"))];synthetic.UnverifiedPaths.Add(cache);Seal(synthetic);
            Check(synthetic.ObservedEvidence!.Owners.Count==0,"Unsafe ancestor blocks every contained name");
            synthetic=Synthetic(cache);synthetic.FileCandidates=[(new FolderEntry(cache+"-other",null),File("a.pst"))];Seal(synthetic);
            Check(synthetic.ObservedEvidence!.Owners.Count==0,"Foreign metadata owner outside selected root is rejected");
            foreach(string bad in new[]{@"nested\a.pst",@"..\a.pst","nested/a.pst","a:stream.pst"})
            {var s=Synthetic(cache);s.FileCandidates=[(s.Root,File(bad))];Seal(s);Check(s.ObservedEvidence!.Owners.Count==0,"Non-leaf name cannot escape observation owner: "+bad);}
            synthetic=Synthetic(cache);synthetic.FileCandidates=[(synthetic.Root,File("a.pst"))];var ex=ScanExclusions.Create([cache],[Path.Combine(cache,"a.pst")]);Seal(synthetic,ex);
            Check(synthetic.ObservedEvidence!.Owners.Count==0,"Prepared excluded entry is omitted before name summary");
            synthetic=Synthetic(cache);synthetic.FileCandidates=[(synthetic.Root,File("safe.bin"))];synthetic.Files.Add(new(Path.Combine(cache,"not-observed.pst"),1,1,DateTime.MinValue,false,""));Seal(synthetic);
            Check(Candidate(synthetic,home).Evidence!.DataRisks==FileDataRisk.None,"Authoritative metadata mode never falls back to unrelated retained row");
            synthetic=Synthetic(cache);synthetic.Files.Add(new(Path.Combine(cache,"legacy.pst"),1,1,DateTime.MinValue,false,""));var legacy=Candidate(synthetic,home);
            Check(legacy.Evidence!.ClueSource==FileClueSource.RetainedList&&ReviewText.Explain(legacy).FileClues.Contains("yalnız tutulan"),"Legacy/synthetic result without full observations declares retained-only fallback");
            synthetic=Synthetic(@"C:\Windows\Installer");synthetic.FileCandidates=[(synthetic.Root,File("save.vhdx"))];Seal(synthetic);
            Check(Rules(home).Analyze(synthetic).Single().Kind==ReviewKind.Protected,"Observed type clue never weakens protected system candidate");
            var sdk=Synthetic(@"D:\SDK");sdk.Root.Children.Add(new(@"D:\SDK\platform-tools",sdk.Root));sdk.Root.Children.Add(new(@"D:\SDK\build-tools",sdk.Root));Seal(sdk);
            Check(ReviewText.Explain(Rules(home).Analyze(sdk).Single()).Association.Contains("olası araç ilişkisi"),"Structural SDK recognition stays distinct from known-location association");
            using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();bool threw=false;try{Rules(home).Analyze(first!,cancelled.Token);}catch(OperationCanceledException){threw=true;}Check(threw,"Evidence-to-candidate preparation honors explicit cancellation");}
            using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();var s=VerifiedScan.Scan([cache],null,cancelled.Token,1,4);Check(s.Cancelled&&s.ObservedEvidence!=null&&Rules(home).Analyze(s).Count==0,"Already-cancelled scan returns qualified empty metadata evidence");}
            bool cancelAtValidation=false;using(var cancel=new CancellationTokenSource())
            {
                var s=VerifiedScan.Scan([cache],new Callback(p=>{if(!cancelAtValidation&&p.Current=="Doğrulama: "+cache){cancelAtValidation=true;cancel.Cancel();}}),cancel.Token,1,4);
                var c=Candidate(s,home);Check(cancelAtValidation&&s.Cancelled&&c.Incomplete&&ReviewText.Explain(c).Coverage.Contains("kısmi"),"Cancelled-after-observation candidate keeps partial coverage qualification");
            }
            var safety=Synthetic(cache);safety.ExcludedPaths=[Path.Combine(cache,"omit")];
            Check(!safety.CanUsePath(Path.Combine(cache,"omit","file.pst"))&&safety.CanUsePath(Path.Combine(cache,"healthy","file.pst")),"Empty-uncertainty fast path preserves exclusion boundaries");
            safety.ScopeChanged=true;Check(!safety.CanUsePath(Path.Combine(cache,"healthy","file.pst")),"Empty-uncertainty fast path cannot bypass scope loss");
            safety.ScopeChanged=false;safety.UnverifiedPaths.Add(Path.Combine(cache,"unsafe"));
            Check(!safety.CanUsePath(Path.Combine(cache,"unsafe","file.pst"))&&safety.CanUsePath(Path.Combine(cache,"healthy","file.pst")),"Nonempty uncertainty still blocks ancestors and permits healthy siblings");
            var wide=Synthetic(cache);for(int i=0;i<10000;i++)wide.Root.Children.Add(new(Path.Combine(cache,"owner-"+i.ToString("D5")),wide.Root));
            wide.FileCandidates=wide.Root.Children.Select(owner=>(owner,File("small.pst")));ObservedFileEvidence.Capture([wide]);
            long wideStart=GC.GetAllocatedBytesForCurrentThread();var wideWatch=Stopwatch.StartNew();var wideSummary=ObservedFileEvidence.Capture([wide]);wideWatch.Stop();long wideAllocated=GC.GetAllocatedBytesForCurrentThread()-wideStart;
            Check(wideSummary.Owners.Count==10000&&wideSummary.VisitedNames==10000,"Separate risk owners retain exact sparse folder summaries");
            Check(wideAllocated<10000L*2048,"Many risk owners avoid empty-uncertainty ancestor allocation explosion");
            log.Add($"MEASURE capture-only 10k risk owners: {wideWatch.Elapsed.TotalMilliseconds:F2} ms, {wideAllocated} managed bytes; canonical path validation included");
            foreach(bool positive in new[]{false,true})
            {
                var s=Synthetic(cache);s.FileCandidates=Enumerable.Repeat((s.Root,File(positive?"mail.pst":"file.bin")),500000);
                ObservedFileEvidence.Capture([s]);long start=GC.GetAllocatedBytesForCurrentThread();var timer=Stopwatch.StartNew();var summary=ObservedFileEvidence.Capture([s]);timer.Stop();long allocation=GC.GetAllocatedBytesForCurrentThread()-start;
                Check(summary.VisitedNames==500000&&summary.Owners.Count==(positive?1:0)&&allocation<65536,"500k names retain sparse bounded summary: positive="+positive);
                if(positive)Check(summary.Owners[s.Root].Count(FileDataRisk.Mail)==500000,"All positive names counted with one sample, no per-file retained wrapper");
                log.Add($"MEASURE capture-only 500k positive={positive}: {timer.Elapsed.TotalMilliseconds:F2} ms, {allocation} managed bytes; synthetic, not full app/disk");
            }
            log.Add($"ALL CHECKS PASSED ({log.Count(s=>s.StartsWith("PASS "))})");System.IO.File.WriteAllLines(Path.Combine(output,"results.txt"),log);Console.WriteLine(log[^1]);return 0;
        }
        catch(Exception ex){log.Add("FAIL "+ex);System.IO.File.WriteAllLines(Path.Combine(output,"results.txt"),log);Console.WriteLine(ex);return 1;}
    }
}
