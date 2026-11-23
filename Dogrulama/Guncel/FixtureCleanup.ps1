# Dot-source from the developer runner. Never imported by the application.
function Get-ValidationFixtureDirectory {
    param([string]$RunDirectory, [string]$Suite)
    $parents = if ($Suite -eq 'Capabilities') { @($RunDirectory) }
        else { @((Join-Path $RunDirectory "results/$Suite"), (Join-Path $RunDirectory "build/$Suite")) }
    foreach ($parent in $parents) {
        if (!(Test-Path -LiteralPath $parent -PathType Container)) { continue }
        Assert-ValidationFixtureParent $RunDirectory $parent
        foreach ($child in Get-ChildItem -LiteralPath $parent -Directory -Force) {
            if ($child.Name -match '^(?:(?:fixtures?|own-fixtures?|native-fixture)(?:-[0-9a-f]{32})?|capability-fixtures)$') {
                Assert-ValidationFixtureParent $RunDirectory $child.FullName
                $child.FullName
            }
        }
    }
}
function Assert-ValidationFixtureParent {
    param([string]$Root, [string]$Path)
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $cursor = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ($cursor -ne $rootPath -and !$cursor.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Outside test run: $cursor" }
    while ($true) {
        $entry = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
        if (!$entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Test path is not a real directory: $cursor" }
        if ($cursor -eq $rootPath) { break }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}
function Move-ValidationFixturesToRecycleBin {
    param([string]$RunDirectory, [string]$Suite, [string]$Status, [switch]$KeepFixtures)
    # Failed, skipped and incomplete tests retain their inputs for diagnosis.
    if ($KeepFixtures -or $Status -ne 'PASS') { return }
    try {
        $paths = @(Get-ValidationFixtureDirectory $RunDirectory $Suite)
        if (!$paths.Count) { return }
        if (!('YerelDisk.ValidationTools.FixtureCleanup' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'FixtureCleanup.cs') }
        foreach ($path in $paths) {
            $row = [pscustomobject]@{ Suite=$Suite; Source=$path; Status=''; RecycledPath=''; Reason='' }
            try {
                $row.RecycledPath = [YerelDisk.ValidationTools.FixtureCleanup]::RecycleDirectory($RunDirectory, $path)
                $row.Status = 'RECYCLED'
            } catch {
                $row.Status = 'RETAINED_OR_UNCONFIRMED'; $row.Reason = $_.Exception.Message
                Write-Warning "Fixture cleanup was not confirmed; no permanent-delete retry: $path. $($row.Reason)"
            }
            $row | Export-Csv -LiteralPath (Join-Path $RunDirectory 'fixture-cleanup.csv') -NoTypeInformation -Encoding UTF8 -Append
        }
    } catch { Write-Warning "Fixture cleanup skipped: $($_.Exception.Message)" }
}
