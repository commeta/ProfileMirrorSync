using ProfileMirrorSync.Services;
using Xunit;

namespace ProfileMirrorSync.Tests;

/// <summary>
/// Regression tests for audit B-3: Path.Combine drive-letter bug.
///
/// Before the fix, DestinationRoot = "Z:" produced "Z:MyComputer\\Commeta" (drive-
/// relative) instead of "Z:\\MyComputer\\Commeta" (drive-rooted).  The fix detects
/// the bare-drive case and appends the separator before combining.
/// </summary>
public class PathNormalizationTests
{
    [Theory]
    [InlineData("Z:",             @"Z:\MyComputer\Commeta")]
    [InlineData("Z:\\",           @"Z:\MyComputer\Commeta")]
    [InlineData("Z:/",            @"Z:\MyComputer\Commeta")]
    [InlineData(@"Z:\Backup",     @"Z:\Backup\MyComputer\Commeta")]
    [InlineData(@"Z:\Backup\",    @"Z:\Backup\MyComputer\Commeta")]
    [InlineData(@"\\SRV\Share",   @"\\SRV\Share\MyComputer\Commeta")]
    [InlineData(@"\\SRV\Share\",  @"\\SRV\Share\MyComputer\Commeta")]
    public void NormalizeMachineRoot_BuildsCorrectPath(string input, string expected)
    {
        string actual = SyncController.NormalizeMachineRoot(input, "MyComputer", "Commeta");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void NormalizeMachineRoot_BareDriveLetter_GetsSeparatorAppended()
    {
        // The specific regression: without the fix, this produced "Z:MyComputer\\Commeta"
        string actual = SyncController.NormalizeMachineRoot("Z:", "MyComputer", "Commeta");
        Assert.Equal(@"Z:\MyComputer\Commeta", actual);
        Assert.DoesNotContain("Z:P", actual); // explicit check against the bug
    }

    [Fact]
    public void NormalizeMachineRoot_LowercaseDriveLetter_HandledSameWay()
    {
        string actual = SyncController.NormalizeMachineRoot("c:", "M", "U");
        Assert.Equal(@"c:\M\U", actual);
    }
}
