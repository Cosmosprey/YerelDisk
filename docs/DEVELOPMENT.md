# Building, testing, and packaging

Use Windows x64, the .NET SDK 10.0.403 compatible with `global.json`, and the
WindowsDesktop reference assemblies. Later SDK patches in the same feature
band are supported. The ready-to-run package includes what users need to run it
without installing the SDK.

## Local developer build

```powershell
.\Kaynak\Derle.cmd
# Or
.\Kaynak\Derle.ps1
```

The output goes to `Kaynak/Derleme` and requires the .NET 10 Windows Desktop
Runtime. The CMD launcher keeps its window open and sets the PowerShell
execution policy for its own process only. `NuGet.Config` disables external
package sources. The build reports an error if the SDK or reference assemblies
are missing.

## Tests

```powershell
.\Kaynak\Dogrula.cmd
.\Kaynak\Dogrula.cmd -Native
.\Kaynak\Dogrula.cmd -All
```

The tests are separate from normal app use and run without access to a personal
disk or list of installed applications. Native tests make changes to ACLs,
junctions, hard links, and streams only in their own fixtures. Missing
environment capabilities are reported separately as SKIP. [Current verification guide](../Dogrulama/Guncel/README.md).

## Portable ZIP

```powershell
.\scripts\Publish.ps1
# Use a separate empty output directory for a second run
.\scripts\Publish.ps1 -OutputRoot .\artifacts\ikinci
```

When SDK runtime packages are missing, the explicit publish command downloads
Microsoft's packages from nuget.org. Normal builds and tests use their
existing source configuration. The package is a self-contained win-x64 folder
with trimming and single-file publishing disabled. Debug PDB files stay out of
the distribution. The package uses runtime 10.0.14. Future runtime security
updates require checking SDK compatibility and publishing a new package.
The publish script preserves previous output and refuses to overwrite an
existing ZIP.

Use the same tests against the generated package's DLLs without rebuilding them.

```powershell
$package = Get-Content .\artifacts\package.json -Raw | ConvertFrom-Json
.\Kaynak\Dogrula.ps1 -All -PublishedProductDirectory $package.Package
```

Verification first matches the package's `SOURCE-SHA256SUMS.csv` against the
55 current source files. It checks the DLL hashes referenced by the test hosts
against the package. The manifest provides checksums and assumes a trusted
source and local build chain. It provides neither a signature nor an
attestation. The test host uses the SDK and runtime. A hands-on test on a
machine without .NET is a separate check.

`scripts/Screenshots.ps1` can regenerate the README screenshots entirely from
synthetic metadata. Run a developer build first or point `-ProductDirectory`
at an existing product folder.

The output goes under `artifacts` and includes the ZIP, `SHA256SUMS.txt`, an
internal file manifest, and .NET license notices. Built EXE and DLL files are
distributed as GitHub Release assets and stay out of source commits. SHA256
checks file integrity and provides no digital signature.

GitHub Actions runs the Normal tests on Windows and builds the portable
package. The Native tests can be triggered manually. Raw runs are kept as CI
artifacts, with FAIL and SKIP reported separately. Passing on a GitHub VM
covers that environment. Other Windows versions and physical hardware require
separate verification.

## Publishing a release

CI artifacts are temporary build outputs. Publish the downloadable package in
GitHub Releases. Check the Windows CI result after the first push. Extract the
ZIP to an empty folder and manually test startup, a small folder scan, F6, CSV
export, and closing the app.

For the `v1.18.0` release, upload only `YerelDisk-1.18.0-win-x64.zip` and
`SHA256SUMS.txt`. Keep `package.json`, build logs, and caches local because
they may contain local build paths. The latest Release link and versioned
documentation links become available once the tag and Release exist.

## Layers and data sources

Core handles Windows metadata scanning without following links, measurement
models, and review rules. Application handles views, queries, reports, and
the optional, user-initiated application record provider. It is independent
of WPF. WPF handles XAML and user actions. The `Inventory/` module includes a
budgeted registry reader limited to four sources and four application values,
an index of observed paths, and the scan, generation, and cancellation lifecycle.

Results are session metadata. Inspecting contents, processes, or active use
would change the model's scope and require separate design and privacy
documentation. Preserve review safeguards and treat a missing match as
inconclusive. Prefer a small synthetic scenario for bug reports involving
sensitive data.
