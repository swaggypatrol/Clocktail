#!/usr/bin/env bash
# Builds the release: four self-contained single-file executables, the npm package (the setup genie plus the server
# and the service) and a zip for manual installs. Usage: build/release.sh 0.1.0   (output in ./out)
set -euo pipefail
ver="${1:?version, e.g. 0.1.0}"
root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/out"
rm -rf "$out" && mkdir -p "$out/bin"
for p in src/Clocktail.Broker tools/Clocktail.Mcp tools/Clocktail.Lab src/Clocktail.Cli; do
  dotnet publish "$root/$p" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:Version="$ver" -o "$out/bin" -nologo -v quiet
done
mv "$out/bin/Clocktail.Cli.exe" "$out/bin/clocktail.exe"

# PresentMon (Intel, MIT): the console build the service runs for game frame rates, pinned by version and SHA-256 (the
# same hash the service checks before it runs it, FrameCapture.PinnedSha256), with its licence and third-party notices.
pm_ver=2.6.0
pm_sha=b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af
grep -q "PinnedSha256 = \"$pm_sha\"" "$root/src/Clocktail.Broker/FrameCapture.cs" || { echo "release.sh and FrameCapture.cs pin different PresentMon hashes"; exit 1; }
pm_cache="$root/build/cache/PresentMon-$pm_ver-x64.exe"
if [ ! -f "$pm_cache" ]; then
  mkdir -p "$(dirname "$pm_cache")"
  curl -fsSL -o "$pm_cache" "https://github.com/GameTechDev/PresentMon/releases/download/v$pm_ver/PresentMon-$pm_ver-x64.exe"
fi
echo "$pm_sha *$pm_cache" | sha256sum -c --quiet || { echo "PresentMon $pm_ver is not the pinned build"; rm -f "$pm_cache"; exit 1; }
cp "$pm_cache" "$out/bin/PresentMon.exe"
for f in LICENSE THIRD_PARTY; do
  curl -fsSL -o "$out/bin/PresentMon-$f.txt" "https://raw.githubusercontent.com/GameTechDev/PresentMon/v$pm_ver/$f.txt"
done

# npm: the genie ships the server, the lab beside it and the service (the CLI stays in the zip).
pkg="$out/npm"
cp -r "$root/npm" "$pkg"
mkdir -p "$pkg/dist"
cp "$out/bin/clocktail-mcp.exe" "$out/bin/clocktail-lab.exe" "$out/bin/Clocktail.Broker.exe" "$out/bin"/PresentMon* "$pkg/dist/"
cp "$root/README.md" "$root/LICENSE" "$root/THIRD_PARTY_NOTICES.md" "$pkg/"
(cd "$pkg" && npm pkg set version="$ver" >/dev/null && npm pack --pack-destination "$out" >/dev/null)

# zip: everything, for people who would rather not use npx.
(cd "$out/bin" && cp "$root/README.md" "$root/LICENSE" "$root/THIRD_PARTY_NOTICES.md" . && powershell -NoProfile -Command \
  "Compress-Archive -Path * -DestinationPath '$(cygpath -w "$out")\Clocktail-$ver-win-x64.zip' -Force")
ls -la "$out"
