# YerelDisk 1.18.0 — first GitHub release

A local disk space analyzer for Windows x64. The interface is in Turkish, and
the initial release targets Windows 11 x64. It supports multiple scan folders,
exclusions, large files, disk usage distribution, experimental review
candidates, and explanations in F6 and CSV.

**Download and run** by extracting the entire `YerelDisk-1.18.0-win-x64.zip`
and opening `YerelDisk.exe`. The package includes the .NET 10.0.14 runtime.
You can run it without the SDK or a separate runtime installation. Keep the
DLLs beside the EXE. The Source code ZIP is for developers building the app.
Use `SHA256SUMS.txt` to check the download's integrity.

Windows may display an unknown publisher warning because the app is unsigned.
SHA256 provides an integrity check and no publisher signature. You can leave
your security software enabled.

## Privacy and limits

The app has no networking, telemetry, automatic updates, or automatic deletion.
Normal scans inspect metadata without reading file contents. The optional
application record inspection reads records locally when requested. CSV
inclusion is a separate option that starts off. [Privacy and data scope](https://github.com/Cosmosprey/YerelDisk/blob/v1.18.0/docs/PRIVACY.md).

Experimental candidates provide clues about what data may be used for. Current
use, whether the data is needed, and safe deletion remain unverified.
[Measurement and compatibility limits](https://github.com/Cosmosprey/YerelDisk/blob/v1.18.0/docs/USAGE.md).

The code and technical design received substantial AI assistance.
[Development process and AI contribution](https://github.com/Cosmosprey/YerelDisk/blob/v1.18.0/README.md#development-process-and-ai-contribution).

## Verification scope

The local source copy and local portable package DLLs ran the catalog in
separate passes. Each passed **866 Normal and 117 Native checks**, for 983
passes per run, with zero failures or skips in the selected suites. Both runs
used the same checks. The total of 1966 checks includes repetitions, and
independent review is separate. The package file manifest and source hashes
were verified. Packaging was also exercised in Windows PowerShell 5.1.

GitHub Windows CI builds the downloadable release package and tests its DLLs
with **866 Normal checks**. The CI run for this first release covers the
Normal group. The 117 local Native checks provide separate evidence. These
checks cover specific environments and cases, with other Windows versions,
hardware, and deletion decisions requiring separate verification.

The bundled-runtime app was launched with a separate, empty `DOTNET_ROOT`
and loaded its own hostfxr, hostpolicy, coreclr, and native WPF files. That
check exercised runtime loading. A full usage test on a separate Windows
machine without .NET and a hands-on check of clicks, scanning, and CSV export
are still pending. Offscreen WPF regression tests cover a separate set of
interactions.

The scan and classification code comes from the local 1.18.0 application.
The GitHub release adds portable packaging, documentation, sample screenshots,
and CI for distribution.
