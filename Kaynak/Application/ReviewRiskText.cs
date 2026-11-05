namespace YerelDisk;

// The finite set translated by this presentation version. New enum bits must
// remain visible until a specific explanation is deliberately added.
internal static class ReviewRiskText
{
    internal const FileDataRisk Known = FileDataRisk.VirtualDisk | FileDataRisk.Mail | FileDataRisk.Model | FileDataRisk.Diagnostics;
    internal static bool HasUnknown(FileDataRisk risks) => (risks & ~Known) != FileDataRisk.None;
}
