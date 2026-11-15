SPIRV-Cross for the Metal renderer
==================================

  osx-arm64/libspirv-cross-c-shared.dylib   Apple Silicon
  osx-x64/libspirv-cross-c-shared.dylib     Intel Macs

SPIRV-Cross (Apache 2.0, https://github.com/KhronosGroup/SPIRV-Cross, tag vulkan-sdk-1.4.304.0) turns the engine's SPIR-V shaders
into Metal Shading Language when a project draws with Metal (Engine.Brutal/Metal/SpirvToMsl.cs). Directory.Build.targets copies the
one for the Mac being built for next to the editor and the player. Its license is Licenses/SPIRVCross-Apache.txt.

Built from that tag with only the C API, GLSL and MSL turned on, for macOS 11 and later:

  cmake -S . -B build -DCMAKE_BUILD_TYPE=Release -DCMAKE_OSX_ARCHITECTURES=arm64 (or x86_64) -DCMAKE_OSX_DEPLOYMENT_TARGET=11.0 \
      -DSPIRV_CROSS_SHARED=ON -DSPIRV_CROSS_STATIC=OFF -DSPIRV_CROSS_CLI=OFF -DSPIRV_CROSS_ENABLE_TESTS=OFF \
      -DSPIRV_CROSS_ENABLE_HLSL=OFF -DSPIRV_CROSS_ENABLE_CPP=OFF -DSPIRV_CROSS_ENABLE_REFLECT=OFF -DSPIRV_CROSS_ENABLE_UTIL=OFF \
      -DSPIRV_CROSS_ENABLE_C_API=ON -DSPIRV_CROSS_ENABLE_MSL=ON -DSPIRV_CROSS_ENABLE_GLSL=ON
  cmake --build build

then renamed to libspirv-cross-c-shared.dylib, given that name as its install name (install_name_tool -id @rpath/...), stripped
(strip -x) and signed (codesign --force -s -).
