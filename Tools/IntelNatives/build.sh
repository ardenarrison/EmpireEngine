#!/bin/sh
# Builds the Intel Mac (x86_64) native libraries that BRUTAL's NuGet packages don't ship, into ThirdParty/IntelMac.
# Needs a Mac with Xcode's command line tools, cmake and curl. See README.txt.
set -e
here="$(cd "$(dirname "$0")" && pwd)"
repo="$here/../.."
work="${TMPDIR:-/tmp}/empire-intel-natives"
out="$repo/ThirdParty/IntelMac"
rm -rf "$work" && mkdir -p "$work" "$out"
cd "$work"

# GLFW (zlib licence), with BRUTAL's extra scroll-detail and trackpad callbacks added by glfw-empire.patch.
git clone -q https://github.com/glfw/glfw.git glfw
git -C glfw checkout -q 92dcf4ce74f2e2554a98fea09be7c705c17daa5a
git -C glfw apply "$here/glfw-empire.patch"
cmake -S glfw -B glfw/build -DCMAKE_BUILD_TYPE=Release -DCMAKE_OSX_ARCHITECTURES=x86_64 -DCMAKE_OSX_DEPLOYMENT_TARGET=11.0 \
    -DBUILD_SHARED_LIBS=ON -DGLFW_BUILD_EXAMPLES=OFF -DGLFW_BUILD_TESTS=OFF -DGLFW_BUILD_DOCS=OFF >/dev/null
cmake --build glfw/build -j8 >/dev/null
cp glfw/build/src/libglfw.3.6.dylib "$out/libglfw.dylib"
install_name_tool -id @rpath/libglfw.dylib "$out/libglfw.dylib"

# Dear ImGui (MIT), the fork BRUTAL uses, plus the plain-C exports BRUTAL's bindings call.
curl -sL https://github.com/RocketWerkz/imgui/archive/refs/tags/v1.92.2-docking-brutal.tar.gz | tar xz
imgui="$work/imgui-1.92.2-docking-brutal"
[ -d "$imgui" ] || imgui="$(ls -d "$work"/imgui-* | head -1)"
for f in imgui imgui_draw imgui_tables imgui_widgets imgui_demo; do
    clang++ -arch x86_64 -mmacosx-version-min=11.0 -std=c++17 -O2 -fvisibility=hidden -I"$imgui" -c "$imgui/$f.cpp" -o "$f.o" &
done
wait
clang++ -arch x86_64 -mmacosx-version-min=11.0 -std=c++17 -O2 -fvisibility=hidden -I"$imgui" -c "$here/imgui_wrappers.cpp" -o wrappers.o
clang++ -arch x86_64 -dynamiclib *.o -o "$out/libimgui.dylib" -install_name @rpath/libimgui.dylib

# libVulkanEx: four small helpers BRUTAL's Vulkan bindings expect beside the loader.
clang -arch x86_64 -mmacosx-version-min=11.0 -O2 -dynamiclib -fvisibility=hidden "$here/VulkanEx.c" -o "$out/libVulkanEx.dylib" \
    -install_name @rpath/libVulkanEx.dylib "$repo/ThirdParty/Vulkan/osx-x64/libvulkan.1.dylib" -Wl,-rpath,@loader_path

codesign --force -s - "$out"/libglfw.dylib "$out"/libimgui.dylib "$out"/libVulkanEx.dylib
echo "Built into $out (libshaderc_shared.1.dylib is the x86_64 half of the Vulkan SDK's, see README.txt)"
