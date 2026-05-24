using ProfileMirrorSync.Services;
using Xunit;

namespace ProfileMirrorSync.Tests;

/// <summary>
/// Regression test for audit N-2: Pass 3 used to sort by string
/// LENGTH instead of path DEPTH.  Result: a single dir with a long name
/// could come before a deeply-nested tree of short-named dirs, breaking
/// the bottom-up invariant that lets us propagate directory timestamps
/// without children's writes clobbering their parents.
///
/// These tests pin the CountSeparators helper directly (the unit Pass 3
/// now uses for its sort key).
/// </summary>
public class PassDepthSortTests
{
    [Theory]
    [InlineData(@"C:\a",                 1)]
    [InlineData(@"C:\a\b",               2)]
    [InlineData(@"C:\a\b\c\d\e",         5)]
    [InlineData(@"\\SRV\Share\a\b",      5)]  // UNC: \\(2) + \Share(1) + \a(1) + \b(1)
    [InlineData(@"C:/a/b/c",             3)]  // forward slashes count too
    [InlineData(@"C:\verylongnamehere",  1)]  // long name, shallow depth
    public void CountSeparators_ReturnsExpected(string path, int expected)
    {
        Assert.Equal(expected, FileMirror.CountSeparators(path));
    }

    [Fact]
    public void DepthSort_DeepShortBeatsLongShallow()
    {
        // THE regression scenario: pre-, this list sorted by Length
        // would put `C:\verylongname\sub` (len 18, depth 2) BEFORE
        // `C:\a\b\c\d\e` (len 12, depth 5) — deeper dir touched too late.
        var dirs = new List<string>
        {
            @"C:\verylongname\sub",
            @"C:\a\b\c\d\e",
            @"C:\a",
            @"C:\a\b\c",
        };

        // Apply the same comparator as FileMirror.ReconcileRootAsync Pass 3.
        dirs.Sort((a, b) => FileMirror.CountSeparators(b).CompareTo(FileMirror.CountSeparators(a)));

        // Expect: deepest first (separator counts: 5, 3, 2, 1).
        Assert.Equal(@"C:\a\b\c\d\e",         dirs[0]);
        Assert.Equal(@"C:\a\b\c",             dirs[1]);
        Assert.Equal(@"C:\verylongname\sub",  dirs[2]);
        Assert.Equal(@"C:\a",                 dirs[3]);
    }

    [Fact]
    public void DepthSort_TiesPreserveStableOrderUnderListSort()
    {
        // List.Sort is NOT stable, so we don't assert order WITHIN the same
        // depth — only that all paths of depth N come before depth N-1.
        var dirs = new List<string>
        {
            @"C:\a\b",   // 2
            @"C:\x\y",   // 2
            @"C:\a",     // 1
        };
        dirs.Sort((a, b) => FileMirror.CountSeparators(b).CompareTo(FileMirror.CountSeparators(a)));

        Assert.Equal(2, FileMirror.CountSeparators(dirs[0]));
        Assert.Equal(2, FileMirror.CountSeparators(dirs[1]));
        Assert.Equal(1, FileMirror.CountSeparators(dirs[2]));
    }
}
