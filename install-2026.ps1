$ErrorActionPreference = 'Stop'
$assembly = Join-Path $PSScriptRoot 'build\2026\KoreaTerrain.dll'
if (-not (Test-Path -LiteralPath $assembly)) { throw 'Missing build/2026/KoreaTerrain.dll' }
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'build\2026\NetTopologySuite.dll'))) { throw 'Missing NetTopologySuite.dll. Extract the complete package.' }
if (Get-Process Revit -ErrorAction SilentlyContinue) { throw 'Close Revit before installing.' }
$addinDirectory = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2026'
$manifest = Join-Path $addinDirectory 'KoreaTerrain.addin'
if (Test-Path -LiteralPath $manifest) {
    $oldManifest = [xml][IO.File]::ReadAllText($manifest)
    if ($oldManifest.RevitAddIns.AddIn.AddInId -ne 'F47CB7B5-C2C7-483E-BC99-A4F6012D301D' -or $oldManifest.RevitAddIns.AddIn.FullClassName -ne 'KoreaTerrain.Command') { throw 'Another add-in uses this filename. Installation stopped.' }
    $backup = $manifest + '.' + [DateTime]::Now.ToString('yyyyMMddHHmmssfff') + '.bak'
    Copy-Item -LiteralPath $manifest -Destination $backup
}
New-Item -ItemType Directory -Path $addinDirectory -Force | Out-Null
$escaped = [System.Security.SecurityElement]::Escape($assembly)
$xml = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Command">
    <Name>KoreaTerrain</Name>
    <Assembly>$escaped</Assembly>
    <AddInId>F47CB7B5-C2C7-483E-BC99-A4F6012D301D</AddInId>
    <FullClassName>KoreaTerrain.Command</FullClassName>
    <Text>한국 지형 생성</Text>
    <VendorId>KRTR</VendorId>
    <VendorDescription>Korea terrain prototype</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
[System.IO.File]::WriteAllText($manifest, $xml, [System.Text.UTF8Encoding]::new($false))
Write-Host 'Installed for Revit 2026. Keep this folder in its current location.'
Write-Host $manifest
