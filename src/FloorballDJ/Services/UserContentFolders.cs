namespace FloorballDJ.Services;

public static class UserContentFolders
{
    public const string ProfilesFolderName = "Profiles";
    public const string CreatedJinglesFolderName = "Created Jingles";

    public static string RootDirectory => ResolveRoot();
    public static string ProfilesDirectory => EnsureSubfolder(ProfilesFolderName);
    public static string CreatedJinglesDirectory => EnsureSubfolder(CreatedJinglesFolderName);

    public static void EnsureCreated()
    {
        _ = ProfilesDirectory;
        _ = CreatedJinglesDirectory;
    }

    public static string EnsureSubfolder(string folderName, string? rootDirectory = null)
    {
        var root = string.IsNullOrWhiteSpace(rootDirectory) ? ResolveRoot() : rootDirectory;
        var path = Path.Combine(root, folderName);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ResolveRoot()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(string.IsNullOrWhiteSpace(documents)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : documents, "FloorballDJ");
    }
}
