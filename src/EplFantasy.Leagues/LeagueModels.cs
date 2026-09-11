using EplFantasy.SharedKernel;

namespace EplFantasy.Leagues;

// Persistence shapes for the League & Season context (Architecture v1.15 §6.2; physical schema:
// 06-database-migrations/migrations/V004__league_and_season.sql). See IdentityModels.cs for the
// "anemic for now" note — same applies here.

public enum LeagueStatus
{
    Active,
    Archived,
}

public enum MembershipStatus
{
    Invited,
    Active,
    Left,
}

public enum SeasonStatus
{
    Setup,
    DraftInProgress,
    InSeason,
    Completed,
}

public enum InvitationStatus
{
    Pending,
    Accepted,
    Expired,
    Revoked,
}

public enum InvitationChannel
{
    Email,
    Sms,
}

public class League
{
    public Guid LeagueId { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public LeagueStatus Status { get; set; } = LeagueStatus.Active;
    public Guid CreatedByMembershipId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// IT-03 (F-003.1, BR-023/BR-024/BR-026): the one correct way to construct a newly-created
    /// League — always `Active`, always carrying its founding Administrator's already-generated
    /// <see cref="LeagueMembership.LeagueMembershipId"/> (the two rows are mutually referential;
    /// see LeagueConfigurations.cs's header comment on the deferred FK this relies on). Name's
    /// length limits are a DTO-level (CreateLeagueRequest) concern, not re-checked here.
    /// </summary>
    public static League Create(Guid leagueId, Guid creatorMembershipId, string name, string? description, DateTimeOffset now) =>
        new()
        {
            LeagueId = leagueId,
            Name = name,
            Description = description,
            Status = LeagueStatus.Active,
            CreatedByMembershipId = creatorMembershipId,
            CreatedAt = now,
        };

    /// <summary>BR-025: League-Administrator-only mutation of name/description/status. Every parameter is optional (UpdateLeagueRequest's own fields all are) — a null argument leaves that field unchanged rather than clearing it.</summary>
    public void UpdateDetails(string? name, string? description, LeagueStatus? status)
    {
        if (name is not null)
        {
            Name = name;
        }

        if (description is not null)
        {
            Description = description;
        }

        if (status is not null)
        {
            Status = status.Value;
        }
    }
}

public class LeagueMembership
{
    public Guid LeagueMembershipId { get; set; }
    public Guid LeagueId { get; set; }
    public Guid UserId { get; set; }
    public bool IsAdministrator { get; set; }
    public Guid? LeagueIconId { get; set; }
    public MembershipStatus Status { get; set; } = MembershipStatus.Invited;
    public DateTimeOffset? JoinedAt { get; set; }
    public DateTimeOffset? LeftAt { get; set; }

    /// <summary>BR-024: the League creator's own membership — always `Active` and always the (sole, BR-283) Administrator from the instant it exists, never a bare object initializer that could get either wrong.</summary>
    public static LeagueMembership CreateFoundingAdministrator(Guid leagueMembershipId, Guid leagueId, Guid userId, DateTimeOffset now) =>
        new()
        {
            LeagueMembershipId = leagueMembershipId,
            LeagueId = leagueId,
            UserId = userId,
            IsAdministrator = true,
            Status = MembershipStatus.Active,
            JoinedAt = now,
        };

    /// <summary>
    /// IT-04 (F-003.2, BR-027): a member joining via invitation acceptance — always `Active`,
    /// never Administrator (only the founding membership starts as one, BR-024). Rejoining after
    /// having left (BR-020) is a *new* row, not an update of the old `Left` one — the partial
    /// unique index on `(league_id, user_id) WHERE status &lt;&gt; 'left'` only forbids a second
    /// non-`Left` row, so a fresh row here is exactly what `db-tests/020...sql`'s
    /// `league.rejoin_after_leaving_allowed` already proves is allowed at the database level.
    /// </summary>
    public static LeagueMembership Join(Guid leagueMembershipId, Guid leagueId, Guid userId, DateTimeOffset now) =>
        new()
        {
            LeagueMembershipId = leagueMembershipId,
            LeagueId = leagueId,
            UserId = userId,
            IsAdministrator = false,
            Status = MembershipStatus.Active,
            JoinedAt = now,
        };

