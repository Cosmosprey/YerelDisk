$ErrorActionPreference = 'Stop'
$projectDirectory = $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:MSBuildEnableWorkloadResolver = 'false'
$env:DOTNET_CLI_HOME = Join-Path $projectDirectory '.build-home'
$env:NUGET_PACKAGES = Join-Path $projectDirectory '.packages'
if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 10 SDK (x64) gerekli. Çalıştırmak için Windows Desktop Runtime, derlemek için SDK gerekir.' }
$project = Join-Path $projectDirectory 'Wpf/YerelDisk.Wpf.csproj'
$destination = Join-Path $projectDirectory 'Derleme'
& dotnet build $project -c Release --configfile (Join-Path $projectDirectory 'NuGet.Config') -p:NuGetAudit=false -p:UseSharedCompilation=false -m:1 -o $destination
if ($LASTEXITCODE -ne 0) { throw 'Derleme başarısız. Yukarıdaki hata metnini kontrol edin. .NET 10 SDK ve WindowsDesktop referansları gerekli.' }
Write-Output "Hazır: $destination\YerelDisk.exe (DLL ve JSON dosyalarıyla birlikte tutun.)"
