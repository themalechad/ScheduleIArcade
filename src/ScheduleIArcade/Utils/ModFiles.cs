using System.Text;

namespace ScheduleIArcade.Utils;

/// <summary>
/// File helpers that stay correct when a mod manager (Vortex) deploys files as symlinks.
/// On Windows, <see cref="FileInfo.Length"/> for a SymbolicLink reparse point is 0 even though
/// <see cref="File.OpenRead"/> follows the link and returns the real stream (FileInfo.LinkTarget
/// can also be empty). Never treat Length == 0 as "missing". Always open the file and use the
/// stream length / header. Lookups are case-insensitive and do not depend on the process cwd.
/// </summary>
internal static class ModFiles
{
    public const int MinDoomWadBytes = 4096;

    public static string? ResolveExisting(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (TryOpen(path, out FileStream? stream, out string resolved))
        {
            stream!.Dispose();
            return resolved;
        }
        return null;
    }

    public static string? ResolveExisting(string fileName, IEnumerable<string?> searchDirectories)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        foreach (string? dir in searchDirectories)
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            string? found = ResolveExisting(Path.Combine(dir, fileName));
            if (found != null)
                return found;
        }

        return null;
    }

    public static bool TryOpen(string? path, out FileStream? stream, out string resolvedPath)
    {
        stream = null;
        resolvedPath = path ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (TryOpenExact(path, out stream))
        {
            resolvedPath = Path.GetFullPath(path);
            return true;
        }

        string? ci = FindCaseInsensitive(path);
        if (ci != null && TryOpenExact(ci, out stream))
        {
            resolvedPath = Path.GetFullPath(ci);
            return true;
        }

        return false;
    }

    private static bool TryOpenExact(string path, out FileStream? stream)
    {
        stream = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException
                                   or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                   or System.Security.SecurityException)
        {
            if (stream != null)
            {
                stream.Dispose();
                stream = null;
            }
            return false;
        }
    }

    public static long GetLength(string? path)
    {
        if (!TryOpen(path, out FileStream? stream, out _))
            return -1;
        using (stream)
            return stream!.Length;
    }

    public static byte[]? ReadAllBytes(string? path)
    {
        if (!TryOpen(path, out FileStream? stream, out _))
            return null;
        using (stream)
        {
            if (stream!.Length > int.MaxValue)
                throw new IOException("File is too large to read into memory.");
            byte[] buffer = new byte[(int)stream.Length];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0)
                    break;
                read += n;
            }
            if (read != buffer.Length)
                Array.Resize(ref buffer, read);
            return buffer;
        }
    }

    public static string[] ReadAllLines(string? path)
    {
        byte[]? bytes = ReadAllBytes(path);
        if (bytes == null)
            return Array.Empty<string>();
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        while (reader.ReadLine() is string line)
            lines.Add(line);
        return lines.ToArray();
    }

    /// <summary>True if the path opens as a Doom IWAD or PWAD at least <see cref="MinDoomWadBytes"/> long.</summary>
    public static bool IsDoomWad(string? path) => IsDoomWad(path, requireIwad: false);

    /// <summary>Header check on the opened stream; <paramref name="requireIwad"/> rejects PWADs.</summary>
    public static bool IsDoomWad(string? path, bool requireIwad)
    {
        if (!TryOpen(path, out FileStream? stream, out _))
            return false;
        using (stream)
        {
            if (stream!.Length < MinDoomWadBytes)
                return false;
            byte[] header = new byte[4];
            if (stream.Read(header, 0, 4) != 4)
                return false;
            string magic = Encoding.ASCII.GetString(header);
            return requireIwad ? magic == "IWAD" : magic is "IWAD" or "PWAD";
        }
    }

    public static string? FindCaseInsensitive(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            string name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name))
                return FindCaseInsensitiveDirectory(path);

            string? resolvedDir = string.IsNullOrEmpty(dir) ? null : FindCaseInsensitiveDirectory(dir);
            if (resolvedDir == null)
                return null;

            foreach (string entry in Directory.EnumerateFileSystemEntries(resolvedDir))
            {
                if (string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase))
                    return entry;
            }
        }
        catch
        {
        }
        return null;
    }

    public static string? FindCaseInsensitiveDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                return Path.GetFullPath(dir);

            string full = Path.GetFullPath(dir);
            string? parent = Path.GetDirectoryName(full);
            string name = Path.GetFileName(full);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
                return Directory.Exists(full) ? full : null;

            string? resolvedParent = FindCaseInsensitiveDirectory(parent);
            if (resolvedParent == null)
                return null;

            foreach (string entry in Directory.EnumerateDirectories(resolvedParent))
            {
                if (string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase))
                    return entry;
            }
        }
        catch
        {
        }
        return null;
    }
}