    /// <summary>
    /// IT-05 (F-003.3, acceptance criterion 1): `Status` transitions to `Left` and `LeftAt` is set
    /// — the row is retained, never deleted, for historical integrity. Idempotent if already
    /// `Left`. Throws if this is the (sole, BR-283) League Administrator's own membership: no
    /// endpoint in this API version can transfer administration to another member first, so the
    /// Administrator cannot leave at all yet (OpenAPI's leaveLeague 409, BR-025/BR-283).
    /// </summary>
    public void Leave(DateTimeOffset now)
    {
        if (Status == MembershipStatus.Left)
        {
            return;
        }

        if (IsAdministrator)
        {
            throw new SoleAdministratorCannotLeaveException();
        }

        Status = MembershipStatus.Left;
        LeftAt = now;
    }

    /// <summary>
    /// IT-10 (F-002.2, BR-007–BR-009): a League-specific icon override — independent per League
    /// (changing it here never touches this User's global default, UserProfile.DefaultIconId, or
    /// any other League's own LeagueMembership row for the same User, BR-009). Takes the already
    /// looked-up ProfileIcon's id/IsActive as primitives, not the entity itself — EplFantasy.Leagues
    /// has no reason to take a project reference on EplFantasy.Identity's ProfileIcon type just for
    /// this one boolean fact (IMembershipService's own lookup already ruled out "unknown" before
    /// this is ever called).
    /// </summary>
    public void SetLeagueIcon(Guid profileIconId, bool profileIconIsActive)
    {
        if (!profileIconIsActive)
        {
            throw new LeagueIconNotActiveException();
        }

        LeagueIconId = profileIconId;
    }

    /// <summary>BR-010/BR-275: reverts to the global default icon (BR-008) — idempotent if no override was ever set.</summary>
    public void ClearLeagueIcon() => LeagueIconId = null;
}

/// <summary>IT-05: no endpoint in this API version can transfer League Administrator status to another member (isAdministrator is read-only everywhere in OpenAPI Specification v1.0), so the Administrator's own membership can never satisfy leaveLeague until one exists.</summary>
public sealed class SoleAdministratorCannotLeaveException() : DomainException(
    "The League Administrator cannot leave without first transferring administration to another member.")
{
    public override string ErrorCode => "sole_administrator_cannot_leave";

    public override int StatusCode => 409;
}

/// <summary>
/// IT-10 (BR-274): the same user-facing failure whether profileIconId names no ProfileIcon row at
/// all (IMembershipService's own lookup returns null and reports this via Result before ever
/// reaching LeagueMembership.SetLeagueIcon) or a real but inactive one (this exception) — shares
/// IT-09's ProfileIconNotActiveException error code/message exactly, since both are BR-011's same
/// "application-controlled icon set" rule applied at a different scope (global default vs.
/// League-specific), and a client's icon picker should treat the two identically.
/// </summary>
public sealed class LeagueIconNotActiveException() : DomainException(
    "profileIconId does not reference an active application-controlled ProfileIcon.")
{
    public override string ErrorCode => "profile_icon_not_active";
}

/// <summary>
/// IT-08 (ADR-011): the full set of BR-291 configurable parameters, independent of whether they're
/// being read/written at League or Season level — the same values either way, just carried by two
/// differently-shaped rows (LeagueConfiguration's own audit columns vs. SeasonConfiguration's
/// LockedFields). A plain data carrier so LeagueConfiguration.Update/SeasonConfiguration.ApplyUpdate
/// don't need a 19-parameter argument list, and so IAdministrativeActionRecorder's before/after
/// snapshots (BR-295) have something serializable to record.
/// </summary>
public sealed record ConfigurationValues(
    int InitialSquadSize,
    int WeeklyRosterSize,
    int PositionalMinimumGk,
    int PositionalMinimumDef,
    int PositionalMinimumMid,
    int PositionalMinimumFwd,
    int DraftTimerSecondsInitial,
    int DraftTimerSecondsSecondary,
    int DraftTimerSecondsReplacement,
    int SecondaryDraftSelectionsPerTeam,
    int SecondaryDraftSchedulingOffsetDays,
    int GameweekRosterLockOffsetBeforeKickoffMinutes,
    int LeaguePointsWin,
    int LeaguePointsDraw,
    int LeaguePointsLoss,
    int InvitationExpirationDays,
    int? ReplacementSelectionCap,
    int GameweekReminderLeadTimeHours,
    string TieBreakRulesetVersion);

/// <summary>
/// ADR-011: one column per configurable parameter — never a literal scattered through domain or
/// application code. See Database Migration Strategy v1.0 §3 for why this is flat columns, not a
/// jsonb blob.
/// </summary>
public class LeagueConfiguration
{
    public Guid LeagueId { get; set; }
    public int InitialSquadSize { get; set; } = 25;
    public int WeeklyRosterSize { get; set; } = 15;
    public int PositionalMinimumGk { get; set; } = 1;
    public int PositionalMinimumDef { get; set; } = 3;
    public int PositionalMinimumMid { get; set; } = 2;
    public int PositionalMinimumFwd { get; set; } = 1;
    public int DraftTimerSecondsInitial { get; set; } = 300;
    public int DraftTimerSecondsSecondary { get; set; } = 300;
    public int DraftTimerSecondsReplacement { get; set; } = 300;
    public int SecondaryDraftSelectionsPerTeam { get; set; } = 5;
    public int SecondaryDraftSchedulingOffsetDays { get; set; } = 1;
    public int GameweekRosterLockOffsetBeforeKickoffMinutes { get; set; } = 60;
    public int LeaguePointsWin { get; set; } = 3;
    public int LeaguePointsDraw { get; set; } = 1;
    public int LeaguePointsLoss { get; set; }
    public int InvitationExpirationDays { get; set; } = 7;
    public int? ReplacementSelectionCap { get; set; }
    public int GameweekReminderLeadTimeHours { get; set; } = 24;
    public string TieBreakRulesetVersion { get; set; } = "v1";
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedByMembershipId { get; set; }

