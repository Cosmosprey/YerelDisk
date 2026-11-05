using System.Globalization;
using YerelDisk;
using YerelDisk.Presentation;

internal static class ReviewTests
{
    const string Home = @"C:\Review-Test\User", Local = Home + @"\AppData\Local", Data = @"C:\ProgramData", Windows = @"C:\Windows";
    static readonly ReviewRules Rules = new(Home, Local, Data, Windows);
    static readonly List<string> log = [];
    static void Check(bool condition, string name) { if (!condition) throw new Exception(name); log.Add("PASS " + name); }
    static ScanResult Scan(string path) => new(new FolderEntry(path, null) { LogicalBytes = 1000, AllocatedBytes = 4096, FileCount = 10 }) { Folders = 1 };
    static FolderEntry Child(FolderEntry parent, string name, MeasurementState measurement = MeasurementState.Measured)
    { var child = new FolderEntry(Path.Combine(parent.Path, name), parent) { Measurement = measurement }; parent.Children.Add(child); return child; }
    static ReviewCandidate One(ScanResult scan) => Rules.Analyze(scan).Single();
    static void File(ScanResult scan, string name, bool unverified = false)
        => scan.Files.Add(new(Path.Combine(scan.Root.Path, name), 100, 128, DateTime.MinValue, false, "", Unverified: unverified));
    static int Main(string[] args)
    {
        Directory.CreateDirectory(args[0]); CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        try
        {
            (string Path, string Id, ReviewKind Kind)[] cases = [
                (Home+@"\.gradle\caches","GRADLE-CACHES",ReviewKind.Cache), (Home+@"\.gradle\wrapper\dists","GRADLE-DISTS",ReviewKind.Cache),
                (Local+@"\npm-cache\_cacache","NPM-CACHE",ReviewKind.Cache), (Home+@"\.nuget\packages","NUGET-PACKAGES",ReviewKind.Conditional),
                (Local+@"\NuGet\v3-cache","NUGET-HTTP",ReviewKind.Cache), (Local+@"\NuGet\plugins-cache","NUGET-PLUGINS",ReviewKind.Cache),
                (Home+@"\.cargo\registry\cache","CARGO-ARCHIVES",ReviewKind.Cache), (Home+@"\.cargo\registry\src","CARGO-SOURCES",ReviewKind.Conditional),
                (Home+@"\.cache\huggingface\hub","HF-HUB",ReviewKind.Conditional), (Home+@"\.ollama\models","OLLAMA-MODELS",ReviewKind.Conditional),
                (Home+@"\.android\avd","ANDROID-AVD",ReviewKind.Conditional), (@"C:\Windows.old","WINDOWS-OLD",ReviewKind.Protected),
                (Local+@"\pip\Cache","PIP-CACHE",ReviewKind.Cache), (Local+@"\CrashDumps","CRASH-DUMPS",ReviewKind.Conditional),
                (Local+@"\NVIDIA\DXCache","NVIDIA-DX",ReviewKind.Cache), (Local+@"\NVIDIA\GLCache","NVIDIA-GL",ReviewKind.Cache),
                (Local+@"\Temp","USER-TEMP",ReviewKind.Temporary), (Windows+@"\Temp","WINDOWS-TEMP",ReviewKind.Temporary),
                (Data+@"\Package Cache","PACKAGE-CACHE",ReviewKind.Protected), (Windows+@"\Installer","WINDOWS-INSTALLER",ReviewKind.Protected),
                (Windows+@"\WinSxS","WINDOWS-COMPONENTS",ReviewKind.Protected), (Data+@"\Naughty Dog","GAME-Naughty Dog",ReviewKind.Conditional),
                (Data+@"\Hogwarts Legacy","GAME-Hogwarts Legacy",ReviewKind.Conditional),
                (Local+@"\Google\Chrome\User Data\Default\Cache","BROWSER-CACHE",ReviewKind.Cache)
            ];
            foreach (var item in cases)
            {
                var candidate = One(Scan(item.Path)); var explanation = ReviewText.Explain(candidate);
                Check(candidate.RuleId == item.Id && candidate.Kind == item.Kind && candidate.Evidence is { Match: ReviewMatchKind.KnownLocation }
                    && candidate.Evidence.MatchedPath.Equals(item.Path, StringComparison.OrdinalIgnoreCase), "Known location/type/evidence: " + item.Id);
                Check(!explanation.Purpose.Contains("Bu kural için") && explanation.Consequence.Length > 15 && explanation.Unknown.Contains("liste sınırı"), "Explicit explanation and uncertainty: " + item.Id);
                Check(Rules.Analyze(Scan(item.Path + "-backup")).Count == 0, "Sibling-name false positive rejected: " + item.Id);
            }
            var nested = One(Scan(Home+@"\.gradle\caches\modules\subset"));
            Check(nested.Evidence?.MatchedPath == Home+@"\.gradle\caches" && ReviewText.Explain(nested).Evidence.Contains("Yalnız bu alt klasör"), "Selected subset is not described as the whole cache");
            var relocated = new ReviewRules(Home,Local,Local+@"\Temp",Windows);
            Check(relocated.Analyze(Scan(Local+@"\Temp\Package Cache")).Single().Kind==ReviewKind.Protected, "More specific protected location wins over a broad temporary location");
            foreach (string browser in new[] { "Google\\Chrome", "Microsoft\\Edge" })
                foreach (string cache in new[] { "Cache", "Code Cache", "GPUCache" })
                    Check(One(Scan(Local+"\\"+browser+"\\User Data\\Profile 12\\"+cache)).RuleId == "BROWSER-CACHE", "Supported browser/profile subcache: " + browser + "/" + cache);
            foreach (string profile in new[] { "Profile ", "Profile 12x", "Profile x", "Default-old" })
                Check(Rules.Analyze(Scan(Local+"\\Google\\Chrome\\User Data\\"+profile+"\\Cache")).Count == 0, "Invalid browser profile rejected: " + profile);
            foreach (string name in new[] { "Cookies", "Local Storage", "Login Data", "CacheStorage" })
                Check(Rules.Analyze(Scan(Local+"\\Google\\Chrome\\User Data\\Default\\"+name)).Count == 0, "Non-cache browser data not targeted: " + name);
            foreach (string path in new[] { @"C:\Backup\Windows\WinSxS", @"C:\ProgramData\Package Cache-old", @"C:\Temp", Home+@"\.gradle", Home+@"\.cargo\bin" })
                Check(Rules.Analyze(Scan(path)).Count == 0, "Unrecognized broad/lookalike folder is not a candidate: " + path);

            var sdk = Scan(@"D:\Android_SDK"); Child(sdk.Root,"platform-tools"); Child(sdk.Root,"build-tools");
            var sdkCandidate = One(sdk); var sdkExplanation = ReviewText.Explain(sdkCandidate);
            Check(sdkCandidate.RuleId == "ANDROID-SDK-STRUCTURE" && sdkCandidate.Evidence?.Match == ReviewMatchKind.FolderStructure
                && sdkCandidate.Reason.Contains("build-tools, platform-tools") && sdkExplanation.Unknown.Contains("gerçek bir SDK"), "SDK states actual observed markers and structural uncertainty");
            foreach (MeasurementState state in new[] { MeasurementState.Unknown, MeasurementState.Previous })
            {
                var unobserved = Scan(@"D:\Android_SDK"); Child(unobserved.Root,"platform-tools",state); Child(unobserved.Root,"build-tools",state);
                Check(Rules.Analyze(unobserved).Count == 0, "Unobserved SDK markers are not evidence: " + state);
            }
            var blocked = Scan(@"D:\SDK"); var blockedMarker=Child(blocked.Root,"platform-tools"); Child(blocked.Root,"build-tools"); blocked.UnverifiedPaths.Add(blockedMarker.Path);
            Check(Rules.Analyze(blocked).Count == 0, "Unsafe SDK marker is ignored");
            var project = Scan(@"D:\SDK"); Child(project.Root,"platform-tools"); Child(project.Root,"build-tools"); Child(project.Root,".git",MeasurementState.Unknown);
            Check(Rules.Analyze(project).Count == 0, "Even an unknown VCS marker protects a project lookalike");
            var indirect = Scan(@"D:\SDK"); var wrapper=Child(indirect.Root,"wrapper"); Child(wrapper,"platform-tools"); Child(wrapper,"build-tools");
            Check(Rules.Analyze(indirect).Count == 0, "Nested SDK markers do not count as direct evidence");
            sdk.Root.Measurement=MeasurementState.Partial; sdk.Root.IssueCount=1; Child(sdk.Root,"other",MeasurementState.Unknown);
            Check(One(sdk).Incomplete && One(sdk).RuleId=="ANDROID-SDK-STRUCTURE", "Healthy SDK markers survive an unrelated unreadable child with a partial warning");

            (string Extension, FileDataRisk Risk)[] types = [(".vhd",FileDataRisk.VirtualDisk),(".VHDX",FileDataRisk.VirtualDisk),(".vmdk",FileDataRisk.VirtualDisk),(".qcow2",FileDataRisk.VirtualDisk),
                (".pst",FileDataRisk.Mail),(".ost",FileDataRisk.Mail),(".gguf",FileDataRisk.Model),(".safetensors",FileDataRisk.Model),(".onnx",FileDataRisk.Model),(".dmp",FileDataRisk.Diagnostics)];
            foreach (var type in types)
            {
                var scan=Scan(Home+@"\.gradle\caches"); File(scan,"work"+type.Extension); var c=One(scan); var e=ReviewText.Explain(c);
                Check(c.Kind==ReviewKind.Conditional && c.Evidence?.DataRisks==type.Risk && c.Category=="Önbellek adayı"
                    && e.Consequence.Contains("Dosya adı gerçek içeriği kanıtlamaz") && c.Advice.StartsWith("Önce ek dosya adı"), "Type risk survives cache context: " + type.Extension);
                Check(FileHints.Classify(scan.Files[0].Path).DataRisk==FileHints.RiskFor(scan.Files[0].Path), "File description and candidate evidence share one type definition: "+type.Extension);
            }
            var temp=Scan(Local+@"\Temp"); File(temp,"personal.pst");
            Check(One(temp).Kind==ReviewKind.Conditional, "Temporary context does not suppress mail data caution");
            var protectedScan=Scan(Windows+@"\Installer"); File(protectedScan,"backup.vhdx");
            Check(One(protectedScan) is {Kind:ReviewKind.Protected,Evidence.DataRisks:FileDataRisk.VirtualDisk}, "Protected system location remains protected with added file clue");
            var many=Scan(Home+@"\.gradle\caches");
            foreach(string ext in new[]{".vhdx",".pst",".gguf",".dmp"})for(int i=0;i<25;i++)File(many,"work-"+i+ext);
            var rich=One(many); var richText=ReviewText.Explain(rich);
            Check(rich.Evidence is { ClueCount:100,Examples.Count:4 } && richText.FileClues.Contains("100 dosya adı"), "Examples are bounded to one per risk type while all positive names are counted");
            Check(rich.Evidence!.Examples!.All(e=>e.Path.StartsWith(rich.Path+"\\",StringComparison.Ordinal)) && !richText.FileClues.Contains(rich.Path+"\\"), "Examples retain exact paths in the model while displaying relative names");
            Check(richText.Unknown.Contains("İpucu görülmemesi") && richText.FileClues.Contains("yalnız tutulan"), "Clue coverage is limited explicitly to retained names");
            var details=SelectionDetails.Create(many,rich,true,"","scope");
            Check(details.Sections[0].Caution && details.Sections[0].Heading=="Ek dosya adı ipuçları" && details.FullText.Contains("work-0.pst"), "Side pane and full details both retain concrete risk clues");
            var fields=ReviewText.CsvFields(rich,"Tutacağım");
            Check(fields.Length==ReviewText.CsvHeader.Length && fields.Length==19 && fields[1].Contains("Kullanımına bağlı") && fields[15].Contains("work-0.pst") && fields[16].Contains("amaç ilişkisi") && fields[17].Contains("İncelenmedi") && fields[14]=="Tutacağım", "CSV preserves decision type, clues and existing feedback column");
            var q=new ViewQuery(WorkspaceView.Review,null,"",false,0,1,"Size",true);
            Check(ResultQuery.Execute(many,[rich],q,CancellationToken.None).Rows.Count==0 && ResultQuery.Execute(many,[rich],q with {ReviewKind=2},CancellationToken.None).Rows.Count==1, "Review filters use the evidence-adjusted kind");
            var none=Scan(Home+@"\.gradle\caches");
            Check(One(none).Kind==ReviewKind.Cache && ReviewText.Explain(One(none)).Unknown.Contains("İpucu görülmemesi"), "No retained clue does not claim absence of personal data");
            var stale=Scan(Home+@"\.gradle\caches"); File(stale,"old.pst",true);
            Check(One(stale).Evidence?.DataRisks==FileDataRisk.None, "Stale retained file is not promoted into evidence");
            var unsafeFile=Scan(Home+@"\.gradle\caches"); File(unsafeFile,"old.pst"); unsafeFile.UnverifiedPaths.Add(unsafeFile.Files[0].Path);
            Check(One(unsafeFile).Evidence?.DataRisks==FileDataRisk.None, "Unsafe retained path is not evidence");
            foreach(string bad in new[]{Home+@"\.gradle\caches-old\x.pst", Home+@"\.gradle\caches\..\x.pst", @"\\server\share\x.pst"})
            { var scan=Scan(Home+@"\.gradle\caches"); scan.Files.Add(new(bad,100,128,DateTime.MinValue,false,"")); Check(One(scan).Evidence?.DataRisks==FileDataRisk.None,"Outside/noncanonical clue rejected: "+bad); }
            var parent=Scan(Local+@"\Temp"); var sdkChild=Child(parent.Root,"SDK"); Child(sdkChild,"platform-tools"); Child(sdkChild,"build-tools");
            Check(Rules.Analyze(parent).Count==1 && Rules.Analyze(parent)[0].RuleId=="USER-TEMP", "Recognized parent subtree is not counted again as a child candidate");
            var outside=Scan(@"C:\allowed"); outside.Root.Children.Add(new(Home+@"\.gradle\caches",outside.Root){LogicalBytes=100});
            Check(Rules.Analyze(outside).Count==0, "Candidate metadata outside selected roots is rejected");
            var unknownRule=rich with {RuleId="UNKNOWN"};
            Check(ReviewText.Explain(unknownRule).Purpose.Contains("doğrulanmış amaç") && ReviewText.Explain(unknownRule).Consequence.Contains("kararı vermeyin"), "Unknown rule keeps conservative fallback");
            Check(ReviewText.Explain(rich with {Evidence=new(ReviewMatchKind.Unspecified,rich.Path)}).Evidence.Contains("yöntemi belirtilmedi"), "Unspecified evidence is not presented as a known-location match");
            using var token=new CancellationTokenSource();token.Cancel();bool cancelled=false;try{Rules.Analyze(many,token.Token);}catch(OperationCanceledException){cancelled=true;}
            Check(cancelled,"Classification honors explicit cancellation");
            log.Add($"ALL CHECKS PASSED ({log.Count})"); Console.WriteLine(log[^1]);return 0;
        }
        catch(Exception ex){log.Add("FAIL "+ex);Console.WriteLine(ex);return 1;}
        finally{System.IO.File.WriteAllLines(Path.Combine(args[0],"results.txt"),log);}
    }
}
