# Privacy and data scope

YerelDisk has no networking, telemetry, account sign-in or automatic updates.
GitHub, developer CI, SDK/package downloads, Windows, .NET, Explorer and security
software have their own activity separate from the app.

## Normal scans

Inside the selected local roots the app reads names, paths, sizes, timestamps,
file identifiers and filesystem metadata, including measurable extra-stream
sizes. It does not read arbitrary file contents or follow links/cloud placeholders.
An excluded branch is not opened, though listing its parent can reveal its name.
Network, UNC and device roots are rejected. No process inventory, WMI/Win32_Product,
project configuration inspection or command-output collection is part of scanning.

The app has no cleanup commands that delete, move, modify or change permissions
on scanned data. Results normally remain in memory. The optional refresh index
retains the same metadata longer for the current session; it is not written to disk.

## References, profiles and optional persistence

Saving a reference explicitly writes folder paths, logical sizes, reliability
flags, measurement time, scope/exclusions and volume identifiers to the chosen
file. Opening one reads that selected file; it does not read the referenced
folders automatically. References are compressed/integrity-checked, not encrypted.
No file payload is included and no automatic reference-file expiration occurs.

Profiles hold names, roots and exclusions. Reference preferences hold a default
folder and up to 10 recent paths/times/sizes. Both are session-only until you
choose Remember. That choice can create these local preference files:

- `%LOCALAPPDATA%\YerelDisk-profiller.json`
- `%LOCALAPPDATA%\YerelDisk-referanslar.json`

Relevant profile/reference dialogs can reload these previously opted-in records.
Keeping Remember enabled permits later updates. Turning it off removes that
application preference file; the app may also remove its own failed temporary
save file. Atomic saves may replace the selected destination after successful
preparation. These operations are separate from scanned-file cleanup.

Removing a recent entry does not delete its reference file. There is no automatic
expiry or reference-folder sweep. Copies in saved references, exports, backups
and the clipboard remain separate. Managed memory has no guaranteed secure erasure.

## Optional application records

Inspection starts off and runs only after a user request in Settings. It reads
local 32-bit/64-bit `Software\Microsoft\Windows\CurrentVersion\Uninstall` entries
in HKLM and the current user's HKCU. It may see records outside scan roots.
The four content values are `InstallLocation`, `DisplayName`, `Publisher` and
`DisplayVersion`; key names, types and sizes are also checked for bounded traversal.

The reader opens registry components as link objects, checks the type/size of
`SymbolicLinkValue` and rejects redirects without reading the target value data.
It does not open listed executables/files, expand environment variables, or read
uninstaller, DisplayIcon, InstallSource or command fields. Access is read-only
and uses normal user permissions.

Limits: 4096 subkeys, 6 MiB total value data, 64 KiB per value; 512 name, 256
publisher, 128 version and 4096 path characters. Cancellation and an eight-second
budget are checked between OS calls; a call in progress may exceed that time.
Incomplete, limited and failed sources are qualified separately.

A path match is an installation clue. Installed state, active use, ownership,
shared-store use and necessity remain unverified. A missing match is inconclusive.
Starting/replacing a scan, Stop/Clear clues, reset and close invalidate old clues;
late results must not restore an earlier generation.

## Sharing

CSV, references, profile names/paths, F6 copies and screenshots may contain private
information. Application-record CSV inclusion is a separate opt-in, limited to
visible report rows; a 4096-match truncation note preserves the main table.
The full machine inventory is not exported. Full F6 copies include the visible
record clues for the selected row. Review all artifacts before sharing them.

Explorer opens only on request and does not execute the selected file. Windows
may manage recent-item/system records separately. Removing the portable folder
does not remove opted-in preferences, saved references, exports or clipboard data.
Use a small synthetic example rather than personal disk dumps for support.
