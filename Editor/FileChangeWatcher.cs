using System.Security.Cryptography;

namespace Editor;

// Notices when a file the editor has open was changed by something else (another editor, git, a build step).
// Call Accept after the editor loads or saves the file, then Poll now and then: it returns true once when the file's
// contents differ from what was accepted and have stopped changing, so a file still being written is not read half way.
public sealed class FileChangeWatcher
{
    private readonly string _path;
    private (long Ticks, long Length)? _stamp;     // the file's date and size when it was last accepted
    private (long Ticks, long Length)? _pending;   // a newer date and size seen on the last poll, waiting to settle
    private string? _hash;                         // the contents when last accepted

    public FileChangeWatcher(string path) => _path = path;

    public string Path => _path;

    // The file as it is now is the one the editor has.
    public void Accept()
    {
        _stamp = Stamp();
        _pending = null;
        _hash = Hash();
    }

    // True once when the file has a new, settled content. After that it counts as accepted: the caller either reloads it or
    // keeps its own version, and is told again only when the file changes again.
    public bool Poll()
    {
        var now = Stamp();
        if (now is null || now == _stamp)
        {
            _pending = null;
            return false;
        }

        if (now != _pending)
        {
            _pending = now;   // changed since the last poll; wait one more to see that it has stopped
            return false;
        }

        _pending = null;
        _stamp = now;
        string? hash = Hash();
        if (hash is null || hash == _hash) return false;   // touched, or unreadable for now, but the contents are the same
        _hash = hash;
        return true;
    }

    private (long, long)? Stamp()
    {
        try
        {
            var info = new FileInfo(_path);
            return info.Exists ? (info.LastWriteTimeUtc.Ticks, info.Length) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private string? Hash()
    {
        try { return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_path))); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
