using EplFantasy.Competition;
using Xunit;

namespace EplFantasy.UnitTests.Competition;

public class StandingsTieBreakTests
{
    private static LeagueStanding Standing(
        Guid? fantasyTeamId = null,
        int leaguePoints = 10,
        int fantasyGoalDifference = 0,
        int fantasyGoalsFor = 0,
        int captainPointsTotal = 0) => new()
    {
        FantasyTeamId = fantasyTeamId ?? Guid.NewGuid(),
        SeasonId = Guid.NewGuid(),
        LeaguePoints = leaguePoints,
        FantasyGoalDifference = fantasyGoalDifference,
        FantasyGoalsFor = fantasyGoalsFor,
        CaptainPointsTotal = captainPointsTotal,
    };

    private sealed class FakeContext(
        Guid seasonId,
        Dictionary<(Guid, Guid, Guid), int>? headToHead = null,
        Dictionary<Guid, SeasonGoalPrediction>? predictions = null) : IStandingsTieBreakContext
    {
        public Guid SeasonId { get; } = seasonId;

        public int? HeadToHeadLeaguePoints(Guid fantasyTeamIdA, Guid fantasyTeamIdB, Guid forFantasyTeamId) =>
            headToHead is not null && headToHead.TryGetValue((fantasyTeamIdA, fantasyTeamIdB, forFantasyTeamId), out var points)
                ? points
                : null;

        public SeasonGoalPrediction? SeasonGoalPredictionFor(Guid fantasyTeamId) =>
            predictions?.GetValueOrDefault(fantasyTeamId);
    }

    private static readonly IStandingsTieBreakContext EmptyContext = new FakeContext(Guid.NewGuid());

    // ---- Tier 1: League Points ----

    [Fact]
    public void LeaguePoints_ranks_the_higher_total_first()
    {
        var a = Standing(leaguePoints: 20);
        var b = Standing(leaguePoints: 15);

        Assert.True(new LeaguePointsTieBreakRule().Compare(a, b, EmptyContext) < 0);
        Assert.True(new LeaguePointsTieBreakRule().Compare(b, a, EmptyContext) > 0);
    }

    [Fact]
    public void LeaguePoints_is_tied_when_equal()
    {
        var a = Standing(leaguePoints: 20);
        var b = Standing(leaguePoints: 20);

        Assert.Equal(0, new LeaguePointsTieBreakRule().Compare(a, b, EmptyContext));
    }

    // ---- Tier 2: Fantasy Goal Difference ----

    [Fact]
    public void FantasyGoalDifference_ranks_the_higher_value_first_and_ties_when_equal()
    {
        var rule = new FantasyGoalDifferenceTieBreakRule();
        Assert.True(rule.Compare(Standing(fantasyGoalDifference: 5), Standing(fantasyGoalDifference: 2), EmptyContext) < 0);
        Assert.Equal(0, rule.Compare(Standing(fantasyGoalDifference: 5), Standing(fantasyGoalDifference: 5), EmptyContext));
    }

    // ---- Tier 3: Fantasy Goals For ----

    [Fact]
    public void FantasyGoalsFor_ranks_the_higher_value_first_and_ties_when_equal()
    {
        var rule = new FantasyGoalsForTieBreakRule();
        Assert.True(rule.Compare(Standing(fantasyGoalsFor: 30), Standing(fantasyGoalsFor: 25), EmptyContext) < 0);
        Assert.Equal(0, rule.Compare(Standing(fantasyGoalsFor: 30), Standing(fantasyGoalsFor: 30), EmptyContext));
    }

    // ---- Tier 4: Head-to-Head League Points ----

    [Fact]
    public void HeadToHead_ranks_the_team_with_more_points_from_their_own_matches_first()
    {
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var context = new FakeContext(seasonId, headToHead: new()
        {
            [(teamA, teamB, teamA)] = 3, // A won their meeting(s).
            [(teamA, teamB, teamB)] = 0,
        });

        var rule = new HeadToHeadLeaguePointsTieBreakRule();
        Assert.True(rule.Compare(Standing(teamA), Standing(teamB), context) < 0);
    }

    [Fact]
    public void HeadToHead_is_tied_when_no_match_between_the_pair_exists_yet()
    {
        // Bye weeks and not-yet-played fixtures both look like "no data" — the rule must fall
        // through, not treat missing data as a 0-0 draw.
        var rule = new HeadToHeadLeaguePointsTieBreakRule();
        Assert.Equal(0, rule.Compare(Standing(), Standing(), EmptyContext));
    }

    // ---- Tier 5: Captain Points ----

