using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace EplFantasy.UnitTests.Architecture;

/// <summary>
/// Enforces Architecture and Domain Model v1.15 §5's module-dependency rule as an executable
/// check (IT-F01), not just a convention: "Dependencies point inward/downward per the table
/// order... Administration and Reporting may read from any module (they are cross-cutting/
/// read-oriented) but no module depends on them."
///
/// A project reference alone (an empty, unused &lt;ProjectReference&gt;) cannot violate this —
/// only an actual type-level dependency (a call, a base type, a field/parameter type, etc.) can,
/// which is exactly what NetArchTest inspects via the compiled assembly's IL, not its manifest.
/// This suite is therefore a real regression guard: it stays green today (no module has any
/// cross-module code yet) and will fail the moment a later task's code takes a dependency in the
/// wrong direction, long before a code reviewer would otherwise notice.
/// </summary>
public class ModuleDependencyTests
{
    // Order matches Architecture §5's module map table exactly, top to bottom. A module may
    // depend on any module earlier in this list, never a later one.
    private static readonly string[] Order =
    [
        "EplFantasy.Identity",
        "EplFantasy.Leagues",
        "EplFantasy.FantasyTeams",
        "EplFantasy.PlayerData",
        "EplFantasy.Drafts",
        "EplFantasy.Rosters",
        "EplFantasy.Scoring",
        "EplFantasy.Competition",
        "EplFantasy.Administration",
        "EplFantasy.Notifications",
        "EplFantasy.Reporting",
    ];

    // Cross-cutting/read-oriented (Architecture §5): may depend on any other module regardless of
    // position; the positional "may only depend on earlier modules" rule does not apply to them.
    private static readonly HashSet<string> CrossCutting = ["EplFantasy.Administration", "EplFantasy.Reporting"];

    public static IEnumerable<object[]> ModulesWithForbiddenTargets()
    {
        for (var i = 0; i < Order.Length; i++)
        {
            var module = Order[i];
            if (CrossCutting.Contains(module))
            {
                continue; // nothing is forbidden for a cross-cutting module; see the dedicated fact below instead.
            }

            // Forbidden: every module positioned after this one in Order — which, since Order
            // lists Administration/Reporting last among the non-earlier entries a non-cross-cutting
            // module could reach, already covers "no module depends on Administration or
            // Reporting" for every module here without needing a special case.
            var forbidden = Order.Skip(i + 1).ToArray();
            yield return [module, forbidden];
        }
    }

    [Theory]
    [MemberData(nameof(ModulesWithForbiddenTargets))]
    public void Module_must_not_depend_on_a_later_module(string moduleAssemblyName, string[] forbiddenAssemblyNames)
    {
        var assembly = Assembly.Load(moduleAssemblyName);

        var result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOnAny(forbiddenAssemblyNames)
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"{moduleAssemblyName} must not depend on any of: {string.Join(", ", forbiddenAssemblyNames)}. " +
            $"Failing types: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Nothing_depends_on_Administration_or_Reporting()
    {
        var everyoneElse = Order.Except(CrossCutting);

        foreach (var module in everyoneElse)
        {
            var assembly = Assembly.Load(module);

            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOnAny("EplFantasy.Administration", "EplFantasy.Reporting")
                .GetResult();

            Assert.True(
                result.IsSuccessful,
                $"{module} must not depend on Administration or Reporting (Architecture §5: " +
                "\"no module depends on them\"). Failing types: " +
                string.Join(", ", result.FailingTypeNames ?? []));
        }
    }

    [Fact]
    public void SharedKernel_never_depends_on_a_bounded_context_module()
    {
        // Architecture §14: SharedKernel is "shared identity/value-object plumbing only, never
        // business rules" — it must never become a back-door coupling point between modules.
        var assembly = Assembly.Load("EplFantasy.SharedKernel");

        var result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOnAny(Order)
            .GetResult();

        Assert.True(result.IsSuccessful, "EplFantasy.SharedKernel must not depend on any bounded-context module.");
    }
}
