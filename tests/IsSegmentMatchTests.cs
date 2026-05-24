using ProfileMirrorSync.Services;
using Xunit;

namespace ProfileMirrorSync.Tests;

/// <summary>
/// Regression tests for the audit B-1 bug: IsSegmentMatch was
/// silently rejecting all patterns with a leading `\` because it required
/// a separator BEFORE the pattern's own leading slash — which never exists
/// in real paths.  The bug let \bin\, \obj\, \.vs\, \node_modules\, \.git\
/// pass through and build artefacts leaked to the sync destination.
///
/// These tests pin both the FIX (patterns with leading `\` now match) and
/// the over-match protection (`bin` doesn't match `binary`).  Any future
/// "smart fix" of IsSegmentMatch must keep ALL of these green.
/// </summary>
public class IsSegmentMatchTests
{
    // ── The critical regression: leading-slash patterns ──────────────────────

    [Theory]
    [InlineData(@"C:\Users\Den\proj\bin\Release\app.exe",          @"\bin\")]
    [InlineData(@"C:\Users\Den\proj\obj\Release\app.dll",          @"\obj\")]
    [InlineData(@"C:\Users\Den\proj\.vs\Browse.SuoFile",           @"\.vs\")]
    [InlineData(@"C:\Users\Den\proj\node_modules\foo\index.js",    @"\node_modules\")]
    [InlineData(@"C:\Users\Den\proj\.git\HEAD",                    @"\.git\")]
    public void LeadingSlashPattern_MatchesInsideTree(string path, string pat)
    {
        Assert.True(SyncController.IsSegmentMatch(path, pat),
            $"Default exclude pattern '{pat}' MUST match '{path}'. " +
            "This is the B-1 regression: v2.4.10 silently disabled all such patterns.");
    }

    // ── Over-match protection: short patterns shouldn't grab substrings ──────

    [Theory]
    [InlineData(@"C:\Users\Den\proj\binaries\x.bin",               @"\bin\")]
    [InlineData(@"C:\Users\Den\objective_c\src.c",                 @"\obj\")]
    [InlineData(@"C:\Users\Den\node_modules_backup\x.js",          @"\node_modules\")]
    [InlineData(@"C:\Users\Den\proj\my.gitignore",                 @"\.git\")]
    public void LeadingSlashPattern_DoesNotOverMatch(string path, string pat)
    {
        Assert.False(SyncController.IsSegmentMatch(path, pat),
            $"Pattern '{pat}' must NOT over-match '{path}' (no false positive).");
    }

    // ── Non-slash patterns: standard segment match ──────────────────────────

    [Theory]
    [InlineData(@"C:\Users\Den\AppData\Local\Temp\foo",            @"AppData\Local\Temp",    true)]
    [InlineData(@"C:\Users\Den\AppData\LocalLow\bar",              @"AppData\Local",         false)]
    [InlineData(@"C:\Users\Den\AppData\Roaming\Spotify\Storage\X", @"AppData\Roaming\Spotify\Storage", true)]
    [InlineData(@"C:\Users\Den\AppData\Local\Microsoft\Edge\User Data\Default\Cache\file",
                @"AppData\Local\Microsoft\Edge\User Data\Default\Cache", true)]
    public void MidPathPattern_RequiresSegmentBoundary(string path, string pat, bool expected)
    {
        Assert.Equal(expected, SyncController.IsSegmentMatch(path, pat));
    }

    // ── Edge cases: pattern at end of path ──────────────────────────────────

    [Theory]
    [InlineData(@"C:\Users\Den\proj\bin",                          @"\bin",  true)]
    [InlineData(@"C:\Users\Den\proj\binary",                       @"\bin",  false)]
    [InlineData(@"C:\Users\Den\Desktop\file.tmp",                  @".tmp",  false)]
    public void TrailingPattern_AtEndOfPath(string path, string pat, bool expected)
    {
        // IsSegmentMatch enforces segment boundaries on BOTH sides for patterns
        // without their own separator markers.  Extension-style matches like
        // `.tmp` are NOT segment-matched here — they are handled separately by
        // _alwaysIgnoreExtensions in SyncController.ShouldIgnore.  This test
        // pins that intentional behaviour: IsSegmentMatch should not become
        // a substring matcher just because the pattern looks extension-like.
        Assert.Equal(expected, SyncController.IsSegmentMatch(path, pat));
    }

    // ── Forward-slash normalisation (already done by caller in ShouldIgnore,
    //    but IsSegmentMatch itself is case-insensitive but separator-agnostic) ─

    [Fact]
    public void IsSegmentMatch_CaseInsensitive()
    {
        Assert.True(SyncController.IsSegmentMatch(
            @"C:\USERS\DEN\proj\BIN\Release\app.exe", @"\bin\"));
    }

    [Fact]
    public void EmptyPattern_ReturnsFalse()
    {
        Assert.False(SyncController.IsSegmentMatch(@"C:\Users\Den\bin\x", ""));
    }
}
