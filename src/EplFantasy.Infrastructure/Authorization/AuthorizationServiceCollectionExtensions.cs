using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace EplFantasy.Infrastructure.Authorization;

public static class AuthorizationServiceCollectionExtensions
{
    /// <summary>
    /// Registers every AuthorizationPolicies.* policy and its handler (AP-002: "the API shall
    /// enforce all authorization... rules"). Call once from EplFantasy.Api's Program.cs; every
    /// controller action then references a policy by name rather than re-implementing a
    /// membership/administrator/ownership check inline.
    /// </summary>
    public static IServiceCollection AddEplFantasyAuthorizationPolicies(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(AuthorizationPolicies.ActiveLeagueMember, p => p.Requirements.Add(new ActiveLeagueMemberRequirement()))
            .AddPolicy(AuthorizationPolicies.LeagueAdministrator, p => p.Requirements.Add(new LeagueAdministratorRequirement()))
            .AddPolicy(AuthorizationPolicies.SystemAdministrator, p => p.Requirements.Add(new SystemAdministratorRequirement()))
            .AddPolicy(AuthorizationPolicies.FantasyTeamOwner, p => p.Requirements.Add(new FantasyTeamOwnerRequirement()))
            .AddPolicy(AuthorizationPolicies.MembershipOwnerOrLeagueAdministrator, p => p.Requirements.Add(new MembershipOwnerOrLeagueAdministratorRequirement()))
            .AddPolicy(AuthorizationPolicies.FantasyTeamOwnerOrActiveLeagueMember, p => p.Requirements.Add(new FantasyTeamOwnerOrActiveLeagueMemberRequirement()))
            .AddPolicy(AuthorizationPolicies.MembershipOwner, p => p.Requirements.Add(new MembershipOwnerRequirement()))
            .AddPolicy(AuthorizationPolicies.DraftTurnOwner, p => p.Requirements.Add(new DraftTurnOwnerRequirement()))
            .AddPolicy(AuthorizationPolicies.DraftLeagueAdministrator, p => p.Requirements.Add(new DraftLeagueAdministratorRequirement()))
            .AddPolicy(AuthorizationPolicies.DraftLeagueMember, p => p.Requirements.Add(new DraftLeagueMemberRequirement()))
            .AddPolicy(AuthorizationPolicies.RosterLeagueAdministrator, p => p.Requirements.Add(new RosterLeagueAdministratorRequirement()))
            .AddPolicy(AuthorizationPolicies.FantasyTeamLeagueMember, p => p.Requirements.Add(new FantasyTeamLeagueMemberRequirement()))
            .AddPolicy(AuthorizationPolicies.ScoreOverrideLeagueAdministrator, p => p.Requirements.Add(new ScoreOverrideLeagueAdministratorRequirement()))
            .AddPolicy(AuthorizationPolicies.FantasyTeamOwnerOrLeagueAdministrator, p => p.Requirements.Add(new FantasyTeamOwnerOrLeagueAdministratorRequirement()));

        services.AddScoped<IAuthorizationHandler, ActiveLeagueMemberAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, LeagueAdministratorAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, SystemAdministratorAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, FantasyTeamOwnerAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, MembershipOwnerOrLeagueAdministratorAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, FantasyTeamOwnerOrActiveLeagueMemberAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, MembershipOwnerAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, DraftTurnOwnerAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, DraftLeagueAdministratorAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, DraftLeagueMemberAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, RosterLeagueAdministratorAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, FantasyTeamLeagueMemberAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, ScoreOverrideLeagueAdministratorAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, FantasyTeamOwnerOrLeagueAdministratorAuthorizationHandler>();

        return services;
    }
}
