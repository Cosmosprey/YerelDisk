#requires -Version 7.0
[CmdletBinding()]
param([string[]]$Only, [switch]$KeepWork, [ValidateRange(10,600)][int]$TimeoutSeconds=120)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Evidence.ps1')
. (Join-Path $PSScriptRoot '../FixtureCleanup.ps1')
$validation=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$disk=[IO.Path]::GetFullPath((Join-Path $validation '../..'))
& (Join-Path $PSScriptRoot 'Test-Altyapi.ps1')
$catalog=@((Import-PowerShellDataFile (Join-Path $PSScriptRoot 'Catalog.psd1')).Mutants)
if (@($catalog.Id | Group-Object | Where-Object Count -ne 1).Count) { throw 'Duplicate mutation IDs' }
if ($Only) {
    foreach ($id in $Only) { if ($id -notin $catalog.Id) { throw "Unknown mutation: $id" } }
    $catalog=@($catalog | Where-Object Id -in $Only)
}
$suites=@((Import-PowerShellDataFile (Join-Path $validation 'Suites.psd1')).Suites)
$run=Join-Path $validation ('Mutasyon-Kosular/'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $run | Out-Null
$work=Join-Path $run 'own-fixtures'
New-Item -ItemType Directory -Path $work | Out-Null
$rows=[Collections.Generic.List[object]]::new(); $exitCode=1
function Get-ProductionHashes {
@(Get-ChildItem -LiteralPath (Join-Path $disk 'Kaynak') -File -Recurse | Where-Object { $_.FullName -notmatch '\\(obj|bin|Derleme|\.build-home|\.packages)\\' -and $_.Extension -in '.cs','.csproj','.xaml','.manifest','.config' } | ForEach-Object {
    [pscustomobject]@{Path=$_.FullName.Substring($disk.Length+1);SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}
} | Sort-Object Path)
}
$source=Get-ProductionHashes
$installed=@('YerelDisk.exe','YerelDisk.dll','YerelDisk.deps.json','YerelDisk.runtimeconfig.json','YerelDisk.Core.dll','YerelDisk.Core.deps.json','YerelDisk.Application.dll','YerelDisk.Application.deps.json') | Where-Object { Test-Path -LiteralPath (Join-Path $disk $_) } | ForEach-Object { [pscustomobject]@{Path=$_;SHA256=(Get-FileHash -LiteralPath (Join-Path $disk $_)).Hash} }
$source | Export-Csv (Join-Path $run 'source-SHA256.csv') -NoTypeInformation -Encoding UTF8
$installed = @($installed | Where-Object { $null -ne $_ })
$installed | Export-Csv (Join-Path $run 'installed-SHA256.csv') -NoTypeInformation -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Catalog.psd1') -Destination (Join-Path $run 'catalog-snapshot.psd1')
$harness=Get-ValidationHarnessHashes $validation
$harness | Export-Csv (Join-Path $run 'harness-SHA256.csv') -NoTypeInformation -Encoding UTF8
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1';$env:DOTNET_CLI_USE_MSBUILD_SERVER='0';$env:MSBuildEnableWorkloadResolver='false'
$env:DOTNET_NOLOGO='1';$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1';$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:DOTNET_CLI_HOME=Join-Path $work 'build-home';$env:NUGET_PACKAGES=Join-Path $work 'packages'
function Invoke-OwnedProcess([string[]]$Arguments,[string]$Log) {
    $start=[Diagnostics.ProcessStartInfo]::new((Get-Command dotnet -ErrorAction Stop).Source)
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach($argument in $Arguments){$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::new();$process.StartInfo=$start
    try {
        if(!$process.Start()){throw 'Could not start owned dotnet process'}
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        $timeout=!$process.WaitForExit($TimeoutSeconds*1000)
        if($timeout){$process.Kill($true);$process.WaitForExit()}
        [IO.File]::WriteAllText($Log,$stdout.GetAwaiter().GetResult()+[Environment]::NewLine+$stderr.GetAwaiter().GetResult(),[Text.UTF8Encoding]::new($true))
        return [pscustomobject]@{ExitCode=$process.ExitCode;TimedOut=$timeout}
    } finally {$process.Dispose()}
}
function New-Shadow([string]$Id) {
    $shadow=Join-Path $work $Id;New-Item -ItemType Directory -Path $shadow | Out-Null
    # Stop MSBuild from inheriting the developer-test Directory.Build.props above this shadow.
    [IO.File]::WriteAllText((Join-Path $shadow 'Directory.Build.props'),'<Project />')
    [IO.File]::WriteAllText((Join-Path $shadow 'Directory.Build.targets'),'<Project />')
    foreach($item in $source){$target=Join-Path $shadow $item.Path;New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force|Out-Null;Copy-Item -LiteralPath (Join-Path $disk $item.Path) -Destination $target
        if((Get-FileHash -LiteralPath $target).Hash -ne $item.SHA256){throw 'Shadow source mismatch'}}
    return $shadow
}
function Build-Product([string]$Shadow,[string]$Product,[bool]$Wpf,[string]$Log) {
    $project=if($Wpf){'Wpf/YerelDisk.Wpf.csproj'}else{'Application/YerelDisk.Application.csproj'}
    Invoke-OwnedProcess @('build',(Join-Path $Shadow ('Kaynak/'+$project)),'-c','Release','--configfile',(Join-Path $Shadow 'Kaynak/NuGet.Config'),'-p:NuGetAudit=false','-p:UseSharedCompilation=false','-m:1','-o',$Product) $Log
}
function Run-Suite([string]$Case,[string]$Product,[object]$Suite) {
    $logRoot=Join-Path $run $Case;New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
    $destination=Join-Path $work ($Case+'/tests/'+$Suite.Name)
    $build=Invoke-OwnedProcess @('build',(Join-Path $validation ('Tests/'+$Suite.Name+'/'+$Suite.Name+'.csproj')),'-c','Release','--configfile',(Join-Path $disk 'Kaynak/NuGet.Config'),'-p:NuGetAudit=false','-p:UseSharedCompilation=false',"-p:ValidationWorkDirectory=$(Join-Path $work $Case)","-p:ProductDirectory=$Product",'-m:1','-o',$destination) (Join-Path $logRoot ('build-'+$Suite.Name+'.txt'))
    if($build.TimedOut -or $build.ExitCode -ne 0){return [pscustomobject]@{BuildExit=$build.ExitCode;TestExit=-1;TimedOut=$build.TimedOut;Lines=@()}}
    foreach($name in @('YerelDisk.Core.dll','YerelDisk.Application.dll','YerelDisk.dll')) {
        $reference=Join-Path $destination $name
        if((Test-Path -LiteralPath $reference) -and (Get-FileHash -LiteralPath $reference).Hash -ne (Get-FileHash -LiteralPath (Join-Path $Product $name)).Hash){throw 'Wrong mutation test dependency'}
    }
    $results=Join-Path $logRoot ('results/'+$Suite.Name);New-Item -ItemType Directory -Path $results -Force | Out-Null
    # The test-created files are bounded by this run, separate from the application and user data.
    $test=Invoke-OwnedProcess @((Join-Path $destination ($Suite.Assembly+'.dll')),$results) (Join-Path $logRoot ('console-'+$Suite.Name+'.txt'))
    $file=Join-Path $results 'results.txt';$lines=if(Test-Path -LiteralPath $file){@(Get-Content -LiteralPath $file -Encoding UTF8)}else{@()}
    [pscustomobject]@{BuildExit=0;TestExit=$test.ExitCode;TimedOut=$test.TimedOut;Lines=$lines}
}
try {
    Write-Output "Mutation evidence: $run"
    $baseline=New-Shadow 'Baseline';$baselineProduct=Join-Path $baseline 'product'
    $build=Build-Product $baseline $baselineProduct $true (Join-Path $run 'build-baseline.txt')
    if($build.TimedOut -or $build.ExitCode -ne 0){throw 'Unmodified baseline product did not build'}
    foreach($name in @($catalog.Suite | Select-Object -Unique)) {
        $suite=@($suites | Where-Object Name -eq $name);if($suite.Count -ne 1 -or $suite[0].ResultKind -ne 'Directory'){throw 'Invalid mapped suite'};$suite=$suite[0]
        $e=Run-Suite 'Baseline' $baselineProduct $suite
        if((Get-MutationVerdict -BuildExit $e.BuildExit -TestExit $e.TestExit -TimedOut $e.TimedOut -Lines $e.Lines -Success $suite.Success -MinimumPass $suite.MinimumPass -ExpectedFailure '') -ne 'SURVIVED'){throw "Baseline suite did not pass: $name"}
        Write-Output "PASS baseline: $name"
        Move-ValidationFixturesToRecycleBin (Join-Path $run 'Baseline') $name 'PASS'
    }
    foreach($mutation in $catalog) {
        Write-Output "Testing $($mutation.Id): $($mutation.Description)"
        $shadow=New-Shadow $mutation.Id;$product=Join-Path $shadow 'product';$target=Join-Path $shadow ('Kaynak/'+$mutation.File)
        $text=[IO.File]::ReadAllText($target);$count=[regex]::Matches($text,[regex]::Escape($mutation.From)).Count
        if($count -ne $mutation.Count){throw "Patch drift: $($mutation.Id), expected $($mutation.Count), found $count"}
        [IO.File]::WriteAllText($target,$text.Replace($mutation.From,$mutation.To),[Text.UTF8Encoding]::new($true))
        $logRoot=Join-Path $run $mutation.Id;New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
        $build=Build-Product $shadow $product $mutation.Wpf (Join-Path $logRoot 'build-product.txt')
        $suite=@($suites | Where-Object Name -eq $mutation.Suite)[0]
        $e=if($build.TimedOut -or $build.ExitCode -ne 0){[pscustomobject]@{BuildExit=$build.ExitCode;TestExit=-1;TimedOut=$build.TimedOut;Lines=@()}}else{
            $changedDll=if($mutation.Wpf){'YerelDisk.dll'}elseif($mutation.File.StartsWith('Core/')){'YerelDisk.Core.dll'}else{'YerelDisk.Application.dll'}
            if((Get-FileHash -LiteralPath (Join-Path $product $changedDll)).Hash -eq (Get-FileHash -LiteralPath (Join-Path $baselineProduct $changedDll)).Hash){throw 'Mutation did not change the product DLL'}
            Run-Suite $mutation.Id $product $suite
        }
        $verdict=Get-MutationVerdict -BuildExit $e.BuildExit -TestExit $e.TestExit -TimedOut $e.TimedOut -Lines $e.Lines -Success $suite.Success -MinimumPass $suite.MinimumPass -ExpectedFailure ([regex]::Escape($mutation.Expected))
        $rows.Add([pscustomobject]@{Id=$mutation.Id;Suite=$mutation.Suite;Verdict=$verdict;Description=$mutation.Description;BuildExit=$e.BuildExit;TestExit=$e.TestExit;Failure=@($e.Lines | Where-Object {$_ -match '^FAIL\b'}) -join ' | ';PatchedSourceSHA256=(Get-FileHash -LiteralPath $target).Hash})
        Write-Output "$verdict $($mutation.Id)"
        # Preserve inputs of surviving/unexpected cases. Expected assertion failures can be recycled after evidence is recorded.
        if($verdict -eq 'KILLED'){Move-ValidationFixturesToRecycleBin $logRoot $mutation.Suite 'PASS'}
    }
    $exitCode=if(@($rows | Where-Object Verdict -ne 'KILLED').Count){1}else{0}
} catch { $rows.Add([pscustomobject]@{Id='Infrastructure';Suite='Runner';Verdict='ERROR';Description=$_.Exception.Message});Write-Output "ERROR: $_";$exitCode=1 }
finally {
    foreach($item in @($source)+@($installed)) {if((Get-FileHash -LiteralPath (Join-Path $disk $item.Path)).Hash -ne $item.SHA256){$exitCode=1;$rows.Add([pscustomobject]@{Id='Integrity';Verdict='ERROR';Description='Original source/installed product changed'})}}
    if(Compare-Object ($source|ForEach-Object{$_.Path+':'+$_.SHA256}) (Get-ProductionHashes|ForEach-Object{$_.Path+':'+$_.SHA256})){$exitCode=1;$rows.Add([pscustomobject]@{Id='Integrity';Verdict='ERROR';Description='Production source set changed during mutation run'})}
    if(Compare-Object ($harness|ForEach-Object{$_.Path+':'+$_.SHA256}) (Get-ValidationHarnessHashes $validation|ForEach-Object{$_.Path+':'+$_.SHA256})){$exitCode=1;$rows.Add([pscustomobject]@{Id='Integrity';Verdict='ERROR';Description='Test harness changed during mutation run'})}
    $rows | Export-Csv (Join-Path $run 'summary.csv') -NoTypeInformation -Encoding UTF8
    [pscustomobject]@{ExitCode=$exitCode;Selected=@($catalog.Id);Killed=@($rows|Where-Object Verdict -eq 'KILLED').Count;Survived=@($rows|Where-Object Verdict -eq 'SURVIVED').Count;Results=$rows.ToArray()} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $run 'summary.json') -Encoding UTF8
    $run | Set-Content (Join-Path $PSScriptRoot 'SON-KOSU.txt') -Encoding UTF8
    if($exitCode -eq 0 -and !$KeepWork){Move-ValidationFixturesToRecycleBin $run 'Capabilities' 'PASS'}
    Write-Output "Finished: exit=$exitCode; $run/summary.json"
}
exit $exitCode
