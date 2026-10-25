Vulkan for Intel Macs (x86_64), ready to bundle (BRUTAL has no Intel Mac natives yet, so nothing runs there at the moment) with the editor and with games built from it, so nothing has to be installed.

  libvulkan.1.dylib    Khronos Vulkan Loader 1.4.363.0   (Apache 2.0)   https://github.com/KhronosGroup/Vulkan-Loader
  libMoltenVK.dylib    MoltenVK (as in LunarG Vulkan SDK 1.4.363.0)   (Apache 2.0)   https://github.com/KhronosGroup/MoltenVK

Both are the x86_64 halves of the universal libraries in the LunarG Vulkan SDK for macOS 1.4.363.0 (https://vulkan.lunarg.com),
with their install names already @rpath, re-signed ad hoc. The same license texts as the Apple Silicon copies apply (Licenses
folder at the repo root). Directory.Build.targets copies these when building for osx-x64, the same way as osx-arm64.

To update: take the x86_64 slice (lipo -thin x86_64) of the new SDK's libvulkan.1.<version>.dylib and libMoltenVK.dylib,
keep the same file names, re-sign with codesign --force -s -, and change the versions above.
