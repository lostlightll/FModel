namespace FModel.Cli;

public static class OutputFiles
{
    public static string Resolve(string root, string relative)
    {
        relative = relative.Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative.Split('/').Any(p => p is "" or "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new IOException("Unsafe asset output path.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsWithin(path, root)) throw new IOException("Output escapes its configured directory.");
        RejectLinks(path);
        return path;
    }

    public static bool IsWithin(string path, string root) =>
        Path.GetFullPath(path).Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) ||
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Output paths cannot contain symbolic links or junctions.");
    }

    public static void Write(string path, Action<Stream> write)
    {
        RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        try { write(stream); }
        catch
        {
            stream.Dispose();
            File.Delete(path);
            throw;
        }
    }
}
