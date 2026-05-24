using ProfileMirrorSync.Services;
using Xunit;

namespace ProfileMirrorSync.Tests;

/// <summary>
/// Tests for the post-sync archiver presets (Archive tab convenience).
/// Presets are pure data; these verify the catalogue is well-formed and that
/// MatchKey round-trips a preset's own arguments back to its key.
/// </summary>
public class PostSyncPresetsTests
{
    [Fact]
    public void All_FirstEntry_IsCustomPlaceholder()
    {
        Assert.Equal(PostSyncPresets.CustomKey, PostSyncPresets.All[0].Key);
        // The custom placeholder has empty exe/args so selecting it never wipes
        // the user's current fields (the UI returns early for it).
        Assert.Equal("", PostSyncPresets.All[0].ExePath);
        Assert.Equal("", PostSyncPresets.All[0].Arguments);
    }

    [Fact]
    public void RealPresets_HaveExeArgsAndHint()
    {
        foreach (var p in PostSyncPresets.All)
        {
            if (p.Key == PostSyncPresets.CustomKey) continue;
            Assert.False(string.IsNullOrWhiteSpace(p.ExePath),   $"{p.Key}: ExePath empty");
            Assert.False(string.IsNullOrWhiteSpace(p.Arguments), $"{p.Key}: Arguments empty");
            Assert.False(string.IsNullOrWhiteSpace(p.Hint),      $"{p.Key}: Hint empty");
            Assert.False(string.IsNullOrWhiteSpace(p.DisplayName),$"{p.Key}: DisplayName empty");
        }
    }

    [Fact]
    public void Keys_AreUnique()
    {
        var keys = PostSyncPresets.All.Select(p => p.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Theory]
    [InlineData("7zip")]
    [InlineData("rar")]
    [InlineData("zip")]
    [InlineData("robocopy")]
    public void MatchKey_RoundTripsKnownPreset(string key)
    {
        var preset = PostSyncPresets.Find(key);
        Assert.NotNull(preset);
        string matched = PostSyncPresets.MatchKey(preset!.ExePath, preset.Arguments);
        Assert.Equal(key, matched);
    }

    [Fact]
    public void MatchKey_EmptyArgs_ReturnsCustom()
    {
        Assert.Equal(PostSyncPresets.CustomKey, PostSyncPresets.MatchKey("", ""));
        Assert.Equal(PostSyncPresets.CustomKey, PostSyncPresets.MatchKey(null, null));
    }

    [Fact]
    public void MatchKey_UnknownArgs_ReturnsCustom()
    {
        Assert.Equal(PostSyncPresets.CustomKey,
            PostSyncPresets.MatchKey(@"C:\my\tool.exe", "--some custom flags here"));
    }

    [Fact]
    public void Find_UnknownKey_ReturnsNull()
    {
        Assert.Null(PostSyncPresets.Find("does-not-exist"));
    }
}
