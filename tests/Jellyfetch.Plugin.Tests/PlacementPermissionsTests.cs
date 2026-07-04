using System;
using System.IO;
using Jellyfetch.Plugin.Download;

namespace Jellyfetch.Plugin.Tests;

public class PlacementPermissionsTests
{
    [Fact]
    public void Denied_message_names_directory_and_the_fix()
    {
        var dir = Path.Combine(Path.GetTempPath(), "media", "movies");
        var ex = PlacementPermissions.Denied(dir, new UnauthorizedAccessException("raw"));

        // Actionable: names the failing dir, states cause + exact remediation, and that we don't self-fix.
        Assert.Contains(dir, ex.Message, StringComparison.Ordinal);
        Assert.Contains("Jellyfin service user", ex.Message, StringComparison.Ordinal);
        Assert.Contains("chown -R jellyfin:jellyfin", ex.Message, StringComparison.Ordinal);
        Assert.Contains("chmod -R u+rwX", ex.Message, StringComparison.Ordinal);
        Assert.Contains("does not change permissions", ex.Message, StringComparison.Ordinal);
        Assert.Equal(dir, ex.TargetDirectory);

        // Stays an UnauthorizedAccessException so the job manager's generic catch still classifies it as
        // a failure, and preserves the raw cause for logs.
        Assert.IsAssignableFrom<UnauthorizedAccessException>(ex);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void EnsureWritable_passes_for_an_existing_writable_dir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jf-writable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            PlacementPermissions.EnsureWritable(dir); // must not throw and must clean its probe file
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EnsureWritable_passes_when_root_missing_but_parent_writable()
    {
        var parent = Path.Combine(Path.GetTempPath(), "jf-parent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        var missingRoot = Path.Combine(parent, "not-created-yet", "deeper");
        try
        {
            // Root doesn't exist; the placer will create it. Pre-flight probes the writable ancestor.
            PlacementPermissions.EnsureWritable(missingRoot);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void EnsureWritable_is_noop_for_blank_root()
    {
        PlacementPermissions.EnsureWritable(string.Empty);
        PlacementPermissions.EnsureWritable("   ");
    }
}
