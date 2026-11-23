# Build, verification and packaging

Use Windows x64 and the .NET SDK selected by `global.json`: 10.0.404 or a later
patch in that feature band. The SDK needs WindowsDesktop reference assemblies.
The portable release includes .NET 10.0.15 so users need no SDK/runtime install.

## Developer build

```powershell
.\Kaynak\Derle.cmd
# Or
.\Kaynak\Derle.ps1
```

Output: `Kaynak/Derleme`. Keep the EXE, DLL and JSON files together. This developer
build needs .NET 10 Windows Desktop Runtime. Source builds/tests disable external
NuGet sources and fail if SDK/reference assemblies are unavailable. The CMD
launcher keeps errors visible and changes execution policy only for its process.

## Verification

```powershell
.\Kaynak\Dogrula.cmd
.\Kaynak\Dogrula.cmd -Native
.\Kaynak\Dogrula.cmd -All
.\Kaynak\Dogrula.cmd -All -List
```

Normal verification also uses small real fixtures. Native suites require local
NTFS/link/stream/ACL capabilities. Missing capabilities produce SKIP and exit 2;
product failures produce exit 1. Only the selected suites passing produces exit 0.
Fixtures belong to each run; personal drives and installed applications are not
used as the test corpus. Successful generated fixtures are moved to the Recycle
Bin when available; failed/skipped inputs are retained. There is no permanent
cleanup fallback. `-KeepFixtures` retains successful inputs too.

The runner validates the test catalog/README, product and test-source hashes,
actual test DLL dependencies, assertion records and success markers. See the
[verification guide](../Dogrulama/Guncel/README.md). Curated mutation experiments
are a separate PowerShell 7 command; they do not modify the source or installed
app. Performance measurements are also separate developer tools, not CI speed gates.

## Portable ZIP

```powershell
.\scripts\Publish.ps1
# A later build must use another output directory
.\scripts\Publish.ps1 -OutputRoot .\artifacts\candidate
```

This explicit developer action restores Microsoft's runtime packs from nuget.org.
Normal app use stays offline. Publishing is self-contained win-x64, with trimming,
single-file output and PDBs disabled. License/notices, source and file manifests
are included. Existing ZIPs are not overwritten. Keep SDK compatibility and the
bundled runtime security patch current when preparing a later release.

Test the **published DLLs** without rebuilding the package:

```powershell
$package = Get-Content .\artifacts\package.json -Raw | ConvertFrom-Json
.\Kaynak\Dogrula.ps1 -All -PublishedProductDirectory $package.Package
```

The runner matches all 80 current production source/config files against the
package's `SOURCE-SHA256SUMS.csv` and checks referenced application DLL hashes.
Checksums assume a trusted source and local build chain; they are not signatures
or provenance attestations. The test host needs an SDK. A hands-on test on a
clean machine without .NET remains separate from the published-DLL tests.

## Screenshots

`scripts/Screenshots.ps1` generates README images entirely from synthetic data.
Build first or supply `-ProductDirectory` pointing at an existing product.
Generated images go to `docs/screenshots`; developer build output goes to artifacts.

## CI and release

Windows CI publishes a portable package, then tests those DLLs. Native suites are
available through manual workflow input. CI artifacts expire and are not GitHub
Releases. A local run does not establish that an unrun GitHub workflow passed.

Before publishing `v1.29.4`, extract the final ZIP to an empty folder and check
startup, a small scan, F6, CSV and closing. Also check the first pushed CI result.
Upload **only** `YerelDisk-1.29.4-win-x64.zip` and `SHA256SUMS.txt` to the Release.
Keep the README preparation notice and the changelog's Unreleased marker until
publication is authorized. Remove the notice and add the actual release date
when the version becomes available; do not imply it was published during preparation.
Do not upload `package.json`, SDKs, build/test logs, private paths, local preferences,
reference exports or developer run directories. Keep `Kosular`, `Mutasyon-Kosular`,
`Olcum-Kosular`, caches and archives out of source commits. Publication is a
separate explicit step; the preparation scripts do not commit, push or create Releases.

## Layers

The package's source manifest hashes exact production-input bytes. Scoped
`.gitattributes` rules preserve those bytes under `Kaynak` on check-in and
checkout, including existing mixed line endings. Text diffs remain enabled;
hash verification does not silently normalize edited source. If you intentionally
change source bytes, regenerate the package before using published-DLL validation.

Core owns Windows metadata traversal, measurement, replay/accounting and rules.
Application owns orchestration, immutable report snapshots, views, profiles,
references and the optional bounded registry provider. WPF owns presentation and
explicit dialogs/clipboard/Explorer actions. Preserve identity/pin/no-follow
checks and atomic result replacement. [Refresh design](REFRESH.md).

No recognized location, missing record, old date or absent risk clue proves safe
deletion. Broader active-use/content inspection would require separate design
and privacy documentation. Prefer a small synthetic example for bug reports.
