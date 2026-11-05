using YerelDisk;
using YerelDisk.Presentation;

internal static class GuidanceTests
{
    static readonly List<string> log = [];
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); log.Add("PASS " + label); }
    static ReviewCandidate Example(string rule = "GRADLE-CACHES", ReviewKind kind = ReviewKind.Cache,
        FileDataRisk risk = FileDataRisk.None, bool partial = false, int excluded = 0)
    {
        const string path = @"C:\Guidance-Fixture\cache";
        return new(path, 100, 4096, 2, "fixture", "known location", rule, "name clue", "ORIGINAL-ADVICE", partial, kind,
            new(ReviewMatchKind.KnownLocation, path, risk, risk == FileDataRisk.None ? 0 : 1,
                risk == FileDataRisk.None ? [] : [new(FileDataRisk.Mail, path + @"\work.pst")], FileClueSource.FinalScanObservations), excluded);
    }
    static int Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        try
        {
            var gradle = Example();
            Check(ReviewGuidance.FirstStep(gradle).Contains("Projelerini") && ReviewGuidance.Details(gradle).Contains("Çevrimdışı"), "Gradle review asks about projects and offline needs, not just a removed IDE");
            Check(ReviewGuidance.Details(gradle).Contains("başka projelerin"), "Removed Android Studio is not proof that Gradle is unused");
            Check(ReviewGuidance.Details(Example("NUGET-PACKAGES")).Contains("özel paket kaynaklarına"), "Shared NuGet guidance includes private source access");
            Check(ReviewGuidance.Details(Example("CARGO-SOURCES")).Contains("yerel değişikliklerin"), "Cargo local source edits remain a separate check");
            Check(ReviewGuidance.Details(Example("ANDROID-SDK-STRUCTURE", ReviewKind.Conditional)).Contains("Flutter, Unity, ADB"), "SDK guidance considers consumers other than Android Studio");
            Check(ReviewGuidance.Details(Example("ANDROID-AVD", ReviewKind.Conditional)).Contains("bu çalışmaları geri getirmez"), "AVD recreation never promises recovery of test data");
            Check(ReviewGuidance.Details(Example("GAME-Hogwarts Legacy", ReviewKind.Conditional)).Contains("ayrı kopyasını"), "Uninstalled game still requires checking saves and settings");
            Check(ReviewGuidance.Details(Example("HF-HUB", ReviewKind.Conditional)).Contains("erişilemeyen"), "Model guidance preserves unavailable and custom data concern");
            Check(ReviewGuidance.Details(Example("USER-TEMP", ReviewKind.Temporary)).Contains("kurtarma dosyalarını"), "Temp guidance considers open work and recovery data");
            Check(ReviewGuidance.Details(Example("CRASH-DUMPS", ReviewKind.Temporary)).Contains("destek talebi"), "Dump guidance checks ongoing support investigations");
            Check(ReviewGuidance.Details(Example("BROWSER-CACHE")).Contains("yalnız önbellek") && ReviewGuidance.Details(Example("BROWSER-CACHE")).Contains("parola"), "Browser guidance distinguishes cache from profile and credentials");
            Check(ReviewGuidance.Details(Example("NVIDIA-DX")).Contains("tüm NVIDIA"), "Graphics cache guidance does not generalize to the entire vendor folder");
            foreach (var rule in new[] { "PACKAGE-CACHE", "WINDOWS-INSTALLER", "WINDOWS-COMPONENTS", "WINDOWS-OLD" })
            {
                var c = Example(rule, ReviewKind.Protected, FileDataRisk.Mail, true, 1);
                Check(ReviewGuidance.FirstStep(c).StartsWith("Elle silme;") && ReviewGuidance.Details(c).Contains("Elle silme."), "Protected warning wins over partial/risk guidance: " + rule);
            }
            var mixed = Example(risk: FileDataRisk.VirtualDisk | FileDataRisk.Mail | FileDataRisk.Model | FileDataRisk.Diagnostics);
            string mixedText = ReviewGuidance.Details(mixed);
            Check(mixedText.Contains("Sanal diskin") && mixedText.Contains("Posta dosyasının") && mixedText.Contains("Modelin") && mixedText.Contains("Dökümün"), "Combined type clues retain four independent checks");
            Check(mixedText.Contains("Ad / uzantı gerçek içeriği doğrulamaz") && ReviewGuidance.FirstStep(mixed).Contains("işaretli dosyaları"), "Risk instructions treat names as clues rather than verified content");
            var partial = mixed with { Incomplete = true, ExcludedSubtrees = 1 };
            Check(ReviewGuidance.FirstStep(partial).Contains("ölçüm eksiklerini") && ReviewGuidance.Details(partial).Contains("Hariç tutulan"), "Partial coverage is checked before a removal decision");
            Check(ReviewGuidance.Details(partial).Contains("Sanal diskin") && ReviewGuidance.Details(partial).Contains("Posta dosyasının"), "Coverage warning never suppresses type risk");
            Check(ReviewGuidance.FirstStep(gradle with { ExcludedSubtrees = 1 }).Contains("ölçüm eksiklerini"), "Explicit exclusions remain material even without incomplete flag");
            var unknown = Example("UNKNOWN", ReviewKind.Conditional) with { Evidence = null };
            Check(ReviewGuidance.FirstStep(unknown).Contains("amacını doğrula") && ReviewGuidance.Details(unknown).Contains("kaldırma kararı verme"), "Unknown rule and missing evidence give no invented cleaning recommendation");
            Check(ReviewGuidance.Details(gradle with { Evidence = null }).Contains("dayanak eksik"), "Known rule ID alone is not verified association evidence");
            var missing = gradle with { Evidence = gradle.Evidence! with { Match = ReviewMatchKind.Unspecified } };
            Check(ReviewGuidance.FirstStep(missing).Contains("amacını doğrula") && ReviewTableText.Signal(missing).Contains("Dayanak belirsiz"), "Unspecified association remains explicit in the compact row");
            Check(ReviewTableText.Signal(unknown).Contains("Kullanım bilinmiyor") && ReviewTableText.Attention(unknown), "Missing usage and association remain visible warnings after deduplication");
            var scan = new ScanResult(new FolderEntry(gradle.Path, null));
            var selected = SelectionDetails.Create(scan, gradle, true, "Tutacağım", "fixture scope");
            Check(selected.Sections[0].Caution && selected.Sections[1].Heading == "İlk kontrol adımı", "Action appears directly after the full consequence warning");
            Check(selected.Sections.Single(s => s.Heading == "İlk kontrol adımı").Text == ReviewGuidance.Details(gradle), "Selected pane uses the shared full check instructions");
            var fields = ReviewText.CsvFields(gradle, "Tutacağım");
            Check(fields.Length == 19 && fields[11].Contains(ReviewGuidance.Details(gradle)) && fields[11].Contains("ORIGINAL-ADVICE") && fields[14] == "Tutacağım", "CSV preserves field layout, original advice, new instructions, and user note");
            Check(ReviewText.Preview(gradle).Contains(ReviewGuidance.Details(gradle)) && selected.FullText.Contains(ReviewGuidance.Details(gradle)), "Preview, F6, and export agree on the check instructions");
            Check(RowValues.Text(mixed, "ReviewStep").Contains(ReviewGuidance.FirstStep(mixed)) && ReviewTableText.Tooltip(mixed).Contains(ReviewGuidance.FirstStep(mixed)), "Table and tooltip share the same first step");
            Check(ReviewText.Usage(gradle).StartsWith("İncelenmedi") && gradle.Kind == ReviewKind.Cache && mixed.Evidence!.DataRisks != FileDataRisk.None, "Presentation guidance leaves usage and classification unchanged");
            log.Add($"ALL CHECKS PASSED ({log.Count})"); File.WriteAllLines(Path.Combine(output, "results.txt"), log); Console.WriteLine(log[^1]); return 0;
        }
        catch (Exception ex) { log.Add("FAIL " + ex); File.WriteAllLines(Path.Combine(output, "results.txt"), log); Console.WriteLine(ex); return 1; }
    }
}
