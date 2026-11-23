[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Evidence.ps1')
$success = '^ALL CHECKS PASSED \(\d+\)$'
$examples = @(
    @{ Label='Compiled mutation survives complete green suite'; Build=0; Exit=0; Lines=@('PASS first','ALL CHECKS PASSED (1)'); Want='SURVIVED' }
    @{ Label='Expected assertion rejects the mutation'; Build=0; Exit=1; Lines=@('FAIL System.Exception: Target invariant'); Want='KILLED' }
    @{ Label='Compilation failure is not a kill'; Build=1; Exit=1; Lines=@('FAIL System.Exception: Target invariant'); Want='INVALID' }
    @{ Label='Missing result cannot pass'; Build=0; Exit=0; Lines=@(); Want='INCOMPLETE' }
    @{ Label='Unexpected crash is not a kill'; Build=0; Exit=134; Lines=@('FAIL System.Exception: Target invariant'); Want='UNEXPECTED_FAILURE' }
    @{ Label='Wrong assertion is not credited'; Build=0; Exit=1; Lines=@('FAIL System.Exception: Different invariant'); Want='UNEXPECTED_FAILURE' }
    @{ Label='IOException is not the planned assertion'; Build=0; Exit=1; Lines=@('FAIL System.IO.IOException: Target invariant'); Want='UNEXPECTED_FAILURE' }
    @{ Label='Exit zero cannot hide FAIL'; Build=0; Exit=0; Lines=@('FAIL System.Exception: Target invariant','ALL CHECKS PASSED (1)'); Want='INCOMPLETE' }
    @{ Label='Skipped assertions are not kills'; Build=0; Exit=1; Lines=@('SKIP environment','FAIL System.Exception: Target invariant'); Want='SKIPPED' }
    @{ Label='Missing success footer cannot survive'; Build=0; Exit=0; Lines=@('PASS first'); Want='INCOMPLETE' }
    @{ Label='Missing minimum assertions cannot survive'; Build=0; Exit=0; Lines=@('ALL CHECKS PASSED (0)'); Want='INCOMPLETE' }
    @{ Label='Nonzero exit cannot hide behind a green footer'; Build=0; Exit=1; Lines=@('PASS first','ALL CHECKS PASSED (1)'); Want='UNEXPECTED_FAILURE' }
    @{ Label='Mixed success and expected failure is rejected'; Build=0; Exit=1; Lines=@('FAIL System.Exception: Target invariant','ALL CHECKS PASSED (1)'); Want='UNEXPECTED_FAILURE' }
    @{ Label='Unanchored substring is not the target assertion'; Build=0; Exit=1; Lines=@('FAIL System.Exception: Other Target invariant text'); Want='UNEXPECTED_FAILURE' }
    @{ Label='Extra infrastructure error cannot hide beside expected failure'; Build=0; Exit=1; Lines=@('FAIL System.Exception: Target invariant','ERROR infrastructure'); Want='UNEXPECTED_FAILURE' }
    @{ Label='Footer count must agree with actual assertion records'; Build=0; Exit=0; Lines=@('PASS first','ALL CHECKS PASSED (99)'); Want='INCOMPLETE' }
)
foreach ($test in $examples) {
    $got = Get-MutationVerdict -BuildExit $test.Build -TestExit $test.Exit -TimedOut $false -Lines $test.Lines -Success $success -MinimumPass 1 -ExpectedFailure 'Target invariant'
    if ($got -ne $test.Want) { throw "$($test.Label): expected $($test.Want), got $got" }
}
if ((Get-MutationVerdict -BuildExit 0 -TestExit 1 -TimedOut $true -Lines @('FAIL System.Exception: Target invariant') -Success $success -MinimumPass 1 -ExpectedFailure 'Target invariant') -ne 'TIMEOUT') { throw 'Timeout counted as assertion failure' }
$catalog = @((Import-PowerShellDataFile (Join-Path $PSScriptRoot '../Suites.psd1')).Suites)
$readme = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../README.md') -Raw -Encoding UTF8
foreach ($suite in $catalog) {
    $pattern = '(?m)^\| ' + [regex]::Escape($suite.Name) + ' \| ' + $suite.Group + ' \| ' + $suite.MinimumPass + ' \|'
    if ($readme -notmatch $pattern) { throw "Test README/catalog mismatch: $($suite.Name)" }
}
"PASS infrastructure: 17 result-classification counterexamples; $($catalog.Count) README/catalog rows agree."
