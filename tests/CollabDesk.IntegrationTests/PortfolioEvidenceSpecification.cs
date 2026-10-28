using Xunit;

namespace CollabDesk.IntegrationTests;

public sealed class PortfolioEvidenceSpecification
{
    [Fact]
    public void Reviewer_evidence_index_and_automation_are_present()
    {
        var root = FindRepositoryRoot();

        foreach (var path in new[]
        {
            "README.md", "LICENSE", ".gitignore", ".github/workflows/ci.yml",
            "docs/architecture.md", "docs/api.md", "docs/testing.md", "docs/benchmarks.md",
            "docs/screenshots.md", "docs/security.md", "docs/cv-bullets.md", "docs/task-reports/NET-013.md",
            "scripts/verify-no-secrets.sh"
        })
            Assert.True(File.Exists(Path.Combine(root, path)), $"Missing reviewer evidence: {path}");

        var architecture = File.ReadAllText(Path.Combine(root, "docs/architecture.md"));
        Assert.Contains("```mermaid", architecture, StringComparison.Ordinal);
        Assert.Contains("SignalR", architecture, StringComparison.Ordinal);
        Assert.Contains("outbox", architecture, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Redis", architecture, StringComparison.Ordinal);

        var ci = File.ReadAllText(Path.Combine(root, ".github/workflows/ci.yml"));
        Assert.Contains("verify-no-secrets.sh", ci, StringComparison.Ordinal);
        Assert.Contains("dotnet test", ci, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CollabDesk.sln"))) return directory.FullName;

        throw new DirectoryNotFoundException("Could not find CollabDesk.sln from the test output directory.");
    }
}
