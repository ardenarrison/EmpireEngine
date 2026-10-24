using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Editor.Terminal;

// A program running in a pseudo-terminal: it believes it is in a real terminal window of Cols x Rows characters, so shells, editors
// and full-screen tools all work. Bytes the program writes arrive in Output; Write sends keys to it.
//
//   macOS and Linux: the system's `script` command provides the pseudo-terminal, so no native code is needed. The window size is
//     set with stty, and changed later by running stty against the same terminal.
//   Windows: the ConPTY API (Windows 10 version 1809 or newer).
public abstract class PtySession : IDisposable
{
    private readonly ConcurrentQueue<byte[]> _output = new();

    public bool HasExited { get; protected set; }
    public int? ExitCode { get; protected set; }

    // Takes everything the program has written since the last call (empty when nothing).
    public byte[] TakeOutput()
    {
        if (_output.IsEmpty) return [];
        using var all = new MemoryStream();
        while (_output.TryDequeue(out var chunk)) all.Write(chunk);
        return all.ToArray();
    }

    protected void Enqueue(byte[] chunk) => _output.Enqueue(chunk);

    public abstract void Write(ReadOnlySpan<byte> data);
    public void Write(string text) => Write(Encoding.UTF8.GetBytes(text));
    public abstract void Resize(int cols, int rows);
    public abstract void Kill();
    public abstract void Dispose();

    // Starts the user's shell (or `command`, for a program to run instead) in workingDirectory. Throws PtyException with a plain
    // explanation if it can't.
    public static PtySession Start(string workingDirectory, int cols, int rows, string[]? command = null)
    {
        cols = Math.Clamp(cols, 20, 500);
        rows = Math.Clamp(rows, 5, 200);
        return OperatingSystem.IsWindows()
            ? new WindowsPty(workingDirectory, cols, rows, command)
            : new UnixPty(workingDirectory, cols, rows, command);
    }

    // The environment a program in the terminal should get: the editor's, without the build-tool and loader settings the editor's own
    // launcher put there, and saying what kind of terminal this is.
    internal static void PrepareEnvironment(IDictionary<string, string?> env)
    {
        foreach (var name in new[] { "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath", "DOTNET_HOST_PATH",
                                     "DYLD_FALLBACK_LIBRARY_PATH", "DYLD_LIBRARY_PATH", "LD_LIBRARY_PATH" })
            env.Remove(name);
        env["TERM"] = "xterm-256color";
        env["COLORTERM"] = "truecolor";
        env["TERM_PROGRAM"] = "EmpireEngineEditor";
        env["DOTNET_NOLOGO"] = "1";
        env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
    }
}

public sealed class PtyException(string message) : Exception(message);

// ---- macOS and Linux -----------------------------------------------------------------------------------------------------

internal sealed class UnixPty : PtySession
{
    private readonly Process _process;
    private readonly Stream _input;
    private readonly string _ttyFile;
    private bool _disposed;

