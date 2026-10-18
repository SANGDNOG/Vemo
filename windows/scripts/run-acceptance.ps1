# Native Windows core acceptance. No UI automation, model downloads, or host configuration.
[CmdletBinding()]
param(
    [string]$BundleRoot = $PSScriptRoot,
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64',
    [string]$ReportDirectory = (Join-Path $env:TEMP ('VemoAcceptance-' + [guid]::NewGuid().ToString())),
    [string]$Settings,
    [string]$AudioFile
)
$ErrorActionPreference = 'Stop'
$BundleRoot = (Resolve-Path -LiteralPath $BundleRoot).Path
$manifest = Get-Content -LiteralPath (Join-Path $BundleRoot 'manifest.json') -Raw | ConvertFrom-Json
if ($manifest.schema_version -ne 1) { throw 'Unsupported publication manifest.' }
$name = 'validation-' + $Runtime
$bundle = $manifest.bundles.PSObject.Properties[$name].Value
if ($null -eq $bundle) { throw "Publication does not contain $name" }
$directory = Join-Path $BundleRoot $name
# Verify every published runner dependency before execution. Unexpected extra files fail closed.
$expected = @{}
foreach ($entry in $bundle.files) {
    if ([IO.Path]::IsPathRooted($entry.path) -or $entry.path -match '(^|[\\/])\.\.([\\/]|$)') { throw 'Unsafe manifest path.' }
    $file = Join-Path $directory $entry.path
    $item = Get-Item -LiteralPath $file
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point: $file" }
    if ($item.Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) { throw "Hash/size mismatch: $file" }
    $expected[$item.FullName] = $true
}
foreach ($file in Get-ChildItem -LiteralPath $directory -Recurse -File) {
    if (-not $expected.ContainsKey($file.FullName)) { throw "Unexpected runner file: $($file.FullName)" }
}
if ([bool]$Settings -ne [bool]$AudioFile) { throw 'Supply both -Settings and -AudioFile for an actual inference run.' }
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
$ReportDirectory = (Resolve-Path -LiteralPath $ReportDirectory).Path
$exe = Join-Path $directory $bundle.exe
$acceptance = Join-Path $ReportDirectory 'acceptance.json'
& $exe $acceptance > (Join-Path $ReportDirectory 'acceptance.stdout.txt') 2> (Join-Path $ReportDirectory 'acceptance.stderr.txt')
$acceptanceExit = $LASTEXITCODE
if (-not (Test-Path -LiteralPath $acceptance)) { throw "Runner produced no acceptance report (exit $acceptanceExit). See $ReportDirectory" }
$result = Get-Content -LiteralPath $acceptance -Raw | ConvertFrom-Json
if ($acceptanceExit -ne 0 -or -not $result.passed) { throw "Native acceptance failed (exit $acceptanceExit). See $acceptance" }
if ($Settings) {
    $settingsFile = (Resolve-Path -LiteralPath $Settings).Path
    $audio = (Resolve-Path -LiteralPath $AudioFile).Path
    $inference = Join-Path $ReportDirectory 'inference.json'
    & $exe --infer $settingsFile $audio $inference > (Join-Path $ReportDirectory 'inference.stdout.txt') 2> (Join-Path $ReportDirectory 'inference.stderr.txt')
    $inferenceExit = $LASTEXITCODE
    if ($inferenceExit -ne 0) { throw "Actual inference failed (exit $inferenceExit). See $ReportDirectory" }
}
Write-Output "Native core acceptance passed on $($result.os) / $($result.architecture). Reports: $ReportDirectory"
Write-Output 'This does not launch or certify the Avalonia UI, CUDA, model quality, or a cloud roundtrip.'
