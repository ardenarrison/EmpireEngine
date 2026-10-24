Vulkan for macOS, bundled with the editor and with games built from it, so nothing has to be installed.

  libvulkan.1.dylib    Khronos Vulkan Loader 1.4.363.0   (Apache 2.0)   https://github.com/KhronosGroup/Vulkan-Loader
  libMoltenVK.dylib    MoltenVK 1.4.2                    (Apache 2.0)   https://github.com/KhronosGroup/MoltenVK

Apple Silicon (arm64) only. Both are the Homebrew builds (formulas vulkan-loader and molten-vk) with their install
names changed to @rpath and re-signed ad hoc. The license texts are in the Licenses folder at the repo root, and the
editor's Help > Credits shows them.

Directory.Build.targets copies these next to the editor and the player (libvulkan.1.dylib beside libVulkanEx.dylib, and
MoltenVK into vulkan/icd.d with a driver manifest). To use other copies, for example the Vulkan SDK's on an Intel Mac,
set the EmpireVulkanLoader and EmpireMoltenVK properties (or environment variables) to their paths.

To update: replace the two files with new builds, keeping the same file names, and change the versions above.
