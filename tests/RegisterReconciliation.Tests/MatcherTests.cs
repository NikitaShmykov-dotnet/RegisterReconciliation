using Xunit;

namespace RegisterReconciliation.Tests;

public sealed class MatcherTests
{
    private static readonly DateTime Noon = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TwoRowsCompetingForOneCandidateStayAmbiguous()
    {
        var outcomes = Matcher.Compare([Row(1, null), Row(2, null, -5)], [Row(3, "provider")], TestContext).ToArray();

        Assert.Equal(3, outcomes.Length);
        Assert.All(outcomes, outcome => Assert.Equal("ambiguous", outcome.Category));
        Assert.Equal(2, outcomes.Single(outcome => outcome.External is not null).CandidateCount);
    }

    [Fact]
    public void ExactIdWinsBeforeFallbackEvenWhenCurrencyDiffers()
    {
        var outcomes = Matcher.Compare([Row(1, "shared"), Row(2, null)], [Row(3, "shared") with { Currency = "EUR" }],
            TestContext).ToArray();

        Assert.Collection(outcomes,
            pair =>
            {
                Assert.Equal("amount-mismatch", pair.Category);
                Assert.Equal("exact-id", pair.Method);
                Assert.Equal(1, pair.Internal!.Id);
                Assert.Equal(3, pair.External!.Id);
            },
            remainder => Assert.Equal("missing-external", remainder.Category));
    }

    [Fact]
    public void ConflictingNonemptyIdsAndOutOfWindowRowsDoNotMatch()
    {
        var outcomes = Matcher.Compare([Row(1, "left"), Row(2, null, -6)], [Row(3, "right")], TestContext).ToArray();

        Assert.Equal(["missing-external", "missing-external", "missing-internal"], outcomes.Select(row => row.Category));
    }

    [Fact]
    public void WindowIsInclusiveAndQuotedFieldsNormalize()
    {
        var (left, error) = Csv.Parse(",+9.1,USD,2026-01-15T15:00:00+03:00,\"order, \"\"A\"\"\"");
        Assert.Null(error);
        Assert.Equal("order, \"A\"", left!.OrderReference);
        Assert.Equal(Noon, left.OccurredAt);

        var atBoundary = left with { Id = 2, OccurredAt = Noon.AddMinutes(5) };
        var late = atBoundary with { OccurredAt = atBoundary.OccurredAt.AddTicks(10) };
        Assert.Equal("fallback", Assert.Single(Matcher.Compare([left], [atBoundary], TestContext)).Method);
        Assert.Equal(2, Matcher.Compare([left], [late], TestContext).Count());
    }

    [Theory]
    [InlineData("=2+2", "\"'=2+2\"")]
    [InlineData("  @SUM(A1)", "\"'  @SUM(A1)\"")]
    [InlineData("order, \"A\"", "\"order, \"\"A\"\"\"")]
    [InlineData("first\r\nsecond", "\"first\r\nsecond\"")]
    public void ExportEscapesUserText(string value, string escaped) => Assert.Equal(escaped, Csv.Cell(value));

    private static Operation Row(long id, string? externalId, int minuteOffset = 0) =>
        new(id, externalId, 9.1m, "USD", Noon.AddMinutes(minuteOffset), "order");

    private static CancellationToken TestContext => CancellationToken.None;
}
