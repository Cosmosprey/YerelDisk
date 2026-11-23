using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace YerelDisk;

// File marker and SHA-256 of the gzip bytes, followed by one gzip payload.
// The payload stores bounded counts and little-endian UTF-16 Windows paths.
internal static class CompactMeasurementFile
{
    static ReadOnlySpan<byte> Magic => "YDKREF01"u8;
    const int HeaderSize = 8 + 32;
    const int BufferSize = 64 * 1024;
    const int MaxPathChars = 32767;

    internal static bool IsCompact(FileStream file)
    {
        Span<byte> prefix = stackalloc byte[6];
        if (file.Length < prefix.Length) return false;
        file.ReadExactly(prefix);
        file.Position = 0;
        return prefix.SequenceEqual(Magic[..prefix.Length]);
    }

    // Claude 1.20.1: the expanded payload has its own limit. Paths are UTF-16, so the
    // decoded size is about twice the UTF-8 JSON it replaces; one shared 128 MiB cap made
    // long-path scans that 1.19.1 could save fail here (see FolderMeasurements.ExpandedLimit).
    internal static FolderMeasurements Read(FileStream file, int maximumBytes, int maximumExpandedBytes)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        if (file.Length < HeaderSize + 18) throw Invalid();
        file.ReadExactly(header);
        if (!header[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("Sıkıştırılmış ölçüm dosyasının sürümü desteklenmiyor.");
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(file, hash);
        if (!CryptographicOperations.FixedTimeEquals(header[Magic.Length..], hash)) throw Invalid();
        // Gzip's final ISIZE lets us reject oversized payloads before decoding.
        // The stream cap still applies if a crafted trailer understates the size.
        Span<byte> size = stackalloc byte[4];
        file.Position = file.Length - size.Length;
        file.ReadExactly(size);
        uint declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(size);
        if (declaredSize > maximumExpandedBytes) throw FolderMeasurements.ExpandedTooLarge();
        file.Position = HeaderSize;
        using var gzip = new GZipStream(file, CompressionMode.Decompress, leaveOpen: true);
        using var limited = new CappedInput(gzip, maximumExpandedBytes);
        using var buffer = new BufferedStream(limited, BufferSize);
        using var reader = new BinaryReader(buffer, Encoding.UTF8, leaveOpen: true);
        byte[] pathBuffer = new byte[MaxPathChars * sizeof(char)];
        string PathValue()
        {
            int length = ReadCount(reader, MaxPathChars);
            if (length < 3) throw Invalid();
            var bytes = pathBuffer.AsSpan(0, length * sizeof(char));
            buffer.ReadExactly(bytes);
            return new string(MemoryMarshal.Cast<byte, char>(bytes));
        }
        try
        {
            long ticks = reader.ReadInt64();
            if (ticks <= 0 || ticks > DateTime.MaxValue.Ticks) throw Invalid();
            var roots = ReadList(reader, FolderMeasurements.MaxScopePaths, PathValue);
            var exclusions = ReadList(reader, FolderMeasurements.MaxScopePaths, PathValue);
            var volumes = ReadList(reader, FolderMeasurements.MaxScopePaths, () =>
            {
                string root = PathValue();
                Span<byte> id = stackalloc byte[16];
                buffer.ReadExactly(id);
                return new MeasurementVolume(root, new Guid(id));
            });
            var folders = ReadList(reader, FolderMeasurements.MaxFolders, () =>
            {
                string path = PathValue();
                long bytes = reader.ReadInt64();
                byte reliable = reader.ReadByte();
                if (bytes < 0 || reliable > 1) throw Invalid();
                return new FolderMeasurement(path, bytes, reliable == 1);
            });
            if (buffer.ReadByte() != -1 || limited.BytesRead != declaredSize) throw Invalid();
            return new(FolderMeasurements.CurrentFormat, new DateTimeOffset(ticks, TimeSpan.Zero), roots, exclusions, folders, volumes);
        }
        catch (Exception ex) when (ex is EndOfStreamException or FormatException)
        {
            throw Invalid(ex);
        }
    }

    internal static void Write(FileStream file, FolderMeasurements data, int maximumBytes, int maximumExpandedBytes)
    {
        if (data.Format != FolderMeasurements.CurrentFormat || data.RootVolumes == null || data.MeasuredAt == default
            || data.Roots.Length is 0 or > FolderMeasurements.MaxScopePaths
            || data.Exclusions.Length > FolderMeasurements.MaxScopePaths || data.RootVolumes.Length > FolderMeasurements.MaxScopePaths
            || data.Folders.Length is 0 or > FolderMeasurements.MaxFolders)
            throw Invalid();
        if (maximumBytes <= HeaderSize) throw FolderMeasurements.TooLarge();
        file.Write(Magic);
        file.Position = HeaderSize;
        using (var stored = new FolderMeasurements.CappedOutput(file, maximumBytes - HeaderSize))
        using (var gzip = new GZipStream(stored, CompressionLevel.Optimal, leaveOpen: true))
        using (var expanded = new FolderMeasurements.CappedOutput(gzip, maximumExpandedBytes, expandedPayload: true))
        using (var buffer = new BufferedStream(expanded, BufferSize))
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(data.MeasuredAt.UtcTicks);
            WritePaths(writer, data.Roots);
            WritePaths(writer, data.Exclusions);
            writer.Write7BitEncodedInt(data.RootVolumes.Length);
            Span<byte> id = stackalloc byte[16];
            foreach (var volume in data.RootVolumes)
            {
                WritePath(writer, volume.Root);
                volume.Id.TryWriteBytes(id);
                writer.Write(id);
            }
            writer.Write7BitEncodedInt(data.Folders.Length);
            foreach (var folder in data.Folders)
            {
                WritePath(writer, folder.Path);
                writer.Write(folder.Bytes);
                writer.Write(folder.Reliable);
            }
        }
        file.Position = HeaderSize;
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(file, hash);
        file.Position = Magic.Length;
        file.Write(hash);
    }

