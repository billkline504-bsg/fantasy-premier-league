using Microsoft.AspNetCore.Authorization;

namespace EplFantasy.Infrastructure.Authorization;

// Marker requirements — each carries no data of its own because the resource id it checks against
// (leagueId, fantasyTeamId) is read from the current request's route values at evaluation time
// (see RouteValueReader), not baked into the requirement/policy at registration time. This is
// what lets one policy registration (Program.cs) serve every endpoint shaped like
// "/leagues/{leagueId}/..." without a per-endpoint requirement instance.

public sealed class ActiveLeagueMemberRequirement : IAuthorizationRequirement;

public sealed class LeagueAdministratorRequirement : IAuthorizationRequirement;

public sealed class SystemAdministratorRequirement : IAuthorizationRequirement;

public sealed class FantasyTeamOwnerRequirement : IAuthorizationRequirement;

/// <summary>"Caller must own {membershipId}, or be the League Administrator of {leagueId}" — leaveLeague's own x-authorization (F-003.3), the composed case FantasyTeamOwnerAuthorizationHandler's remarks anticipated.</summary>
public sealed class MembershipOwnerOrLeagueAdministratorRequirement : IAuthorizationRequirement;

/// <summary>"Caller must own {fantasyTeamId}, or be an active member of {leagueId}" — getSeasonGoalPrediction's own x-authorization (F-010.3), the other composed case FantasyTeamOwnerAuthorizationHandler's remarks anticipated.</summary>
public sealed class FantasyTeamOwnerOrActiveLeagueMemberRequirement : IAuthorizationRequirement;

/// <summary>"Caller must own {membershipId}" — setLeagueIcon/clearLeagueIcon's own x-authorization (F-002.2), with no Administrator-on-behalf-of fallback (unlike MembershipOwnerOrLeagueAdministratorRequirement).</summary>
public sealed class MembershipOwnerRequirement : IAuthorizationRequirement;

/// <summary>"Caller must own the FantasyTeam whose turn it currently is" — makeDraftPick's own x-authorization (F-005.2). Unlike every other requirement here, the resource id it checks against ({draftId}) isn't the id being owned — the handler resolves whose turn it is from the Draft's own state first.</summary>
public sealed class DraftTurnOwnerRequirement : IAuthorizationRequirement;

/// <summary>"Caller must be the League Administrator of the Draft's League" — extendDraftTimer's own x-authorization (F-005.3). The route names only {draftId}; the handler resolves the League via Draft.SeasonId → Season.LeagueId first, the same indirection DraftTurnOwnerRequirement already established for this route shape.</summary>
public sealed class DraftLeagueAdministratorRequirement : IAuthorizationRequirement;

/// <summary>"Caller must be an active member of the Draft's League" — getDraft/listDraftSelections/getDraftPlayerPool's own x-authorization (F-005.5). Same {draftId}-only route shape as DraftLeagueAdministratorRequirement, just checking active membership rather than administrator status.</summary>
public sealed class DraftLeagueMemberRequirement : IAuthorizationRequirement;

/// <summary>"Caller must be the League Administrator of the roster's League" — correctRoster's own x-authorization (F-007.4). The route names only {gameweekRosterId}; the handler resolves the League via GameweekRoster.FantasyTeamId → FantasyTeam.LeagueMembershipId → LeagueMembership.LeagueId, the same "resolve the resource id chain from a narrower route" indirection DraftLeagueAdministratorRequirement already established for a different chain.</summary>
public sealed class RosterLeagueAdministratorRequirement : IAuthorizationRequirement;

/// <summary>"Caller must be an active member of the FantasyTeam's League" — getGameweekScore's own x-authorization (F-008.1), no owner shortcut (unlike FantasyTeamOwnerOrActiveLeagueMemberRequirement — a FantasyTeam's own owner whose membership itself somehow isn't Active still needs to satisfy this the same way anyone else would). Resolves the League via FantasyTeam.LeagueMembershipId → LeagueMembership.LeagueId, since the route names no leagueId at all.</summary>
public sealed class FantasyTeamLeagueMemberRequirement : IAuthorizationRequirement;

/// <summary>"Caller must be the League Administrator of the override's League" — undoScoreOverride's own x-authorization (F-008.5). The route names only {scoreOverrideId}; the handler resolves the League via ScoreOverride.AdministratorMembershipId → LeagueMembership.LeagueId — unambiguous here, unlike createScoreOverride (IScoreOverrideService's own remarks), since it's whichever League the override was originally created under.</summary>
public sealed class ScoreOverrideLeagueAdministratorRequirement : IAuthorizationRequirement;

/// <summary>"Caller must own {fantasyTeamId}, or be the League Administrator of {leagueId}" — listReplacementOpportunities' own x-authorization (F-006.4). Unlike FantasyTeamOwnerOrActiveLeagueMemberRequirement's "any active member" fallback, only the Administrator specifically may view another FantasyTeam's own opportunities.</summary>
public sealed class FantasyTeamOwnerOrLeagueAdministratorRequirement : IAuthorizationRequirement;
