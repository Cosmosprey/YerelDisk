> Historical preparation draft, never released. Superseded by [1.29.4](RELEASE-NOTES-1.29.4.md).

# YerelDisk 1.29.3 — release preparation

A local Windows disk-space analyzer with a Turkish WPF interface.

## Download

Choose **YerelDisk-1.29.3-win-x64.zip**, extract the entire folder and open
`YerelDisk.exe`. The package includes .NET 10.0.15; no SDK or separate runtime
installation is required. The verified target is Windows 11 x64. The app is
unsigned. Keep all extracted files and license notices together.

## Since the previous public release

Save compressed folder references and compare later measurements; reuse scan
profiles; optionally remember profile/reference preferences locally. Refresh a
selected/open folder with a session-only metadata index. The root action measures
the full original scope, and a fast-path fallback explains its reason. Older and
refreshed observations remain visibly qualified, including in exported CSV.

The release also improves list density/scrolling, unknown-size ordering,
file-population inspection, and bounded temporary buffer allocation. Controlled
memory comparisons are not a universal speed guarantee.

## Data and limits

No app networking, telemetry, accounts or automatic updates. Normal scans read
metadata. Explicit reference opening and Remember options read/write the named
local files described in [privacy](PRIVACY.md). Preferences, references, reports
and full detail copies may contain private paths. Review candidates are clues,
not proof of non-use or safe deletion. There are no scanned-file cleanup commands.

[Usage](USAGE.md) · [Refresh scopes](REFRESH.md) · [Privacy](PRIVACY.md)

## Verification

Both the local main build and the published application DLLs passed 1,573 Normal
and 189 Native checks: 1,762 assertions across 47 suites, with no failures or skips.
All 80 production source files match the local main source. Package contents and
checksums were checked separately. Two independent high-effort agent audits
reviewed distribution and privacy/security. They verified the archive contents,
clean source builds and targeted fixes; a separate 12-case malformed-reference
probe produced the expected acceptance/rejection results. The audits did not
independently rerun the full catalog or establish comprehensive coverage.
Curated mutation experiments, synthetic
UI images and controlled filesystem fixtures do not cover every filesystem race,
hardware setup, cloud provider, DPI configuration or real-world classification.
GitHub CI and a clean machine without .NET are separate verification environments.

Pre-release fixes keep exact production source bytes intact across Git checkouts,
limit developer benchmark fixtures to selected workloads, and correct the privacy
description of saved reference fields. No production scanning code changed in
this GitHub preparation. These are separate from the app changes since 1.18.0.

The final ZIP was extracted for a smoke flow, but launching that extracted EXE
through the desktop automation tool was blocked by its application policy.
Automated WPF tests and synthetic screenshot generation passed; opening the
extracted EXE, scanning a small folder, pressing F6, exporting CSV and closing
the window remain a manual pre-release check. GitHub CI has not run for this update.

Publish this version only after that manual smoke flow and the first CI run are
complete. Release assets are the ZIP and `SHA256SUMS.txt`; developer metadata,
SDK caches and raw local logs are not release assets. A SHA256 checksum provides
integrity checking, not a publisher signature.
