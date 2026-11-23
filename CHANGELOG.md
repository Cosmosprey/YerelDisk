# Changelog

## 1.29.4 — 2026-11-23

This source/package update collects local changes since the last public release,
1.18.0. Local version increments between them were not separate GitHub releases.

### Corrections after source review

- Increase the comparison dialog's minimum height from 500 to 540 DIPs so
  compact layouts retain two table rows across Windows frame/font differences.
  Its default size and the existing layout assertion remain unchanged.

- Same-root, non-hard-linked duplicate file identities contribute once to count,
  logical size and top-K. The largest observation is retained as explicitly
  uncertain; this is not a claim about the current path or latest version.
- Cross-root movement removes stale index inputs as well as stale table entries.
  Unsafe observations remain qualified in both presentation and index metadata.
- Wholly unavailable scans cannot become references. Healthy roots in a partial
  multi-root scan remain eligible; failed review views explain missing trusted data.
- Remeasure from Settings first displays the old result table. Full-path search
  and review feedback cannot change the frozen replacement state.
- Reject case-insensitive duplicate/nested reference roots and impossible
  reliable parent/child size totals while preserving case-sensitive sibling paths.
- Known measurement-history limits bypass unnecessary selected scans and show
  their own fallback reason. Admission checks the old pool's minimum scratch
  cost. Details distinguish sub-second refresh times and older refreshed areas.

### Added

- Folder-level measurement references, compressed bounded storage, later-scan
  comparison and a recent-reference library.
- Reusable root/exclusion profiles, with explicit optional local remembering.
- A file-population view to distinguish many small files from a few large ones.
- Selected/open-folder refresh using a session-only 64/128 MiB metadata index;
  original-scope fallback reasons and transactional result replacement.
- Explicit refreshed/older/mixed measurement times in tables, details and CSV;
  mixed results cannot be treated as one fresh full reference.

### Changed and fixed

- Clear new-full-scan, remeasure-all and root-refresh actions and visible targets.
- More compact lists, smoother scrolling, and unknown sizes below measured values.
- A bounded initial top-K buffer reserve reduces temporary managed allocation
  in controlled comparisons. There is no general speedup or RAM-cap claim.
- Developer resource fixtures now follow the selected workloads. A many-files
  run no longer creates unused 32 GiB logical sparse files. Sparse-only runs
  omit the many-file tree, invalid filters fail before generation, and reused
  datasets must cover the requested cases. Summary counts use actual dataset records.
- Production-source Git attributes preserve exact input bytes so a fresh checkout
  can match the portable package's source manifest without line-ending conversion.
- Privacy documentation describes the actual saved reference fields: folder
  logical sizes and reliability, scope, measurement time and volume identifiers.

### Distribution and verification

- Independent expected-value accounting tests, adversarial path-prefix cases,
  resource-reservation guards and curated mutation examples. The latest
  local main-build and published-DLL runs each recorded 1,591 Normal + 195 Native
  = 1,786 passing checks across 47 suites, with no failures or skips.
  These are assertions, not 1,786 independent scenarios or a real-world accuracy score.
- Portable publishing targets Microsoft .NET 10.0.15 and SDK 10.0.404. Public
  documentation remains English; the application interface remains Turkish.

Reference/profile persistence is opt-in. Saved references, preference records and
exports can contain private paths. No networking or scanned-file cleanup commands
were added. See [privacy](docs/PRIVACY.md) and [release notes](docs/RELEASE-NOTES-1.29.4.md).

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
