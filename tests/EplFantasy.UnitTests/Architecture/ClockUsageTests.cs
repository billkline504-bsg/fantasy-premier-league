using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace EplFantasy.UnitTests.Architecture;

/// <summary>
/// Enforces IClock's own contract (IT-F04, SharedKernel/IClock.cs): nothing outside
/// EplFantasy.Infrastructure.SystemClock may call <see cref="DateTime.Now"/>,
/// <see cref="DateTime.UtcNow"/>, <see cref="DateTimeOffset.Now"/>, or
/// <see cref="DateTimeOffset.UtcNow"/> directly. This cannot be expressed as a NetArchTest
/// type-dependency rule (ModuleDependencyTests' approach) because plenty of legitimate code
/// depends on the *type* DateTimeOffset — IClock.UtcNow itself returns one. What must be forbidden
/// is a call to one specific static member, which requires inspecting method bodies at the IL
/// level (via the same Mono.Cecil library NetArchTest itself uses), not just assembly-level
/// dependency detection.
/// </summary>
public class ClockUsageTests
{
    private static readonly (string DeclaringType, string MemberName)[] ForbiddenMembers =
    [
        ("System.DateTime", "get_Now"),
        ("System.DateTime", "get_UtcNow"),
        ("System.DateTimeOffset", "get_Now"),
        ("System.DateTimeOffset", "get_UtcNow"),
    ];

    public static IEnumerable<object[]> AssembliesToScan() =>
    [
        ["EplFantasy.Identity"],
        ["EplFantasy.Leagues"],
        ["EplFantasy.FantasyTeams"],
        ["EplFantasy.PlayerData"],
        ["EplFantasy.Drafts"],
        ["EplFantasy.Rosters"],
        ["EplFantasy.Scoring"],
        ["EplFantasy.Competition"],
        ["EplFantasy.Administration"],
        ["EplFantasy.Notifications"],
        ["EplFantasy.Reporting"],
        ["EplFantasy.Infrastructure"],
        // Added at IT-01: earlier tasks left EplFantasy.Api out of this list because it had no
        // business logic of its own yet (just Program.cs wiring and a thin auth accessor) — the
        // first real controller (AuthController) is exactly the kind of code this scan exists to
        // catch, so it belongs here from here on.
        ["EplFantasy.Api"],
    ];

    [Theory]
    [MemberData(nameof(AssembliesToScan))]
    public void Assembly_never_calls_the_wall_clock_directly(string assemblyName)
    {
        var assemblyPath = Assembly.Load(assemblyName).Location;
        using var module = ModuleDefinition.ReadModule(assemblyPath);

        var violations = new List<string>();

        foreach (var type in module.GetTypes())
        {
            // The one sanctioned exception: SystemClock.UtcNow is where the real clock is allowed
            // to enter the application (see IClock.cs and SystemClock.cs's own remarks).
            if (type.FullName == "EplFantasy.Infrastructure.SystemClock")
            {
                continue;
            }

            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)
                    {
                        continue;
                    }

                    if (instruction.Operand is not MethodReference calledMethod)
                    {
                        continue;
                    }

                    var isForbidden = ForbiddenMembers.Any(f =>
                        calledMethod.DeclaringType.FullName == f.DeclaringType &&
                        calledMethod.Name == f.MemberName);

                    if (isForbidden)
                    {
                        violations.Add($"{type.FullName}.{method.Name} calls {calledMethod.DeclaringType.Name}.{calledMethod.Name}");
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            $"{assemblyName} calls the wall clock directly instead of going through IClock:\n" + string.Join("\n", violations));
    }
}
