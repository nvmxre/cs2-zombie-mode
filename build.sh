#!/usr/bin/env bash
# Build every project and lay the result out exactly as it goes on a server:
#   dist/addons/counterstrikesharp/shared/ZombieMode.Api/ZombieMode.Api.dll
#   dist/addons/counterstrikesharp/plugins/<Plugin>/<Plugin>.dll (+ lang/)
# Then copy dist/addons over your server's game/csgo/addons.
#
#   ./build.sh               build into dist/
#   ./build.sh --zip         also pack dist/cs2-zombie-mode-<version>.zip
set -euo pipefail
cd "$(dirname "$0")"

CONFIG=Release
OUT=dist/addons/counterstrikesharp
rm -rf dist
mkdir -p "$OUT/shared/ZombieMode.Api" "$OUT/plugins"

dotnet build ZombieMode.slnx -c "$CONFIG" --nologo -v quiet

cp "src/ZombieMode.Api/bin/$CONFIG/net10.0/ZombieMode.Api.dll" "$OUT/shared/ZombieMode.Api/"

for plugin in ZombieMode ZombieMode.Money ZombieMode.Magazines ZombieMode.WeaponDamage; do
  src="src/$plugin/bin/$CONFIG/net10.0"
  dst="$OUT/plugins/$plugin"
  mkdir -p "$dst"
  cp "$src/$plugin.dll" "$src/$plugin.deps.json" "$dst/"
  [ -d "$src/lang" ] && cp -r "$src/lang" "$dst/"
done

echo "Built into dist/addons"

if [ "${1:-}" = "--zip" ]; then
  version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
  (cd dist && zip -qr "cs2-zombie-mode-$version.zip" addons)
  echo "Packed dist/cs2-zombie-mode-$version.zip"
fi
