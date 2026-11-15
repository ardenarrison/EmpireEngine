using System.Runtime.InteropServices;

namespace Engine.Brutal.Metal;

// The plain C structs Metal and Core Animation take by value.
[StructLayout(LayoutKind.Sequential)]
internal struct MTLClearColor(double r, double g, double b, double a)
{
    public double Red = r, Green = g, Blue = b, Alpha = a;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MTLViewport(double x, double y, double width, double height)
{
    public double X = x, Y = y, Width = width, Height = height, ZNear = 0, ZFar = 1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MTLScissorRect(int x, int y, int width, int height)
{
    public nuint X = (nuint)Math.Max(x, 0), Y = (nuint)Math.Max(y, 0), Width = (nuint)Math.Max(width, 0), Height = (nuint)Math.Max(height, 0);
}

[StructLayout(LayoutKind.Sequential)]
internal struct MTLRegion(int width, int height)
{
    public nuint X, Y, Z;
    public nuint Width = (nuint)width, Height = (nuint)height, Depth = 1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CGSize(double width, double height)
{
    public double Width = width, Height = height;
}

// Calls into the Objective-C runtime, which is how C# talks to Metal and Core Animation on a Mac. Every message goes through
// objc_msgSend, cast to the exact C signature of the method; each overload here is one signature the Metal code uses. Objects made
// with alloc, new or copy belong to the caller and are given back with Release; anything else is autoreleased, so frames and
// setup run inside an AutoreleasePool.
internal static unsafe class ObjC
{
    private const string Library = "/usr/lib/libobjc.A.dylib";

    [DllImport(Library, EntryPoint = "objc_getClass")] private static extern nint GetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Library, EntryPoint = "sel_registerName")] private static extern nint RegisterName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Library, EntryPoint = "objc_autoreleasePoolPush")] internal static extern nint PoolPush();
    [DllImport(Library, EntryPoint = "objc_autoreleasePoolPop")] internal static extern void PoolPop(nint pool);

    private static readonly nint MsgSend = NativeLibrary.GetExport(NativeLibrary.Load(Library), "objc_msgSend");
    private static readonly Dictionary<string, nint> Selectors = new();

    public static nint Class(string name)
    {
        nint cls = GetClass(name);
        return cls != 0 ? cls : throw new InvalidOperationException($"The Objective-C class {name} isn't available.");
    }

    public static nint Sel(string name)
    {
        lock (Selectors)
        {
            if (!Selectors.TryGetValue(name, out nint sel))
                Selectors[name] = sel = RegisterName(name);
            return sel;
        }
    }

    // Returning an object (or a pointer).
    public static nint Send(nint o, string sel) => ((delegate* unmanaged<nint, nint, nint>)MsgSend)(o, Sel(sel));
    public static nint Send(nint o, string sel, nint a) => ((delegate* unmanaged<nint, nint, nint, nint>)MsgSend)(o, Sel(sel), a);
    public static nint Send(nint o, string sel, nint a, nint b) => ((delegate* unmanaged<nint, nint, nint, nint, nint>)MsgSend)(o, Sel(sel), a, b);
    public static nint Send(nint o, string sel, nint a, nint b, nint c) => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint>)MsgSend)(o, Sel(sel), a, b, c);
    public static nint SendU(nint o, string sel, nuint a) => ((delegate* unmanaged<nint, nint, nuint, nint>)MsgSend)(o, Sel(sel), a);
    public static nint SendU(nint o, string sel, nuint a, nuint b) => ((delegate* unmanaged<nint, nint, nuint, nuint, nint>)MsgSend)(o, Sel(sel), a, b);
    public static nint SendTexture(nint o, string sel, nuint format, nuint width, nuint height, byte mipmapped) =>
        ((delegate* unmanaged<nint, nint, nuint, nuint, nuint, byte, nint>)MsgSend)(o, Sel(sel), format, width, height, mipmapped);

    // Returning a number.
    public static double SendDouble(nint o, string sel) => ((delegate* unmanaged<nint, nint, double>)MsgSend)(o, Sel(sel));
    public static nuint SendNUInt(nint o, string sel) => ((delegate* unmanaged<nint, nint, nuint>)MsgSend)(o, Sel(sel));
    public static bool SendBool(nint o, string sel, nuint a) => ((delegate* unmanaged<nint, nint, nuint, byte>)MsgSend)(o, Sel(sel), a) != 0;

    // Returning nothing.
    public static void Call(nint o, string sel) => ((delegate* unmanaged<nint, nint, void>)MsgSend)(o, Sel(sel));
    public static void Call(nint o, string sel, nint a) => ((delegate* unmanaged<nint, nint, nint, void>)MsgSend)(o, Sel(sel), a);
    public static void CallU(nint o, string sel, nuint a) => ((delegate* unmanaged<nint, nint, nuint, void>)MsgSend)(o, Sel(sel), a);
    public static void CallBool(nint o, string sel, bool a) => ((delegate* unmanaged<nint, nint, byte, void>)MsgSend)(o, Sel(sel), a ? (byte)1 : (byte)0);
    public static void CallDouble(nint o, string sel, double a) => ((delegate* unmanaged<nint, nint, double, void>)MsgSend)(o, Sel(sel), a);
    public static void CallFloat(nint o, string sel, float a) => ((delegate* unmanaged<nint, nint, float, void>)MsgSend)(o, Sel(sel), a);
    public static void Call(nint o, string sel, MTLClearColor a) => ((delegate* unmanaged<nint, nint, MTLClearColor, void>)MsgSend)(o, Sel(sel), a);
    public static void Call(nint o, string sel, MTLViewport a) => ((delegate* unmanaged<nint, nint, MTLViewport, void>)MsgSend)(o, Sel(sel), a);
    public static void Call(nint o, string sel, MTLScissorRect a) => ((delegate* unmanaged<nint, nint, MTLScissorRect, void>)MsgSend)(o, Sel(sel), a);
    public static void Call(nint o, string sel, CGSize a) => ((delegate* unmanaged<nint, nint, CGSize, void>)MsgSend)(o, Sel(sel), a);
    public static void Call(nint o, string sel, nint a, nuint b) => ((delegate* unmanaged<nint, nint, nint, nuint, void>)MsgSend)(o, Sel(sel), a, b);
    public static void Call(nint o, string sel, nint a, nuint b, nuint c) => ((delegate* unmanaged<nint, nint, nint, nuint, nuint, void>)MsgSend)(o, Sel(sel), a, b, c);
    public static void CallU(nint o, string sel, nuint a, nuint b, nuint c) => ((delegate* unmanaged<nint, nint, nuint, nuint, nuint, void>)MsgSend)(o, Sel(sel), a, b, c);
    public static void CallU(nint o, string sel, nuint a, nuint b, nuint c, nuint d, nuint e) =>
        ((delegate* unmanaged<nint, nint, nuint, nuint, nuint, nuint, nuint, void>)MsgSend)(o, Sel(sel), a, b, c, d, e);
    public static void CallIndexed(nint o, string sel, nuint type, nuint count, nuint indexType, nint buffer, nuint offset) =>
        ((delegate* unmanaged<nint, nint, nuint, nuint, nuint, nint, nuint, void>)MsgSend)(o, Sel(sel), type, count, indexType, buffer, offset);
    public static void Call(nint o, string sel, nint bytes, nuint bytesPerRow, MTLRegion region, nuint level) =>
        ((delegate* unmanaged<nint, nint, nint, nuint, MTLRegion, nuint, void>)MsgSend)(o, Sel(sel), bytes, bytesPerRow, region, level);
    public static void Call(nint o, string sel, MTLRegion region, nuint level, nint bytes, nuint bytesPerRow) =>
        ((delegate* unmanaged<nint, nint, MTLRegion, nuint, nint, nuint, void>)MsgSend)(o, Sel(sel), region, level, bytes, bytesPerRow);

    public static void Release(nint o)
    {
        if (o != 0) Call(o, "release");
    }

    public static nint Retain(nint o) => o == 0 ? 0 : Send(o, "retain");

    // A new instance of a class, owned by the caller.
    public static nint New(string cls) => Send(Send(Class(cls), "alloc"), "init");

    // An NSString owned by the caller.
    public static nint String(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text + "\0");
        fixed (byte* p = bytes)
            return Send(Send(Class("NSString"), "alloc"), "initWithUTF8String:", (nint)p);
    }

    public static string? ToText(nint nsString) =>
        nsString == 0 ? null : Marshal.PtrToStringUTF8(Send(nsString, "UTF8String"));

    // The message of an NSError, or a stand-in when there is none.
    public static string ErrorText(nint error) =>
        error == 0 ? "unknown error" : ToText(Send(error, "localizedDescription")) ?? "unknown error";
}

// Collects the autoreleased objects made while it is open, and frees them when disposed.
internal readonly struct AutoreleasePool : IDisposable
{
    private readonly nint _pool;
    private AutoreleasePool(nint pool) => _pool = pool;
    public static AutoreleasePool Open() => new(ObjC.PoolPush());
    public void Dispose() => ObjC.PoolPop(_pool);
}
