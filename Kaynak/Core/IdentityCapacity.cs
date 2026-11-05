namespace YerelDisk;

// Only an eager allocation hint. These limits never cap retained identities:
// Dictionary grows normally when new IDs arrive.
internal static class IdentityCapacity
{
    internal const int MaxInitialHint = 32_768;

    internal static int ForObservations(IEnumerable<ScanNode> nodes)
    {
        int hint = 0;
        foreach (var node in nodes)
            foreach (var file in node.Files)
                if (file.Id != 0 && file.Allocated.HasValue && !file.HardLinked
                    && ++hint == MaxInitialHint) return hint;
        return hint;
    }

    internal static int ForRoots(IEnumerable<ScanResult> roots)
    {
        int hint = 0;
        foreach (var root in roots)
        {
            // Each per-root map already has distinct keys. Its count is a known
            // lower bound for the union; summing roots would count moved IDs again.
            hint = Math.Max(hint, root.PhysicalLinks.Count);
            if (hint >= MaxInitialHint) return MaxInitialHint;
        }
        return hint;
    }
}
