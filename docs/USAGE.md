# Usage and measurement limits

1. **Choose folders** and add one or more scan roots. To avoid scanning all of
   C:, for example, select Program Files, Program Files (x86), and ProgramData
   individually. In the same window, add paths below the selected roots to
   the exclusions list.
2. **Start the scan.** Stop keeps partial results. Hidden folders are scanned
   within your permissions. Standard user permissions are sufficient for
   normal use.
3. **Scan summary** shows the scope and times for the initial scan, verification,
   and targeted refresh. Changed folders receive up to two targeted refresh
   rounds. The scan operates on a live file system, and refresh ends after
   those rounds.
4. Double-click a subfolder or press Enter on a selected folder row. Use the
   up arrow or Alt+Up to return. **Ctrl+F** searches the current list, and × or
   Esc clears it. Navigation may be limited for unmeasurable paths or paths
   with an earlier measurement.
5. **F6** shows scope, risks, and explanations. The open-location action shows
   the path in Explorer. **Save CSV** writes the rows in the visible filter
   and sort order. File backups are separate from CSV reports.

## Views

- **Folders** shows measured sizes. The disk usage view shows the 12 largest
  matching subfolders and the remaining groups. Shares use the selected
  folder's measured logical total as the denominator, which stays consistent
  as you search. CSV exports all filtered folder rows.
- **Large files** shows the largest K files retained during the scan, independent
  of the folder you are browsing. It covers that limited set of files. You can
  change K for the session.
- **Review candidates / Rule matches** shows clues about location, structure,
  and type, along with removal effects and a suggested first check. Notes
  record your assessment for the session separately from technical evidence.
- **Other large areas** shows measurable subtrees that the rules do not recognize.
  Their purpose and use are unknown. The view avoids repeating parent and child
  sizes in the same list. Files directly in scan roots and split ancestor folders
  have no separate rows. Check the Large files view for those files.
- **Warnings** shows access issues, missing measurements, and unverified locations.

A parent folder may appear even when one of its branches is excluded. Its size
covers only the scanned portion. The excluded branch has no measured size or
contents in the results. Treat missing or unmeasurable space as unknown.

## Settings

Worker count controls how many metadata operations run in parallel. The right
value depends on the hardware, disk, and directory tree. More workers may
provide little speedup. Start with Auto or the default and compare timings
over the same scan scope. Settings explains each option. Preferences apply
to the next scan.

Application record inspection is optional. [Data read and sharing behavior](PRIVACY.md).

## Known limits

- Logical totals and measured allocated space describe storage measurements.
  Recoverable space depends on hard links, compression, extra streams, access,
  and partial results.
- OneDrive, CompactOS, and WOF files classified as reparse points may be skipped.
  In particular, this can make the Windows folder total smaller than its
  physical disk usage.
- Protected, partial, and earlier measurements have separate labels. Changes
  during scanning can leave results incomplete, non-atomic, or missing some
  files.
- The app accepts local paths. It rejects network, UNC, and device paths and
  may reject subst paths and short 8.3 names.
- Speed varies with hardware, cache state, and permissions. Test counts describe
  test coverage. Real-world accuracy and safe deletion require separate evidence.
- The initial release targets Windows 11 x64. Physical DPI and multi-monitor
  configurations, file systems, and cloud providers have limited verification.
- The ready-to-run package is unsigned. Runtime security updates require a
  new YerelDisk package because the app bundles the runtime and has no
  automatic updater.
