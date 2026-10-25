// Exports for Empire Engine's Intel Mac build of Dear ImGui (the Brutal fork, v1.92.2-docking-brutal). BRUTAL's bindings
// call these plain C names; each forwards to the matching ImGui function. Only what the editor needs is here.
#include "imgui.h"

#define EXPORT extern "C" __attribute__((visibility("default")))

EXPORT ImGuiContext* CreateContext(ImFontAtlas* shared_font_atlas) { return ImGui::CreateContext(shared_font_atlas); }
EXPORT void DestroyContext(ImGuiContext* ctx) { ImGui::DestroyContext(ctx); }
EXPORT const char* GetClipboardText() { return ImGui::GetClipboardText(); }
EXPORT ImDrawData* GetDrawData() { return ImGui::GetDrawData(); }
EXPORT ImGuiIO* GetIO() { return &ImGui::GetIO(); }
EXPORT bool IsKeyPressed(ImGuiKey key, bool repeat) { return ImGui::IsKeyPressed(key, repeat); }
EXPORT bool IsMouseDoubleClicked(ImGuiMouseButton button) { return ImGui::IsMouseDoubleClicked(button); }
EXPORT void NewFrame() { ImGui::NewFrame(); }
EXPORT void Render() { ImGui::Render(); }
EXPORT void SetClipboardText(const char* text) { ImGui::SetClipboardText(text); }

EXPORT ImGuiContext* GetCurrentContext() { return ImGui::GetCurrentContext(); }
EXPORT void DestroyPlatformWindows() { ImGui::DestroyPlatformWindows(); }
EXPORT ImGuiViewport* FindViewportByPlatformHandle(void* platform_handle) { return ImGui::FindViewportByPlatformHandle(platform_handle); }
EXPORT int GetFrameCount() { return ImGui::GetFrameCount(); }
EXPORT ImGuiViewport* GetMainViewport() { return ImGui::GetMainViewport(); }
EXPORT ImGuiMouseCursor GetMouseCursor() { return ImGui::GetMouseCursor(); }
EXPORT ImGuiPlatformIO* GetPlatformIO() { return &ImGui::GetPlatformIO(); }

EXPORT void ImGuiIO_AddKeyEvent(ImGuiIO* io, ImGuiKey key, bool down) { io->AddKeyEvent(key, down); }
EXPORT void ImGuiIO_AddKeyAnalogEvent(ImGuiIO* io, ImGuiKey key, bool down, float v) { io->AddKeyAnalogEvent(key, down, v); }
EXPORT void ImGuiIO_AddMousePosEvent(ImGuiIO* io, float x, float y) { io->AddMousePosEvent(x, y); }
EXPORT void ImGuiIO_AddMouseButtonEvent(ImGuiIO* io, int button, bool down) { io->AddMouseButtonEvent(button, down); }
EXPORT void ImGuiIO_AddMouseWheelEvent(ImGuiIO* io, float wheel_x, float wheel_y) { io->AddMouseWheelEvent(wheel_x, wheel_y); }
EXPORT void ImGuiIO_AddMouseSourceEvent(ImGuiIO* io, ImGuiMouseSource source) { io->AddMouseSourceEvent(source); }
EXPORT void ImGuiIO_AddMouseViewportEvent(ImGuiIO* io, ImGuiID id) { io->AddMouseViewportEvent(id); }
EXPORT void ImGuiIO_AddFocusEvent(ImGuiIO* io, bool focused) { io->AddFocusEvent(focused); }
EXPORT void ImGuiIO_AddInputCharacter(ImGuiIO* io, unsigned int c) { io->AddInputCharacter(c); }
EXPORT void ImGuiIO_AddInputCharacterUTF16(ImGuiIO* io, ImWchar16 c) { io->AddInputCharacterUTF16(c); }
EXPORT void ImGuiIO_AddInputCharactersUTF8(ImGuiIO* io, const char* str) { io->AddInputCharactersUTF8(str); }

EXPORT void ImTextureData_Create(ImTextureData* t, ImTextureFormat format, int w, int h) { t->Create(format, w, h); }
EXPORT void ImTextureData_DestroyPixels(ImTextureData* t) { t->DestroyPixels(); }
EXPORT void* ImTextureData_GetPixels(ImTextureData* t) { return t->GetPixels(); }
EXPORT void* ImTextureData_GetPixelsAt(ImTextureData* t, int x, int y) { return t->GetPixelsAt(x, y); }
EXPORT int ImTextureData_GetSizeInBytes(ImTextureData* t) { return t->GetSizeInBytes(); }
EXPORT int ImTextureData_GetPitch(ImTextureData* t) { return t->GetPitch(); }
EXPORT ImTextureRef ImTextureData_GetTexRef(ImTextureData* t) { return t->GetTexRef(); }
EXPORT ImTextureID ImTextureData_GetTexID(ImTextureData* t) { return t->GetTexID(); }
EXPORT void ImTextureData_SetTexID(ImTextureData* t, ImTextureID id) { t->SetTexID(id); }
EXPORT void ImTextureData_SetStatus(ImTextureData* t, ImTextureStatus status) { t->Status = status; }

EXPORT void* MemAlloc(size_t size) { return ImGui::MemAlloc(size); }
EXPORT void MemFree(void* ptr) { ImGui::MemFree(ptr); }

// Platform callbacks that return an ImVec2 (window position, size, framebuffer scale). The managed backend hands over a
// function returning a plain two-float struct; the shims below return it as the ImVec2 ImGui expects.
struct Pod2 { float x, y; };
#define VEC2_SHIM(Name) \
    extern "C" { __attribute__((visibility("default"))) void* Platform_##Name##_ManagedFunctionPointer = nullptr; } \
    static ImVec2 Shim_##Name(ImGuiViewport* viewport) \
    { \
        Pod2 r = ((Pod2(*)(ImGuiViewport*))Platform_##Name##_ManagedFunctionPointer)(viewport); \
        return ImVec2(r.x, r.y); \
    } \
    EXPORT void* Get_##Name##_InteropPointer() { return (void*)&Shim_##Name; }

VEC2_SHIM(GetWindowPos)
VEC2_SHIM(GetWindowSize)
VEC2_SHIM(GetWindowFramebufferScale)