    /// <summary>
    /// IT-03/IT-04: every League has exactly one of these rows from the moment it's created (1:1
    /// FK, V004) — LeagueService.CreateAsync seeds it alongside the League/LeagueMembership rows
    /// so a feature reading a configurable parameter (e.g. IT-04's InvitationExpirationDays) always
    /// has a row to read (AP-006), without waiting on IT-08's own get/update-with-locking
    /// endpoints. Every field already defaults to BR-291's stated default via its own property
    /// initializer above, so this factory only needs to stamp the two fields that don't.
    /// </summary>
    public static LeagueConfiguration CreateDefault(Guid leagueId, DateTimeOffset now) =>
        new()
        {
            LeagueId = leagueId,
            UpdatedAt = now,
        };

    public ConfigurationValues ToValues() => new(
        InitialSquadSize,
        WeeklyRosterSize,
        PositionalMinimumGk,
        PositionalMinimumDef,
        PositionalMinimumMid,
        PositionalMinimumFwd,
        DraftTimerSecondsInitial,
        DraftTimerSecondsSecondary,
        DraftTimerSecondsReplacement,
        SecondaryDraftSelectionsPerTeam,
        SecondaryDraftSchedulingOffsetDays,
        GameweekRosterLockOffsetBeforeKickoffMinutes,
        LeaguePointsWin,
        LeaguePointsDraw,
        LeaguePointsLoss,
        InvitationExpirationDays,
        ReplacementSelectionCap,
        GameweekReminderLeadTimeHours,
        TieBreakRulesetVersion);