    [Fact]
    public void CaptainPoints_ranks_the_higher_total_first_and_ties_when_equal()
    {
        var rule = new CaptainPointsTieBreakRule();
        Assert.True(rule.Compare(Standing(captainPointsTotal: 80), Standing(captainPointsTotal: 60), EmptyContext) < 0);
        Assert.Equal(0, rule.Compare(Standing(captainPointsTotal: 80), Standing(captainPointsTotal: 80), EmptyContext));
    }

    // ---- Tier 6: Season Goal Prediction (BR-132/BR-133) ----

    private static SeasonGoalPrediction Prediction(Guid fantasyTeamId, int predicted, int? actual, int? absoluteDifference) => new()
    {
        PredictionId = Guid.NewGuid(),
        FantasyTeamId = fantasyTeamId,
        PredictedEplGoals = predicted,
        FinalActualGoals = actual,
        FinalAbsoluteDifference = absoluteDifference,
    };

    [Fact]
    public void SeasonGoalPrediction_BR132_the_closer_prediction_wins_outright()
    {
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var context = new FakeContext(Guid.NewGuid(), predictions: new()
        {
            [teamA] = Prediction(teamA, 1230, 1234, absoluteDifference: 4),
            [teamB] = Prediction(teamB, 1235, 1234, absoluteDifference: 1), // BR-134: a higher raw prediction still wins on a smaller difference.
        });

        var result = new SeasonGoalPredictionTieBreakRule().Compare(Standing(teamA), Standing(teamB), context);

        Assert.True(result > 0, "Team B's closer prediction should outrank Team A");
    }

    [Fact]
    public void SeasonGoalPrediction_BR133_equal_difference_the_at_or_under_prediction_wins()
    {
        // BR-133's own worked example: Actual=1234, A=1233 (diff 1, under), B=1235 (diff 1, over) → A wins.
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var context = new FakeContext(Guid.NewGuid(), predictions: new()
        {
            [teamA] = Prediction(teamA, 1233, 1234, absoluteDifference: 1),
            [teamB] = Prediction(teamB, 1235, 1234, absoluteDifference: 1),
        });

        var result = new SeasonGoalPredictionTieBreakRule().Compare(Standing(teamA), Standing(teamB), context);

        Assert.True(result < 0, "the at-or-under prediction (Team A) should win an equal-difference tie");
    }

    [Fact]
    public void SeasonGoalPrediction_remains_tied_when_both_predictions_are_on_the_same_side_at_equal_difference()
    {
        // BR-133 only distinguishes under-or-equal vs. over — two identical-side predictions with
        // the same difference (e.g. both exactly at the actual total) is a genuine residual tie.
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var context = new FakeContext(Guid.NewGuid(), predictions: new()
        {
            [teamA] = Prediction(teamA, 1234, 1234, absoluteDifference: 0),
            [teamB] = Prediction(teamB, 1234, 1234, absoluteDifference: 0),
        });

        Assert.Equal(0, new SeasonGoalPredictionTieBreakRule().Compare(Standing(teamA), Standing(teamB), context));
    }

    [Fact]
    public void SeasonGoalPrediction_is_tied_when_the_season_has_not_ended_yet()
    {
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var context = new FakeContext(Guid.NewGuid(), predictions: new()
        {
            [teamA] = Prediction(teamA, 1230, actual: null, absoluteDifference: null),
            [teamB] = Prediction(teamB, 1235, actual: null, absoluteDifference: null),
        });

        Assert.Equal(0, new SeasonGoalPredictionTieBreakRule().Compare(Standing(teamA), Standing(teamB), context));
    }

    // ---- Tier 7: Random fallback (BR-125) ----

    [Fact]
    public void RandomFallback_is_deterministic_for_the_same_season_and_teams()
    {
        var seasonId = Guid.NewGuid();
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var context = new FakeContext(seasonId);
        var rule = new RandomFallbackTieBreakRule();

        var first = rule.Compare(Standing(teamA), Standing(teamB), context);
        var second = rule.Compare(Standing(teamA), Standing(teamB), context);

        Assert.Equal(first, second);
        Assert.NotEqual(0, first); // a total order must actually separate two distinct teams.
    }

    [Fact]
    public void RandomFallback_is_antisymmetric()
    {
        var context = new FakeContext(Guid.NewGuid());
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var rule = new RandomFallbackTieBreakRule();

        var aVersusB = rule.Compare(Standing(teamA), Standing(teamB), context);
        var bVersusA = rule.Compare(Standing(teamB), Standing(teamA), context);

        Assert.Equal(Math.Sign(aVersusB), -Math.Sign(bVersusA));
    }

