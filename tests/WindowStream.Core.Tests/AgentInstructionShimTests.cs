using Xunit;

namespace WindowStream.Core.Tests;

/// <summary>
/// Pins the agent-instruction shim files at the repository root. CLAUDE.md and GEMINI.md are excluded from markdownlint
/// (a bare <c>@AGENTS.md</c> import trips MD041/first-line-heading), so this test is the only guard that they still
/// exist and redirect to AGENTS.md instead of drifting back into hand-maintained copies.
/// </summary>
public sealed class AgentInstructionShimTests
{
    [Theory]
    [InlineData("CLAUDE.md")]
    [InlineData("GEMINI.md")]
    public void ShimRedirectsToAgentsFile(string fileName)
    {
        var repositoryRoot = LocateRepositoryRoot();
        var shimPath = Path.Combine(repositoryRoot, fileName);

        Assert.True(File.Exists(shimPath), $"{fileName} is missing at the repository root ({shimPath}).");
        Assert.Equal("@AGENTS.md", File.ReadAllText(shimPath).Trim());
    }

    static string LocateRepositoryRoot()
    {
        var directory = Path.GetDirectoryName(typeof(AgentInstructionShimTests).Assembly.Location)!;
        for (var hops = 0;
             hops < 8 && !File.Exists(Path.Combine(directory, "WindowStream.sln"));
             hops++)
        {
            directory = Path.GetDirectoryName(directory)!;
        }

        Assert.True(
            File.Exists(Path.Combine(directory, "WindowStream.sln")),
            "could not locate WindowStream.sln walking up from the test assembly directory.");
        return directory;
    }
}
