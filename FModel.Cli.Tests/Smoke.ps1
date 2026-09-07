param(
    [Parameter(Mandatory = $true)][string]$Profile,
    [string]$Asset = 'NZM/Content/AIBehavior/BaseAIBluprints/DataConfig/DataAsset/AITarget/DA_AITarget_WeaponFriend.uasset'
)

$ErrorActionPreference = 'Stop'
$dll = Join-Path $PSScriptRoot '../FModel.Cli/bin/Release/net10.0/FModel.Cli.dll'
if (!(Test-Path -LiteralPath $dll)) { throw 'Build FModel.Cli in Release first.' }
$configuration = Get-Content -LiteralPath $Profile -Raw | ConvertFrom-Json
$root = Join-Path ([IO.Path]::GetTempPath()) ('fmodel-smoke-' + [guid]::NewGuid().ToString('N'))
$configuration.OutputDirectory = Join-Path $root 'exports'
[IO.Directory]::CreateDirectory($root) | Out-Null
$temporaryProfile = Join-Path $root 'profile.json'
# The private profile is temporary and is deleted even when a check fails.
$configuration | ConvertTo-Json | Set-Content -LiteralPath $temporaryProfile
function Invoke-Cli([string[]]$Arguments, [int]$ExpectedExit = 0) {
    $raw = & dotnet $dll @Arguments --profile $temporaryProfile 2> (Join-Path $root 'diagnostics.log')
    if ($LASTEXITCODE -ne $ExpectedExit) { throw "Unexpected exit code $LASTEXITCODE (expected $ExpectedExit)." }
    $raw | ConvertFrom-Json
}
try {
    $mount = Invoke-Cli -Arguments @('mount')
    if (!$mount.ok -or $mount.result.mounted -lt 1) { throw 'Mount failed.' }
    $search = Invoke-Cli -Arguments @('search', '--query', $Asset, '--limit', '1')
    if ($search.result.assets.Count -ne 1) { throw 'Sample asset was not found.' }
    $inspect = Invoke-Cli -Arguments @('inspect', '--asset', $Asset)
    $objects = Get-Content -LiteralPath $inspect.result.output -Raw | ConvertFrom-Json
    if (@($objects).Count -lt 1) { throw 'No parsed exports.' }
    $extract = Invoke-Cli -Arguments @('extract', '--asset', $Asset)
    foreach ($file in $extract.result.files) {
        if ((Get-Item -LiteralPath $file.output).Length -ne $file.bytes) { throw 'Export size mismatch.' }
    }
    $hashBefore = Get-FileHash -LiteralPath $extract.result.files[0].output
    $repeat = Invoke-Cli -Arguments @('extract', '--asset', $Asset) -ExpectedExit 1
    $hashAfter = Get-FileHash -LiteralPath $extract.result.files[0].output
    if ($repeat.ok -or $hashBefore.Hash -ne $hashAfter.Hash) { throw 'Non-overwrite contract failed.' }
    [pscustomobject]@{ Passed = $true; Mounted = $mount.result.mounted; Files = $mount.result.files; ParsedObjects = @($objects).Count; ExtractedFiles = $extract.result.files.Count; Artifacts = $root }
}
finally {
    Remove-Item -LiteralPath $temporaryProfile -Force
}
