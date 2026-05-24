namespace ProfileMirrorSync.Services;

/// <summary>
/// Ready-made post-sync "recipes".  Selecting a preset in the Archive
/// tab fills the program path, arguments and working directory with sensible
/// maximum-setting defaults, so an admin doesn't have to remember 7-Zip / WinRAR
/// command-line flags.  Pure data + a lookup; the UI owns all the wiring.
///
/// Placeholders ({dest}, {backup}, {machine}, {user}, {date}, {time}) are
/// expanded by PostSyncRunner.ExpandPlaceholders at launch — presets only
/// produce the template strings.
///
/// Design note (Karpathy simplicity): presets are NOT persisted.  They are a
/// one-shot convenience that writes into the existing PostSyncExePath/Arguments/
/// WorkingDir fields; the saved settings remain the three plain strings, so
/// nothing downstream (PostSyncRunner, settings.json, migration) needs to know
/// presets exist.
/// </summary>
public sealed record PostSyncPreset(
    string Key,
    string DisplayName,
    string ExePath,
    string Arguments,
    string WorkingDir,
    string Hint);

public static class PostSyncPresets
{
    // The first entry is a non-preset placeholder shown when the current
    // arguments don't match any known recipe ("custom / manual setup").
    public const string CustomKey = "custom";

    public static readonly IReadOnlyList<PostSyncPreset> All = new[]
    {
        new PostSyncPreset(
            CustomKey,
            "— Свои настройки —",
            ExePath: "",
            Arguments: "",
            WorkingDir: "",
            Hint: "Ручная настройка. Выберите готовый пресет ниже, чтобы " +
                  "подставить параметры на максимальных настройках, затем при " +
                  "необходимости отредактируйте их."),

        new PostSyncPreset(
            "7zip",
            "7-Zip — архив .7z (максимальное сжатие)",
            ExePath: @"C:\Program Files\7-Zip\7z.exe",
            // -mx=9 ultra, -mmt=on multithread, -xr!backup excludes the backup
            // dir itself so runs don't nest old archives into new ones.
            Arguments: "a -t7z -mx=9 -mmt=on -xr!backup \"{backup}\\{machine}_{user}_{date}.7z\" \"{dest}\\*\"",
            WorkingDir: "",
            Hint: "Создаёт {backup}\\{machine}_{user}_{date}.7z с максимальным " +
                  "сжатием 7-Zip (-mx=9, многопоточно). Требуется установленный " +
                  "7-Zip. Каталог backup\\ исключён из архива."),

        new PostSyncPreset(
            "rar",
            "WinRAR — архив .rar (максимальное сжатие, recovery)",
            ExePath: @"C:\Program Files\WinRAR\Rar.exe",
            // a add, -m5 best, -ma5 RAR5, -md128m dict, -rr3p 3% recovery record,
            // -ep1 strip base path, -x*\backup\* exclude the backup dir.
            Arguments: "a -m5 -ma5 -md128m -rr3p -ep1 -x*\\backup\\* \"{backup}\\{machine}_{user}_{date}.rar\" \"{dest}\\*\"",
            WorkingDir: "",
            Hint: "Создаёт .rar с максимальным сжатием (-m5), форматом RAR5 и " +
                  "3% записью для восстановления (-rr3p). Требуется консольный " +
                  "Rar.exe из WinRAR (не WinRAR.exe)."),

        new PostSyncPreset(
            "zip",
            "ZIP — архив .zip через 7-Zip (совместимый)",
            ExePath: @"C:\Program Files\7-Zip\7z.exe",
            Arguments: "a -tzip -mx=9 -mmt=on -xr!backup \"{backup}\\{machine}_{user}_{date}.zip\" \"{dest}\\*\"",
            WorkingDir: "",
            Hint: "Создаёт обычный .zip (максимальное deflate-сжатие) через " +
                  "7-Zip — открывается штатными средствами Windows без " +
                  "стороннего ПО. Требуется установленный 7-Zip."),

        new PostSyncPreset(
            "robocopy",
            "Robocopy — зеркальная копия в backup\\ (без сжатия)",
            ExePath: @"C:\Windows\System32\Robocopy.exe",
            // /MIR mirror, /XD backup excludes the dest's own backup subtree,
            // /R:1 /W:1 fast retry, /MT:4 modest multithread, /NFL /NDL quiet.
            Arguments: "\"{dest}\" \"{backup}\\mirror\" /MIR /XD \"{backup}\" /R:1 /W:1 /MT:4 /NFL /NDL /NP",
            WorkingDir: "",
            Hint: "Делает несжатую зеркальную копию приёмника в " +
                  "{backup}\\mirror через встроенный Robocopy (/MIR). Полезно " +
                  "как вторая «горячая» копия. Robocopy входит в состав Windows."),

        new PostSyncPreset(
            "forfiles_delete",
            "Удаление архивов старше N дней (forfiles)",
            ExePath: @"C:\Windows\System32\forfiles.exe",
            // /P path, /S recurse, /M mask, /D -<days>, /C command (delete).
            // Default 30 days; admin edits the -30 to taste.
            Arguments: "/P \"{backup}\" /S /M *.* /D -30 /C \"cmd /c del /q @path\"",
            WorkingDir: "",
            Hint: "Удаляет в {backup}\\ файлы старше 30 дней (forfiles, входит в " +
                  "Windows). Отредактируйте «-30» под нужный срок хранения. " +
                  "ВНИМАНИЕ: операция удаляет файлы безвозвратно."),

        new PostSyncPreset(
            "7zip_prune",
            "7-Zip архив + удаление архивов старше N дней",
            ExePath: @"C:\Windows\System32\cmd.exe",
            // Chain: make the .7z, then prune old archives. cmd /c "A && B".
            Arguments: "/c \"\"C:\\Program Files\\7-Zip\\7z.exe\" a -t7z -mx=9 -mmt=on -xr!backup \"{backup}\\{machine}_{user}_{date}.7z\" \"{dest}\\*\" && forfiles /P \"{backup}\" /M *.7z /D -30 /C \"cmd /c del /q @path\"\"",
            WorkingDir: "",
            Hint: "Сначала создаёт .7z (максимальное сжатие), затем удаляет .7z " +
                  "старше 30 дней в {backup}\\. Требуется 7-Zip. Отредактируйте " +
                  "«-30» под нужный срок хранения."),
    };

    /// <summary>
    /// Best-effort match of the current arguments back to a preset key, so the
    /// dropdown can show the right selection when the dialog opens.  Matches on
    /// the distinctive head of the argument template; returns CustomKey if none
    /// fits.
    /// </summary>
    public static string MatchKey(string? exePath, string? arguments)
    {
        string args = (arguments ?? "").Trim();
        if (args.Length == 0) return CustomKey;

        foreach (var p in All)
        {
            if (p.Key == CustomKey) continue;
            if (string.Equals(p.Arguments.Trim(), args, StringComparison.OrdinalIgnoreCase))
                return p.Key;
        }
        return CustomKey;
    }

    public static PostSyncPreset? Find(string key) =>
        All.FirstOrDefault(p => p.Key == key);
}
