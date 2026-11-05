# Source code

YerelDisk has three .NET 10 Windows projects in the order Core → Application → WPF.
The main project is `Wpf/YerelDisk.Wpf.csproj`. The solution file
`YerelDisk.slnx` is at the repository root.

Run `Derle.cmd` to build on Windows. The output goes to `Derleme` and requires
the .NET 10 Windows Desktop Runtime. GitHub Releases provides a ready-to-run
portable package with the runtime included. Run `Dogrula.cmd` for developer
verification.

[Main README](../README.md) · [Build, test, and packaging](../docs/DEVELOPMENT.md)
