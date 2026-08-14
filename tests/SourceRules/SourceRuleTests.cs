using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace UnturnedGodot.Tests.SourceRules;

// Rules about the SHAPE of the code, checked against the parsed syntax tree.
//
// These are the survivors of tests/PhysicsBodyOrderTests.cs — the assertions in that file that were
// about a real invariant rather than about a particular spelling of it. The difference is the whole
// point of moving them here:
//
//   Assert.Contains("PhysicsServer3D.BodyAddShape", source)
//
// passes when the call sits in a comment, passes when it sits in a string, passes when it has been
// moved AFTER the call it is supposed to precede as long as both still appear, and FAILS when someone
// re-wraps the argument list across two lines. It is sensitive to everything that does not matter and
// blind to the thing that does. Parsing gives the opposite trade: a call is a call wherever it is
// written and however it is formatted, and its position relative to another call is a fact about the
// program rather than about the file.
//
// WHAT BELONGS HERE, AND WHAT DOES NOT
//
// A rule earns a place here when it is (a) genuinely a static property of the code — "this call must
// precede that one", "this API is banned outside these files" — and (b) not reachable behaviourally.
// Everything else belongs in tests/Runtime/ against a live engine, or in the hermetic suite against
// core/, or nowhere.
//
// A rule that can only be expressed as a string match is not a rule that belongs here. It is a sign
// that the code needs a seam — a named method, a wrapper, a type — that a test can hold on to. Adding
// another Assert.Contains over source text is how the file this replaces reached 1 800 lines and 302
// of them.
public class SourceRuleTests
{
    // ---------------------------------------------------------------------------------------------
    // The rule the original file was named after, and the one real bug it guards.
    //
    // InstancedStaticBody hands thousands of shapes to PhysicsServer3D. Adding them while the body is
    // already in a space makes the server re-register it with the broadphase on EVERY shape
    // (godotengine/godot#24026); on a 105k-object map that turned an otherwise ~700 ms scene attach
    // into a hang long enough to look like a freeze.
    //
    // It cannot be checked behaviourally in this suite — a physics space needs a live Godot runtime —
    // and it cannot honestly be checked behaviourally in tests/Runtime either, because what went wrong
    // was wall-clock cost on a map far larger than a test builds, not an observable difference in
    // outcome. Both orders produce the same collision world. That is exactly the case a static rule is
    // for.
    public static TheoryData<string, string> BodiesThatJoinASpace => new()
    {
        { "src/World/InstancedStaticBody.cs", "InstancedStaticBody" },
        { "src/World/InstancedStaticBodies.cs", "InstancedStaticBodies" },
    };

    [Theory]
    [MemberData(nameof(BodiesThatJoinASpace))]
    public void EveryShapeIsAddedBeforeTheBodyJoinsItsSpace(string file, string what)
    {
        SyntaxNode root = RepositorySource.Parse(file.Split('/'));

        var addShapes = new List<InvocationExpressionSyntax>();
        var setSpaces = new List<InvocationExpressionSyntax>();
        foreach (InvocationExpressionSyntax invocation in RepositorySource.Invocations(root))
        {
            if (RepositorySource.IsStaticCall(invocation, "PhysicsServer3D", "BodyAddShape"))
                addShapes.Add(invocation);
            else if (RepositorySource.IsStaticCall(invocation, "PhysicsServer3D", "BodySetSpace"))
                setSpaces.Add(invocation);
        }

        Assert.True(addShapes.Count > 0, $"{what} no longer calls PhysicsServer3D.BodyAddShape at all");
        Assert.True(setSpaces.Count > 0, $"{what} no longer calls PhysicsServer3D.BodySetSpace at all");

        int lastAdd = addShapes.Max(call => call.SpanStart);
        int firstJoin = setSpaces.Min(call => call.SpanStart);

        Assert.True(firstJoin > lastAdd,
            $"{what} joins its space before every shape is added. Joining first makes each "
            + "body_add_shape re-register the body with the broadphase (godotengine/godot#24026), which "
            + "is the difference between a ~700 ms scene attach and a hang on a 105k-object map.");
    }