    /// <summary>BR-292: the League-level defaults are mutable at any time — unlike a Season's own copy, nothing here ever locks (only SeasonConfiguration.ApplyUpdate enforces BR-293/BR-294's per-field lock).</summary>
    public void Update(ConfigurationValues values, Guid updatedByMembershipId, DateTimeOffset now)
    {
        InitialSquadSize = values.InitialSquadSize;
        WeeklyRosterSize = values.WeeklyRosterSize;
        PositionalMinimumGk = values.PositionalMinimumGk;
        PositionalMinimumDef = values.PositionalMinimumDef;
        PositionalMinimumMid = values.PositionalMinimumMid;
        PositionalMinimumFwd = values.PositionalMinimumFwd;
        DraftTimerSecondsInitial = values.DraftTimerSecondsInitial;
        DraftTimerSecondsSecondary = values.DraftTimerSecondsSecondary;
        DraftTimerSecondsReplacement = values.DraftTimerSecondsReplacement;
        SecondaryDraftSelectionsPerTeam = values.SecondaryDraftSelectionsPerTeam;
        SecondaryDraftSchedulingOffsetDays = values.SecondaryDraftSchedulingOffsetDays;
        GameweekRosterLockOffsetBeforeKickoffMinutes = values.GameweekRosterLockOffsetBeforeKickoffMinutes;
        LeaguePointsWin = values.LeaguePointsWin;
        LeaguePointsDraw = values.LeaguePointsDraw;
        LeaguePointsLoss = values.LeaguePointsLoss;
        InvitationExpirationDays = values.InvitationExpirationDays;
        ReplacementSelectionCap = values.ReplacementSelectionCap;
        GameweekReminderLeadTimeHours = values.GameweekReminderLeadTimeHours;
        TieBreakRulesetVersion = values.TieBreakRulesetVersion;
        UpdatedAt = now;
        UpdatedByMembershipId = updatedByMembershipId;
    }
}

public class Season
{
    public Guid SeasonId { get; set; }
    public Guid LeagueId { get; set; }
    public string EplSeasonIdentifier { get; set; } = null!;
    public SeasonStatus Status { get; set; } = SeasonStatus.Setup;
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }

    /// <summary>IT-06 (F-003.4, BR-032): a League may be reused for any number of Seasons — always starts `Setup`, `EndDate` unset. Nothing about a prior Season (membership, rosters, standings) carries forward; BR-033's reinvitation is a separate, manual IT-04 step, not a side effect of this factory.</summary>
    public static Season Create(Guid seasonId, Guid leagueId, string eplSeasonIdentifier, DateOnly startDate) =>
        new()
        {
            SeasonId = seasonId,
            LeagueId = leagueId,
            EplSeasonIdentifier = eplSeasonIdentifier,
            Status = SeasonStatus.Setup,
            StartDate = startDate,
            EndDate = null,
        };
}

/// <summary>
/// Copied from <see cref="LeagueConfiguration"/> at Season creation (BR-292), then independently
/// overridable per field up to that field's own lock point, tracked in <see cref="LockedFields"/>
/// (BR-293/BR-294).
/// </summary>
public class SeasonConfiguration
{
    public Guid SeasonId { get; set; }
    public int InitialSquadSize { get; set; }
    public int WeeklyRosterSize { get; set; }
    public int PositionalMinimumGk { get; set; }
    public int PositionalMinimumDef { get; set; }
    public int PositionalMinimumMid { get; set; }
    public int PositionalMinimumFwd { get; set; }
    public int DraftTimerSecondsInitial { get; set; }
    public int DraftTimerSecondsSecondary { get; set; }
    public int DraftTimerSecondsReplacement { get; set; }
    public int SecondaryDraftSelectionsPerTeam { get; set; }
    public int SecondaryDraftSchedulingOffsetDays { get; set; }
    public int GameweekRosterLockOffsetBeforeKickoffMinutes { get; set; }
    public int LeaguePointsWin { get; set; }
    public int LeaguePointsDraw { get; set; }
    public int LeaguePointsLoss { get; set; }
    public int InvitationExpirationDays { get; set; }
    public int? ReplacementSelectionCap { get; set; }
    public int GameweekReminderLeadTimeHours { get; set; }
    public string TieBreakRulesetVersion { get; set; } = null!;
    public List<string> LockedFields { get; set; } = [];

