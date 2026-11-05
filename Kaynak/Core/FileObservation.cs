namespace YerelDisk;

// Session-only, hint-free metadata. Keeping names rather than full file paths
// allows a changed directory to be replaced without losing other top-K candidates.
internal readonly record struct FileObservation(string Name, long Size, long? Allocated, DateTime Modified,
    uint Volume, ulong Id, bool HardLinked, int ExtraStreams, long ExtraStreamBytes, bool Unverified, string Note);
