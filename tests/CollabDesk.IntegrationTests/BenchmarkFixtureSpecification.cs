using CollabDesk.Application;
using Xunit;

namespace CollabDesk.IntegrationTests;

public sealed class BenchmarkFixtureSpecification
{
    [Fact]
    public void Generator_produces_deterministic_fictional_ticket_data_at_each_supported_size()
    {
        foreach (var size in new[] { 10_000, 50_000, 100_000 })
        {
            var first = ReportBenchmarkFixture.CreateTickets(size, 42);
            var second = ReportBenchmarkFixture.CreateTickets(size, 42);
            Assert.Equal(size, first.Count);
            Assert.Equal(first[0].Title, second[0].Title);
            Assert.Equal(first[0].CreatedAt, second[0].CreatedAt);
            Assert.Equal(first[^1].CustomerId, second[^1].CustomerId);
            Assert.All(first, ticket => Assert.Contains("Synthetic local-only", ticket.Description, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("collabdesk")]
    [InlineData("collabdesk_test_net012")]
    [InlineData("production")]
    public void Fixture_refuses_any_database_outside_the_benchmark_namespace(string database) =>
        Assert.Throws<InvalidOperationException>(() => ReportBenchmarkFixture.RequireBenchmarkDatabase(database));

    [Fact]
    public void Fixture_accepts_only_the_intended_isolated_database_namespace() =>
        ReportBenchmarkFixture.RequireBenchmarkDatabase("collabdesk_benchmark_net012");

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void Fixture_accepts_loopback_hosts(string host) => ReportBenchmarkFixture.RequireLoopbackHost(host);

    [Theory]
    [InlineData("db")]
    [InlineData("10.0.0.8")]
    [InlineData("benchmark.example.test")]
    public void Fixture_refuses_non_loopback_hosts(string host) =>
        Assert.Throws<InvalidOperationException>(() => ReportBenchmarkFixture.RequireLoopbackHost(host));
}
