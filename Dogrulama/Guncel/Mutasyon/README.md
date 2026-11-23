# Curated mutation experiments

This developer tool tests whether selected assertions reject known semantic
faults. It changes only owned source copies. PowerShell 7 and the local SDK are
required; no external library packages are added. Normal verification retains
Windows PowerShell 5.1 support. A source-only repository needs no root-level EXE.

```powershell
& .\Dogrulama\Guncel\Mutasyon\Run.ps1
& .\Dogrulama\Guncel\Mutasyon\Run.ps1 -Only M01,M03,M10
& .\Dogrulama\Guncel\Mutasyon\Run.ps1 -KeepWork
```

The unchanged source baseline must first build and pass the mapped suites.
Each fault has an explicit file, replacement, exact occurrence count and expected
assertion. Source drift stops the run. Isolated MSBuild roots avoid inheriting
the test directory's project defaults. Test dependencies must match that mutant's
DLLs. Original production/test sources and any present root app files are hashed.

- **KILLED:** product built; the test exited 1 with the catalog's exact expected
  assertion. A different exception or arbitrary nonzero exit is insufficient.
- **SURVIVED:** the mutated product passed its mapped suite. This exposes a test
  gap, not proof that the unchanged app contains that fault.
- **INVALID, TIMEOUT, INCOMPLETE, SKIPPED, UNEXPECTED_FAILURE, ERROR:** not successful
  detection. Baseline failure also invalidates the experiment.
- Exit 0 requires every selected mutation to be KILLED. Process timeout defaults
  to 120 seconds and terminates only the tool's own child process tree.

Eleven examples cover over-reserving the full list limit, duplicate alias allocation,
top-K direction, sibling-prefix scope, shared outside identity, late cancellation,
lease validation, edited versus original scope, OOM fallback and actual review-cell
time provenance. Each uses one mapped suite, not all suites per mutation. These
examples are not a general mutation coverage score or proof against all faults.

Evidence stays in `../Mutasyon-Kosular/<run>`: catalog/source/test/product hashes,
logs and summary JSON/CSV. Expected-failure inputs may be recycled after evidence
is recorded. On complete success, working source/build copies under own-fixtures
are also recycled unless KeepWork is enabled. Surviving/unexpected inputs remain
for diagnosis. No permanent cleanup fallback. Run output is not release material.

M11 reintroduces counting for a discarded moved observation. The independent accounting assertion must reject it; a build failure is not a killed mutant.