    /// <summary>BR-292: takes a snapshot of the League's *current* default configuration at the moment this Season is created — never a live reference to it, so a later change to the League's own defaults (or another Season's copy) never retroactively alters this one (BR-293/BR-296). Starts with no locked fields; each one locks independently as this Season passes that field's own "Locks At" point (BR-291, IT-08).</summary>
    public static SeasonConfiguration CopyFrom(Guid seasonId, LeagueConfiguration source) =>
        new()
        {
            SeasonId = seasonId,
            InitialSquadSize = source.InitialSquadSize,
            WeeklyRosterSize = source.WeeklyRosterSize,
            PositionalMinimumGk = source.PositionalMinimumGk,
            PositionalMinimumDef = source.PositionalMinimumDef,
            PositionalMinimumMid = source.PositionalMinimumMid,
            PositionalMinimumFwd = source.PositionalMinimumFwd,
            DraftTimerSecondsInitial = source.DraftTimerSecondsInitial,
            DraftTimerSecondsSecondary = source.DraftTimerSecondsSecondary,
            DraftTimerSecondsReplacement = source.DraftTimerSecondsReplacement,
            SecondaryDraftSelectionsPerTeam = source.SecondaryDraftSelectionsPerTeam,
            SecondaryDraftSchedulingOffsetDays = source.SecondaryDraftSchedulingOffsetDays,
            GameweekRosterLockOffsetBeforeKickoffMinutes = source.GameweekRosterLockOffsetBeforeKickoffMinutes,
            LeaguePointsWin = source.LeaguePointsWin,
            LeaguePointsDraw = source.LeaguePointsDraw,
            LeaguePointsLoss = source.LeaguePointsLoss,
            InvitationExpirationDays = source.InvitationExpirationDays,
            ReplacementSelectionCap = source.ReplacementSelectionCap,
            GameweekReminderLeadTimeHours = source.GameweekReminderLeadTimeHours,
            TieBreakRulesetVersion = source.TieBreakRulesetVersion,
            LockedFields = [],
        };

    public ConfigurationValues ToValues() => new(
        InitialSquadSize,
        WeeklyRosterSize,
        PositionalMinimumGk,
        PositionalMinimumDef,
        PositionalMinimumMid,
        PositionalMinimumFwd,
        DraftTimerSecondsInitial,
        DraftTimerSecondsSecondary,
        DraftTimerSecondsReplacement,
        SecondaryDraftSelectionsPerTeam,
        SecondaryDraftSchedulingOffsetDays,
        GameweekRosterLockOffsetBeforeKickoffMinutes,
        LeaguePointsWin,
        LeaguePointsDraw,
        LeaguePointsLoss,
        InvitationExpirationDays,
        ReplacementSelectionCap,
        GameweekReminderLeadTimeHours,
        TieBreakRulesetVersion);

