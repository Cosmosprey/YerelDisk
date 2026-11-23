# Current verification

Run `Kaynak/Dogrula.cmd` or `Kaynak/Dogrula.ps1` on Windows x64 with the selected
.NET SDK and WindowsDesktop references. Normal build/test package sources are
empty. User app startup does not run these developer tests.

```powershell
.\Kaynak\Dogrula.cmd
.\Kaynak\Dogrula.cmd -Native
.\Kaynak\Dogrula.cmd -All
.\Kaynak\Dogrula.cmd -All -List
.\Kaynak\Dogrula.cmd -All -KeepFixtures
```

The latest local main-build run recorded 1,573 Normal + 189 Native = 1,762 passing
assertions in 47 suites. Counts include related checks and theme/size repetitions;
they are not independent bug scenarios, an accuracy score or a coverage percentage.
The package must be verified against its own published DLLs as described in
[development](../../docs/DEVELOPMENT.md). Normal includes some real filesystem
fixtures; Native requires hard-link/junction/stream/ACL capabilities. Real user
C:/D: scans, installed-app inventories and arbitrary file contents are not the corpus.

## Catalog

The minimum column is the runner's floor; suites can legitimately report more.

| Suite | Group | Minimum checks | Scope |
|---|---|---:|---|
| AccountingOracle | Normal | 84 | Independent declared-value accounting, 64 seeded cases, alias/volume/unknown-allocation/top-K checks, cancellation and bounded reservations |
| SelectedRefresh | Normal | 48 | Session index preference, frozen result scope, lease/commit/rollback, mixed times, CSV, native service flow |
| SelectedRefreshUi | Normal | 54 | Actual controls and table cells, target labels, fallback reasons, locks, mixed-time details and theme/size checks |
| RefreshExecution | Normal | 41 | Real selected scans, cloned generations, top-K refill, cancellation, final identity checks, fallback and growth limits |
| RefreshExecutionNative | Native | 20 | Hard links, streams, junctions, multi-root replay, boundary moves and full-scan parity |
| RefreshPlanner | Normal | 51 | Two-stage decisions, scope/identity/index consistency, prefix siblings, cross-boundary IDs, budgets and OOM policy |
| RefreshPlannerNative | Native | 13 | Real alias creation, exclusions, multi-root scope, unknown junctions and root replacement |
| RefreshIndex | Normal | 40 | Opt-in defaults, immutable pages/slices, budgets, incomplete inputs, cancellation and long-name accounting |
| RefreshIndexNative | Native | 20 | Real metadata capture, links/streams, exclusions, parity, privacy boundaries and cancellation |
| Remeasure | Normal | 46 | Original-scope transaction, old result preservation, cancellation, failures, query/report generations and native fixtures |
| RemeasureUi | Normal | 39 | Actual action, old rows/cards during replacement, controls, rollback, navigation and bindings |
| ScanProfiles | Normal | 75 | Bounded profiles, opt-in persistence, conflicts and multi-process merges, corrupt/locked storage |
| ScanProfilesUi | Normal | 34 | Profile editing/reuse, remember choice, conflict actions, themes and compact layouts |
| ScanProfilesNative | Native | 11 | Owned preference data, unchanged scan access, parent links, read-data ACLs and integrity |
| Population | Normal | 32 | Metadata-wide counts independent of top-K, empty files, type/owner bounds and synthetic allocation cases |
| PopulationUi | Normal | 22 | Selection/navigation summaries, F6, both tables, missing data, close/cancel and bindings |
| PopulationNative | Native | 10 | Real links, roots, exclusions, junctions, streams, repair and cancellation |
| CompactMeasurements | Normal | 37 | Legacy JSON, compressed format, long paths, corruption, read/write/expanded-size bounds and atomic writes |
| Comparison | Normal | 63 | Scopes, volume identity, supported formats, deep trees, bounded writes and reference preferences |
| ComparisonUi | Normal | 52 | Save/open/recent references, differences navigation, partial/old/excluded boundaries, async races and errors |
| Inventory | Normal | 61 | Fake/shim local record provider, bounded fields/traversal, path clues, cancellation and CSV opt-in; no real inventory |
| InventoryUi | Normal | 27 | Explicit action, scopes/risks in table/F6, export choice, forgetting, generations and bindings |
| Filtering | Normal | 22 | Visible/hidden reasons, scope boundaries, clear filters, pending query races and report snapshots |
| FilteringUi | Normal | 27 | Actual controls/keys, footer, unsafe-row navigation guards, themes and bindings |
| ClassificationCorpus | Normal | 44 | Reviewable synthetic JSON scenarios; misleading contexts, shared stores, protection, unknown risks; not real-world accuracy |
| OtherAreas | Normal | 40 | Non-overlapping unknown subtrees, protected/recognized branches, direct-file boundaries, races and bounded large-tree presentation |
| OtherAreasUi | Normal | 30 | Compact/large themes, warnings, selection/F6/CSV, navigation and virtualization |
| ReviewGuidance | Normal | 31 | Candidate-specific first checks, risk types, incomplete/excluded evidence and F6/CSV agreement |
| ReviewTable | Normal | 58 | Purpose/signal/effect/first-step cells, uncertainty, compact risk text and virtualization |
| ReviewMetadata | Normal | 55 | Evidence outside top-K, repaired/moved/cancelled snapshots, risk fields and owner allocation bounds |
| ReviewMetadataUi | Normal | 26 | Usage uncertainty, F6/side details, report fields, theme/size checks |
| ReviewMetadataNative | Native | 6 | Alias names versus physical allocation, junction/ACL coverage and fixture data preservation |
| Distribution | Normal | 43 | Shares, groups, unknown/previous observations, search/CSV, navigation and bounded presentation |
| DistributionUi | Normal | 28 | Bars, scrolling, selection, navigation and return to table in both themes/sizes |
| Exclusions | Normal | 61 | Path boundaries, roots, moves/repair/cancel, review scope, CSV and bounded setup |
| ExclusionsUi | Normal | 26 | Actual scope editor, errors, summary and F6 across themes/sizes |
| ExclusionsNative | Native | 9 | Excluded links/junctions/ACL targets, physical totals and content preservation |
| Application | Normal | 55 | UI-independent scan/query/selection/report/cancel flows and small real fixtures |
| Summary | Normal | 41 | Phase timing, local repair, cancellation, multi-root summary and report fields |
| IdentityCapacity | Normal | 10 | Bounded identity reservation, growth, null/zero allocation and early stop |
| Review | Normal | 139 | Rule evidence, scope, SDK markers, type risks, filters/CSV and negative examples |
| ReviewUi | Normal | 19 | Long paths, themes, compact/large layouts, F6 and clues |
| SummaryUi | Normal | 30 | Warning visibility, size ordering, scroll arrows and small wheel events |
| Movement | Native | 20 | Cross-root moves/shrinkage, top-K refill and real hard links |
| Recovery | Native | 57 | Stable/changing trees, repair, cancellation, root/folder replacement, ACLs and aliases |
| Regression | Native | 17 | NTFS streams, hard links and root/descendant junction races |
| Errors | Native | 12 | Error priority, inaccessible roots, cancellation and multiple roots |
## Evidence and limits