    [Fact]
    public void RandomFallback_is_not_systematically_biased_toward_either_side()
    {
        // Fixing the same two FantasyTeamIds and varying only SeasonId, a hash-based fallback
        // that were secretly biased (e.g. "the numerically larger Guid always wins") would show
        // only one sign across many seasons. A fair hash should show both.
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var rule = new RandomFallbackTieBreakRule();

        var signsObserved = Enumerable.Range(0, 30)
            .Select(_ => Math.Sign(rule.Compare(Standing(teamA), Standing(teamB), new FakeContext(Guid.NewGuid()))))
            .Distinct()
            .ToList();

        Assert.Contains(-1, signsObserved);
        Assert.Contains(1, signsObserved);
    }

    // ---- Pipeline: chains tiers, only advancing past a tier that returns tied ----

    [Fact]
    public void Pipeline_uses_the_first_tier_that_separates_two_teams()
    {
        var pipeline = new StandingsTieBreakPipeline([new V1StandingsTieBreakRuleset()]);
        var comparer = pipeline.GetComparer("v1", EmptyContext);

        // Tied on League Points and Fantasy Goal Difference, separated at Fantasy Goals For (tier 3).
        var a = Standing(leaguePoints: 10, fantasyGoalDifference: 5, fantasyGoalsFor: 20);
        var b = Standing(leaguePoints: 10, fantasyGoalDifference: 5, fantasyGoalsFor: 15);

        Assert.True(comparer.Compare(a, b) < 0);
    }

    [Fact]
    public void Pipeline_falls_all_the_way_through_to_the_random_fallback_when_every_deterministic_tier_ties()
    {
        var pipeline = new StandingsTieBreakPipeline([new V1StandingsTieBreakRuleset()]);
        var comparer = pipeline.GetComparer("v1", EmptyContext);

        var a = Standing(leaguePoints: 10, fantasyGoalDifference: 5, fantasyGoalsFor: 20, captainPointsTotal: 40);
        var b = Standing(leaguePoints: 10, fantasyGoalDifference: 5, fantasyGoalsFor: 20, captainPointsTotal: 40);

        // Every deterministic tier ties (no head-to-head/prediction data in EmptyContext either) —
        // must still resolve to a definitive, non-zero order via the random fallback, never 0.
        Assert.NotEqual(0, comparer.Compare(a, b));
    }

    [Fact]
    public void Pipeline_throws_for_an_unknown_ruleset_version()
    {
        var pipeline = new StandingsTieBreakPipeline([new V1StandingsTieBreakRuleset()]);

        Assert.Throws<InvalidOperationException>(() => pipeline.GetComparer("v999-does-not-exist", EmptyContext));
    }

    // ---- IT-43 (F-010.2, Testing Strategy §2/BR-252): "every tier... plus at least one test per
    // tier confirming it is only reached when every earlier tier is tied (not evaluated
    // independently)." Each test below gives a LATER tier the opposite verdict from an EARLIER,
    // already-decisive tier — proving the pipeline stops at the first tier that separates the two
    // FantasyTeams and never lets a later tier override it, however that later tier alone would
    // have ranked them. ----

    private static IComparer<LeagueStanding> V1Comparer(IStandingsTieBreakContext context) =>
        new StandingsTieBreakPipeline([new V1StandingsTieBreakRuleset()]).GetComparer("v1", context);

    [Fact]
    public void Tier2_FantasyGoalDifference_is_never_consulted_when_Tier1_LeaguePoints_already_differs()
    {
        var a = Standing(leaguePoints: 20, fantasyGoalDifference: -10); // far worse Goal Difference...
        var b = Standing(leaguePoints: 10, fantasyGoalDifference: 10); // ...but B's is far better.

        Assert.True(V1Comparer(EmptyContext).Compare(a, b) < 0); // A still wins outright on League Points alone.
    }

    [Fact]
    public void Tier3_FantasyGoalsFor_is_never_consulted_when_Tier2_FantasyGoalDifference_already_differs()
    {
        var a = Standing(leaguePoints: 10, fantasyGoalDifference: 5, fantasyGoalsFor: 5); // low Goals For...
        var b = Standing(leaguePoints: 10, fantasyGoalDifference: 2, fantasyGoalsFor: 50); // ...but B's is far higher.

        Assert.True(V1Comparer(EmptyContext).Compare(a, b) < 0); // A's superior Goal Difference decides it.
    }

    [Fact]
    public void Tier4_HeadToHead_is_never_consulted_when_Tier3_FantasyGoalsFor_already_differs()
    {
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var a = Standing(teamA, leaguePoints: 10, fantasyGoalDifference: 5, fantasyGoalsFor: 30);
        var b = Standing(teamB, leaguePoints: 10, fantasyGoalDifference: 5, fantasyGoalsFor: 20);
        // Head-to-head says B beat A outright — would favor B if this tier were ever reached.
        var context = new FakeContext(Guid.NewGuid(), headToHead: new()
        {
            [(teamA, teamB, teamA)] = 0,
            [(teamA, teamB, teamB)] = 3,
        });

        Assert.True(V1Comparer(context).Compare(a, b) < 0); // A's higher Goals For decides it first.
    }

