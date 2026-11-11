Linux arm64 native libraries
============================

BRUTAL's NuGet packages have no linux-arm64 native libraries, so Empire Engine carries its own: GLFW (zlib licence, with
Tools/IntelNatives/glfw-empire.patch, X11 only), Dear ImGui (MIT, the BRUTAL fork, only the functions the editor and game use)
and libVulkanEx. Directory.Build.targets copies them next to the program when building for linux-arm64.
Rebuild them with Tools/IntelNatives/build-linux-arm64.sh. Vulkan itself comes from the computer's libvulkan.so.1.

They were cross-compiled, and a game built with them has been run on a simulated Raspberry Pi-style Debian system. It hasn't been tried on real arm64 hardware.
