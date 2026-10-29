#!/usr/bin/env bash
# Builds SpirePilot for both game branches and packs ready-to-install zips into dist/.
#   dist/SpirePilot-<version>-game0.111.zip   (Slay the Spire 2 public beta 0.111.x)
#   dist/SpirePilot-<version>-game0.107.zip   (Slay the Spire 2 main branch 0.107.x)
# Each zip contains a "SpirePilot" folder to drop into <game>/mods/.
# Usage: scripts/package.sh [extra dotnet build args, e.g. -p:Sts2Dir=/path/to/game]
set -euo pipefail
cd "$(dirname "$0")/.."

version=$(sed -n 's:.*<SpirePilotVersion>\(.*\)</SpirePilotVersion>.*:\1:p' Directory.Build.props)
rm -rf dist
mkdir -p dist

dotnet test tests/SpirePilot.Core.Tests -c Release --nologo -v q

for game in 0.111 0.107; do
  out="dist/build-$game"
  dotnet build src/SpirePilot -c Release --nologo -v q -p:Sts2GameVersion="$game" -o "$out" "$@"
  stage="dist/stage-$game/SpirePilot"
  mkdir -p "$stage"
  cp "$out/SpirePilot.dll" "$out/mod_manifest.json" "$stage/"
  cp README.md "$stage/README.md"
  (cd "dist/stage-$game" && zip -qr "../SpirePilot-$version-game$game.zip" SpirePilot)
  rm -rf "$out" "dist/stage-$game"
done

ls -l dist