    public UnixPty(string workingDirectory, int cols, int rows, string[]? command)
    {
        string script = new[] { "/usr/bin/script", "/bin/script", "/usr/local/bin/script" }.FirstOrDefault(File.Exists)
            ?? throw new PtyException("The terminal needs the 'script' command, which wasn't found. It is part of macOS and of the util-linux package on Linux.");

        // The terminal's device name goes in a private temporary file, so the size can be changed later.
        string dir = Path.Combine(Path.GetTempPath(), "empire-terminal-" + Environment.UserName);
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _ttyFile = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tty");
        foreach (var stale in Directory.GetFiles(dir, "*.tty"))
        {
            try { if (File.GetLastWriteTimeUtc(stale) < DateTime.UtcNow.AddDays(-1)) File.Delete(stale); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        string run = command is { Length: > 0 }
            ? "exec " + string.Join(' ', command.Select(Quote))
            : "exec \"${SHELL:-/bin/sh}\" -il";
        string init = $"tty > {Quote(_ttyFile)} 2>/dev/null; stty rows {rows} cols {cols} 2>/dev/null; {run}";

        // `script` makes the terminal. It is started by a small shell that also watches the editor: if the editor is killed or
        // crashes without closing the terminal, the shell closes the terminal, which hangs up the shell inside it, so nothing is left running in the background.
        string[] scriptArgs = OperatingSystem.IsMacOS()
            ? ["-q", "/dev/null", "/bin/sh", "-c", init]
            : ["-q", "-f", "-e", "-c", init, "/dev/null"];
        string watchdog = $"{Quote(script)} {string.Join(' ', scriptArgs.Select(Quote))} <&0 & s=$!; " +
                          $"while kill -0 {Environment.ProcessId} 2>/dev/null && kill -0 $s 2>/dev/null; do sleep 1; done; " +
                          $"kill -TERM $s 2>/dev/null; sleep 1; kill -KILL $s 2>/dev/null; wait $s; r=$?; rm -f {Quote(_ttyFile)}; exit $r";

        var info = new ProcessStartInfo("/bin/sh")
        {
            WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(watchdog);
        var env = info.Environment;
        PrepareEnvironment(env);

        try { _process = Process.Start(info) ?? throw new PtyException("The terminal couldn't start."); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new PtyException("The terminal couldn't start: " + ex.Message);
        }
        _input = _process.StandardInput.BaseStream;
        _ = Task.Run(() => ReadLoop(_process.StandardOutput.BaseStream));
        _ = Task.Run(() => ReadLoop(_process.StandardError.BaseStream));
        _ = Task.Run(async () =>
        {
            try
            {
                await _process.WaitForExitAsync();
                await Task.Delay(100);   // let the last output be read first
                ExitCode = _process.ExitCode;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
            HasExited = true;
        });
    }

    private async Task ReadLoop(Stream stream)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0) Enqueue(buffer.AsSpan(0, read).ToArray());
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    public override void Write(ReadOnlySpan<byte> data)
    {
        if (HasExited || _disposed) return;
        try { _input.Write(data); _input.Flush(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    // Sets the terminal's size by running stty against it. The program is told with the usual window-change signal.
    public override void Resize(int cols, int rows)
    {
        cols = Math.Clamp(cols, 20, 500);
        rows = Math.Clamp(rows, 5, 200);
        if (HasExited || _disposed || !File.Exists(_ttyFile)) return;
        string tty;
        try { tty = File.ReadAllText(_ttyFile).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        if (!tty.StartsWith("/dev/") || tty.Any(c => c < 32 || c == '\'' || c == ' ')) return;   // only ever a device path

        try
        {
            using var stty = Process.Start(new ProcessStartInfo("/bin/sh")
            {
                ArgumentList = { "-c", $"stty rows {rows} cols {cols} < '{tty}'" },
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            });
            stty?.WaitForExit(2000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    public override void Kill()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Kill();
        HasExited = true;
        try { File.Delete(_ttyFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _process.Dispose();
    }

    // Single quotes around text for /bin/sh, so nothing in it is treated as a command.
    private static string Quote(string text) => "'" + text.Replace("'", "'\\''") + "'";
}

// ---- Windows (ConPTY) --------------------------------------------------------------------------------------------------------

internal sealed class WindowsPty : PtySession
{
    [StructLayout(LayoutKind.Sequential)] private struct Coord { public short X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb; public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    private const uint ExtendedStartupInfoPresent = 0x00080000, CreateUnicodeEnvironment = 0x00000400;
    private static readonly IntPtr PseudoConsoleAttribute = (IntPtr)0x00020016;

    [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(IntPtr console, Coord size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
        uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    private readonly IntPtr _console;
    private readonly IntPtr _process;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private bool _disposed;

    public WindowsPty(string workingDirectory, int cols, int rows, string[]? command)
    {
        CreatePipe(out var ptyInputRead, out var ourInputWrite, IntPtr.Zero, 0);
        CreatePipe(out var ourOutputRead, out var ptyOutputWrite, IntPtr.Zero, 0);
        int hr;
        try { hr = CreatePseudoConsole(new Coord { X = (short)cols, Y = (short)rows }, ptyInputRead, ptyOutputWrite, 0, out _console); }
        catch (EntryPointNotFoundException) { throw new PtyException("The terminal needs Windows 10 version 1809 or newer."); }
        ptyInputRead.Dispose();
        ptyOutputWrite.Dispose();
        if (hr != 0) throw new PtyException($"The terminal couldn't be created (error {hr}). It needs Windows 10 version 1809 or newer.");

        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        IntPtr list = Marshal.AllocHGlobal(size);
        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size)) throw new PtyException("The terminal couldn't be set up.");
            if (!UpdateProcThreadAttribute(list, 0, PseudoConsoleAttribute, _console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new PtyException("The terminal couldn't be set up.");

            var startup = new StartupInfoEx { lpAttributeList = list };
            startup.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            string shell = command is { Length: > 0 }
                ? string.Join(' ', command.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a))
                : (File.Exists(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
                    ? "\"" + Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") + "\" -NoLogo"
                    : Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe");
            string dir = Directory.Exists(workingDirectory) ? workingDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!CreateProcessW(null, new StringBuilder(shell), IntPtr.Zero, IntPtr.Zero, false, ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                    IntPtr.Zero, dir, ref startup, out var info))
                throw new PtyException($"The terminal couldn't start the shell (error {Marshal.GetLastWin32Error()}).");
            _process = info.hProcess;
            CloseHandle(info.hThread);
        }
        finally
        {
            DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
        }

        _input = new FileStream(ourInputWrite, FileAccess.Write, 1, isAsync: false);
        _output = new FileStream(ourOutputRead, FileAccess.Read, 1, isAsync: false);
        _ = Task.Run(ReadLoop);
        _ = Task.Run(async () =>
        {
            while (!_disposed)
            {
                await Task.Delay(300);
                if (GetExitCodeProcess(_process, out uint code) && code != 259 /* STILL_ACTIVE */) { ExitCode = (int)code; HasExited = true; return; }
            }
        });
    }

    private void ReadLoop()
    {
        var buffer = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = _output.Read(buffer, 0, buffer.Length)) > 0) Enqueue(buffer.AsSpan(0, read).ToArray());
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    public override void Write(ReadOnlySpan<byte> data)
    {
        if (HasExited || _disposed) return;
        try { _input.Write(data); _input.Flush(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    public override void Resize(int cols, int rows)
    {
        if (_disposed) return;
        ResizePseudoConsole(_console, new Coord { X = (short)Math.Clamp(cols, 20, 500), Y = (short)Math.Clamp(rows, 5, 200) });
    }

    public override void Kill()
    {
        if (!HasExited) TerminateProcess(_process, 1);
    }

    public override void Dispose()
    {
        if (_disposed) return;
        Kill();
        _disposed = true;
        ClosePseudoConsole(_console);
        _input.Dispose();
        _output.Dispose();
        CloseHandle(_process);
    }
}
