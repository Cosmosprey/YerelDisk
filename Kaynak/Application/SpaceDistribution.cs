namespace YerelDisk.Presentation;

internal enum DistributionGroup { Folder, Remaining, FilteredOut, Direct }
internal sealed record DistributionSegment(string Title, long Bytes, double? Share, string Note,
    DistributionGroup Group, FolderEntry? Folder = null, bool CanEnter = false)
{
    public string SizeLabel => PresentationText.Size(Bytes);
    public string ShareLabel => Share is not { } share ? "Pay bilinmiyor"
        : share > 0 && share < 0.001 ? "< %0,1" : $"%{share * 100:N1}";
    public double BarValue => (Share ?? 0) * 100;
    public bool HasFolder => Folder != null;
    public string ToolTip => (Folder?.Path ?? Title) + "\n" + SizeLabel + " · " + ShareLabel + "\n" + Note;
}

// Presentation only. Bounded top selection over already measured metadata; no I/O.
internal sealed record SpaceDistribution(string Caption, string Notice, IReadOnlyList<DistributionSegment> Segments)
{
    internal const int FolderLimit = 12;
    internal static SpaceDistribution Empty { get; } = new("Bir klasörü taradıktan sonra alan dağılımını açabilirsiniz.", "", []);
    internal static SpaceDistribution Create(ScanResult scan, FolderEntry folder, IReadOnlyList<object> visible,
        bool filtered, CancellationToken token)
    {
        var largest = new List<FolderEntry>(FolderLimit + 1);
        decimal allBytes = 0, knownBytes = 0, unknownBytes = 0, matchingBytes = 0;
        int unknown = 0, zero = 0, matchingPositive = 0, allPositive = 0, index = 0;
        bool invalid = folder.LogicalBytes < 0;
        foreach (var child in folder.Children)
        {
            if ((index++ & 1023) == 0) token.ThrowIfCancellationRequested();
            if (child.LogicalBytes < 0) { invalid = true; continue; }
            allBytes += child.LogicalBytes;
            if (child.Measurement == MeasurementState.Unknown) { unknown++; unknownBytes += child.LogicalBytes; continue; }
            knownBytes += child.LogicalBytes;
            if (child.LogicalBytes == 0) zero++; else allPositive++;
        }
        foreach (var item in visible)
        {
            if ((index++ & 1023) == 0) token.ThrowIfCancellationRequested();
            if (item is not FolderEntry child || child.Measurement == MeasurementState.Unknown || child.LogicalBytes <= 0) continue;
            matchingBytes += child.LogicalBytes; matchingPositive++;
            int position = 0;
            while (position < largest.Count)
            {
                var other = largest[position];
                if (child.LogicalBytes > other.LogicalBytes || (child.LogicalBytes == other.LogicalBytes
                    && StringComparer.OrdinalIgnoreCase.Compare(child.Path, other.Path) < 0)) break;
                position++;
            }
            if (position < FolderLimit) largest.Insert(position, child);
            if (largest.Count > FolderLimit) largest.RemoveAt(FolderLimit);
        }
        invalid |= allBytes > folder.LogicalBytes || matchingBytes > knownBytes;
        bool sharesKnown = !invalid && folder.Measurement != MeasurementState.Unknown && folder.LogicalBytes > 0;
        var segments = new List<DistributionSegment>(FolderLimit + 3);
        double? Share(long bytes) => sharesKnown ? (double)bytes / folder.LogicalBytes : null;
        foreach (var child in largest)
            segments.Add(new(child.Name, child.LogicalBytes, Share(child.LogicalBytes), RowValues.MeasurementOf(child),
                DistributionGroup.Folder, child, scan.CanUsePath(child.Path) && child.Measurement != MeasurementState.Previous));
        void Group(string title, decimal bytes, string note, DistributionGroup kind)
        {
            if (bytes > 0 && bytes <= long.MaxValue)
                segments.Add(new(title, (long)bytes, Share((long)bytes), note, kind));
        }
        Group($"Diğer eşleşen klasörler ({matchingPositive - largest.Count:N0})",
            matchingBytes - largest.Sum(child => (decimal)child.LogicalBytes), "Tek tek incelemek için Tablo görünümünü kullanın.", DistributionGroup.Remaining);
        if (filtered) Group($"Arama dışında kalan klasörler ({allPositive - matchingPositive:N0})", knownBytes - matchingBytes,
            "Bu grup aramaya uymayan klasörlerdir; payların tabanı değişmez.", DistributionGroup.FilteredOut);
        if (!invalid && folder.Measurement != MeasurementState.Unknown)
            Group("Bu klasördeki dosyalar ve kendi ek akışları", folder.LogicalBytes - allBytes,
                "Alt klasörlere ait olmayan taranmış veri; ayrı bir klasör değildir.", DistributionGroup.Direct);
        string caption = folder.Measurement == MeasurementState.Unknown ? "Seçili klasörün toplam boyutu bilinmiyor; paylar gösterilmez."
            : $"{RowValues.Text(folder, "Size")} · {RowValues.MeasurementOf(folder)}. Paylar seçili klasörün taranan mantıksal toplamına göredir.";
        string notice = $"En büyük {FolderLimit} eşleşen alt klasör ayrı gösterilir. {unknown:N0} klasörün boyutu bilinmiyor; {zero:N0} klasör 0 bayt ölçüldü. Bunlara çubuk çizilmez. "
            + (unknownBytes > 0 ? "Bilinmeyen klasörlerden kalan kısmi gözlemler toplamda bulunabilir; bunlar doğrudan dosyalara atanmaz ve payları çizilmez. " : "")
            + (filtered ? "Arama yalnız eşleşen klasörleri ayırır; arama dışı toplam ayrı gruptadır. " : "")
            + (folder.Measurement != MeasurementState.Measured || scan.Cancelled || scan.Failed || scan.ScopeChanged || scan.NeedsRetry
                ? "Kısmi / son gözlemler içerir; güncellik ve eksiksizlik doğrulanmış sayılmaz. " : "")
            + (folder.ExcludedSubtrees > 0 ? $"{folder.ExcludedSubtrees:N0} yol hariç tutuldu; toplamlar ve paylar bunları içermez. " : "")
            + (invalid ? "Toplamlar arasında tutarsızlık var; paylar ve doğrudan veri farkı gösterilmez. " : "")
            + "Silerek kazanılacak alanı göstermez. CSV, filtrelenmiş klasör tablosunun tüm satırlarını kaydeder; çubuk grupları CSV satırı değildir.";
        if (segments.Count == 0) notice = "Çubukla gösterilebilen pozitif boyut yok. Bu, bilinmeyen veya hariç tutulan alanların boş olduğunu kanıtlamaz. " + notice;
        token.ThrowIfCancellationRequested();
        return new(caption, notice, segments);
    }
}
