using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace UnturnedGodot.Tests.SourceRules;

// Finds and PARSES the repository's own sources, for the handful of rules that are genuinely about the
// shape of the code rather than about its behaviour.
//
// The distinction that matters is in Locate: a repository root that cannot be found is a run from
// outside the tree with nothing to check, and it FAILS rather than returning quietly. That is the
// lesson tests/Helpers/RealDataFact.cs wrote down after a whole class of tests was found asserting
// nothing while counting as green — "xUnit cannot tell an early return from a test that ran and
// asserted" — and it applies here for exactly the same reason.
public static class RepositorySource
{
    // The repository root, or a failure. Never null, and never a silent skip.
    public static string Root
    {
        get
        {
            var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "unturned-godot.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }

            Assert.Fail(
                $"no unturned-godot.sln above '{Directory.GetCurrentDirectory()}', so the repository "
                + "sources these rules read could not be found. These tests assert on the code in the "
                + "tree; a run with no tree to read has to fail rather than pass having checked nothing.");
            return string.Empty; // unreachable: Assert.Fail throws
        }
    }

    // The parsed syntax tree of one repository file. Fails if it is missing — the file being gone is
    // precisely when a rule about it has to speak up.
    public static SyntaxNode Parse(params string[] relativeParts)
    {
        string relative = Path.Combine(relativeParts);
        string path = Path.Combine(Root, relative);
        Assert.True(File.Exists(path),
            $"'{relative}' does not exist. A rule below reads that file, so it cannot pass without it: "
            + "either update the path to where the code moved, or delete the rule along with the thing "
            + "it was guarding.");

        SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path);
        return tree.GetRoot();
    }

    // Every .cs file under a repository subdirectory, parsed. For rules that are about the codebase
    // rather than about one file — a banned API, say — where naming the files would mean the rule
    // silently stopped covering whatever was added next.
    public static IEnumerable<(string Relative, SyntaxNode Root)> ParseAll(string relativeDirectory)
    {
        string directory = Path.Combine(Root, relativeDirectory);
        Assert.True(Directory.Exists(directory), $"'{relativeDirectory}' does not exist.");

        foreach (string path in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            // Generated partials carry whatever the generator emitted and are nobody's to fix.
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            {
                continue;
            }

            yield return (Path.GetRelativePath(Root, path),
                CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot());
        }
    }

    // "PhysicsServer3D.BodyAddShape(...)" and friends: the receiver and method of a static call, as
    // written. Reading this off the syntax tree rather than off the text is the whole point — an
    // argument list re-wrapped across three lines is the same call here, and a matching substring
    // inside a comment or a string literal is not a call at all.
    public static bool IsStaticCall(InvocationExpressionSyntax invocation, string type, string method)
    {
        return invocation.Expression is MemberAccessExpressionSyntax member
            && member.Name.Identifier.ValueText == method
            && member.Expression is IdentifierNameSyntax receiver
            && receiver.Identifier.ValueText == type;
    }

    // Every invocation inside a node, in source order.
    public static List<InvocationExpressionSyntax> Invocations(SyntaxNode node)
    {
        var found = new List<InvocationExpressionSyntax>();
        foreach (InvocationExpressionSyntax invocation in node.DescendantNodes()
            .OfType<InvocationExpressionSyntax>())
        {
            found.Add(invocation);
        }
        return found;
    }
}