Each run has isolated product/test outputs, source and test-support manifests,
actual DLL dependency checks, raw results and summary JSON/CSV. A source or test
set change during the run invalidates it. A catalog/README mismatch and 17 result
classification counterexamples are checked before tests. Framework-dependent
source tests build a product; `-PublishedProductDirectory` checks a source-matching
self-contained package without rebuilding its DLLs.

PASS requires a successful process, expected ending marker, enough real PASS
records and no error/skip records. Exit 1 means failure or infrastructure error.
Exit 2 reports missing environment capabilities or skipped checks and is not an
all-pass result. Only the selected suites passing yields exit 0; omitted suites
are listed separately. An ACL restoration failure is an infrastructure failure.

Successful owned fixture trees are moved to the Recycle Bin when available.
Failed/skipped/incomplete fixtures stay for diagnosis; no permanent-delete
fallback exists. `fixture-cleanup.csv` records outcomes. Run logs, images,
manifests and build outputs remain local. `-KeepFixtures` also keeps successful
inputs. Empty the Recycle Bin separately if you want to release that space.

The 12,000-file recovery example checks accounting parity, not a speed guarantee.
Synthetic reference expectations reduce shared-implementation blind spots but
cannot prove every path/race correct. UI tests use actual WPF controls and
synthetic metadata, with limited themes/sizes; physical touchpads, DPI/monitor
combinations, long sessions, cold-cache disks and cloud providers need separate
validation. The classification corpus does not measure real-world safe-deletion
accuracy. Historical local audits and personal run reports are not included.

## Optional developer tools

PowerShell 7: `Dogrulama/Guncel/Mutasyon/Run.ps1` checks ten curated semantic
faults in separate source copies, after an unchanged baseline passes. A mapped
expected assertion must reject each fault; a crash, build error or timeout does
not count. [Mutation scope](Mutasyon/README.md). It is not run automatically on
every Normal test and does not establish a general mutation coverage percentage.

`Dogrulama/Olcum-Araci/Run.ps1` measures controlled workloads separately. Build a
developer product first. [Resource measurement method](../Olcum-Araci/README.md).
Do not publish `Kosular`, `Mutasyon-Kosular`, `Olcum-Kosular`, preferences,
references or raw personal run evidence with source or release assets.
