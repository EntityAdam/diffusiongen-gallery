namespace Gallery.Tests;

[Trait("Category", "class_test")]
public static class VisionScenarioAssertionTests
{
    [Fact]
    public static void AssertAllObligationsAreMet_WhenEveryComparisonMatches_ThenAcceptsCompleteEvidence()
    {
        // Arrange
        var expected = ExpectedVisionScenario.Create("every promised observation matches",
            new ExpectedVisionObligation("models", new[] { "first", "second" }), new ExpectedVisionObligation("policy", "private", VisionComparison.Contains),
            new ExpectedVisionObligation("redaction", "secret", VisionComparison.Excludes), new ExpectedVisionObligation("error", null));
        var actual = new Dictionary<string, object?> { ["models"] = new[] { "first", "second" }, ["policy"] = "PRIVATE origin", ["redaction"] = "safe authority", ["error"] = null };
        // Act
        var error = Record.Exception(() => AsserterVisionScenario.AssertAllObligationsAreMet(expected, actual));
        // Assert - the oracle accepts all independently supplied matching obligations
        TestAssertions.Null(error, "all comparison forms accept matching evidence");
    }

    [Fact]
    public static void AssertAllObligationsAreMet_WhenSeveralComparisonsFail_ThenReportsEveryDiscrepancy()
    {
        // Arrange
        var expected = ExpectedVisionScenario.Create("all observations must match",
            new ExpectedVisionObligation("models", new[] { "first" }), new ExpectedVisionObligation("policy", "private", VisionComparison.Contains),
            new ExpectedVisionObligation("redaction", "secret", VisionComparison.Excludes), new ExpectedVisionObligation("missing", 1));
        var actual = new Dictionary<string, object?> { ["models"] = new[] { "wrong" }, ["policy"] = "public", ["redaction"] = "secret" };
        var expectedError = new ExpectedException("the oracle reports every mismatch and the promised rule",
            "models: Expected", "policy: Expected", "redaction: Expected", "missing: Expected", "Actual:", "all observations must match");
        // Act
        var error = Record.Exception(() => AsserterVisionScenario.AssertAllObligationsAreMet(expected, actual));
        // Assert - one failure carries every independently introduced discrepancy
        TestAssertions.Exception<Xunit.Sdk.TrueException>(expectedError, error);
    }
}
