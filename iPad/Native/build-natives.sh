#!/bin/bash
# Builds the native libraries the iPad app needs and puts them in iPad/Native/out as xcframeworks (iPad and simulator):
#   shaderc.xcframework    Google's GLSL compiler, built from source for iPadOS
#   MoltenVK.xcframework   Vulkan on Metal, from the MoltenVK release, keeping only the iPadOS parts
# Needs Xcode, CMake and git. Sources and build files go in WORK (default iPad/Native/.work), which can be deleted after.
set -euo pipefail

SHADERC_VERSION=v2026.4
MOLTENVK_VERSION=v1.4.2
DEPLOYMENT_TARGET=17.0

HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$HERE/out"
WORK="${WORK:-$HERE/.work}"
export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode.app/Contents/Developer}"
mkdir -p "$OUT" "$WORK"

build_shaderc() {   # $1: iphonesimulator or iphoneos
    local sdk="$1" build="$WORK/shaderc-build-$1" stage="$WORK/stage/$1/shaderc.framework"
    cmake -S "$WORK/shaderc" -B "$build" -DCMAKE_BUILD_TYPE=MinSizeRel \
        -DCMAKE_SYSTEM_NAME=iOS -DCMAKE_OSX_SYSROOT="$sdk" -DCMAKE_OSX_ARCHITECTURES=arm64 \
        -DCMAKE_OSX_DEPLOYMENT_TARGET="$DEPLOYMENT_TARGET" \
        -DSHADERC_SKIP_TESTS=ON -DSHADERC_SKIP_EXAMPLES=ON -DSHADERC_SKIP_COPYRIGHT_CHECK=ON \
        -DSPIRV_SKIP_EXECUTABLES=ON -DSPIRV_SKIP_TESTS=ON -DENABLE_GLSLANG_BINARIES=OFF -DSPIRV_TOOLS_BUILD_STATIC=ON
    cmake --build "$build" --target shaderc_shared -j "$(sysctl -n hw.ncpu)"

    rm -rf "$stage"
    mkdir -p "$stage"
    cp -L "$build"/libshaderc/libshaderc_shared.dylib "$stage/shaderc"
    install_name_tool -id @rpath/shaderc.framework/shaderc "$stage/shaderc"
    cat > "$stage/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleExecutable</key><string>shaderc</string>
  <key>CFBundleIdentifier</key><string>com.google.shaderc</string>
  <key>CFBundlePackageType</key><string>FMWK</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>MinimumOSVersion</key><string>$DEPLOYMENT_TARGET</string>
</dict></plist>
PLIST
    rm -rf "$build"   # large, and only the library above is needed
}

if [ ! -d "$OUT/shaderc.xcframework" ]; then
    if [ ! -d "$WORK/shaderc" ]; then
        git clone --depth 1 --branch "$SHADERC_VERSION" https://github.com/google/shaderc.git "$WORK/shaderc"
        (cd "$WORK/shaderc" && python3 utils/git-sync-deps)
    fi
    build_shaderc iphonesimulator
    build_shaderc iphoneos
    xcodebuild -create-xcframework \
        -framework "$WORK/stage/iphoneos/shaderc.framework" \
        -framework "$WORK/stage/iphonesimulator/shaderc.framework" \
        -output "$OUT/shaderc.xcframework"
fi

if [ ! -d "$OUT/MoltenVK.xcframework" ]; then
    if [ ! -d "$WORK/MoltenVK" ]; then
        curl -fL -o "$WORK/MoltenVK-all.tar" "https://github.com/KhronosGroup/MoltenVK/releases/download/$MOLTENVK_VERSION/MoltenVK-all.tar"
        tar xf "$WORK/MoltenVK-all.tar" -C "$WORK"
        rm "$WORK/MoltenVK-all.tar"
    fi
    source="$WORK/MoltenVK/MoltenVK/dynamic/MoltenVK.xcframework"
    xcodebuild -create-xcframework \
        -framework "$source/ios-arm64/MoltenVK.framework" \
        -framework "$source/ios-arm64_x86_64-simulator/MoltenVK.framework" \
        -output "$OUT/MoltenVK.xcframework"
fi

echo "Done: $OUT"
