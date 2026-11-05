[CmdletBinding()]
param([switch]$Native, [switch]$All, [switch]$List, [string]$PublishedProductDirectory)
$ErrorActionPreference = 'Stop'
$catalog = @((Import-PowerShellDataFile -LiteralPath (Join-Path $PSScriptRoot 'Suites.psd1')).Suites | ForEach-Object { [pscustomobject]$_ })
if ($Native -and $All) { throw '-Native ve -All birlikte kullanılmaz.' }
$selected = @($catalog | Where-Object { $All -or ($Native -and $_.Group -eq 'Native') -or (!$Native -and !$All -and $_.Group -eq 'Normal') })
if ($List) { $selected | Select-Object Name,Group,MinimumPass,@{Name='Capabilities';Expression={$_.Capabilities -join ', '}} | Format-Table -AutoSize; return }
$diskDir = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runsDir = Join-Path $PSScriptRoot 'Kosular'
foreach ($directory in @($diskDir, (Join-Path $diskDir 'Dogrulama'), $PSScriptRoot, $runsDir)) {
    if ((Test-Path -LiteralPath $directory) -and ((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Doğrulama dizini bağlantı olamaz: $directory" }
}
New-Item -ItemType Directory -Path $runsDir -Force | Out-Null
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$runDir = Join-Path $runsDir $runId
New-Item -ItemType Directory -Path $runDir | Out-Null
$productDir = if ($PublishedProductDirectory) { [IO.Path]::GetFullPath($PublishedProductDirectory) } else { Join-Path $runDir 'product' }
$rows = [Collections.Generic.List[object]]::new()
$exitCode = 0
$capabilities = $null
$productFiles = @('YerelDisk.exe','YerelDisk.dll','YerelDisk.deps.json','YerelDisk.runtimeconfig.json','YerelDisk.Core.dll','YerelDisk.Application.dll')
if (!$PublishedProductDirectory) { $productFiles += @('YerelDisk.Core.deps.json','YerelDisk.Application.deps.json') }
$installedMatch = $false
function Invoke-Dotnet([string[]]$Arguments, [string]$Log) {
    # Encode argv for CreateProcess, not for a shell. Works on Windows PowerShell 5.1 too.
    $encoded = foreach ($argument in $Arguments) {
        $escaped = [regex]::Replace($argument, '(\\*)"', '$1$1\"')
        $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
        '"' + $escaped + '"'
    }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName=(Get-Command dotnet -ErrorAction Stop).Source; $start.Arguments=$encoded -join ' '
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $process = New-Object Diagnostics.Process
    $process.StartInfo=$start
    try {
        if (!$process.Start()) { throw 'dotnet başlatılamadı.' }
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        [IO.File]::WriteAllText($Log, $stdout.GetAwaiter().GetResult()+[Environment]::NewLine+$stderr.GetAwaiter().GetResult(), (New-Object Text.UTF8Encoding($true)))
        return $process.ExitCode
    } finally { $process.Dispose() }
}
function Build-Test([string]$name) {
    $project = Join-Path $PSScriptRoot "Tests/$name/$name.csproj"
    $destination = Join-Path $runDir "build/$name"
    $buildExit=Invoke-Dotnet @('build',$project,'-c','Release','--configfile',(Join-Path $diskDir 'Kaynak/NuGet.Config'),'-p:NuGetAudit=false','-p:UseSharedCompilation=false',"-p:ProductDirectory=$productDir","-p:ValidationWorkDirectory=$runDir",'-m:1','-o',$destination) (Join-Path $runDir "build-$name.txt")
    if ($buildExit -ne 0) { throw "Derleme başarısız: $name. Günlük: $(Join-Path $runDir "build-$name.txt")" }
    return $destination
}
function Source-Hashes {
    @(Get-ChildItem -LiteralPath (Join-Path $diskDir 'Kaynak') -Recurse -File | Where-Object { $_.FullName -notmatch '\\(obj|bin|Derleme|\.build-home|\.packages)\\' -and $_.Extension -in '.cs','.csproj','.xaml','.manifest','.config' } | ForEach-Object {
        [pscustomobject]@{ Path=$_.FullName.Substring($diskDir.Length+1); SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    } | Sort-Object Path)
}
try {
    if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 10 SDK gerekli; çalışma zamanı tek başına testleri derleyemez.' }
    $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; $env:DOTNET_NOLOGO='1'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false'; $env:MSBuildEnableWorkloadResolver='false'
    $env:DOTNET_CLI_HOME=Join-Path $runDir 'build-home'; $env:NUGET_PACKAGES=Join-Path $runDir 'packages'
    $sourceBefore = Source-Hashes
    $sourceBefore | Export-Csv -LiteralPath (Join-Path $runDir 'source-SHA256SUMS.csv') -NoTypeInformation -Encoding UTF8
    Write-Output "Doğrulama: $runDir"
    if ($PublishedProductDirectory) {
        $publishedSources=@(Import-Csv -LiteralPath (Join-Path $productDir 'SOURCE-SHA256SUMS.csv'))
        if ($publishedSources.Count -ne $sourceBefore.Count -or (Compare-Object @($sourceBefore | ForEach-Object { $_.Path+':'+$_.SHA256 }) @($publishedSources | ForEach-Object { $_.Path+':'+$_.SHA256 }))) { throw 'Yayımlanan paketin kaynak manifesti mevcut kaynakla eşleşmiyor.' }
        $runtime=Get-Content -LiteralPath (Join-Path $productDir 'YerelDisk.runtimeconfig.json') -Raw | ConvertFrom-Json
        if (!$runtime.runtimeOptions.includedFrameworks -or $runtime.runtimeOptions.frameworks -or $runtime.runtimeOptions.framework) { throw 'Self-contained paket bekleniyordu.' }
        Write-Output 'Kaynak manifesti eşleşen portable paketin DLL dosyaları sınanıyor; paket yeniden derlenmez.'
    } else {
        Write-Output 'Güncel kaynak derleniyor; ana uygulama dosyaları değiştirilmez.'
        $buildExit=Invoke-Dotnet @('build',(Join-Path $diskDir 'Kaynak/Wpf/YerelDisk.Wpf.csproj'),'-c','Release','--configfile',(Join-Path $diskDir 'Kaynak/NuGet.Config'),'-p:NuGetAudit=false','-p:UseSharedCompilation=false','-m:1','-o',$productDir) (Join-Path $runDir 'build-product.txt')
        if ($buildExit -ne 0) { throw "Ürün derlenemedi: $(Join-Path $runDir 'build-product.txt')" }
    }
    $hashes = foreach ($name in $productFiles) { [pscustomobject]@{Path=$name; SHA256=(Get-FileHash -LiteralPath (Join-Path $productDir $name)).Hash} }
    $hashes | Export-Csv -LiteralPath (Join-Path $runDir 'product-SHA256SUMS.csv') -NoTypeInformation -Encoding UTF8
    $installedMatch = @($hashes | Where-Object { !(Test-Path -LiteralPath (Join-Path $diskDir $_.Path)) -or (Get-FileHash -LiteralPath (Join-Path $diskDir $_.Path)).Hash -ne $_.SHA256 }).Count -eq 0
    if (!$installedMatch) { Write-Output 'NOT: Test edilen kaynak derlemesi kurulu paketle aynı değil; sonuç kurulu EXE için sayılmaz.' }
    if (@($selected | Where-Object Group -eq 'Native').Count -gt 0) {
        $probeBuild = Build-Test 'Capabilities'
        $probeExit=Invoke-Dotnet @((Join-Path $probeBuild 'YerelDisk.Validation.Capabilities.dll'),(Join-Path $runDir 'capability-fixtures'),(Join-Path $runDir 'capabilities.json')) (Join-Path $runDir 'capabilities-console.txt')
        if ($probeExit -ne 0) { throw 'Native ortam kontrolü beklenmedik biçimde başarısız oldu; SKIP sayılmadı.' }
        $capabilities = Get-Content -LiteralPath (Join-Path $runDir 'capabilities.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    foreach ($suite in $selected) {
        $unavailable = @($suite.Capabilities | Where-Object {
            if ($null -eq $capabilities.$_) { throw "Ortam kontrolünde özellik eksik: $_" }
            !$capabilities.$_.Available
        })
        if ($unavailable.Count -gt 0) {
            $reason = ($unavailable | ForEach-Object { $_ + ': ' + $capabilities.$_.Reason }) -join ' | '
            $rows.Add([pscustomobject]@{Suite=$suite.Name; Group=$suite.Group; Status='SKIP'; Assertions=0; Reason=$reason})
            Write-Output "SKIP $($suite.Name): $reason"; if ($exitCode -eq 0) { $exitCode=2 }; continue
        }
        Write-Output "Çalıştırılıyor: $($suite.Name)"
        try {
            $testDir = Build-Test $suite.Name
            foreach ($name in @('YerelDisk.Core.dll','YerelDisk.Application.dll','YerelDisk.dll')) {
                $testDll = Join-Path $testDir $name
                if ((Test-Path -LiteralPath $testDll) -and (Get-FileHash -LiteralPath $testDll).Hash -ne (Get-FileHash -LiteralPath (Join-Path $productDir $name)).Hash) { throw "Eski/yanlış test referansı: $name" }
            }
            $outputDir = Join-Path $runDir ('results/' + $suite.Name)
            New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
            $logFile = Join-Path $outputDir 'results.txt'
            $testArguments = switch ($suite.ResultKind) {
                'Directory' { @($outputDir) }
                'CoreAndLog' { @((Join-Path $productDir 'YerelDisk.Core.dll'), $logFile) }
                'CoreLogAndFixtures' { @((Join-Path $productDir 'YerelDisk.Core.dll'), $logFile, (Join-Path $outputDir 'fixtures')) }
                default { throw "Bilinmeyen sonuç biçimi: $($suite.ResultKind)" }
            }
            $testExit=Invoke-Dotnet (@((Join-Path $testDir ($suite.Assembly+'.dll')))+@($testArguments)) (Join-Path $outputDir 'console.txt')
            if (!(Test-Path -LiteralPath $logFile)) { throw "Sonuç dosyası oluşmadı; çıkış kodu $testExit" }
            $lines = @(Get-Content -LiteralPath $logFile -Encoding UTF8)
            $passes = @($lines | Where-Object { $_ -match '^PASS\s' }).Count
            if ($testExit -ne 0 -or @($lines | Where-Object { $_ -match '^(FAIL|FINDING|ERROR)\b' }).Count -gt 0 -or $lines[-1] -notmatch $suite.Success -or $passes -lt $suite.MinimumPass) { throw "Test başarısız/eksik: çıkış=$testExit, PASS=$passes; $logFile" }
            if (@($lines | Where-Object { $_ -match '^SKIP\b' }).Count -gt 0) {
                $rows.Add([pscustomobject]@{Suite=$suite.Name; Group=$suite.Group; Status='SKIP'; Assertions=$passes; Reason='Paket içinde atlanan kontrol var; ham sonucu okuyun.'})
                if ($exitCode -eq 0) { $exitCode=2 }; Write-Output "SKIP $($suite.Name): paket içinde atlanan kontrol var."
            } else {
                $rows.Add([pscustomobject]@{Suite=$suite.Name; Group=$suite.Group; Status='PASS'; Assertions=$passes; Reason=''})
                Write-Output "PASS $($suite.Name): $passes kontrol"
            }
        } catch {
            $rows.Add([pscustomobject]@{Suite=$suite.Name; Group=$suite.Group; Status='FAIL'; Assertions=0; Reason=$_.Exception.Message})
            $exitCode=1; Write-Output "FAIL $($suite.Name): $($_.Exception.Message)"
        }
    }
    $sourceAfter = Source-Hashes
    if (Compare-Object ($sourceBefore | ForEach-Object { $_.Path+':'+$_.SHA256 }) ($sourceAfter | ForEach-Object { $_.Path+':'+$_.SHA256 })) { throw 'Test sırasında ürün kaynağı değişti; sonuç tutarlı kaynak sürümü için sayılmaz.' }
} catch {
    $rows.Add([pscustomobject]@{Suite='Infrastructure'; Group='Runner'; Status='FAIL'; Assertions=0; Reason=$_.Exception.Message})
    $exitCode=1; Write-Output "FAIL altyapı: $($_.Exception.Message)"
} finally {
    $summary = [pscustomobject]@{
        Run=$runId; ProductVersion=$(if (Test-Path -LiteralPath (Join-Path $productDir 'YerelDisk.exe')) { (Get-Item -LiteralPath (Join-Path $productDir 'YerelDisk.exe')).VersionInfo.ProductVersion } else { $null })
        InstalledPackageMatches=$installedMatch; ExitCode=$exitCode
        ProductOrigin=$(if ($PublishedProductDirectory) {'PublishedPackage'} else {'SourceBuild'})
        Selected=@($selected.Name); NotSelected=@($catalog.Name | Where-Object { $_ -notin $selected.Name })
        PassedAssertions=[int](($rows | Where-Object Status -eq 'PASS' | Measure-Object -Property Assertions -Sum).Sum)
        Results=$rows.ToArray()
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runDir 'summary.json') -Encoding UTF8
    $rows | Export-Csv -LiteralPath (Join-Path $runDir 'summary.csv') -NoTypeInformation -Encoding UTF8
    $runId | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'SON-KOSU.txt') -Encoding UTF8
    Write-Output "Bitti: $($summary.PassedAssertions) geçen kontrol; çıkış=$exitCode. Özet: $(Join-Path $runDir 'summary.json')"
    if (!$Native -and !$All) { Write-Output 'Native paket seçilmedi. Ek izin gerektiren kontroller için -Native veya -All kullanın.' }
}
exit $exitCode
