using EplFantasy.SharedKernel;

namespace EplFantasy.Drafts;

/// <summary>Architecture v1.15 §7's Draft-context domain event catalog. Constructed (as a log entry, not yet a dispatched object — see Identity's UsernameChanged for why) by IDraftService.MakePickAsync on a successful pick, in the same transaction as the DraftSelection/SquadPlayer inserts.</summary>
public sealed record PlayerDrafted(Guid DraftId, Guid FantasyTeamId, Guid PlayerId, int Round, int PickNumber, DateTimeOffset OccurredAt) : IDomainEvent;
