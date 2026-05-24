using ProfileMirrorSync.Services;
using Xunit;

namespace ProfileMirrorSync.Tests;

/// <summary>
/// Tests for ProfileRoots (was 0% coverage, flagged 🔴 in the review).
/// Covers custom-root sanitisation, dedup-by-existence, and the "filter out
/// folders that don't exist" contract.  Uses real temp directories because
/// both GetDefaultRoots and GetCustomRoots filter on Directory.Exists.
/// </summary>
public class ProfileRootsTests : IDisposable
{
    private readonly string _baseDir;

    public ProfileRootsTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "PMSRoots_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch { }
    }

    // T-PR-01 — GetCustomRoots filters out a path that doesn't exist.
    [Fact]
    public void GetCustomRoots_DropsNonexistentPath()
    {
        string real    = Path.Combine(_baseDir, "real");
        string missing = Path.Combine(_baseDir, "missing");
        Directory.CreateDirectory(real);

        var roots = ProfileRoots.GetCustomRoots(new[] { real, missing });

        Assert.Single(roots);
        Assert.Equal(real, roots[0].SourcePath);
    }

    // T-PR-02 — empty/whitespace entries are skipped.
    [Fact]
    public void GetCustomRoots_SkipsEmptyEntries()
    {
        string real = Path.Combine(_baseDir, "work");
        Directory.CreateDirectory(real);

        var roots = ProfileRoots.GetCustomRoots(new[] { real, "", "   " });

        Assert.Single(roots);
    }

    // T-PR-03 — SanitizeName replaces every non-alphanumeric char with '_'
    // (verified via the RelativePrefix the sanitiser produces).
    [Fact]
    public void GetCustomRoots_PrefixSanitisesPath()
    {
        string real = Path.Combine(_baseDir, "My Work!");
        Directory.CreateDirectory(real);

        var roots = ProfileRoots.GetCustomRoots(new[] { real });

        Assert.Single(roots);
        string prefix = roots[0].RelativePrefix;
        Assert.StartsWith("Custom\\", prefix);
        // No path separators, colons, spaces or punctuation survive after the
        // "Custom\" prefix — only letters, digits and underscores.
        string tail = prefix.Substring("Custom\\".Length);
        Assert.All(tail, c => Assert.True(char.IsLetterOrDigit(c) || c == '_',
            $"unexpected char '{c}' in sanitised prefix"));
        // The space and '!' from "My Work!" must have become underscores.
        Assert.DoesNotContain(' ', tail);
        Assert.DoesNotContain('!', tail);
    }

    [Fact]
    public void GetCustomRoots_NameIsLeafFolder()
    {
        string real = Path.Combine(_baseDir, "Projects");
        Directory.CreateDirectory(real);

        var roots = ProfileRoots.GetCustomRoots(new[] { real });

        Assert.Single(roots);
        Assert.Equal("Custom:Projects", roots[0].Name);
    }

    // GetDefaultRoots: a folder toggled ON but absent on disk is filtered out.
    // We can't relocate the real special folders, but we CAN assert the
    // contract holds for at least the existence filter: every returned root
    // points at an existing directory.
    [Fact]
    public void GetDefaultRoots_AllReturnedPathsExist()
    {
        var roots = ProfileRoots.GetDefaultRoots(
            desktop: true, documents: true, downloads: true, pictures: true,
            videos: true, music: true, favorites: true, contacts: true,
            links: true, searches: true, savedGames: true,
            appDataRoaming: true, appDataLocal: true, appDataLocalLow: true);

        Assert.All(roots, r => Assert.True(Directory.Exists(r.SourcePath)));
    }

    [Fact]
    public void GetDefaultRoots_NothingSelected_IsEmpty()
    {
        var roots = ProfileRoots.GetDefaultRoots(
            false, false, false, false, false, false, false,
            false, false, false, false, false, false, false);

        Assert.Empty(roots);
    }
}
