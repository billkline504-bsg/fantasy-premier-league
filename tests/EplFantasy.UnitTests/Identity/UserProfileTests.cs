using EplFantasy.Identity;
using Xunit;

namespace EplFantasy.UnitTests.Identity;

public class UserProfileTests
{
    [Fact]
    public void SetDefaultIcon_changes_the_icon_and_re_stamps_updatedAt_for_an_active_icon()
    {
        var profile = new UserProfile { UserId = Guid.NewGuid(), DefaultIconId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var icon = new ProfileIcon { ProfileIconId = Guid.NewGuid(), Name = "Eagle", AssetIdentifier = "icons/profile/eagle.svg", IsActive = true, SortOrder = 2 };
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

        profile.SetDefaultIcon(icon, now);

        Assert.Equal(icon.ProfileIconId, profile.DefaultIconId);
        Assert.Equal(now, profile.UpdatedAt);
    }

    [Fact]
    public void SetDefaultIcon_rejects_an_inactive_icon_and_leaves_the_profile_unchanged()
    {
        var originalIconId = Guid.NewGuid();
        var originalUpdatedAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var profile = new UserProfile { UserId = Guid.NewGuid(), DefaultIconId = originalIconId, CreatedAt = originalUpdatedAt, UpdatedAt = originalUpdatedAt };
        var inactiveIcon = new ProfileIcon { ProfileIconId = Guid.NewGuid(), Name = "Retired Icon", AssetIdentifier = "icons/profile/retired.svg", IsActive = false, SortOrder = 99 };

        var exception = Assert.Throws<ProfileIconNotActiveException>(
            () => profile.SetDefaultIcon(inactiveIcon, new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero)));

        Assert.Equal("profile_icon_not_active", exception.ErrorCode);
        Assert.Equal(400, exception.StatusCode);
        Assert.Equal(originalIconId, profile.DefaultIconId);
        Assert.Equal(originalUpdatedAt, profile.UpdatedAt);
    }
}
