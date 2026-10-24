using System.Runtime.InteropServices;

namespace Engine.Brutal.Gfx;

// Sets the app's Dock icon on macOS. Empire Engine's programs are plain executables rather than .app bundles, so macOS has no
// icon file to read and shows a generic one unless the running app sets its own. This calls AppKit through the
// Objective-C runtime: [NSApp setApplicationIconImage:[[NSImage alloc] initWithData:png]].
// It uses nothing but P/Invoke, so Empire Engine Hub compiles this same file without referencing the engine.
public static class MacDockIcon
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr bytes, nuint length);

    // png is a PNG file's bytes. Call it on the main thread once the app's first window exists. Does nothing on other
    // systems, and quietly gives up if AppKit can't be reached (the app keeps the generic icon).
    public static void Set(byte[] png)
    {
        if (!OperatingSystem.IsMacOS() || png.Length == 0) return;
        try
        {
            NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
            var bytes = GCHandle.Alloc(png, GCHandleType.Pinned);
            try
            {
                // NSData copies the bytes, so the pinned array can be let go of afterwards.
                IntPtr data = Send(objc_getClass("NSData"), sel_registerName("dataWithBytes:length:"),
                    bytes.AddrOfPinnedObject(), (nuint)png.Length);
                IntPtr image = Send(Send(objc_getClass("NSImage"), sel_registerName("alloc")), sel_registerName("initWithData:"), data);
                if (image == IntPtr.Zero) return;
                IntPtr app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
                Send(app, sel_registerName("setApplicationIconImage:"), image);
            }
            finally
            {
                bytes.Free();
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }
}
