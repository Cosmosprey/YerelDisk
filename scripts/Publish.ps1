[CmdletBinding()]
param([string]$OutputRoot, [string]$RuntimeVersion = '10.0.14')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$OutputRoot) { $OutputRoot = Join-Path $repo 'artifacts' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (!$OutputRoot.StartsWith((Join-Path $repo 'artifacts') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and $OutputRoot -ne (Join-Path $repo 'artifacts')) { throw 'OutputRoot must be artifacts or a child directory of artifacts.' }
if ($RuntimeVersion -notmatch '^10\.0\.\d+$') { throw 'RuntimeVersion must be a stable .NET 10 patch version.' }
$project = Join-Path $repo 'Kaynak/Wpf/YerelDisk.Wpf.csproj'
[xml]$xml = Get-Content -LiteralPath $project -Raw
$version = [string]$xml.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid application version.' }
$name = "YerelDisk-$version-win-x64"
$archivePath = Join-Path $OutputRoot ($name + '.zip')
if (Test-Path -LiteralPath $archivePath) { throw 'ZIP already exists; select another empty OutputRoot. Existing files are never deleted.' }
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$work = Join-Path $OutputRoot ('build-' + [Guid]::NewGuid().ToString('N').Substring(0, 10))
$package = Join-Path $work $name
New-Item -ItemType Directory -Path $work -Force | Out-Null
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; $env:DOTNET_NOLOGO='1'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'; $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false'; $env:MSBuildEnableWorkloadResolver='false'
$env:DOTNET_CLI_HOME=Join-Path $repo 'artifacts/.cli-home'
$env:NUGET_PACKAGES=Join-Path $repo 'artifacts/.packages'
$config=Join-Path $repo 'Kaynak/NuGet.Config'
$sourceBefore=@(Get-ChildItem -LiteralPath (Join-Path $repo 'Kaynak') -Recurse -File | Where-Object { $_.FullName -notmatch '\\(obj|bin|Derleme|\.build-home|\.packages)\\' -and $_.Extension -in '.cs','.csproj','.xaml','.manifest','.config' } | ForEach-Object { [pscustomobject]@{Path=$_.FullName.Substring($repo.Length+1);SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash} } | Sort-Object Path)

# This explicit developer step alone permits downloading Microsoft runtime packs.
& dotnet restore $project --runtime win-x64 --source https://api.nuget.org/v3/index.json --configfile $config "-p:RuntimeFrameworkVersion=$RuntimeVersion" -p:SelfContained=true -p:NuGetAudit=false *> (Join-Path $work 'restore.txt')
if ($LASTEXITCODE -ne 0) { throw "Runtime restore failed; inspect $work/restore.txt" }
& dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore "-p:RuntimeFrameworkVersion=$RuntimeVersion" -p:PublishTrimmed=false -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -p:UseSharedCompilation=false -m:1 -o $package *> (Join-Path $work 'publish.txt')
if ($LASTEXITCODE -ne 0) { throw "Publish failed; inspect $work/publish.txt" }
foreach ($required in @('YerelDisk.exe','YerelDisk.dll','YerelDisk.Core.dll','YerelDisk.Application.dll','coreclr.dll','hostfxr.dll','hostpolicy.dll','PresentationFramework.dll','wpfgfx_cor3.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $package $required))) { throw "Self-contained file missing: $required" }
}
$runtime = Get-Content -LiteralPath (Join-Path $package 'YerelDisk.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtime.runtimeOptions.frameworks -or $runtime.runtimeOptions.framework -or !$runtime.runtimeOptions.includedFrameworks) { throw 'Unexpected framework-dependent runtime configuration.' }
if (@(Get-ChildItem -LiteralPath $package -Recurse -Filter '*.pdb').Count -ne 0) { throw 'Debug symbols must not ship.' }

Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $package 'LICENSE-YerelDisk.txt')
Copy-Item -LiteralPath (Join-Path $repo 'docs/PORTABLE-OKU.txt') -Destination (Join-Path $package 'OKU-BENI.txt')
$licenses = Join-Path $package 'Lisanslar'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
foreach ($id in @('microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64')) {
    $pack = Join-Path $env:NUGET_PACKAGES "$id/$RuntimeVersion"
    $notices = @(Get-ChildItem -LiteralPath $pack -Recurse -File | Where-Object { [regex]::IsMatch($_.Name, 'license|third.?party|notices', [Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [Text.RegularExpressions.RegexOptions]::CultureInvariant) -and $_.Extension -in '.txt','.md','' })
    if (!$notices.Count) { throw "Runtime license notices missing: $id" }
    foreach ($file in $notices) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $licenses ($id + '-' + $file.Name)) }
}
@("Application: $version", "Runtime: $RuntimeVersion", 'Platform: win-x64', 'Self-contained: true', 'Trimming: false', 'Single-file: false') | Set-Content -LiteralPath (Join-Path $package 'VERSION.txt') -Encoding UTF8
foreach ($source in $sourceBefore) { if ((Get-FileHash -LiteralPath (Join-Path $repo $source.Path)).Hash -ne $source.SHA256) { throw 'Source changed during publication.' } }
$sourceBefore | Export-Csv -LiteralPath (Join-Path $package 'SOURCE-SHA256SUMS.csv') -NoTypeInformation -Encoding UTF8
$manifest = @(Get-ChildItem -LiteralPath $package -Recurse -File | Sort-Object FullName | ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $_.FullName.Substring($package.Length+1).Replace('\','/') })
$manifest | Set-Content -LiteralPath (Join-Path $package 'FILES-SHA256SUMS.txt') -Encoding UTF8
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in (Get-ChildItem -LiteralPath $package -Recurse -File | Sort-Object FullName)) {
        # .NET Framework/Windows PowerShell may use backslashes in CreateFromDirectory.
        # Explicit entry names keep the ZIP format stable across supported shells.
        $relative = $file.FullName.Substring($package.Length+1).Replace([char]92, [char]47)
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, "$name/$relative", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zip.Dispose() }
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
# LF matters as well as omitting the BOM for Unix sha256sum compatibility.
[IO.File]::WriteAllText((Join-Path $OutputRoot 'SHA256SUMS.txt'), "$hash  $name.zip" + [char]10, [Text.Encoding]::ASCII)
[pscustomobject]@{Version=$version;Runtime=$RuntimeVersion;Platform='win-x64';Package=$package;Archive=$archivePath;SHA256=$hash;Files=@(Get-ChildItem -LiteralPath $package -Recurse -File).Count} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputRoot 'package.json') -Encoding UTF8
Write-Output "Portable package ready: $archivePath"
Write-Output "SHA256: $hash"
