# Builds SpirePilot for both game branches and packs ready-to-install zips into dist\.
#   dist\SpirePilot-<version>-game0.111.zip   (Slay the Spire 2 public beta 0.111.x)
#   dist\SpirePilot-<version>-game0.107.zip   (Slay the Spire 2 main branch 0.107.x)
# Each zip contains a "SpirePilot" folder to drop into <game>\mods\.
# Usage: powershell -ExecutionPolicy Bypass -File scripts\package.ps1 [-Sts2Dir "D:\SteamLibrary\steamapps\common\Slay the Spire 2"]
param([string]$Sts2Dir = "")
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.SpirePilotVersion | Where-Object { $_ } | Select-Object -First 1
if (Test-Path dist) { Remove-Item dist -Recurse -Force }
New-Item -ItemType Directory dist | Out-Null

dotnet test tests/SpirePilot.Core.Tests -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "tests failed" }

foreach ($game in @("0.111", "0.107")) {
    $out = "dist\build-$game"
    $extra = @()
    if ($Sts2Dir -ne "") { $extra += "-p:Sts2Dir=$Sts2Dir" }
    dotnet build src/SpirePilot -c Release --nologo -v q "-p:Sts2GameVersion=$game" -o $out @extra
    if ($LASTEXITCODE -ne 0) { throw "build failed for game $game" }
    $stage = "dist\stage-$game\SpirePilot"
    New-Item -ItemType Directory $stage -Force | Out-Null
    Copy-Item "$out\SpirePilot.dll", "$out\mod_manifest.json", "README.md" $stage
    Compress-Archive -Path "dist\stage-$game\SpirePilot" -DestinationPath "dist\SpirePilot-$version-game$game.zip"
    Remove-Item $out, "dist\stage-$game" -Recurse -Force
}
Get-ChildItem dist
