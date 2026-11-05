# Changelog

## 1.18.0

First GitHub release of YerelDisk. It uses the scan and classification code
from the local 1.18.0 application.

- Portable Windows x64 ZIP with the .NET runtime, SHA256 checksum, and license notices.
- WPF interface, multiple scan folders, exclusions, large files, and disk usage distribution.
- Experimental review candidates, other large areas, and explanations available through F6.
- Optional local installation records and path matches, with CSV inclusion off by default.
- A clean source and test repository, sample screenshots, and Windows CI.
- An earlier local verification pass completed 866 Normal checks with the
  existing Core. The 117 Native checks were outside that pass. The release notes
  report GitHub distribution checks separately from the earlier results.
- GitHub preparation passed 983 checks on a clean source copy and 983 checks
  in a separate run against the portable package DLLs (866 Normal and 117 Native).
  GitHub Windows CI also built the package and passed 866 Normal checks against
  its DLLs. The Native group was outside this first CI release run. Repeated
  checks count as repetitions of the same catalog, with independent review
  recorded separately.

Review candidates remain experimental. Current use and safe deletion are
unverified. [Usage guide and known measurement limits](docs/USAGE.md).
