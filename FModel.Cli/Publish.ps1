param(
    [Parameter(Mandatory = $true)][string]$PrivateProfile,
    [string]$Destination = (Join-Path $PSScriptRoot '../artifacts')
)

$ErrorActionPreference = 'Stop'
$profile = Get-Content -LiteralPath $PrivateProfile -Raw | ConvertFrom-Json
$profileBase = Split-Path -Parent ([IO.Path]::GetFullPath($PrivateProfile))
$profile.Directory = [IO.Path]::GetFullPath($profile.Directory, $profileBase)
$profile.OutputDirectory = 'exports'
if ($profile.Mappings) { $profile.Mappings = [IO.Path]::GetFullPath($profile.Mappings, $profileBase) }
$destinationPath = [IO.Path]::GetFullPath($Destination)
$name = 'FModel-CLI-NZM-win-x64-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
$output = Join-Path $destinationPath $name
if (Test-Path -LiteralPath $output) { throw 'Package destination already exists.' }
[IO.Directory]::CreateDirectory($destinationPath) | Out-Null
& dotnet publish (Join-Path $PSScriptRoot 'FModel.Cli.csproj') -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:PublishTrimmed=false' '-p:DebugType=None' '-p:DebugSymbols=false' -o $output --nologo '-clp:ErrorsOnly'
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Package/nzm.cmd'), (Join-Path $PSScriptRoot 'Package/README.txt') -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../LICENSE'), (Join-Path $PSScriptRoot '../NOTICE') -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../CUE4Parse/LICENSE') -Destination (Join-Path $output 'CUE4Parse-LICENSE')
$profile | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $output 'nzm.local.json') -Encoding utf8
$archive = $output + '.zip'
Compress-Archive -LiteralPath $output -DestinationPath $archive
Get-Item -LiteralPath $archive | Select-Object FullName, Length
Get-FileHash -LiteralPath $archive -Algorithm SHA256
