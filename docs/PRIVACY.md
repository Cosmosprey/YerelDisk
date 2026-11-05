# Privacy and data scope

YerelDisk has no networking, telemetry, account sign-in, or automatic updates.
GitHub downloads, developer CI, and packaging are separate from the app.
Windows, .NET, Explorer, and security software manage their own activity and
records separately.

## Normal scan

Within the selected local roots, the app reads file and folder names, paths,
sizes, timestamps, file system identifiers, and measurement metadata.
File contents, processes, WMI/Win32_Product, and command output stay outside
the scan. The app does not follow symbolic links, junctions, or cloud
placeholders. An excluded branch is skipped without opening it. Listing its
parent can still reveal the branch name in metadata. The app rejects network,
UNC, and device roots.

The app does not delete or move files or change their contents or permissions.
Results and settings stay in the session. The app does not save reports or
debug inventories automatically.

## Optional application records

This feature is off by default. After a disk scan, explicitly request
application record inspection in Settings. Startup, a scan, or changing tabs
leaves the records unread until you request inspection.

- Sources are the 32-bit and 64-bit views of
  `Software\Microsoft\Windows\CurrentVersion\Uninstall` in local HKLM and
  the current user's HKCU only.
- The reader uses `InstallLocation`, `DisplayName`, `Publisher`, and
  `DisplayVersion`. It also reads key names, value types, and sizes to limit
  traversal.
- The reader opens path components as link objects and checks only the type
  and size of `SymbolicLinkValue` to reject redirects. It leaves the target
  data unread.
- It may inspect records for applications outside the selected scan roots.
  The results show clues only for scanned, usable rows.
- The reader leaves referenced files and executables unopened. It skips
  uninstaller, DisplayIcon, InstallSource, and command fields, keeps environment
  variables unexpanded, and limits its scope to the local registry and current
  user's profile. It runs with standard permissions and uses read-only access.

A record match is a literal installation path match. Current installation
status, active use, ownership, and whether files are needed remain unverified.
A missing match is inconclusive. Portable apps, empty paths, and stale
records can leave these sources incomplete. Names alone do not establish
which applications use shared Gradle or NuGet stores. Project configuration
and contents stay outside the inspection.

The limits are 4096 subkeys, 6 MiB of total value data read, and 64 KiB per
value. Character limits are 512 for names, 256 for publishers, 128 for versions,
and 4096 for paths. The reader checks cancellation and an eight-second read
budget between OS calls. An OS call already in progress can run beyond that
budget. The app reports incomplete, failed, and limited sources separately.

Starting a new scan, using Stop or Clear clues, resetting settings, or closing
the app clears the live clues. Late results from an earlier task cannot
restore them. Managed memory has no guaranteed secure erasure. Previously
exported reports and clipboard copies remain separate.

## User sharing actions

CSV reports and copies of paths or full details may contain file and folder
names. Application record clues are off by default in CSV. The separate
setting adds explanations only for the rows in the report. At the 4096-match
limit, the report adds a truncation note and keeps the main table rows.
The export excludes the full machine inventory. **Copy full text** in the F6
details includes the visible application record information for the selection.
Review reports and screenshots before sharing them.

The open-location action opens Explorer when you request it and leaves the
selected file unexecuted. Windows may manage Explorer's recent items and system
records separately. For support, prefer sample paths and small synthetic
scenarios over real disk dumps.
