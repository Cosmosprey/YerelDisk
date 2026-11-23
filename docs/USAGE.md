# Usage and measurement limits

Version 1.29.4 rejects references with no reliable folder measurements and
case-insensitive duplicate/nested roots. Healthy measured areas of partial scans
can still be compared. During filesystem changes, repeated single-link identities
are counted once within a root using the largest uncertain observation; that
representative is not proof of the latest size or a currently usable path.

The interface is in Turkish. English names below describe the corresponding actions.

## Scan and browse

1. Choose one or more local folders. To avoid a complete C: scan, select folders
   such as Program Files, Program Files (x86) and ProgramData individually.
   Exclude unwanted branches in the same scope dialog.
2. **New full scan** uses the configured next-scan roots/exclusions and clears the
   old result when it starts. Stop can leave explicitly partial results.
3. Browse folders by double-click/Enter; go up with Alt+Up. Ctrl+F searches the
   current view; Esc or the clear button clears the search. F6 opens details.
4. **Save CSV** exports visible rows in their applied filter/sort order. Opening
   a location shows it in Explorer without executing the selected file.

Verification and bounded repair run over a live filesystem. This is neither an
atomic snapshot nor continuous monitoring. Watch status and warning labels.
Unknown/partial/previous measurements are not zero-sized or empty folders.

## Views

- **Folders:** measured logical/allocated sizes, table, file-population summary,
  and distribution of the 12 largest matching subfolders plus remaining groups.
  Distribution percentages use the current folder's measured logical total.
- **Large files:** the largest K retained files, independent of the open folder.
  It is a limited set, not a listing of every scanned file.
- **Review candidates / Rule matches:** purpose/risk clues, removal effects and
  a suggested first check. Personal assessment notes apply only to the session.
- **Other large areas:** measurable, non-overlapping subtrees without a recognized
  purpose. Direct files in roots/split ancestors are inspected through Large files.
- **Warnings:** access errors, missing measurements and unverified locations.

An included parent can contain excluded descendants. Its total covers the scanned
portion; the excluded branch has no measured contents. Review the scope in F6.

## Folder measurements and comparison

After a completed, compatible full measurement, open **Scan difference**, save
that measurement as a reference, then scan the same scope later and open the
reference to compare. The reference stores folder measurements and scope, not
file contents or a complete per-file history. Compressed `.yereldisk` files are
bounded and integrity-checked; older supported JSON references can also be read.
Compression and checksums provide no encryption or authenticity guarantee.

The reference library shows up to 10 recent entries and can remember a default
save folder. Removing a recent entry only changes the list. Reference files have
no automatic expiration or deletion; manage them separately. Profiles hold up to
20 root/exclusion combinations. Both libraries start session-only; opt into
Remember if you want local persistence. [Stored data](PRIVACY.md).

## Remeasure and refresh

**Remeasure all** uses the current result's original roots and exclusions, keeps
old results visible during preparation, and replaces them only after validation.
Cancellation or failure preserves the old measurement. Editable next-scan paths
are separate. [Selected-folder behavior and fallback](REFRESH.md).

Worker, list and in-memory index options explain their costs in Settings. More
workers do not guarantee a speedup. Auto or 4–6 workers is a reasonable comparison
starting point for a directory-rich SSD workload; test the same scope on your
hardware. The optional 64/128 MiB index is session-only and defaults off. This is
an index accounting budget, not a process-RAM limit.

## Known limits

- Logical size, measured allocated space and recoverable space are different.
  Hard links, sparse files, compression, extra streams and partial coverage matter.
- Reparse points, including some OneDrive/CompactOS/WOF files, may be skipped;
  Windows-folder totals can therefore be incomplete.
- Network/UNC/device roots are rejected. Subst drives and short 8.3 names may be
  rejected by canonical-location checks.
- Concurrent changes and unavailable paths can leave warnings or incomplete data.
  No observed warning is a promise that every possible file was measured.
- Mixed-time results cannot be saved or compared as one fresh full reference;
  remeasure the full scope first.
- Real hardware, cloud providers, unusual filesystems, physical DPI/multi-monitor
  setups and long-running stress have limited verification. The supported release
  target is Windows 11 x64; the unsigned package has no automatic updater.
