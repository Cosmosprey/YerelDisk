# YerelDisk 1.29.4

A local disk-space analyzer for Windows 11 x64, with a Turkish WPF interface.
This update collects local work since the public 1.18.0 release.

## Download

Download YerelDisk-1.29.4-win-x64.zip, extract the entire folder
and open YerelDisk.exe. The package includes .NET 10.0.15; a separate SDK or
runtime installation is not required. The application is unsigned.

## Changes

Compressed folder references and later-scan comparisons, optional remembered
profiles/reference preferences, a session-only selected-refresh index, explicit
mixed measurement times, clearer scan actions, compact lists and smoother scroll.
See [changelog](../CHANGELOG.md), [usage](USAGE.md) and [refresh scopes](REFRESH.md).

Source-review fixes count uncertain moved identities once, preserve the largest
uncertain observation without a claim about its current path, and keep stale
observations out of refresh index inputs. Wholly unusable results cannot become
references; healthy areas in partial scans remain available. Frozen search state,
reference root/topology validation and known refresh admission limits were fixed.

## Privacy

Normal scans read metadata. There is no app networking, telemetry, account or
automatic update code. Saving/opening references, CSV, clipboard and Explorer
are explicit actions. Optional Remember preferences are local. References and
exports can contain private paths; references are not encrypted. Review clues
do not prove safe deletion or current use. [Data scope](PRIVACY.md).

## Verification and publication status

Local source and published-DLL regression runs each passed 1,591 Normal and
195 Native assertions (1,786 across 47 suites), with no failures or skips. The catalog includes accounting, refresh, reference,
synthetic WPF and capability-dependent native cases. Counts describe assertions,
not independent scenarios or a real-world accuracy percentage.

The earlier package/privacy audits reviewed 1.29.3. Gemini then reported source
issues; 1.29.4 contains the corrections and new counterexamples. A focused external
recheck was reported clean by the project owner (Gemini 4 Argon). Earlier audit results are not presented as
independent validation of every 1.29.4 change.

The extracted executable's hands-on startup/small scan/F6/CSV/close check, a clean
Windows machine without .NET, and the first GitHub CI run remain separate checks.
Desktop automation previously blocked EXE launch by application policy; the block
was not bypassed. Offscreen WPF tests do not replace those environment checks.

For this release's CI and hands-on verification status, see the GitHub Release
page. [Windows CI](https://github.com/Cosmosprey/YerelDisk/actions/workflows/ci.yml). Release assets are only the final ZIP
and SHA256SUMS.txt. Do not upload SDKs, caches, developer package.json, raw local
logs, preferences, saved references or private audit evidence. SHA256 provides
integrity checking rather than a publisher signature.
