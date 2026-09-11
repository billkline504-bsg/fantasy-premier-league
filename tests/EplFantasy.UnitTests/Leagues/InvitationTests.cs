using EplFantasy.Leagues;
using Xunit;

namespace EplFantasy.UnitTests.Leagues;

public class InvitationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private static Invitation Issue(int expirationDays = 7, DateTimeOffset? now = null) =>
        Invitation.Issue(Guid.NewGuid(), Guid.NewGuid(), null, "a-token", "invitee@example.com", InvitationChannel.Email, expirationDays, now ?? Now);

    [Fact]
    public void Issue_expires_expirationDays_after_the_issuance_clock_not_a_fixed_constant()
    {
        var invitation = Issue(expirationDays: 7);

        Assert.Equal(Now.AddDays(7), invitation.ExpiresAt);
        Assert.Equal(Now, invitation.CreatedAt);
        Assert.Equal(InvitationStatus.Pending, invitation.Status);
    }

    [Fact]
    public void Issue_honors_whatever_expirationDays_the_caller_supplies()
    {
        // BR-293: this is the League's *current* configured value at issuance, not a hard-coded 7 —
        // a League that has overridden InvitationExpirationDays gets a different expiry entirely.
        var invitation = Issue(expirationDays: 14);

        Assert.Equal(Now.AddDays(14), invitation.ExpiresAt);
    }

    [Fact]
    public void IsAcceptable_is_true_for_a_pending_invitation_before_its_expiry()
    {
        var invitation = Issue();

        Assert.True(invitation.IsAcceptable(Now.AddDays(6)));
    }

    [Fact]
    public void IsAcceptable_is_false_once_past_expiresAt_even_though_status_is_still_stored_as_Pending()
    {
        var invitation = Issue(expirationDays: 7);

        Assert.False(invitation.IsAcceptable(Now.AddDays(7).AddSeconds(1)));
        Assert.Equal(InvitationStatus.Pending, invitation.Status); // nothing rewrites the column
    }

    [Fact]
    public void IsAcceptable_is_false_once_accepted_or_revoked()
    {
        var accepted = Issue();
        accepted.Accept();
        Assert.False(accepted.IsAcceptable(Now));

        var revoked = Issue();
        revoked.Revoke();
        Assert.False(revoked.IsAcceptable(Now));
    }

    [Fact]
    public void EffectiveStatus_reports_Expired_for_a_time_expired_invitation_still_stored_as_Pending()
    {
        var invitation = Issue(expirationDays: 7);

        Assert.Equal(InvitationStatus.Expired, invitation.EffectiveStatus(Now.AddDays(8)));
        Assert.Equal(InvitationStatus.Pending, invitation.Status);
    }

    [Fact]
    public void EffectiveStatus_reports_the_stored_status_once_accepted_or_revoked_regardless_of_expiry()
    {
        var invitation = Issue(expirationDays: 7);
        invitation.Accept();

        Assert.Equal(InvitationStatus.Accepted, invitation.EffectiveStatus(Now.AddYears(1)));
    }

    [Fact]
    public void Revoke_is_idempotent_for_a_pending_or_already_revoked_invitation()
    {
        var invitation = Issue();

        invitation.Revoke();
        Assert.Equal(InvitationStatus.Revoked, invitation.Status);

        invitation.Revoke();
        Assert.Equal(InvitationStatus.Revoked, invitation.Status);
    }

    [Fact]
    public void Revoke_throws_for_an_already_accepted_invitation()
    {
        var invitation = Issue();
        invitation.Accept();

        Assert.Throws<InvitationAlreadyAcceptedException>(invitation.Revoke);
    }

    [Fact]
    public void Accept_marks_the_invitation_accepted()
    {
        var invitation = Issue();

        invitation.Accept();

        Assert.Equal(InvitationStatus.Accepted, invitation.Status);
    }
}
