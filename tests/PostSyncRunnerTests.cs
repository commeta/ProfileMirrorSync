using ProfileMirrorSync.Services;
using Xunit;

namespace ProfileMirrorSync.Tests;

/// <summary>
/// Placeholder-expansion contract for the post-sync external-program
/// hook.  The launch path itself spawns a real process, so it is exercised
/// manually / in the field; here we pin the deterministic argument templating
/// that the operator relies on (the 7-Zip example and any custom command).
/// </summary>
public class PostSyncRunnerTests
{
    [Fact]
    public void Expand_ReplacesAllPlaceholders()
    {
        var now = new DateTime(2026, 5, 22, 14, 5, 9, DateTimeKind.Local);
        string dest   = @"\\SRV\Backup\PMS\MyComputer\Commeta";
        string backup = dest + @"\backup";

        string tmpl = "a -t7z -mx=9 \"{backup}\\{machine}_{user}_{date}_{time}.7z\" \"{dest}\\*\"";
        string outp = PostSyncRunner.ExpandPlaceholders(tmpl, dest, backup, now);

        Assert.Contains(backup, outp);
        Assert.Contains(dest, outp);
        Assert.Contains("2026-05-22", outp);
        Assert.Contains("14-05-09", outp);
        Assert.Contains(Environment.MachineName, outp);
        Assert.Contains(Environment.UserName, outp);
        // No unexpanded tokens remain.
        Assert.DoesNotContain("{dest}", outp);
        Assert.DoesNotContain("{backup}", outp);
        Assert.DoesNotContain("{machine}", outp);
        Assert.DoesNotContain("{user}", outp);
        Assert.DoesNotContain("{date}", outp);
        Assert.DoesNotContain("{time}", outp);
    }

    [Fact]
    public void Expand_IsCaseInsensitive()
    {
        var now = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local);
        string outp = PostSyncRunner.ExpandPlaceholders("{DEST}|{Backup}|{DATE}",
            @"C:\d", @"C:\d\backup", now);
        Assert.Equal(@"C:\d|C:\d\backup|2026-01-02", outp);
    }

    [Fact]
    public void Expand_EmptyTemplate_ReturnsEmpty()
    {
        Assert.Equal("", PostSyncRunner.ExpandPlaceholders("", "x", "y", DateTime.Now));
    }

    [Fact]
    public void Expand_NoPlaceholders_PassesThrough()
    {
        string outp = PostSyncRunner.ExpandPlaceholders("--verbose --quiet", "x", "y", DateTime.Now);
        Assert.Equal("--verbose --quiet", outp);
    }
}
