# YerelDisk source

C# / .NET 10 / WPF for Windows x64. Use the SDK selected by the repository's
`global.json` and WindowsDesktop references. Run `Derle.cmd` or `Derle.ps1` here.
Output is `Derleme/YerelDisk.exe` plus DLL/JSON files. This developer build needs
.NET 10 Windows Desktop Runtime; the published self-contained package does not.
Normal source builds/tests use no external NuGet library packages.

- **Core:** no-follow/no-recall native metadata traversal, bounded repair and
  verification, physical/logical accounting, advisory immutable refresh indexes,
  selected-refresh planning/execution, and experimental review rules.
- **Application:** orchestration, view/query state, transactional replacement,
  provenance-aware reports, folder references/comparison, optional profile and
  reference preferences, and a bounded opt-in local registry provider. No WPF.
- **Wpf:** presentation, dialogs and explicit clipboard/Explorer/file actions.

Initial scan repair is bounded; the filesystem view is live and non-atomic.
Selected refresh holds native pins through query preparation and commit checks.
Outside metadata can remain older, so time provenance must remain visible.
The 64/128 MiB setting is an index budget, not a total-RAM cap. Top-K initial
reservation is bounded by observed names, the list limit and 32,768 entries.

[Build, tests, packaging](../docs/DEVELOPMENT.md) · [Refresh design](../docs/REFRESH.md)
· [Privacy](../docs/PRIVACY.md) · [Verification catalog](../Dogrulama/Guncel/README.md)
