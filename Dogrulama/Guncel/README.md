# Current verification

Verification requires Windows x64, the .NET 10 SDK, and the WindowsDesktop
reference assemblies. `Kaynak/Dogrula.cmd` runs the Normal group by default
with **866 checks**. Use `-Native` for the 117 checks that require additional
environment capabilities, or `-All` to select both groups. Use `-All -List`
to display the catalog without running it.

`Suites.psd1` defines the suites and minimum passing counts. The active test
sources are under `Tests` and are independent of old local reports or backups.
The suites use custom console test hosts. `dotnet test` does not run this catalog.

With `-PublishedProductDirectory`, verification matches the self-contained
package's source manifest against the current source and tests the package's
existing DLLs. The report records `ProductOrigin=PublishedPackage` in
`summary.json`. The default command builds a new product from source and
records `SourceBuild`. [Example](../../docs/DEVELOPMENT.md).

The Normal group covers application flow, queries and reports, the
classification corpus, scan scope and exclusions, disk usage distribution,
tables, F6 and keyboard controls, registry matches with fake and native shims,
and offscreen WPF checks. The registry tests avoid reading or writing the real
list of installed applications. The Native group uses its own fixtures for
hard links, junctions, ACLs, NTFS streams, and changing directory trees.
Its scope is limited to those fixtures and excludes full scans of personal
C: or D: drives.

By default, each run builds a fresh product under `Kosular` and verifies
test host DLL hashes. It keeps `summary.json` and `summary.csv`, source and
product SHA256 manifests, logs, and test PNGs. When no package is installed,
`InstalledPackageMatches=false` is an expected result. Installing a package
later leaves the existing run report as a record of that run.

- **PASS** requires a successful process, the expected completion message,
  the minimum passing count, and zero errors.
- **FAIL**, exit code 1, covers build errors, infrastructure issues, missing
  output, failed assertions, or changed source files.
- **SKIP**, exit code 2, means a separate environment probe found a missing
  capability or the test skipped a check.
- **Exit code 0** means every selected suite passed. Unselected groups are
  outside the result.

`Tests/Capabilities` checks native support separately so the report can distinguish
environment limitations from product defects. A failed ACL restoration is a
FAIL. Tests run without automatic elevation or SDK updates. The catalog has
limited coverage of physical DPI and input, cloud providers, race conditions,
and accuracy in real use.

The initial repository release came from a selected copy of the local 1.18.0
source. Personal reports, backups, and test output from the development history
stay local. New runs produce their own evidence under `Kosular` and in GitHub
Actions artifacts.
