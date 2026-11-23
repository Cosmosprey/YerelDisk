using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YerelDisk;

internal sealed record FolderMeasurement(string Path, long Bytes, bool Reliable);
internal sealed record MeasurementVolume(string Root, Guid Id);

internal sealed record FolderMeasurements(int Format, DateTimeOffset MeasuredAt, string[] Roots,
    string[] Exclusions, FolderMeasurement[] Folders,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MeasurementVolume[]? RootVolumes = null)
{
    internal const int CurrentFormat = 2;
    internal const int MaxFolders = 1_000_000;
    internal const int MaxScopePaths = 10_000;
    const int MaxBytes = 128 * 1024 * 1024;
    static readonly JsonSerializerOptions WriteJson = new()
    {
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    static readonly JsonSerializerOptions ReadJson = new(WriteJson)
    {
        Converters = { new BoundedArray<FolderMeasurement>(MaxFolders), new BoundedArray<string>(MaxScopePaths),
            new BoundedArray<MeasurementVolume>(MaxScopePaths) }
    };

    internal static bool CanCapture(ScanResult scan)
        => !scan.Cancelled && !scan.HasMixedMeasurementTimes
            && scan.Root.Measurement is not (MeasurementState.Unknown or MeasurementState.Previous)
            && scan.RootPaths.Any(scan.CanUsePath);

    internal static FolderMeasurements Capture(ScanResult scan, DateTimeOffset measuredAt)
    {
        if (scan.HasMixedMeasurementTimes)
            throw new InvalidDataException("Bu sonuç farklı zamanlarda ölçülen alanlar içeriyor. Referans kaydetmeden önce kapsamın tamamını yeniden ölçün.");
        if (!CanCapture(scan))
            throw new InvalidDataException("Bu tarama yarıda kaldı. Karşılaştırma için yeni bir tarama yapın.");
        if (scan.RootPaths.Count > MaxScopePaths || scan.ExcludedPaths.Count > MaxScopePaths)
            throw new InvalidDataException("Ölçüm kapsamındaki yol sayısı sınırı aşıyor. Daha dar bir alanı tarayın.");

        var unsafePaths = scan.UnverifiedPaths.Select(path => path.TrimEnd('\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folders = new List<FolderMeasurement>();
        var pending = new Stack<(FolderEntry Folder, bool Unsafe)>();
        if (scan.RootPaths.Count == 1) pending.Push((scan.Root, false));
        else foreach (var root in scan.Root.Children) pending.Push((root, false));
        while (pending.TryPop(out var item))
        {
            var folder = item.Folder;
            bool unsafeBranch = item.Unsafe || folder.Measurement is MeasurementState.Unknown or MeasurementState.Previous
                || (unsafePaths.Count > 0 && unsafePaths.Contains(folder.Path.TrimEnd('\\')));
            bool reliable = !unsafeBranch && folder.Measurement == MeasurementState.Measured && folder.IssueCount == 0;
            if (folders.Count == MaxFolders) throw new InvalidDataException("Ölçüm bir milyon klasör sınırını aşıyor. Daha dar bir alanı tarayın.");
            folders.Add(new(folder.Path, folder.LogicalBytes, reliable));
            foreach (var child in folder.Children) pending.Push((child, unsafeBranch));
        }
        var volumes = scan.RootVolumeIds?.Select(pair => new MeasurementVolume(pair.Key, pair.Value)).ToArray() ?? [];
        if (!folders.Any(folder => folder.Reliable))
            throw new InvalidDataException("Bu sonuçta referans için doğrulanmış klasör ölçümü yok. Yeniden tarayın.");
        return new(CurrentFormat, measuredAt, scan.RootPaths.ToArray(), scan.ExcludedPaths.ToArray(), folders.ToArray(), volumes);
    }

    // Claude 1.20.1: the compact payload stores paths as UTF-16 (2 bytes per character) while
    // the JSON it replaces stores ASCII paths in about one byte. A record costs ~2L+10 decoded
    // bytes against ~L+45 in JSON, so sharing the 128 MiB budget cut capacity once the average
    // path passed ~40 characters (700,001 folders of ~100 characters: JSON 107 MiB, decoded
    // compact 143 MiB). Twice the stored budget keeps every path length at least as capable as
    // 1.19.1; decoded UTF-16 is also roughly the string memory the reader allocates.
    internal static int ExpandedLimit(int maximumBytes) => (int)Math.Min(int.MaxValue, 2L * maximumBytes);

    internal static FolderMeasurements Read(string filename, int maximumBytes = MaxBytes, int? maximumExpandedBytes = null)
        => ReadWithSize(filename, maximumBytes, maximumExpandedBytes).Measurement;

    internal static (FolderMeasurements Measurement, long FileBytes) ReadWithSize(
        string filename, int maximumBytes = MaxBytes, int? maximumExpandedBytes = null)
    {
        string path = LocalFile(filename);
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
            throw new InvalidDataException("Bağlantı veya çevrimdışı dosya yerine yerel bir ölçüm dosyası seçin.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long fileBytes = file.Length;
        if (fileBytes > maximumBytes) throw TooLarge();
        var measurement = CompactMeasurementFile.IsCompact(file)
            ? CompactMeasurementFile.Read(file, maximumBytes, maximumExpandedBytes ?? ExpandedLimit(maximumBytes))
            : JsonSerializer.Deserialize<FolderMeasurements>(file, ReadJson)
                ?? throw new InvalidDataException("Ölçüm dosyası boş.");
        return (measurement, fileBytes);
    }

    internal void Write(string filename, int maximumBytes = MaxBytes, bool compressed = false, int? maximumExpandedBytes = null)
        => WriteWithSize(filename, maximumBytes, compressed, maximumExpandedBytes);

    internal long WriteWithSize(string filename, int maximumBytes = MaxBytes, bool compressed = false, int? maximumExpandedBytes = null)
    {
        string path = LocalFile(filename);
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".yereldisk-{Guid.NewGuid():N}.tmp");
        bool created = false;
        long fileBytes;
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, compressed ? FileAccess.ReadWrite : FileAccess.Write, FileShare.None))
            {
                created = true;
                // Claude 1.20.1: a write interrupted by a crash or power loss leaves this file
                // behind; keep it hidden and make the finished reference visible again.
                File.SetAttributes(file.SafeFileHandle, FileAttributes.Hidden);
                if (compressed)
                    CompactMeasurementFile.Write(file, this, maximumBytes, maximumExpandedBytes ?? ExpandedLimit(maximumBytes));
                else
                {
                    using var output = new CappedOutput(file, maximumBytes);
                    JsonSerializer.Serialize(output, this, WriteJson);
                }
                file.Flush(flushToDisk: true);
                File.SetAttributes(file.SafeFileHandle, FileAttributes.Archive);
                fileBytes = file.Length;
            }
            File.Move(temporary, path, overwrite: true);
            created = false;
            return fileBytes;
        }
        finally { if (created) File.Delete(temporary); }
    }

    static string LocalFile(string filename)
    {
        string path = Path.GetFullPath(filename);
        if (!IsLocalPath(path)) throw new InvalidDataException("Ölçüm için yerel bir dosya seçin.");
        DiskScanner.ValidateRoot(Path.GetDirectoryName(path)!);
        return path;
    }

    internal static InvalidDataException TooLarge() => new("Ölçüm dosyası 128 MiB sınırını aşıyor. Daha dar bir alanı tarayın.");
    internal static InvalidDataException ExpandedTooLarge() => new("Açılmış ölçüm verisi 256 MiB sınırını aşıyor. Daha dar bir alanı tarayın.");

    internal static bool IsLocalPath(string? path) => path is { Length: >= 3 and <= 32767 }
        && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\'
        && !path.AsSpan(3).ContainsAny(':', '/', '\0');

    internal static string? ParentOf(string path)
    {
        int length = ParentLength(path);
        return length == 0 ? null : path[..length];
    }

    internal static int ParentLength(ReadOnlySpan<char> path)
        => path.Length <= 3 ? 0 : Math.Max(3, path.LastIndexOf('\\'));

    internal sealed class CappedOutput(Stream destination, int maximumBytes, bool expandedPayload = false) : Stream
    {
        int written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => destination.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > maximumBytes - written) throw expandedPayload ? ExpandedTooLarge() : TooLarge();
            destination.Write(buffer);
            written += buffer.Length;
        }
    }

    sealed class BoundedArray<T>(int limit) : JsonConverter<T[]>
    {
        public override T[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException();
            var items = new List<T>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) return items.ToArray();
                if (items.Count == limit) throw new JsonException("Ölçüm dosyasındaki kayıt sayısı sınırı aşıyor.");
                items.Add(JsonSerializer.Deserialize<T>(ref reader, options)!);
            }
            throw new JsonException();
        }

        public override void Write(Utf8JsonWriter writer, T[] value, JsonSerializerOptions options)
            => throw new NotSupportedException();
    }
}
