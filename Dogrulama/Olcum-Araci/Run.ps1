#requires -Version 7.0
[CmdletBinding()]
param([ValidateRange(1,5)][int]$Rounds=3,[ValidateRange(1,100)][int]$FilesPerDirectory=50,[string]$ProductDirectory,[string]$CompareProductDirectory,
    [ValidateRange(1,500000)][int]$FileLimit=100000,[string[]]$Operations,[ValidateSet(1,4,8)][int[]]$Workers=@(1,4,8),
    [ValidateSet('many','sparse')][string[]]$Workloads=@('many','sparse'),[string]$ExistingFixtureRun,[switch]$KeepFixtures)
$ErrorActionPreference='Stop'
$cases=@(foreach($workload in @('many','sparse')){foreach($worker in @(1,4,8)){foreach($cache in @(0,128)){[pscustomobject]@{Workload=$workload;Operation='scan';Workers=$worker;Cache=$cache}}}})
$cases+=@(foreach($operation in @('selected-small','selected-half','whole','missing','remeasure')){[pscustomobject]@{Workload='many';Operation=$operation;Workers=4;Cache=128}})
if($Operations){foreach($operation in $Operations){if($operation -notin $cases.Operation){throw 'Unknown requested operation'}};$cases=@($cases|Where-Object Operation -in $Operations)}
$cases=@($cases|Where-Object {$_.Workers -in $Workers -and $_.Workload -in $Workloads})
if(!$cases.Count){throw 'No cases match the requested filters'}
$requiredWorkloads=@($cases.Workload|Select-Object -Unique)
$dataset=$null;$fixtureFiles=$null;$generatedFiles=$null
$disk=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$product=if($ProductDirectory){[IO.Path]::GetFullPath($ProductDirectory)}else{Join-Path $disk 'Kaynak/Derleme'}
$run=Join-Path $disk ('Dogrulama/Olcum-Kosular/'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $run | Out-Null
$root=Join-Path $run 'own-fixtures';$fixtureOwner=$run;$token=[Guid]::NewGuid().ToString('N');$exitCode=1;$fixtureLock=$null
if($ExistingFixtureRun){
    if($ExistingFixtureRun -notmatch '^\d{8}-\d{6}-[a-f0-9]{8}$'){throw 'Only an owned measurement run ID can be reused'}
    $fixtureOwner=Join-Path $disk ('Dogrulama/Olcum-Kosular/'+$ExistingFixtureRun);$root=Join-Path $fixtureOwner 'own-fixtures'
    . (Join-Path $disk 'Dogrulama/Guncel/FixtureCleanup.ps1');Assert-ValidationFixtureParent $fixtureOwner $root
    $declared=Get-Content -LiteralPath (Join-Path $root 'dataset.json') -Raw|ConvertFrom-Json
    if('many' -in $requiredWorkloads -and $declared.Files -ne 1000*$FilesPerDirectory){throw 'Reused dataset does not contain the requested many-file workload'}
    if('sparse' -in $requiredWorkloads -and ($declared.SparseFiles -ne 32 -or $declared.SparseBytes -ne 32GB)){throw 'Reused dataset does not contain the requested sparse workload'}
    $token=$declared.Token
}
$files=@('YerelDisk.exe','YerelDisk.dll','YerelDisk.deps.json','YerelDisk.runtimeconfig.json','YerelDisk.Core.dll','YerelDisk.Core.deps.json','YerelDisk.Application.dll','YerelDisk.Application.deps.json')
$manifest=@($files|ForEach-Object{[pscustomobject]@{Path=$_;SHA256=(Get-FileHash -LiteralPath (Join-Path $product $_)).Hash}})
$manifest|Export-Csv (Join-Path $run 'product-SHA256.csv') -NoTypeInformation -Encoding UTF8
$installed=@($files|Where-Object{Test-Path -LiteralPath (Join-Path $disk $_)}|ForEach-Object{[pscustomobject]@{Path=$_;SHA256=(Get-FileHash -LiteralPath (Join-Path $disk $_)).Hash}})
$installed|Export-Csv (Join-Path $run 'installed-before.csv') -NoTypeInformation -Encoding UTF8
$harness=@(Get-ChildItem -LiteralPath $PSScriptRoot -File |ForEach-Object{[pscustomobject]@{Path=$_.Name;SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
$harness|Export-Csv (Join-Path $run 'probe-source-SHA256.csv') -NoTypeInformation -Encoding UTF8
$copy=Join-Path $run 'product';New-Item -ItemType Directory -Path $copy|Out-Null
foreach($file in $files){Copy-Item -LiteralPath (Join-Path $product $file) -Destination (Join-Path $copy $file)}
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1';$env:DOTNET_CLI_USE_MSBUILD_SERVER='0';$env:MSBuildEnableWorkloadResolver='false';$env:DOTNET_NOLOGO='1';$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:DOTNET_CLI_HOME=Join-Path $run 'build-home';$env:NUGET_PACKAGES=Join-Path $run 'packages'
function Invoke-Owned([string[]]$Arguments,[string]$Log) {
    $start=[Diagnostics.ProcessStartInfo]::new((Get-Command dotnet -ErrorAction Stop).Source)
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach($arg in $Arguments){$start.ArgumentList.Add($arg)}
    $process=[Diagnostics.Process]::new();$process.StartInfo=$start
    try{if(!$process.Start()){throw 'Could not start probe'};$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        if(!$process.WaitForExit(180000)){$process.Kill($true);$process.WaitForExit();throw 'Probe timed out'}
        [IO.File]::WriteAllText($Log,$stdout.GetAwaiter().GetResult()+[Environment]::NewLine+$stderr.GetAwaiter().GetResult(),[Text.UTF8Encoding]::new($true))
        if($process.ExitCode -ne 0){throw "Probe failed: $Log"}
    }finally{$process.Dispose()}
}
$results=[Collections.Generic.List[object]]::new()
try {
    Write-Output "Resource measurement: $run"
    if((New-Object IO.DriveInfo ([IO.Path]::GetPathRoot($run))).AvailableFreeSpace -lt 2GB){throw 'At least 2 GiB free space required for bounded fixtures and build output'}
    $build=Join-Path $run 'build'
    Invoke-Owned @('build',(Join-Path $PSScriptRoot 'ResourceProbe.csproj'),'-c','Release','--configfile',(Join-Path $disk 'Kaynak/NuGet.Config'),'-p:NuGetAudit=false','-p:UseSharedCompilation=false',"-p:ProductDirectory=$copy","-p:ValidationWorkDirectory=$run",'-m:1','-o',$build) (Join-Path $run 'build-probe.txt')
    foreach($name in @('YerelDisk.Core.dll','YerelDisk.Application.dll')){if((Get-FileHash -LiteralPath (Join-Path $build $name)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $copy $name)).Hash){throw 'Wrong probe DLL reference'}}
    $dll=Join-Path $build 'YerelDisk.Application.Tests.dll'
    $variants=@([pscustomobject]@{Label='baseline';Dll=$dll})
    if($CompareProductDirectory){
        $other=[IO.Path]::GetFullPath($CompareProductDirectory);$otherCopy=Join-Path $run 'candidate-product';New-Item -ItemType Directory -Path $otherCopy|Out-Null
        $otherManifest=@(foreach($file in $files){Copy-Item -LiteralPath (Join-Path $other $file) -Destination (Join-Path $otherCopy $file);[pscustomobject]@{Path=$file;SHA256=(Get-FileHash -LiteralPath (Join-Path $otherCopy $file)).Hash}})
        $otherManifest|Export-Csv (Join-Path $run 'candidate-product-SHA256.csv') -NoTypeInformation -Encoding UTF8
        $otherBuild=Join-Path $run 'candidate-build'
        Invoke-Owned @('build',(Join-Path $PSScriptRoot 'ResourceProbe.csproj'),'-c','Release','--configfile',(Join-Path $disk 'Kaynak/NuGet.Config'),'-p:NuGetAudit=false','-p:UseSharedCompilation=false',"-p:ProductDirectory=$otherCopy","-p:ValidationWorkDirectory=$(Join-Path $run 'candidate-work')",'-m:1','-o',$otherBuild) (Join-Path $run 'build-candidate-probe.txt')
        foreach($name in @('YerelDisk.Core.dll','YerelDisk.Application.dll')){if((Get-FileHash -LiteralPath (Join-Path $otherBuild $name)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $otherCopy $name)).Hash){throw 'Wrong candidate DLL reference'}}
        $variants+=@([pscustomobject]@{Label='candidate';Dll=(Join-Path $otherBuild 'YerelDisk.Application.Tests.dll')})
    }
    if(!$ExistingFixtureRun){Invoke-Owned @($dll,'generate',$root,$token,"$FilesPerDirectory",($requiredWorkloads -join ',')) (Join-Path $run 'generation.txt')}
    $fixtureLock=[IO.File]::Open((Join-Path $root 'benchmark.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    Copy-Item -LiteralPath (Join-Path $root 'dataset.json') -Destination (Join-Path $run 'dataset.json')
    $dataset=Get-Content -LiteralPath (Join-Path $run 'dataset.json') -Raw|ConvertFrom-Json
    $fixtureFiles=100+[int]$dataset.Files+[int]$dataset.SparseFiles
    $generatedFiles=if($ExistingFixtureRun){0}else{$fixtureFiles}
    for($round=0;$round -lt $Rounds;$round++) {
        $ordered=if($round%2){@($cases[($cases.Count-1)..0])}else{$cases}
        $position=0
        foreach($case in $ordered){$baseId=$case.Workload+'-'+$case.Operation+'-w'+$case.Workers+'-i'+$case.Cache+'-r'+$round
          $variantOrder=if(($round+$position)%2){@($variants[($variants.Count-1)..0])}else{$variants};$pair=@();$position++
          foreach($variant in $variantOrder){$id=$variant.Label+'-'+$baseId
            Write-Output "Measuring $id"
            $out=Join-Path $run ($id+'.json')
            Invoke-Owned @($variant.Dll,'measure',$root,$token,$case.Workload,$case.Operation,"$($case.Workers)","$($case.Cache)",$out,"$FileLimit") (Join-Path $run ($id+'.txt'))
            $value=Get-Content -LiteralPath $out -Raw|ConvertFrom-Json
            if(!$value.correctness){throw 'Unvalidated measurement'}
            $value|Add-Member -NotePropertyName round -NotePropertyValue $round;$value|Add-Member -NotePropertyName variant -NotePropertyValue $variant.Label;$results.Add($value);$pair+=@($value)
          }
          if($pair.Count -eq 2){foreach($field in @('files','folders','logical_bytes','allocated_bytes','retained_files','mixed','refresh_action','refresh_reason','size_digest')){if($pair[0].$field -ne $pair[1].$field){throw "Candidate parity differs: $field"}};if($pair[0].files -le $FileLimit -and $pair[0].path_digest -ne $pair[1].path_digest){throw 'Full retained path/hint parity differs'}}
        }
    }
    $results|Export-Csv (Join-Path $run 'results.csv') -NoTypeInformation -Encoding UTF8
    $results.ToArray()|ConvertTo-Json -Depth 6|Set-Content (Join-Path $run 'results.json') -Encoding UTF8
    $exitCode=0
}catch{$_|Out-String|Set-Content (Join-Path $run 'error.txt') -Encoding UTF8;Write-Output "ERROR $_";$exitCode=1}
finally {
    try{if($null -ne $fixtureLock){$fixtureLock.Dispose()}}catch{$exitCode=1;$_|Out-String|Add-Content (Join-Path $run 'error.txt')}
    foreach($item in $installed){if((Get-FileHash -LiteralPath (Join-Path $disk $item.Path)).Hash -ne $item.SHA256){$exitCode=1}}
    foreach($item in $harness){if((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $item.Path)).Hash -ne $item.SHA256){$exitCode=1}}
    [pscustomobject]@{ExitCode=$exitCode;Measurements=$results.Count;Rounds=$Rounds;FixtureFiles=$fixtureFiles;GeneratedFiles=$generatedFiles;RequestedWorkloads=$requiredWorkloads;ReusedFixtureRun=$ExistingFixtureRun;PhysicalSsdReadBytesMeasured=$false;Source='recorded product DLL hashes; optional candidate pair';Cache='filesystem cache not flushed; fresh warmed child process per case';RenderingMeasured=$false;CpuIncludesSampler=$true;MemoryPeak='sampled every 50 ms; not an exact maximum';ProcessorCount=[Environment]::ProcessorCount;Runtime=[System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription;ProductVersion=(Get-Item -LiteralPath (Join-Path $product 'YerelDisk.dll')).VersionInfo.ProductVersion}|ConvertTo-Json|Set-Content (Join-Path $run 'summary.json') -Encoding UTF8
    if($exitCode -eq 0 -and !$KeepFixtures){. (Join-Path $disk 'Dogrulama/Guncel/FixtureCleanup.ps1');Move-ValidationFixturesToRecycleBin $fixtureOwner 'Capabilities' 'PASS'}
    Write-Output "Finished exit=$exitCode; $run"
}
exit $exitCode
