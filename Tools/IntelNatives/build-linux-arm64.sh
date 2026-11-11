#!/bin/sh
# Cross-compiles the Linux arm64 native libraries that BRUTAL's NuGet packages don't ship (GLFW, Dear ImGui, libVulkanEx) into
# ThirdParty/LinuxArm64, from a Mac or Linux computer. Needs zig (brew install zig), llvm-strip, git, curl, and the X11 header
# files (on a Mac: brew install libx11 libxrandr libxinerama libxcursor libxi libxext libxrender libxfixes xorgproto).
# GLFW is built for X11 only (Wayland desktops run it through XWayland); it finds X11 at run time, so nothing is linked.
set -e
here="$(cd "$(dirname "$0")" && pwd)"
repo="$here/../.."
work="${TMPDIR:-/tmp}/empire-linux-arm64-natives"
out="$repo/ThirdParty/LinuxArm64"
target=aarch64-linux-gnu.2.28
strip="${LLVM_STRIP:-llvm-strip}"
rm -rf "$work" && mkdir -p "$work" "$out"
cd "$work"

git clone -q https://github.com/glfw/glfw.git glfw
git -C glfw checkout -q 92dcf4ce74f2e2554a98fea09be7c705c17daa5a
git -C glfw apply "$here/glfw-empire.patch"
(cd glfw && zig cc -target $target -O2 -fPIC -shared -I"${X11_INCLUDE:-/opt/homebrew/include}" -D_GLFW_X11 -D_GLFW_BUILD_DLL -Iinclude -Isrc \
    src/context.c src/init.c src/input.c src/monitor.c src/platform.c src/vulkan.c src/window.c src/egl_context.c src/osmesa_context.c \
    src/null_init.c src/null_monitor.c src/null_window.c src/null_joystick.c \
    src/posix_module.c src/posix_poll.c src/posix_thread.c src/posix_time.c src/linux_joystick.c \
    src/x11_init.c src/x11_monitor.c src/x11_window.c src/xkb_unicode.c src/glx_context.c \
    -Wl,-soname,libglfw.so.3 -o "$out/libglfw.so.3")
cp "$out/libglfw.so.3" "$out/libglfw.so"

curl -sL https://github.com/RocketWerkz/imgui/archive/refs/tags/v1.92.2-docking-brutal.tar.gz | tar xz
imgui="$(ls -d "$work"/imgui-* | head -1)"
for f in imgui imgui_draw imgui_tables imgui_widgets imgui_demo; do
    zig c++ -target $target -std=c++17 -O2 -fPIC -fvisibility=hidden -I"$imgui" -c "$imgui/$f.cpp" -o "$f.o" &
done
wait
zig c++ -target $target -std=c++17 -O2 -fPIC -fvisibility=hidden -I"$imgui" -c "$here/imgui_wrappers.cpp" -o wrappers.o 2>/dev/null
zig c++ -target $target -shared *.o -o "$out/libimgui.so"

zig cc -target $target -O2 -fPIC -shared -fvisibility=hidden "$here/VulkanEx.c" -ldl -o "$out/libVulkanEx.so"

"$strip" --strip-unneeded "$out"/*.so*
echo "Built into $out"