    /// <summary>
    /// IT-08 (BR-293/BR-294): applies every field from <paramref name="values"/> whose new value
    /// actually differs from this Season's current one, rejecting the *whole* update (throwing
    /// <see cref="SeasonConfigurationFieldsLockedException"/>, listing every offending field) if
    /// any changed field is already in <see cref="LockedFields"/> — resubmitting a locked field's
    /// unchanged current value is never a violation, since nothing is actually changing. Checked
    /// before anything is applied, so a rejected update leaves every field untouched, not partially
    /// applied.
    /// </summary>
    public void ApplyUpdate(ConfigurationValues values)
    {
        var violations = new List<string>();

        void Check<T>(string fieldName, T currentValue, T newValue)
        {
            if (!EqualityComparer<T>.Default.Equals(currentValue, newValue) && LockedFields.Contains(fieldName))
            {
                violations.Add(fieldName);
            }
        }

        Check(nameof(InitialSquadSize), InitialSquadSize, values.InitialSquadSize);
        Check(nameof(WeeklyRosterSize), WeeklyRosterSize, values.WeeklyRosterSize);
        Check(nameof(PositionalMinimumGk), PositionalMinimumGk, values.PositionalMinimumGk);
        Check(nameof(PositionalMinimumDef), PositionalMinimumDef, values.PositionalMinimumDef);
        Check(nameof(PositionalMinimumMid), PositionalMinimumMid, values.PositionalMinimumMid);
        Check(nameof(PositionalMinimumFwd), PositionalMinimumFwd, values.PositionalMinimumFwd);
        Check(nameof(DraftTimerSecondsInitial), DraftTimerSecondsInitial, values.DraftTimerSecondsInitial);
        Check(nameof(DraftTimerSecondsSecondary), DraftTimerSecondsSecondary, values.DraftTimerSecondsSecondary);
        Check(nameof(DraftTimerSecondsReplacement), DraftTimerSecondsReplacement, values.DraftTimerSecondsReplacement);
        Check(nameof(SecondaryDraftSelectionsPerTeam), SecondaryDraftSelectionsPerTeam, values.SecondaryDraftSelectionsPerTeam);
        Check(nameof(SecondaryDraftSchedulingOffsetDays), SecondaryDraftSchedulingOffsetDays, values.SecondaryDraftSchedulingOffsetDays);
        Check(nameof(GameweekRosterLockOffsetBeforeKickoffMinutes), GameweekRosterLockOffsetBeforeKickoffMinutes, values.GameweekRosterLockOffsetBeforeKickoffMinutes);
        Check(nameof(LeaguePointsWin), LeaguePointsWin, values.LeaguePointsWin);
        Check(nameof(LeaguePointsDraw), LeaguePointsDraw, values.LeaguePointsDraw);
        Check(nameof(LeaguePointsLoss), LeaguePointsLoss, values.LeaguePointsLoss);
        // BR-291: invitation expiration applies prospectively only — nothing ever adds it to
        // LockedFields, but the check costs nothing extra to leave in rather than special-case out.
        Check(nameof(InvitationExpirationDays), InvitationExpirationDays, values.InvitationExpirationDays);
        Check(nameof(ReplacementSelectionCap), ReplacementSelectionCap, values.ReplacementSelectionCap);
        Check(nameof(GameweekReminderLeadTimeHours), GameweekReminderLeadTimeHours, values.GameweekReminderLeadTimeHours);
        Check(nameof(TieBreakRulesetVersion), TieBreakRulesetVersion, values.TieBreakRulesetVersion);

        if (violations.Count > 0)
        {
            throw new SeasonConfigurationFieldsLockedException(violations);
        }

        InitialSquadSize = values.InitialSquadSize;
        WeeklyRosterSize = values.WeeklyRosterSize;
        PositionalMinimumGk = values.PositionalMinimumGk;
        PositionalMinimumDef = values.PositionalMinimumDef;
        PositionalMinimumMid = values.PositionalMinimumMid;
        PositionalMinimumFwd = values.PositionalMinimumFwd;
        DraftTimerSecondsInitial = values.DraftTimerSecondsInitial;
        DraftTimerSecondsSecondary = values.DraftTimerSecondsSecondary;
        DraftTimerSecondsReplacement = values.DraftTimerSecondsReplacement;
        SecondaryDraftSelectionsPerTeam = values.SecondaryDraftSelectionsPerTeam;
        SecondaryDraftSchedulingOffsetDays = values.SecondaryDraftSchedulingOffsetDays;
        GameweekRosterLockOffsetBeforeKickoffMinutes = values.GameweekRosterLockOffsetBeforeKickoffMinutes;
        LeaguePointsWin = values.LeaguePointsWin;
        LeaguePointsDraw = values.LeaguePointsDraw;
        LeaguePointsLoss = values.LeaguePointsLoss;
        InvitationExpirationDays = values.InvitationExpirationDays;
        ReplacementSelectionCap = values.ReplacementSelectionCap;
        GameweekReminderLeadTimeHours = values.GameweekReminderLeadTimeHours;
        TieBreakRulesetVersion = values.TieBreakRulesetVersion;
    }

    /// <summary>
    /// Core mechanics only (IT-08): marks a field locked so a later <see cref="ApplyUpdate"/>
    /// rejects any attempt to actually change it. Nothing calls this yet — each parameter's real
    /// "Locks At" trigger (BR-291: Initial Draft start, Season start, etc.) is the owning feature's
    /// own job (e.g. IT-23 for squad size at Draft start), not this task's. Idempotent.
    /// </summary>
    public void Lock(string fieldName)
    {
        if (!LockedFields.Contains(fieldName))
        {
            LockedFields.Add(fieldName);
        }
    }
}

/// <summary>IT-08 (BR-293): one or more fields in an updateSeasonConfiguration PUT are already locked for this Season and cannot be changed.</summary>
public sealed class SeasonConfigurationFieldsLockedException(IReadOnlyList<string> fieldNames) : DomainException(
    $"The following fields are already locked for this Season and cannot be changed: {string.Join(", ", fieldNames)}.")
{
    public IReadOnlyList<string> FieldNames { get; } = fieldNames;

    public override string ErrorCode => "season_configuration_fields_locked";

    public override int StatusCode => 409;
}

