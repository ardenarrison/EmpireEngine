#!/bin/bash
# Builds the iPad app, and on the simulator also starts it.
#   iPad/build.sh                     build for the iPad simulator and run it there
#   iPad/build.sh device              build for the connected iPad, install it and start it (needs an Apple ID in Xcode)
# Set DOTNET to use a .NET SDK other than the one on the PATH; it needs the iOS workload (dotnet workload install ios).
# For a real iPad, set BUNDLE_ID to the bundle ID your Apple ID has a provisioning profile for.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
DOTNET="${DOTNET:-dotnet}"
BUNDLE_ID="${BUNDLE_ID:-com.empireengine.ipad}"
PACKAGES="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode.app/Contents/Developer}"

[ -d "$HERE/Native/out/shaderc.xcframework" ] && [ -d "$HERE/Native/out/MoltenVK.xcframework" ] || "$HERE/Native/build-natives.sh"

"$DOTNET" run --project "$HERE/Tools/BrutalPatch" -- "$HERE/obj/BrutalPatched" \
    "$PACKAGES/ahwoo.brutal.core/2026.10.1/lib/net10.0/Brutal.Core.dll" \
    "$PACKAGES/ahwoo.brutal.vulkan/2026.10.2/lib/net10.0/Brutal.Vulkan.dll" \
    "$PACKAGES/ahwoo.brutal.vulkan.abstractions/2026.10.2/lib/net10.0/Brutal.Vulkan.Abstractions.dll" \
    "$PACKAGES/ahwoo.brutal.shaderc/2026.10.2/lib/net10.0/Brutal.ShaderC.dll"

if [ "${1:-simulator}" = device ]; then
    "$DOTNET" build "$HERE/EmpireiPad.csproj" -c Debug -r ios-arm64 -p:ApplicationId="$BUNDLE_ID" -p:CodesignKey="Apple Development" -p:CodesignProvision=Automatic
    app="$(find "$HERE/bin/Debug" -type d -name 'EmpireiPad.app' | grep ios-arm64 | head -1)"
    device="${IPAD:-$(xcrun devicectl list devices 2>/dev/null | grep -E 'connected|available' | grep -oE '[0-9A-F]{8}-[0-9A-F]{16}' | head -1)}"
    [ -n "$device" ] || { echo "No iPad found: connect it with a cable and unlock it."; exit 1; }
    xcrun devicectl device install app --device "$device" "$app"
    xcrun devicectl device process launch --device "$device" "$BUNDLE_ID"
    exit
fi

"$DOTNET" build "$HERE/EmpireiPad.csproj" -c Debug -r iossimulator-arm64
app="$(find "$HERE/bin/Debug" -type d -name 'EmpireiPad.app' | grep iossimulator | head -1)"
device="${SIMULATOR:-iPad Pro 11-inch (M5)}"
xcrun simctl boot "$device" 2>/dev/null || true
open -a Simulator
xcrun simctl install "$device" "$app"
xcrun simctl launch --console-pty --terminate-running-process "$device" com.empireengine.ipad
