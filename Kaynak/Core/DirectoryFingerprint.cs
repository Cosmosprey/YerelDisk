using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace YerelDisk;

// Order-independent directory metadata multiset. This detects ordinary churn;
// identity/no-follow checks remain the security boundary, never this digest.
internal record struct DirectoryFingerprint(long Count, ulong SumA, ulong SumB, ulong XorC, ulong XorD)
{
    internal void Add(NativeEntry entry)
    {
        int length = checked(28 + entry.Name.Length * 2);
        Span<byte> data = length <= 2048 ? stackalloc byte[length] : new byte[length];
        BinaryPrimitives.WriteUInt64LittleEndian(data, entry.Id);
        // A directory's own contents/streams are verified through its handle.
        // Its duplicate timestamps in the parent listing can be refreshed merely
        // by opening/closing that directory; do not mistake that for file churn.
        bool directory = (entry.Attributes & FileAttributes.Directory) != 0;
        BinaryPrimitives.WriteInt64LittleEndian(data[8..], directory ? 0 : entry.Size);
        BinaryPrimitives.WriteInt64LittleEndian(data[16..], directory ? 0 : entry.Modified.Ticks);
        BinaryPrimitives.WriteUInt32LittleEndian(data[24..], (uint)entry.Attributes);
        MemoryMarshal.AsBytes(entry.Name.AsSpan()).CopyTo(data[28..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);
        Count++;
        unchecked { SumA += BinaryPrimitives.ReadUInt64LittleEndian(hash); SumB += BinaryPrimitives.ReadUInt64LittleEndian(hash[8..]); }
        XorC ^= BinaryPrimitives.ReadUInt64LittleEndian(hash[16..]); XorD ^= BinaryPrimitives.ReadUInt64LittleEndian(hash[24..]);
    }
}