public class Invitation
{
    public Guid InvitationId { get; set; }
    public Guid LeagueId { get; set; }
    public Guid? SeasonId { get; set; }
    public string Token { get; set; } = null!;
    public string Destination { get; set; } = null!;
    public InvitationChannel Channel { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public InvitationStatus Status { get; set; } = InvitationStatus.Pending;

    /// <summary>
    /// BR-027–BR-029: issues a new, `Pending` invitation expiring `expirationDays` after issuance
    /// — the League's *current* <see cref="LeagueConfiguration.InvitationExpirationDays"/> at the
    /// moment of issuance, captured once here. BR-293: a later change to that configuration never
    /// rewrites an already-issued invitation's <see cref="ExpiresAt"/>.
    /// </summary>
    public static Invitation Issue(
        Guid invitationId,
        Guid leagueId,
        Guid? seasonId,
        string token,
        string destination,
        InvitationChannel channel,
        int expirationDays,
        DateTimeOffset now) =>
        new()
        {
            InvitationId = invitationId,
            LeagueId = leagueId,
            SeasonId = seasonId,
            Token = token,
            Destination = destination,
            Channel = channel,
            CreatedAt = now,
            ExpiresAt = now.AddDays(expirationDays),
            Status = InvitationStatus.Pending,
        };

    /// <summary>BR-029: whether this invitation can still be redeemed via acceptInvitation right now. Nothing proactively flips the stored `Status` column to `Expired` on a timer (unlike IT-F08's deadline-sweep targets) — a `Pending` invitation past its `ExpiresAt` is simply treated as unacceptable here, at the point of use.</summary>
    public bool IsAcceptable(DateTimeOffset now) => Status == InvitationStatus.Pending && ExpiresAt > now;

    /// <summary>The status to present to a caller right now (e.g. listInvitations) — `Pending` past `ExpiresAt` reads as `Expired` even though nothing ever rewrites the stored column (see <see cref="IsAcceptable"/>).</summary>
    public InvitationStatus EffectiveStatus(DateTimeOffset now) =>
        Status == InvitationStatus.Pending && ExpiresAt <= now ? InvitationStatus.Expired : Status;

    /// <summary>League-Administrator-only. Idempotent for a `Pending`, already-time-expired, or already-`Revoked` invitation (mirrors <c>IAuthenticationService.RevokeRefreshTokenAsync</c>'s "revoking an already-revoked token is not an error" convention) — but an invitation already redeemed into a real LeagueMembership cannot be revoked after the fact.</summary>
    public void Revoke()
    {
        if (Status == InvitationStatus.Accepted)
        {
            throw new InvitationAlreadyAcceptedException();
        }

        Status = InvitationStatus.Revoked;
    }

    /// <summary>Marks this invitation redeemed. Callers must have already checked <see cref="IsAcceptable"/>.</summary>
    public void Accept() => Status = InvitationStatus.Accepted;
}

/// <summary>IT-04: an already-accepted invitation is a terminal state — it was already redeemed into a real LeagueMembership, so it can no longer be revoked.</summary>
public sealed class InvitationAlreadyAcceptedException() : DomainException("This invitation has already been accepted and cannot be revoked.")
{
    public override string ErrorCode => "invitation_already_accepted";
}

/// <summary>
/// IT-51 (F-003.6, BR-221-BR-223): a League Administrator's own announcement, visible to every
/// active member of the League — retained under the same historical-retention policy as every
/// other League-scoped record (BR-223, BR-174, IT-50's own "nothing purges history" precedent), so
/// this never needs an edit/delete path of its own. <see cref="Body"/> is user-supplied content —
/// BR-167's own output-encoding requirement is a client-side rendering concern; this API layer's
/// only obligation is to never itself interpret it as markup (no server-side rendering of it at all).
/// </summary>
public class LeagueMessage
{
    public Guid LeagueMessageId { get; set; }
    public Guid LeagueId { get; set; }
    public Guid AuthorMembershipId { get; set; }
    public string Body { get; set; } = null!;
    public DateTimeOffset PublishedAt { get; set; }

    public static LeagueMessage Publish(Guid leagueMessageId, Guid leagueId, Guid authorMembershipId, string body, DateTimeOffset now) =>
        new()
        {
            LeagueMessageId = leagueMessageId,
            LeagueId = leagueId,
            AuthorMembershipId = authorMembershipId,
            Body = body,
            PublishedAt = now,
        };
}
