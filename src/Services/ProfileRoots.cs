namespace ProfileMirrorSync.Services;

/// <summary>Resolves actual file-system paths for each profile folder.</summary>
public sealed record SyncRootSpec(string Name, string SourcePath, string RelativePrefix);

public static class ProfileRoots
{
    private static readonly string UserProfile =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static List<SyncRootSpec> GetDefaultRoots(
        bool desktop, bool documents, bool downloads, bool pictures,
        bool videos, bool music, bool favorites, bool contacts,
        bool links, bool searches, bool savedGames,
        bool appDataRoaming, bool appDataLocal, bool appDataLocalLow)
    {
        var list = new List<SyncRootSpec>();

        Add(list, desktop,          "Desktop",         Environment.SpecialFolder.DesktopDirectory,       null);
        Add(list, documents,        "Documents",       Environment.SpecialFolder.MyDocuments,             null);
        AddPath(list, downloads,    "Downloads",       Path.Combine(UserProfile, "Downloads"),            "Downloads");
        Add(list, pictures,         "Pictures",        Environment.SpecialFolder.MyPictures,              null);
        Add(list, videos,           "Videos",          Environment.SpecialFolder.MyVideos,                null);
        Add(list, music,            "Music",           Environment.SpecialFolder.MyMusic,                 null);
        AddPath(list, savedGames,   "SavedGames",      Path.Combine(UserProfile, "Saved Games"),          "SavedGames");
        Add(list, favorites,        "Favorites",       Environment.SpecialFolder.Favorites,               null);
        AddPath(list, contacts,     "Contacts",        Path.Combine(UserProfile, "Contacts"),             "Contacts");
        AddPath(list, links,        "Links",           Path.Combine(UserProfile, "Links"),                "Links");
        AddPath(list, searches,     "Searches",        Path.Combine(UserProfile, "Searches"),             "Searches");
        Add(list, appDataRoaming,   "AppData_Roaming", Environment.SpecialFolder.ApplicationData,        "AppData\\Roaming");
        Add(list, appDataLocal,     "AppData_Local",   Environment.SpecialFolder.LocalApplicationData,   "AppData\\Local");
        AddPath(list, appDataLocalLow, "AppData_LocalLow",
            Path.Combine(UserProfile, "AppData", "LocalLow"), "AppData\\LocalLow");

        return list.Where(r => Directory.Exists(r.SourcePath)).ToList();
    }

    /// <summary>Build SyncRootSpec entries for user-defined arbitrary folders.</summary>
    public static List<SyncRootSpec> GetCustomRoots(IEnumerable<string> paths)
    {
        var list = new List<SyncRootSpec>();
        foreach (string raw in paths)
        {
            string path = raw.Trim();
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;

            // Derive a safe name and relative prefix from the folder name
            string name   = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name)) name = "CustomRoot";

            // Make relative prefix unique per source drive/path to avoid collisions
            string prefix = $"Custom\\{SanitizeName(path)}";
            list.Add(new SyncRootSpec($"Custom:{name}", path, prefix));
        }
        return list;
    }

    private static string SanitizeName(string path)
    {
        // Turn e.g. "C:\Users\Den\Work" into "C_Users_Den_Work"
        return string.Concat(path
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Select(c => char.IsLetterOrDigit(c) ? c : '_'));
    }

    private static void Add(List<SyncRootSpec> list, bool include, string name,
        Environment.SpecialFolder folder, string? prefix)
    {
        if (!include) return;
        string path = Environment.GetFolderPath(folder);
        if (string.IsNullOrEmpty(path)) return;
        list.Add(new SyncRootSpec(name, path, prefix ?? Path.GetFileName(path)));
    }

    private static void AddPath(List<SyncRootSpec> list, bool include, string name,
        string path, string prefix)
    {
        if (!include || string.IsNullOrEmpty(path)) return;
        list.Add(new SyncRootSpec(name, path, prefix));
    }
}