    static T[] ReadList<T>(BinaryReader reader, int limit, Func<T> read)
    {
        var items = new T[ReadCount(reader, limit)];
        for (int i = 0; i < items.Length; i++) items[i] = read();
        return items;
    }

    static int ReadCount(BinaryReader reader, int limit)
    {
        int count = reader.Read7BitEncodedInt();
        return count >= 0 && count <= limit ? count : throw Invalid();
    }

    static void WritePaths(BinaryWriter writer, string[] paths)
    {
        writer.Write7BitEncodedInt(paths.Length);
        foreach (string path in paths) WritePath(writer, path);
    }

    static void WritePath(BinaryWriter writer, string path)
    {
        if (path.Length is < 3 or > MaxPathChars) throw Invalid();
        writer.Write7BitEncodedInt(path.Length);
        writer.Write(MemoryMarshal.AsBytes(path.AsSpan()));
    }

    static InvalidDataException Invalid(Exception? inner = null)
        => new("Sıkıştırılmış ölçüm dosyası bozuk veya eksik.", inner);

    sealed class CappedInput(Stream source, int maximumBytes) : Stream
    {
        internal int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;
            int remaining = maximumBytes - BytesRead;
            if (remaining == 0)
            {
                if (Decode(() => source.ReadByte()) >= 0) throw FolderMeasurements.ExpandedTooLarge();
                return 0;
            }
            var slice = buffer[..Math.Min(buffer.Length, remaining)];
            int count;
            try { count = source.Read(slice); }
            catch (InvalidDataException ex) { throw Invalid(ex); }
            BytesRead += count;
            return count;
        }

        // Claude 1.20.1: GZipStream reports CRC/length/deflate damage with an English
        // "unsupported compression method" message; show the container's own text instead.
        static int Decode(Func<int> read)
        {
            try { return read(); }
            catch (InvalidDataException ex) { throw Invalid(ex); }
        }
    }
}