    // The other half of the same lifecycle: the placement tuples the shapes were built from may only be
    // released once PhysicsServer has copied every transform out of them.
    [Fact]
    public void PlacementsAreReleasedOnlyAfterTheBodyHasJoinedItsSpace()
    {
        SyntaxNode root = RepositorySource.Parse("src", "World", "InstancedStaticBody.cs");

        int join = RepositorySource.Invocations(root)
            .Where(call => RepositorySource.IsStaticCall(call, "PhysicsServer3D", "BodySetSpace"))
            .Select(call => call.SpanStart)
            .DefaultIfEmpty(-1)
            .Max();
        Assert.True(join >= 0, "InstancedStaticBody no longer joins a space");

        // The release is an assignment of an empty array to Placements, wherever it is written and
        // however the empty array is spelled.
        List<AssignmentExpressionSyntax> releases = root.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(assignment => NameOf(assignment.Left) == "Placements"
                && assignment.Right.ToString().Contains("Empty"))
            .ToList();

        Assert.True(releases.Count > 0,
            "InstancedStaticBody no longer releases its placement tuples; if that is deliberate, delete "
            + "this rule with the code it guarded.");
        foreach (AssignmentExpressionSyntax release in releases)
        {
            Assert.True(release.SpanStart > join,
                "the placement tuples are released before PhysicsServer has copied every transform out "
                + "of them.");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Hardcoded WASD must ask for the PHYSICAL key, never the keycode.
    //
    // A keycode names the character the key prints, which moves with the layout: on AZERTY the "W" key
    // sits where QWERTY has Z and "A" where QWERTY has Q, so a free camera bound by keycode answers to
    // a scattering of keys under nobody's fingers.
    //
    // Checked across the whole of src/ rather than against FreeCamera by name, which is the upgrade
    // over the assertion this replaces: the old one read one file, so the next hardcoded binding
    // written anywhere else was not covered and nothing would have said so.
    //
    // PlayerController is exempt on purpose: it reads the player's own Unturned binds, where the
    // character IS the question being asked.
    [Fact]
    public void HardcodedBindingsAskForThePhysicalKeyRatherThanTheKeycode()
    {
        var offenders = new List<string>();

        foreach ((string relative, SyntaxNode root) in RepositorySource.ParseAll("src"))
        {
            if (relative.EndsWith("PlayerController.cs", System.StringComparison.Ordinal))
                continue;

            foreach (InvocationExpressionSyntax invocation in RepositorySource.Invocations(root))
            {
                if (RepositorySource.IsStaticCall(invocation, "Input", "IsKeyPressed"))
                {
                    FileLinePositionSpan at = invocation.GetLocation().GetLineSpan();
                    offenders.Add($"{relative}:{at.StartLinePosition.Line + 1}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Input.IsKeyPressed reads the character a key PRINTS, which moves with the keyboard layout. "
            + "Hardcoded bindings must use Input.IsPhysicalKeyPressed so they stay under the same "
            + $"fingers off QWERTY. Found at: {string.Join(", ", offenders)}");
    }

    // ...and the positive half, so the rule above cannot pass by the free camera having lost its
    // movement bindings altogether.
    [Fact]
    public void TheFreeCameraStillBindsItsMovementKeysPhysically()
    {
        SyntaxNode root = RepositorySource.Parse("src", "UI", "FreeCamera.cs");

        var bound = new HashSet<string>();
        foreach (InvocationExpressionSyntax invocation in RepositorySource.Invocations(root))
        {
            if (!RepositorySource.IsStaticCall(invocation, "Input", "IsPhysicalKeyPressed"))
                continue;
            foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments)
                bound.Add(argument.Expression.ToString());
        }

        foreach (string key in new[] { "Key.W", "Key.S", "Key.A", "Key.D", "Key.E", "Key.Q" })
            Assert.True(bound.Contains(key), $"the free camera no longer binds {key} physically");
    }

    private static string? NameOf(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => null,
    };
}
