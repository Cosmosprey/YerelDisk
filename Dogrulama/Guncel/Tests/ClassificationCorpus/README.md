# Classification counterexamples

Version 1, November 2, 2026: 44 synthetic metadata scenarios for developer
verification. The normal YerelDisk application does not load these fixtures.

`Cases.json` is the reviewable record of expected outcomes. `Facts` describes
information known to the test author; it is neither evidence discovered by the
application nor an input to product methods. For example, the same Gradle path
must yield the same uncertain usage result in both active-project and abandoned-
project scenarios. This distinction prevents claims about unobservable facts.

## Fields and coverage

- `Id`, `Theme`, `Facts`: identity, evaluation group and fixture scenario.
- `Path`: synthetic user/locations; `$HOME`, `$LOCAL`, `$DATA` and `$WINDOWS`
  expand only inside the test harness. No real user path is used.
- `Rule`, `Kind`, `Risks`: expected purpose rule, decision condition and
  name/extension risk. A missing rule means no match, not an unnecessary-file label.
- `Files`, `Markers`: in-memory file/folder metadata. No content is created or
  read, and no real user disk is scanned. `Unverified` means uncertain file identity.
- `Source`: `final`, `retained`, `empty-final`. Final observations are independent
  of top-K retention; empty-final must not treat stale retained risk names as
  decisive evidence.
- `Excluded`, `Unverified`, `Partial`, `ScopeChanged`, `Cancelled`: scope and
  measurement conditions. An excluded name must not become positive risk evidence.
- `Consequence`, `Guidance`, `Signal`: consequence/check/warning meanings that
  must survive presentation. There are no golden labels derived automatically
  from current product output.
- `FutureRisk`: presentation resilience only. It adds an enum bit not produced
  by today's engine, rather than a real new detection capability.

Nine groups cover shared usage, uninstalled applications, mixed personal data,
protected locations, misleading names/paths, structural similarity, scope and
observation, culture/case, and future compatibility. Outputs are `matrix.csv`
(one scenario per row), `corpus.txt` (corpus hash and scope) and `results.txt`
(the 44 scenario results).

## Acceptance and limits

When adding cases, first separate observable data from facts known externally to
the test. Do not change expectations to fit current output; explain the reasoning.
Wrong purpose, lost personal-data risk, cleanup guidance in protected locations,
out-of-scope evidence and false certainty are separate failure classes. A new
filename remains a clue, not proof of actual format or content.

This corpus is not independently labelled real user data. Passing 44/44 does not
establish general accuracy, precision/recall or a safe-deletion percentage.
Moved/portable locations outside rule coverage can be represented explicitly;
failure to identify a known-purpose fixture can reflect a declared support limit.
Real user validation and new evidence sources require separate work.

Run through `Kaynak/Dogrula.cmd` or `Kaynak/Dogrula.ps1 -All` from the repository
root. Historical local audit fixtures are intentionally excluded from this repo.
