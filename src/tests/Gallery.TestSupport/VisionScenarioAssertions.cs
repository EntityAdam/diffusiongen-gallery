using Xunit;
using System.Collections.Immutable;
using System.Text.Json;

namespace Gallery.Tests;

internal enum VisionComparison { Equal, Contains, Excludes }
internal sealed record ExpectedVisionObligation
{
    public string Name { get; }
    public string ValueJson { get; }
    public string? Fragment { get; }
    public VisionComparison Comparison { get; }
    public ExpectedVisionObligation(string name, object? value, VisionComparison comparison = VisionComparison.Equal)
    {
        Name = name;
        ValueJson = JsonSerializer.Serialize(value);
        Fragment = value as string;
        Comparison = comparison;
    }
}
internal sealed record ExpectedVisionScenario(string BusinessRule, ImmutableArray<ExpectedVisionObligation> Obligations)
{
    public static ExpectedVisionScenario Create(string businessRule, params ExpectedVisionObligation[] obligations) =>
        new(businessRule, [.. obligations]);
}

internal static class AsserterVisionScenario
{
    public static void AssertAllObligationsAreMet(ExpectedVisionScenario expected, IReadOnlyDictionary<string, object?> actual)
    {
        var discrepancies = new List<string>();
        foreach (var obligation in expected.Obligations)
        {
            var present = actual.TryGetValue(obligation.Name, out var value);
            var matches = present && obligation.Comparison switch
            {
                VisionComparison.Equal => obligation.ValueJson == JsonSerializer.Serialize(value),
                VisionComparison.Contains => value is string text && obligation.Fragment is string fragment && text.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                VisionComparison.Excludes => value is string text && obligation.Fragment is string fragment && !text.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                _ => false
            };
            if (!matches)
                discrepancies.Add($"{obligation.Name}: Expected ({obligation.Comparison}): {obligation.ValueJson}; Actual: {(present ? JsonSerializer.Serialize(value) : "<missing evidence>")}");
        }
        Assert.True(discrepancies.Count == 0,
            "VISION SCENARIO ASSERTION FAILED\n" + string.Join("\n", discrepancies) + "\nBusiness Rule Violated: " + expected.BusinessRule);
    }
}

