Intel Mac native libraries
==========================

BRUTAL's NuGet packages hold their native libraries (GLFW, Dear ImGui, the Vulkan helpers, shaderc) for Apple Silicon,
Windows and Linux only. For Intel Macs (osx-x64) Empire Engine therefore carries its own, in ThirdParty/IntelMac, and
Directory.Build.targets copies them next to the editor and the player when building for osx-x64.

  libglfw.dylib            GLFW (zlib licence) at commit 92dcf4c, plus glfw-empire.patch: the scroll-detail, trackpad zoom
                           and trackpad rotate callbacks BRUTAL's GLFW has and its bindings call.
  libimgui.dylib           Dear ImGui 1.92.2 docking, built from RocketWerkz/imgui tag v1.92.2-docking-brutal (the fork
                           BRUTAL uses; MIT), plus imgui_wrappers.cpp: the plain C functions BRUTAL's bindings import.
                           Only the functions the editor's backends use are exported; a binding that needs another one
                           stops with an EntryPointNotFoundException naming it (add it to imgui_wrappers.cpp).
  libVulkanEx.dylib        Four helper functions (vxDestroyImages and friends), from VulkanEx.c.
  libshaderc_shared.1.dylib  The x86_64 half of the one in LunarG's Vulkan SDK 1.4.363.0 (Apache 2.0), see
                           ThirdParty/Vulkan/osx-x64/README.txt.

Run build.sh on a Mac to rebuild the first three (it downloads GLFW and the ImGui fork). It does not touch shaderc.

Status: the editor with these libraries starts, draws scenes, selects and focuses objects and shows the code view when
run as an Intel program under Rosetta on an Apple Silicon Mac. It has not been run on a real Intel Mac.
