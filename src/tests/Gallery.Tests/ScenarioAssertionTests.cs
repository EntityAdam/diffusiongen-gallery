using System.Security.Cryptography;
using System.Text.Json;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class ScenarioAssertionTests
{
    [Fact]
    public void ScenarioObservations_WhenValuesAndOrderedSequencesMatch_ThenAcceptsTheCompleteScenario()
    {
        // Arrange
        var expectedScenario = ExpectedScenario.CreateFrom("All observations and their sequence order must match.",
            new { Count = 2, IsReady = true, Order = new[] { 1, 2 } });
        var actualObservations = new { Count = 2, IsReady = true, Order = new[] { 1, 2 } };

        // Act
        var actualException = Record.Exception(() => AsserterScenario.AssertObservationsMatch(expectedScenario, actualObservations));

        // Assert
        Assert.True(actualException is null,
            $"Expected matching scenario observations to be accepted; Actual: {actualException?.ToString() ?? "<no exception>"}.");
    }

    [Fact]
    public void ScenarioObservations_WhenSeveralValuesDiffer_ThenReportsEveryExpectedAndActualValue()
    {
        // Arrange
        const string ExpectedBusinessRule = "All observations and their sequence order must match.";
        var expectedScenario = ExpectedScenario.CreateFrom(ExpectedBusinessRule, new { Count = 2, IsReady = true, Order = new[] { 1, 2 } });
        var actualObservations = new { Count = 3, IsReady = false, Order = new[] { 2, 1 } };
        string[] expectedMessagePhrases =
        [
            ExpectedBusinessRule, "SCENARIO ASSERTION FAILED", "3 discrepancies", "Count: Expected: 2; Actual: 3",
            "IsReady: Expected: True; Actual: False", "Order: Expected: [1,2]; Actual: [2,1]"
        ];

        // Act
        var actualTrueException = Assert.Throws<Xunit.Sdk.TrueException>(
            () => AsserterScenario.AssertObservationsMatch(expectedScenario, actualObservations));

        // Assert
        AsserterExceptionContract.AssertMessageContains(expectedMessagePhrases, actualTrueException.Message);
    }

    // Duplicate and substituted IDs both violate complete membership, even when group size and sharing match.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScenarioObservations_WhenGroupMembershipIsIncorrect_ThenReportsExpectedAndActualImageIds(bool duplicatesImage)
    {
        // Arrange
        const string ExpectedBusinessRule = "A shared prompt group contains exactly its expected images.";
        string[] expectedImageIds =
        [
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
        ];
        string[] actualImageIds =
        [
            expectedImageIds[0],
            duplicatesImage ? expectedImageIds[0] : Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
        ];
        var expectedScenario = ExpectedScenario.CreateFrom(ExpectedBusinessRule, new
        {
            GroupSize = expectedImageIds.Length, SharedGroup = true,
            GroupImageIds = expectedImageIds.Order(StringComparer.Ordinal).ToArray()
        });
        var actualObservations = new
        {
            GroupSize = actualImageIds.Length, SharedGroup = true,
            GroupImageIds = actualImageIds.Order(StringComparer.Ordinal).ToArray()
        };
        string[] expectedMessagePhrases =
        [
            ExpectedBusinessRule,
            $"GroupImageIds: Expected: {JsonSerializer.Serialize(expectedImageIds.Order(StringComparer.Ordinal))}; " +
            $"Actual: {JsonSerializer.Serialize(actualImageIds.Order(StringComparer.Ordinal))}."
        ];

        // Act
        var actualTrueException = Assert.Throws<Xunit.Sdk.TrueException>(
            () => AsserterScenario.AssertObservationsMatch(expectedScenario, actualObservations));

        // Assert
        AsserterExceptionContract.AssertMessageContains(expectedMessagePhrases, actualTrueException.Message);
    }
}