    [Fact]
    public void Tier5_CaptainPoints_is_never_consulted_when_Tier4_HeadToHead_already_differs()
    {
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var a = Standing(teamA, leaguePoints: 10, fantasyGoalDifference: 5, fantasyGoalsFor: 20, captainPointsTotal: 10);
        var b = Standing(teamB, leaguePoints: 10, fantasyGoalDifference: 5, fantasyGoalsFor: 20, captainPointsTotal: 100); // far higher Captain Points.
        // Head-to-head says A beat B outright — decisive before Captain Points is ever reached.
        var context = new FakeContext(Guid.NewGuid(), headToHead: new()
        {
            [(teamA, teamB, teamA)] = 3,
            [(teamA, teamB, teamB)] = 0,
        });

        Assert.True(V1Comparer(context).Compare(a, b) < 0); // A wins on head-to-head despite far fewer Captain Points.
    }

    [Fact]
    public void Tier6_SeasonGoalPrediction_is_never_consulted_when_Tier5_CaptainPoints_already_differs()
    {
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var a = Standing(teamA, leaguePoints: 10, fantasyGoalDifference: 5, fantasyGoalsFor: 20, captainPointsTotal: 100);
        var b = Standing(teamB, leaguePoints: 10, fantasyGoalDifference: 5, fantasyGoalsFor: 20, captainPointsTotal: 50);
        // No head-to-head data between this pair (tier 4 ties), but B's prediction is far closer —
        // would win tier 6 outright if it were ever reached.
        var context = new FakeContext(Guid.NewGuid(), predictions: new()
        {
            [teamA] = Prediction(teamA, 1000, actual: 1234, absoluteDifference: 234),
            [teamB] = Prediction(teamB, 1234, actual: 1234, absoluteDifference: 0),
        });

        Assert.True(V1Comparer(context).Compare(a, b) < 0); // A's higher Captain Points decides it first.
    }

    [Fact]
    public void Tier7_RandomFallback_is_never_consulted_when_Tier6_SeasonGoalPrediction_already_differs()
    {
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var a = Standing(teamA, leaguePoints: 10, fantasyGoalDifference: 5, fantasyGoalsFor: 20, captainPointsTotal: 40);
        var b = Standing(teamB, leaguePoints: 10, fantasyGoalDifference: 5, fantasyGoalsFor: 20, captainPointsTotal: 40);

        // Run against many distinct SeasonIds (only SeasonId feeds the random hash) — if Tier 6
        // weren't fully decisive first, a differently-seeded hash would eventually flip the sign.
        for (var i = 0; i < 20; i++)
        {
            var seededContext = new FakeContext(Guid.NewGuid(), predictions: new()
            {
                [teamA] = Prediction(teamA, 1233, actual: 1234, absoluteDifference: 1),
                [teamB] = Prediction(teamB, 1000, actual: 1234, absoluteDifference: 234),
            });
            Assert.True(V1Comparer(seededContext).Compare(a, b) < 0); // A's closer prediction always wins.
        }
    }

    /// <summary>ADR-008/BR-122: the tier sequence is data (a Season's own configured ruleset version), never hard-coded branching — reordering just two tiers (Captain Points ahead of League Points, everything else omitted so neither tier's own decision can be masked by a later one) changes which tier decides a given pair, with no rule's own code touched.</summary>
    private sealed class CaptainPointsFirstRuleset : IStandingsTieBreakRuleset
    {
        public string Version => "captain-points-first-for-test";
        public IReadOnlyList<IStandingsTieBreakRule> Tiers { get; } = [new CaptainPointsTieBreakRule(), new LeaguePointsTieBreakRule()];
    }

    [Fact]
    public void Tier_sequence_is_configuration_not_code_a_different_ruleset_version_changes_the_deciding_tier()
    {
        // A wins on League Points (the v1 ruleset's own tier 1); B wins on Captain Points.
        var a = Standing(leaguePoints: 20, captainPointsTotal: 10);
        var b = Standing(leaguePoints: 10, captainPointsTotal: 100);
        var pipeline = new StandingsTieBreakPipeline([new V1StandingsTieBreakRuleset(), new CaptainPointsFirstRuleset()]);

        var underV1 = pipeline.GetComparer("v1", EmptyContext).Compare(a, b);
        var underCaptainPointsFirst = pipeline.GetComparer("captain-points-first-for-test", EmptyContext).Compare(a, b);

        Assert.True(underV1 < 0); // v1: League Points decides first — A wins.
        Assert.True(underCaptainPointsFirst > 0); // reordered: Captain Points now decides first — B wins.
    }
}
