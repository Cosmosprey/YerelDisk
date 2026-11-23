# Kept compatible with Windows PowerShell 5.1; no filesystem mutations.
function Get-ValidationHarnessHashes {
    param([string]$ValidationRoot)
    $root=[IO.Path]::GetFullPath($ValidationRoot)
    $files=@(Get-ChildItem -LiteralPath (Join-Path $root 'Tests') -File -Recurse)
    $files+=@(Get-ChildItem -LiteralPath (Join-Path $root 'Mutasyon') -File | Where-Object Extension -in '.ps1','.psd1','.md')
    $files+=@('Run.ps1','Suites.psd1','Directory.Build.props','FixtureCleanup.ps1','FixtureCleanup.cs','README.md') | ForEach-Object {Get-Item -LiteralPath (Join-Path $root $_)}
    @($files | Sort-Object FullName -Unique | ForEach-Object {
        [pscustomobject]@{Path=$_.FullName.Substring($root.Length+1);SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
}
function Get-MutationVerdict {
    param([int]$BuildExit, [int]$TestExit, [bool]$TimedOut, [string[]]$Lines,
        [string]$Success, [int]$MinimumPass, [string]$ExpectedFailure)
    if ($TimedOut) { return 'TIMEOUT' }
    if ($BuildExit -ne 0) { return 'INVALID' }
    if (!$Lines -or !$Lines.Count) { return 'INCOMPLETE' }
    if (@($Lines | Where-Object { $_ -match '^SKIP\b' }).Count) { return 'SKIPPED' }
    $failures = @($Lines | Where-Object { $_ -match '^(FAIL|FINDING|ERROR)\b' })
    $passes = @($Lines | Where-Object { $_ -match '^PASS\s' }).Count
    $footer = [regex]::Match($Lines[-1], '\((\d+)\)$')
    if ($TestExit -eq 0 -and !$failures.Count -and $passes -ge $MinimumPass -and $Lines[-1] -match $Success -and
        $footer.Success -and [int]$footer.Groups[1].Value -eq $passes) { return 'SURVIVED' }
    # A crash, build error or arbitrary nonzero exit is not evidence that a test caught the fault.
    if ($TestExit -eq 1 -and $failures.Count -eq 1 -and $ExpectedFailure -and
        @($failures | Where-Object { $_ -match ('^FAIL System\.Exception: ' + $ExpectedFailure + '$') }).Count -and
        !@($Lines | Where-Object { $_ -match $Success }).Count) { return 'KILLED' }
    if ($TestExit -eq 0) { return 'INCOMPLETE' }
    return 'UNEXPECTED_FAILURE'
}
