namespace Gallery.Tests;

[Trait("Category", "class_test")]
public static class VisionScenarioAssertionTests
{
    [Fact]
    public static void AssertAllObligationsAreMet_WhenEveryComparisonMatches_ThenAcceptsCompleteEvidence()
    {
        // Arrange
        var expectedVisionScenario = ExpectedVisionScenario.Create("every promised observation matches",
            new ExpectedVisionObligation("models", new[] { "first", "second" }), new ExpectedVisionObligation("policy", "private", VisionComparison.Contains),
            new ExpectedVisionObligation("redaction", "secret", VisionComparison.Excludes), new ExpectedVisionObligation("error", null));
        var actualVisionValues = new Dictionary<string, object?> { ["models"] = new[] { "first", "second" }, ["policy"] = "PRIVATE origin", ["redaction"] = "safe authority", ["error"] = null };

        // Act
        var error = Record.Exception(() => AsserterVisionScenario.AssertAllObligationsAreMet(expectedVisionScenario, actualVisionValues));

        // Assert - the oracle accepts all independently supplied matching obligations
        Assert.Null(error);
    }

    [Fact]
    public static void AssertAllObligationsAreMet_WhenSeveralComparisonsFail_ThenReportsEveryDiscrepancy()
    {
        // Arrange
        var expectedVisionScenario = ExpectedVisionScenario.Create("all observations must match",
            new ExpectedVisionObligation("models", new[] { "first" }), new ExpectedVisionObligation("policy", "private", VisionComparison.Contains),
            new ExpectedVisionObligation("redaction", "secret", VisionComparison.Excludes), new ExpectedVisionObligation("missing", 1));
        var actualVisionValues = new Dictionary<string, object?> { ["models"] = new[] { "wrong" }, ["policy"] = "public", ["redaction"] = "secret" };
        string[] expectedMismatchLines =
        [
            "models: Expected (Equal): [\"first\"]; Actual: [\"wrong\"]",
            "policy: Expected (Contains): \"private\"; Actual: \"public\"",
            "redaction: Expected (Excludes): \"secret\"; Actual: \"secret\"",
            "missing: Expected (Equal): 1; Actual: <missing evidence>"
        ];

        // Act
        var error = Record.Exception(() => AsserterVisionScenario.AssertAllObligationsAreMet(expectedVisionScenario, actualVisionValues));

        // Assert - one failure carries every independently introduced discrepancy
        var assertionFailure = Assert.IsType<Xunit.Sdk.TrueException>(error);
        Assert.Contains("4-of-4 obligations failed", assertionFailure.Message, StringComparison.Ordinal);
        AsserterExceptionContract.AssertMessageContains(expectedMismatchLines, assertionFailure.Message);
        Assert.Contains("all observations must match", assertionFailure.Message, StringComparison.Ordinal);
    }
}
