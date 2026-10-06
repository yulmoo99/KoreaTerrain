param([ValidateSet('2025','2026')][string]$Version = '2026')
$ErrorActionPreference = 'Stop'
$api = "C:\Program Files\Autodesk\Revit $Version\RevitAPI.dll"
if (-not (Test-Path -LiteralPath $api)) { throw "Revit $Version API assembly not installed. Build requires the matching version." }
& dotnet build (Join-Path $PSScriptRoot 'src\KoreaTerrain.csproj') -c Release "-p:RevitVersion=$Version" -o (Join-Path $PSScriptRoot "build\$Version")
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
