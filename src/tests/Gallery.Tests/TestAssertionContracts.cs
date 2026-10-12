namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class TestAssertionContracts
{
    [Fact]
    public void ExceptionMessage_WhenAllRequiredPhrasesArePresent_ThenAcceptsTheException()
    {
        // Arrange
        var actualException = new InvalidOperationException("The vault is locked.");

        // Act
        var capturedException = TestAssertions.Exception<InvalidOperationException>(new ExpectedException("the vault failure explains its condition", "vault", "locked"), actualException);

        // Assert
        Assert.Same(actualException, capturedException);
    }

    [Fact]
    public void ExceptionMessage_WhenSeveralPhrasesAreAbsent_ThenReportsEveryMissingPhraseAndTheActualMessage()
    {
        // Arrange
        var actualException = new InvalidOperationException("The vault is unavailable.");
        string[] expectedMessageParts = ["vault", "locked", "retry"];

        // Act
        var assertionFailure = Assert.Throws<Xunit.Sdk.TrueException>(() =>
            TestAssertions.Exception<InvalidOperationException>(new ExpectedException("the failure explains its condition and remedy", expectedMessageParts), actualException));

        // Assert
        Assert.Contains("Expected:", assertionFailure.Message);
        Assert.Contains(actualException.Message, assertionFailure.Message);
        Assert.Contains("Missing: [locked, retry]", assertionFailure.Message);
    }

    [Fact]
    public void ExceptionMessage_WhenTheExceptionTypeIsWrong_ThenRejectsMatchingMessageText()
    {
        // Arrange
        var actualException = new IOException("The vault is locked.");

        // Act
        var assertionFailure = Assert.Throws<Xunit.Sdk.IsTypeException>(() =>
            TestAssertions.Exception<InvalidOperationException>(new ExpectedException("the operation raises the expected application failure", "vault", "locked"), actualException));

        // Assert
        Assert.Contains(nameof(InvalidOperationException), assertionFailure.Message);
        Assert.Contains(nameof(IOException), assertionFailure.Message);
    }

    // Rephrasing must preserve required meaning without pinning the complete diagnostic sentence.
    [Theory]
    [InlineData("The vault is locked.")]
    [InlineData("The VAULT is LOCKED.")]
    [InlineData("Unlock the locked vault before continuing.")]
    public void ExceptionMessage_WhenWordingChangesButMeaningRemains_ThenAcceptsRequiredPhrases(string message)
    {
        // Arrange
        var actualException = new InvalidOperationException(message);

        // Act
        var capturedException = TestAssertions.Exception<InvalidOperationException>(new ExpectedException("the message identifies a locked vault", "locked", "vault"), actualException);

        // Assert
        Assert.Same(actualException, capturedException);
    }
    // Every comparison accepts matching evidence and reports its complete diagnostic on mismatch.
    // Columns: assertion family, passing comparison, failing comparison, complete expected diagnostic.
    public static IEnumerable<object[]> ComparisonCases()
    {
        yield return ["True", (Action)(() => TestAssertions.True(true, "enabled")),
            (Action)(() => TestAssertions.True(false, "enabled")), "Expected: enabled; Actual: False."];
        yield return ["False", (Action)(() => TestAssertions.False(false, "disabled")),
            (Action)(() => TestAssertions.False(true, "disabled")), "Expected: disabled; Actual: True."];
        yield return ["Contains text", (Action)(() => TestAssertions.Contains("cat", "a cat", "contains cat")),
            (Action)(() => TestAssertions.Contains("cat", "a dog", "contains cat")),
            "Expected: contains cat (containing \"cat\"); Actual: \"a dog\"."];
        yield return ["StartsWith", (Action)(() => TestAssertions.StartsWith("cat", "catfish", "starts with cat")),
            (Action)(() => TestAssertions.StartsWith("cat", "dogfish", "starts with cat")),
            "Expected: starts with cat (starting with \"cat\"); Actual: \"dogfish\"."];
        yield return ["EndsWith", (Action)(() => TestAssertions.EndsWith("cat", "wildcat", "ends with cat")),
            (Action)(() => TestAssertions.EndsWith("cat", "wilddog", "ends with cat")),
            "Expected: ends with cat (ending with \"cat\"); Actual: \"wilddog\"."];
        yield return ["Excludes text", (Action)(() => TestAssertions.DoesNotContain("cat", "a dog", "excludes cat")),
            (Action)(() => TestAssertions.DoesNotContain("cat", "a cat", "excludes cat")),
            "Expected: excludes cat (not containing \"cat\"); Actual: \"a cat\"."];
        yield return ["Excludes value", (Action)(() => TestAssertions.DoesNotContain(1, new[] { 2, 3 }, "excludes one")),
            (Action)(() => TestAssertions.DoesNotContain(1, new[] { 1, 3 }, "excludes one")),
            "Expected: excludes one (excluding \"1\"); Actual: [1, 3]."];
        yield return ["Includes value", (Action)(() => TestAssertions.Contains(1, new[] { 1, 3 }, "includes one")),
            (Action)(() => TestAssertions.Contains(1, new[] { 2, 3 }, "includes one")),
            "Expected: includes one (including \"1\"); Actual: [2, 3]."];
        yield return ["Includes matching value", (Action)(() => TestAssertions.Contains(new[] { 1, 3 }, value => value == 1, "includes one")),
            (Action)(() => TestAssertions.Contains(new[] { 2, 3 }, value => value == 1, "includes one")),
            "Expected: includes one; Actual: [2, 3]."];
        yield return ["Single", (Action)(() => TestAssertions.Single(new[] { 1 }, "one result")),
            (Action)(() => TestAssertions.Single(new[] { 1, 2 }, "one result")),
            "Expected: exactly one value because one result; Actual: 2 values [1, 2]."];
        yield return ["Null", (Action)(() => TestAssertions.Null(null, "absent")),
            (Action)(() => TestAssertions.Null("present", "absent")), "Expected: null because absent; Actual: present."];
        yield return ["NotNull", (Action)(() => TestAssertions.NotNull("present", "present")),
            (Action)(() => TestAssertions.NotNull<string>(null, "present")), "Expected: a non-null value because present; Actual: <null>."];
        yield return ["Empty", (Action)(() => TestAssertions.Empty(Array.Empty<int>(), "no results")),
            (Action)(() => TestAssertions.Empty(new[] { 1, 2 }, "no results")),
            "Expected: no values because no results; Actual: 2 values [1, 2]."];
    }

    // All comparison families obey the same pass and complete mismatch-report contract.
    [Theory]
    [MemberData(nameof(ComparisonCases))]
    public void Comparison_WhenEvidenceMatchesOrDiffers_ThenAcceptsMatchesAndReportsTheCompleteMismatch(
        string family, Action passingComparison, Action failingComparison, string expectedDiagnostic)
    {
        // Arrange - the row supplies independent evidence and its expected report.
        Assert.NotEmpty(family);

        // Act
        var passingFailure = Record.Exception(passingComparison);
        var mismatchFailure = Record.Exception(failingComparison);

        // Assert
        Assert.Null(passingFailure);
        var assertionFailure = Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(mismatchFailure);
        Assert.Contains(expectedDiagnostic, assertionFailure.Message);
    }
}
