using EplFantasy.SharedKernel;

namespace EplFantasy.Identity;

// Persistence shapes for the Identity & User context (Architecture v1.15 §6.1; physical schema:
// 06-database-migrations/migrations/V002__identity.sql). Deliberately anemic for now (IT-F03,
// EF Core wiring) — invariant-enforcing behavior (registration, retirement, username changes) is
// added to these same classes by the feature tasks that own it (IT-01, IT-13, IT-14, IT-15).

public enum UserStatus
{
    Active,
    Retired,
}

public class User
{
    public Guid UserId { get; set; }
    public string Username { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;
    public UserStatus Status { get; set; } = UserStatus.Active;
    public bool IsSystemAdministrator { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }

    /// <summary>
    /// IT-01: the one correct way to construct a newly-registered User — establishes BR-002's
    /// immutable identifier and the Active/non-administrator/matching-timestamps initial state a
    /// hand-built object initializer could otherwise get wrong. Username/email format (shape) is a
    /// DTO-level concern already enforced by RegisterRequest's data annotations (Architecture
    /// §9.3's "DTO-level validation is separate from domain-invariant validation"); username/email
    /// *uniqueness* (BR-004) and password *strength* (BR-285) both require information this factory
    /// doesn't have (a database query; the plaintext password, never stored) — IUserAccountService
    /// checks both before ever calling this.
    /// </summary>
    public static User Register(Guid userId, string username, string email, string passwordHash, DateTimeOffset now) =>
        new()
        {
            UserId = userId,
            Username = username,
            Email = email,
            PasswordHash = passwordHash,
            Status = UserStatus.Active,
            IsSystemAdministrator = false,
            CreatedAt = now,
            UpdatedAt = now,
            RetiredAt = null,
        };

    /// <summary>
    /// IT-14 (F-001.3, BR-270): changes the display username without ever touching <see cref="UserId"/>
    /// — there is no code path anywhere in this domain model that mutates UserId, which is the
    /// actual defense BR-270's "changing the username shall not change the underlying User
    /// identity" asks for, not a runtime check this method could meaningfully add. Uniqueness
    /// (BR-004/BR-266) is IUsernameService's own pre-check, before this is ever called — the same
    /// division of responsibility IUserAccountService.RegisterAsync already established.
    /// </summary>
    public void ChangeUsername(string newUsername, DateTimeOffset now)
    {
        Username = newUsername;
        UpdatedAt = now;
    }

    /// <summary>
    /// IT-15 (F-001.4, BR-013/ADR-010): a soft delete only — `Status` transitions to `Retired` and
    /// `RetiredAt` is set; no code path anywhere in this domain model physically deletes a `User`
    /// row (BR-014/BR-175: historical records must keep resolving it). Idempotent if already
    /// retired. Freeing the username for reuse (BR-298) needs no extra step here — `ux_users_username_active`
    /// (V002) is scoped to `Status = 'active'` already, so this state transition alone is what does it.
    /// </summary>
    public void Retire(DateTimeOffset now)
    {
        if (Status == UserStatus.Retired)
        {
            return;
        }

        Status = UserStatus.Retired;
        RetiredAt = now;
        UpdatedAt = now;
    }
}

public class UserProfile
{
    public Guid UserId { get; set; }
    public Guid DefaultIconId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// IT-09 (F-002.1, BR-006/BR-011): changes the caller's global default icon. Takes the actual
    /// <see cref="ProfileIcon"/> row (already looked up by IProfileService — "unknown" iconId is
    /// ruled out there, before this is ever called) rather than a bare id, so this method can
    /// itself enforce BR-011's "active application-controlled icon only" rule by inspecting
    /// <see cref="ProfileIcon.IsActive"/> directly.
    /// </summary>
    public void SetDefaultIcon(ProfileIcon icon, DateTimeOffset now)
    {
        if (!icon.IsActive)
        {
            throw new ProfileIconNotActiveException();
        }

        DefaultIconId = icon.ProfileIconId;
        UpdatedAt = now;
    }
}

public class UsernameHistory
{
    public Guid UsernameHistoryId { get; set; }
    public Guid UserId { get; set; }
    public string Username { get; set; } = null!;
    public DateTimeOffset EffectiveFrom { get; set; }
    public DateTimeOffset? EffectiveTo { get; set; }

    /// <summary>BR-326: opens a new, open-ended row for a User's now-current username. `ux_username_history_open` (V002) enforces at most one such row per User at a time — IUsernameService.ChangeUsernameAsync always closes the prior open row (see <see cref="Close"/>) in the same SaveChangesAsync() before adding this one.</summary>
    public static UsernameHistory Open(Guid usernameHistoryId, Guid userId, string username, DateTimeOffset now) =>
        new()
        {
            UsernameHistoryId = usernameHistoryId,
            UserId = userId,
            Username = username,
            EffectiveFrom = now,
            EffectiveTo = null,
        };

    /// <summary>BR-326: marks this row no longer current, effective now — retained permanently (never deleted) so historical screens can still resolve the display name that was active at any past record's own timestamp.</summary>
    public void Close(DateTimeOffset now) => EffectiveTo = now;
}

public class ProfileIcon
{
    public Guid ProfileIconId { get; set; }
    public string Name { get; set; } = null!;
    public string AssetIdentifier { get; set; } = null!;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

/// <summary>
/// IT-09 (BR-011): the same user-facing failure whether profileIconId names no ProfileIcon row at
/// all (IProfileService's own lookup returns null and reports this via Result before ever reaching
/// UserProfile.SetDefaultIcon) or a real but inactive one (this exception, thrown by
/// SetDefaultIcon itself) — OpenAPI documents one 400 either way, so both paths share this
/// ErrorCode/message rather than exposing which case actually happened.
/// </summary>
public sealed class ProfileIconNotActiveException() : DomainException(
    "profileIconId does not reference an active application-controlled ProfileIcon.")
{
    public override string ErrorCode => "profile_icon_not_active";
}

public class RefreshToken
{
    public Guid RefreshTokenId { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = null!;
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public class PasswordResetToken
{
    public Guid PasswordResetTokenId { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }

    /// <summary>BR-284: whether this token can still be redeemed right now — not yet used, and not yet past its ExpiresAt. Nothing proactively flips a status column on a timer (mirrors IT-04's Invitation.IsAcceptable convention); this is checked at the point of use instead.</summary>
    public bool IsValid(DateTimeOffset now) => UsedAt is null && ExpiresAt > now;

    /// <summary>
    /// Consumes the token. IPasswordResetService already checks <see cref="IsValid"/> before ever
    /// getting this far (so the normal-path caller sees a clean Result failure rather than this
    /// exception) — this is the defensive re-check immediately before persisting, the same
    /// second-layer-of-defense role the database's own unique index plays for username/email
    /// uniqueness in IUserAccountService.RegisterAsync, just expressed as an app-level guard here
    /// since "already used" isn't something a unique index can enforce directly.
    /// </summary>
    public void MarkUsed(DateTimeOffset now)
    {
        if (!IsValid(now))
        {
            throw new PasswordResetTokenInvalidException();
        }

        UsedAt = now;
    }
}

/// <summary>IT-13 (BR-284): the same user-facing failure whether resetToken matches no row at all (IPasswordResetService's own lookup returns null and reports this via Result before ever reaching PasswordResetToken.MarkUsed) or a real but expired/already-used one (this exception) — OpenAPI documents one 400 either way, never revealing which case actually happened.</summary>
public sealed class PasswordResetTokenInvalidException() : DomainException(
    "The password reset token is unknown, expired, or already used.")
{
    public override string ErrorCode => "invalid_reset_token";
}
