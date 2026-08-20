global using File = RepositoryFile;

using System.IO;

internal static class RepositoryFile
{
    public static bool Exists(string path)
    {
        if (System.IO.File.Exists(path))
        {
            return true;
        }

        var sourcePath = RedirectRootSource(path);
        return !string.Equals(sourcePath, path, StringComparison.Ordinal) &&
               System.IO.File.Exists(sourcePath);
    }

    public static string ReadAllText(string path)
    {
        if (System.IO.File.Exists(path))
        {
            return System.IO.File.ReadAllText(path);
        }

        return System.IO.File.ReadAllText(RedirectRootSource(path));
    }

    private static string RedirectRootSource(string path)
    {
        var directory = Path.GetDirectoryName(path);
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(directory) ||
            string.IsNullOrEmpty(fileName) ||
            !fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        return Path.Combine(directory, "src", fileName);
    }
}
