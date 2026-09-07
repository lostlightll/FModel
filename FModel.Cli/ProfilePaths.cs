namespace FModel.Cli;

public static class ProfilePaths
{
    public static string Resolve(string path, string profileDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Profile paths cannot be empty.");
        return Path.GetFullPath(path, profileDirectory);
    }
}
