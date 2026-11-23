using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using YerelDisk;

string output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(output);
string compact = Path.Combine(output, "baseline.yereldisk");
string json = Path.Combine(output, "baseline.yereldisk.json");
string invalid = Path.Combine(output, "invalid.yereldisk");
var log = new List<string>();
const string root = @"C:\Örnek";
const string other = @"D:\Diğer";
const string child = root + @"\日本語";
var stamp = new DateTimeOffset(2026, 11, 5, 23, 4, 5, TimeSpan.FromHours(3));
Guid id = new("9984a110-f691-43ee-93b7-211f9e45e82e");

if (args.Length == 2 && args[1] is "benchmark-json" or "benchmark-compact")
    return Benchmark(args[1] == "benchmark-compact");

try
{
    var data = new FolderMeasurements(2, stamp, [root, other], [other + @"\Hariç"],
        [new(root, 256, true), new(child, 256, true), new(other, 0, false)], [new(root, id)]);
    data.Write(compact, compressed: true);
    byte[] saved = File.ReadAllBytes(compact);
    var restored = FolderMeasurements.Read(compact);
    Check(restored.MeasuredAt == stamp && restored.Roots.SequenceEqual(data.Roots)
        && restored.Exclusions.SequenceEqual(data.Exclusions), "Compact reference round-trips time, roots and exclusions");
    Check(restored.Folders.SequenceEqual(data.Folders) && restored.RootVolumes!.SequenceEqual(data.RootVolumes!),
        "Unicode paths, size, uncertainty and volume identity round-trip");
    Check(new FolderComparison(restored, data).Largest.Count == 0, "Compact reference feeds the existing comparison");
    data.Write(json);
    Check(new FolderComparison(FolderMeasurements.Read(json), restored).Largest.Count == 0,
        "1.19.1 JSON remains compatible");
    (data with { Format = 1, RootVolumes = null }).Write(json);
    Check(new FolderComparison(FolderMeasurements.Read(json), restored).HasUnverifiedVolumeIdentity,
        "1.19.0 JSON remains compatible with its existing volume warning");
    File.WriteAllBytes(json, saved);
    Check(FolderMeasurements.Read(json).Folders.SequenceEqual(data.Folders), "Reader detects the container by content");

    byte[] damaged = (byte[])saved.Clone();
    damaged[^1] ^= 1;
    RejectBytes(damaged, "Altered compressed payload fails integrity");
    RejectBytes(saved[..^1], "Truncated compact file fails integrity");
    RejectBytes([.. saved, 0], "Appended bytes fail integrity");
    damaged = (byte[])saved.Clone();
    damaged[7] = (byte)'9';
    RejectBytes(damaged, "Unsupported container version is rejected");
    damaged = (byte[])saved.Clone();
    damaged[^8] ^= 1;
    SHA256.HashData(damaged.AsSpan(40), damaged.AsSpan(8, 32));
    RejectMessage(damaged, "bozuk veya eksik", "Gzip CRC damage is rejected even after rehashing the container");
    // Claude 1.20.1: GZipStream damage reaches the user as the container's Turkish message.
    damaged = (byte[])saved.Clone();
    damaged[42] = 7;
    SHA256.HashData(damaged.AsSpan(40), damaged.AsSpan(8, 32));
    RejectMessage(damaged, "bozuk veya eksik", "Invalid gzip method behind a valid checksum uses the Turkish message");
    byte[] additional = Envelope(writer => writer.Write((byte)0));
    RejectBytes(Seal([.. saved.AsSpan(40), .. additional.AsSpan(40)]), "Concatenated members cannot append expanded data");

    byte[] raw = Expand(saved);
    RejectBytes(Envelope(writer => writer.Write(0L)), "Invalid timestamp is rejected even with a valid checksum");
    RejectBytes(Envelope(writer =>
    {
        writer.Write(stamp.UtcTicks);
        writer.Write7BitEncodedInt(FolderMeasurements.MaxScopePaths + 1);
    }), "Scope count checked before allocation");
    RejectBytes(Envelope(writer =>
    {
        WritePrefix(writer);
        writer.Write7BitEncodedInt(FolderMeasurements.MaxFolders + 1);
    }), "Folder count checked before allocation");
    RejectBytes(Envelope(writer =>
    {
        WritePrefix(writer);
        writer.Write7BitEncodedInt(1);
        writer.Write7BitEncodedInt(32768);
    }), "Path length checked before reading its bytes");
    RejectBytes(Envelope(writer =>
    {
        WritePrefix(writer);
        writer.Write7BitEncodedInt(1);
        WritePath(writer, root);
        writer.Write(0L);
        writer.Write((byte)2);
    }), "Invalid measurement state rejected");
    RejectBytes(Envelope(writer => writer.Write(raw[..^1])), "Truncated record rejected even with a valid checksum");
    RejectBytes(Envelope(writer => { writer.Write(raw); writer.Write((byte)0); }), "Trailing expanded payload rejected");

    var larger = data with { Roots = [root], Exclusions = [], RootVolumes = [new(root, id)],
        Folders = [new(root, 500, true), .. Enumerable.Range(0, 500).Select(i =>
            new FolderMeasurement($@"{root}\a-repeated-folder-prefix-{i:D5}", 1, true))] };
    larger.Write(compact, compressed: true);
    byte[] large = File.ReadAllBytes(compact);
    const int lowCap = 8192;
    Check(large.Length < lowCap && Expand(large).Length > lowCap, "Expansion fixture is smaller on disk than the read limit");
    Reject(() => FolderMeasurements.Read(compact, lowCap, lowCap), "Declared expanded size is bounded");
    Reject(() => FolderMeasurements.Read(compact, large.Length - 1), "Stored file size is bounded before reading");
    int expandedLength = Expand(large).Length;
    Check(FolderMeasurements.Read(compact, expandedLength, expandedLength).Folders.Length == larger.Folders.Length,
        "Payload ending exactly at expanded limit is accepted");
    Reject(() => FolderMeasurements.Read(compact, expandedLength, expandedLength - 1), "Payload one byte over the expanded limit is rejected");
    Check(FolderMeasurements.ExpandedLimit(lowCap) == 2 * lowCap && FolderMeasurements.ExpandedLimit(int.MaxValue) == int.MaxValue,
        "Default expanded budget is twice the stored budget");
    damaged = (byte[])large.Clone();
    BinaryPrimitives.WriteUInt32LittleEndian(damaged.AsSpan(damaged.Length - 4), 1);
    SHA256.HashData(damaged.AsSpan(40), damaged.AsSpan(8, 32));
    File.WriteAllBytes(invalid, damaged);
    Reject(() => FolderMeasurements.Read(invalid, lowCap, lowCap), "Understated trailer cannot bypass streaming expansion limit");
    Reject(() => larger.Write(compact, lowCap, compressed: true, maximumExpandedBytes: lowCap), "Expanded limit is applied during compressed write");
    Check(File.ReadAllBytes(compact).SequenceEqual(large) && !Directory.EnumerateFiles(output, "*.tmp").Any(),
        "Limited write preserves the old reference and removes temporary output");
    var small = data with { Roots = [root], Exclusions = [], RootVolumes = [], Folders = [new(root, 0, true)] };
    small.Write(invalid, compressed: true);
    byte[] tiny = File.ReadAllBytes(invalid);
    int storedCap = tiny.Length - 1;
    Check(Expand(tiny).Length < storedCap, "Stored-size fixture fits the expanded budget");
    Reject(() => small.Write(compact, storedCap, compressed: true), "Stored limit includes the header and gzip footer");
    Check(File.ReadAllBytes(compact).SequenceEqual(large) && !Directory.EnumerateFiles(output, "*.tmp").Any(),
        "Stored limit also preserves the old reference");
    using (var pin = new FileStream(compact, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        try { data.Write(compact, compressed: true); throw new InvalidOperationException("Locked compact reference was replaced"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Check(File.ReadAllBytes(compact).SequenceEqual(large) && !Directory.EnumerateFiles(output, "*.tmp").Any(),
                "Locked destination preserves the compressed reference");
        }
    }
    data.Write(compact, compressed: true);
    Check(FolderMeasurements.Read(compact).Folders.SequenceEqual(data.Folders), "Explicit overwrite replaces a compact reference");
    Check((File.GetAttributes(compact) & FileAttributes.Hidden) == 0, "Finished reference is not left hidden");

    // Claude 1.20.1: UTF-16 paths decode to about twice the UTF-8 JSON size. Long paths that
    // fit the JSON budget must also fit the compact reader and writer under the same stored limit.
    const int pathCap = 64 * 1024;
    const string longRoot = @"C:\Uzun";
    var longPaths = data with { Roots = [longRoot], Exclusions = [], RootVolumes = [new(longRoot, id)],
        Folders = [new(longRoot, 300, true), .. Enumerable.Range(0, 300).Select(i =>
            new FolderMeasurement($@"{longRoot}\{i:D4}-" + new string('k', 120 - longRoot.Length - 6), 1, true))] };
    longPaths.Write(json, pathCap);
    longPaths.Write(compact, pathCap, compressed: true);
    byte[] longFile = File.ReadAllBytes(compact);
    Check(new FileInfo(json).Length < pathCap && Expand(longFile).Length > pathCap,
        "Long-path fixture fits JSON but decodes past the stored budget");
    Check(FolderMeasurements.Read(compact, pathCap).Folders.SequenceEqual(longPaths.Folders),
        "Compact budget is not smaller than the JSON budget for long paths");

    log.Add($"ALL CHECKS PASSED ({log.Count})");
    return 0;
}
catch (Exception ex)
{
    log.Add("FAIL " + ex);
    return 1;
}
finally
{
    File.WriteAllLines(Path.Combine(output, "results.txt"), log);
    foreach (string line in log) Console.WriteLine(line);
}

void Check(bool passed, string label)
{
    if (!passed) throw new InvalidOperationException(label);
    log.Add("PASS " + label);
}

void Reject(Action action, string label)
{
    try { action(); }
    catch (InvalidDataException) { Check(true, label); return; }
    throw new InvalidOperationException(label);
}

void RejectBytes(byte[] bytes, string label)
{
    File.WriteAllBytes(invalid, bytes);
    Reject(() => FolderMeasurements.Read(invalid), label);
}

void RejectMessage(byte[] bytes, string text, string label)
{
    File.WriteAllBytes(invalid, bytes);
    try { FolderMeasurements.Read(invalid); }
    catch (InvalidDataException ex) when (ex.Message.Contains(text)) { Check(true, label); return; }
    throw new InvalidOperationException(label);
}

void WritePrefix(BinaryWriter writer)
{
    writer.Write(stamp.UtcTicks);
    writer.Write7BitEncodedInt(1);
    WritePath(writer, root);
    writer.Write7BitEncodedInt(0);
    writer.Write7BitEncodedInt(0);
}

static void WritePath(BinaryWriter writer, string path)
{
    writer.Write7BitEncodedInt(path.Length);
    writer.Write(MemoryMarshal.AsBytes(path.AsSpan()));
}

static byte[] Envelope(Action<BinaryWriter> write)
{
    using var bytes = new MemoryStream();
    using (var gzip = new GZipStream(bytes, CompressionLevel.Fastest, leaveOpen: true))
    using (var writer = new BinaryWriter(gzip))
        write(writer);
    return Seal(bytes.ToArray());
}

static byte[] Seal(byte[] gzipBytes) => [.. "YDKREF01"u8, .. SHA256.HashData(gzipBytes), .. gzipBytes];

static byte[] Expand(byte[] bytes)
{
    using var source = new MemoryStream(bytes, 40, bytes.Length - 40);
    using var gzip = new GZipStream(source, CompressionMode.Decompress);
    using var destination = new MemoryStream();
    gzip.CopyTo(destination);
    return destination.ToArray();
}

int Benchmark(bool compressed)
{
    const int count = 700_000;
    const string drive = @"C:\";
    var folders = new FolderMeasurement[count + 1];
    folders[0] = new(drive, count * (count - 1L) / 2, true);
    for (int i = 0; i < count; i++)
        folders[i + 1] = new(drive + $"long-synthetic-folder-name-{i:D7}", i, true);
    var measurement = new FolderMeasurements(2, stamp, [drive], [], folders, [new(drive, id)]);
    string filename = Path.Combine(output, compressed ? "benchmark.yereldisk" : "benchmark.yereldisk.json");
    try
    {
        GC.Collect();
        var watch = Stopwatch.StartNew();
        long start = GC.GetTotalAllocatedBytes(true);
        measurement.Write(filename, compressed: compressed);
        Print("write");
        var restored = FolderMeasurements.Read(filename);
        Print("read");
        var comparison = new FolderComparison(restored, measurement);
        Print("compare");
        Console.WriteLine($"folders={restored.Folders.Length}; bytes={new FileInfo(filename).Length}; changes={comparison.Largest.Count}");
        return restored.Folders.Length == count + 1 && comparison.Largest.Count == 0 ? 0 : 1;

        void Print(string stage)
        {
            long allocated = GC.GetTotalAllocatedBytes(true) - start;
            Console.WriteLine($"{stage}: {watch.Elapsed.TotalMilliseconds:F1} ms; {allocated / 1048576.0:F2} MiB allocated");
            start = GC.GetTotalAllocatedBytes(true);
            watch.Restart();
        }
    }
    finally { File.Delete(filename); }
}
