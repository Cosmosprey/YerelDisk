[CmdletBinding()]
param([switch]$Native, [switch]$All, [switch]$List, [string]$PublishedProductDirectory)
& (Join-Path $PSScriptRoot '../Dogrulama/Guncel/Run.ps1') @PSBoundParameters
if ($List) { return }
exit $LASTEXITCODE
