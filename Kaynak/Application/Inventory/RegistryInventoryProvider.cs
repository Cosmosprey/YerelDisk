using System.Diagnostics;

namespace YerelDisk.Presentation;

internal enum RegistryReadResult { Success, Missing, End, Denied, Link, Invalid, Error }
internal sealed record RegistryText(RegistryReadResult State, string? Text = null, int Bytes = 0);
internal sealed record InventorySource(bool Machine, bool View64)
{
    internal string Label => (Machine ? "HKLM" : "HKCU") + (View64 ? "/64" : "/32");
    internal static readonly IReadOnlyList<InventorySource> All = Array.AsReadOnly(new[] { new InventorySource(true, true), new(true, false), new(false, true), new(false, false) });
}
internal interface IInventoryRegistryKey : IDisposable
{
    RegistryReadResult Name(uint index, out string name);
    RegistryReadResult OpenChild(string name, out IInventoryRegistryKey? child, Action? checkpoint = null);
    RegistryText ReadText(string field, int maximumBytes, Action? checkpoint = null);
}
internal interface IInventoryRegistryBackend { RegistryReadResult Open(InventorySource source, out IInventoryRegistryKey? key, Action? checkpoint = null); }
internal sealed class InventoryBudgetExceededException : Exception;
internal sealed record InventoryLimits(int Keys = 4096, int Bytes = 6 * 1024 * 1024, int ValueBytes = 64 * 1024, TimeSpan? Time = null)
{ internal TimeSpan Duration => Time ?? TimeSpan.FromSeconds(8); }

internal sealed class RegistryInventoryProvider(IInventoryRegistryBackend backend, InventoryLimits? limits = null) : IInventoryProvider
{
    internal RegistryInventoryProvider() : this(new WindowsInventoryRegistryBackend()) { }
    public Task<InventorySnapshot> ReadAsync(CancellationToken token) => Task.Run(() => Read(token), token);
    internal InventorySnapshot Read(CancellationToken token)
    {
        var budget = limits ?? new(); var watch = Stopwatch.StartNew(); var started = DateTimeOffset.Now;
        var apps = new List<InventoryApplication>(); var sources = new List<InventorySourceStatus>(); int keys = 0, bytes = 0;
        void Checkpoint() { token.ThrowIfCancellationRequested(); if (watch.Elapsed >= budget.Duration) throw new InventoryBudgetExceededException(); }
        bool Limited() => keys >= budget.Keys || bytes >= budget.Bytes || watch.Elapsed >= budget.Duration;
        foreach (var source in InventorySource.All)
        {
            token.ThrowIfCancellationRequested();
            int visited = 0, unusable = 0, errors = 0; var state = InventoryReadState.Complete;
            if (Limited()) { sources.Add(new(source.Label, InventoryReadState.Limited, 0, 0, 0)); continue; }
            try
            {
                var opened = backend.Open(source, out var root, Checkpoint);
                using (root)
                {
                    if (opened != RegistryReadResult.Success || root == null)
                    { sources.Add(new(source.Label, Map(opened), 0, 0, 0)); continue; }
                    for (uint index = 0; ; index++)
                    {
                        token.ThrowIfCancellationRequested();
                        if (Limited()) { state = InventoryReadState.Limited; break; }
                        var enumerated = root.Name(index, out string name);
                        if (enumerated == RegistryReadResult.End) break;
                        keys++; visited++;
                        if (enumerated != RegistryReadResult.Success) { errors++; state = InventoryReadState.Partial; break; }
                        if (name.Length is 0 or > 255 || name.IndexOfAny(['\\', '/', '\0']) >= 0) { errors++; unusable++; continue; }
                        token.ThrowIfCancellationRequested();
                        if (watch.Elapsed >= budget.Duration) { state = InventoryReadState.Limited; break; }
                        var childState = root.OpenChild(name, out var child, Checkpoint);
                        using (child)
                        {
                            if (childState != RegistryReadResult.Success || child == null) { errors++; unusable++; continue; }
                            string? Read(string field, int chars)
                            {
                                token.ThrowIfCancellationRequested();
                                if (bytes >= budget.Bytes || watch.Elapsed >= budget.Duration) { state = InventoryReadState.Limited; return null; }
                                int cap = Math.Max(0, Math.Min(budget.ValueBytes, budget.Bytes - bytes));
                                var value = child.ReadText(field, cap, Checkpoint);
                                if (value.Bytes < 0 || value.Bytes > cap) { errors++; state = InventoryReadState.Partial; return null; }
                                bytes += value.Bytes;
                                if (value.State == RegistryReadResult.Missing) return null;
                                if (value.State != RegistryReadResult.Success || value.Text == null || value.Text.Length > chars || value.Text.Length * 2L > cap || value.Text.Any(char.IsControl))
                                { errors++; return null; }
                                return value.Text;
                            }
                            string? path = InventoryPaths.Normalize(Read("InstallLocation", 4096));
                            if (path == null) { unusable++; continue; }
                            string? display = InventoryPaths.Text(Read("DisplayName", 512), 512);
                            if (string.IsNullOrEmpty(display)) { unusable++; continue; }
                            string publisher = InventoryPaths.Text(Read("Publisher", 256), 256) ?? "";
                            string version = InventoryPaths.Text(Read("DisplayVersion", 128), 128) ?? "";
                            token.ThrowIfCancellationRequested();
                            if (state == InventoryReadState.Limited) break;
                            apps.Add(new(display, publisher, version, path, source.Label));
                        }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (InventoryBudgetExceededException) { state = InventoryReadState.Limited; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.ComponentModel.Win32Exception)
            { state = InventoryReadState.Error; errors++; }
            if (state == InventoryReadState.Complete && errors > 0) state = InventoryReadState.Partial;
            sources.Add(new(source.Label, state, visited, unusable, errors));
        }
        token.ThrowIfCancellationRequested();
        // Merge only identical name/publisher/version and ordinal-ignore-case path.
        var merged = apps.GroupBy(a => a.InstallPath, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.GroupBy(a => (a.Name, a.Publisher, a.Version)).Select(same =>
                same.First() with { Sources = string.Join(", ", same.Select(a => a.Sources).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) })).ToArray();
        return new(started, DateTimeOffset.Now, merged, sources);
    }
    static InventoryReadState Map(RegistryReadResult result) => result switch
    { RegistryReadResult.Missing => InventoryReadState.Missing, RegistryReadResult.Denied => InventoryReadState.Denied, RegistryReadResult.Link => InventoryReadState.Link, _ => InventoryReadState.Error };
}
