using System.Text.Json;
using Xunit;

namespace Gallery.Tests;

internal sealed record ExpectedScenario(JsonElement Value, string BusinessRuleDescription)
{
    public static ExpectedScenario CreateFrom<T>(string businessRuleDescription, T observations) =>
        new(JsonSerializer.SerializeToElement(observations), businessRuleDescription);
}

internal static class AsserterScenario
{
    public static void AssertObservationsMatch<T>(ExpectedScenario expectedScenario, T actualObservations)
    {
        var actualJsonElement = JsonSerializer.SerializeToElement(actualObservations);
        var discrepancies = new List<string>();
        foreach (var expectedJsonProperty in expectedScenario.Value.EnumerateObject())
        {
            if (!actualJsonElement.TryGetProperty(expectedJsonProperty.Name, out var actualPropertyValueJsonElement))
                discrepancies.Add($"{expectedJsonProperty.Name}: Expected: {expectedJsonProperty.Value}; Actual: <missing>.");
            else if (!JsonElement.DeepEquals(expectedJsonProperty.Value, actualPropertyValueJsonElement))
                discrepancies.Add($"{expectedJsonProperty.Name}: Expected: {expectedJsonProperty.Value}; Actual: {actualPropertyValueJsonElement}.");
        }
        foreach (var actualJsonProperty in actualJsonElement.EnumerateObject())
            if (!expectedScenario.Value.TryGetProperty(actualJsonProperty.Name, out _))
                discrepancies.Add($"{actualJsonProperty.Name}: Expected: <absent>; Actual: {actualJsonProperty.Value}.");

        Assert.True(discrepancies.Count == 0,
            "SCENARIO ASSERTION FAILED\n" +
            $"Expected all scenario observations to match; Actual: {discrepancies.Count} discrepancies.\n" +
            string.Join("\n", discrepancies) +
            $"\nBusiness rule violated: {expectedScenario.BusinessRuleDescription}" +
            $"\nExpected observations: {expectedScenario.Value}\nActual observations: {actualJsonElement}");
    }
}
