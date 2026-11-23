# Refreshing a folder safely

Three actions have different scopes:

| Action | Scope | Existing result |
|---|---|---|
| New full scan | Editable next-scan roots/exclusions | Cleared at start |
| Remeasure all | Original roots/exclusions of the current result | Preserved until validated replacement |
| Refresh folder | Selected folder, or open folder without a selection | Preserved until validated replacement |

At the result root, the folder action is labeled **Refresh whole scope** and
measures the complete original scope. Selecting 128 MiB does not make a root
refresh incremental. The target path is visible above the list.

Enable the session-only 64/128 MiB index before scanning to retain advisory
metadata. An absent or oversized index can require a full-scope measurement.
A selected refresh checks original roots, folder/disk identities, link relations,
old/fresh metadata, memory accounting and final publication conditions. An
unusable fast path can fall back to the original scope, with the actual reason
shown during progress and after completion. Identity uncertainty can instead
stop replacement and preserve the old result. Links/placeholders are not followed.

A successful incremental refresh remeasures its target and rebuilds global totals,
risk evidence and top-K metadata in memory. Outside areas keep their older
observations. Changes outside the target need not appear until full remeasurement.
The banner, measurement cells, tooltips, F6 and CSV identify refreshed/older/mixed
times. One fresh-reference comparison is disabled until the complete scope is
remeasured. At most 64 refreshed-area time records are retained.

The native lease remains alive through analysis/query preparation and final
validation. Cancellation, stale tasks and failed validation must not publish a
partially prepared replacement. A current allocation failure does not authorize
a larger automatic scan. Memory accounting and scratch estimates are admission
policies, not exact process-memory caps. Opening, index retention and refresh
add no content reads or background filesystem watcher.
