using System.Text.Json;
using Gallery.Core.Models;
using Xunit;

namespace Gallery.Tests;

internal sealed record ExpectedImageRecord(JsonElement Value)
{
    public static ExpectedImageRecord CreateFrom(ImageRecord record) => new(JsonSerializer.SerializeToElement(record));
}

internal static class AsserterImageRecord
{
    public static void AssertValuesMatch(ExpectedImageRecord expected, ImageRecord actual)
    {
        var actualValue = JsonSerializer.SerializeToElement(actual);
        var discrepancies = new List<string>();
        foreach (var property in expected.Value.EnumerateObject())
        {
            if (!actualValue.TryGetProperty(property.Name, out var value))
                discrepancies.Add($"{property.Name}: Expected: {property.Value}; Actual: <missing>.");
            else if (!JsonElement.DeepEquals(property.Value, value))
                discrepancies.Add($"{property.Name}: Expected: {property.Value}; Actual: {value}.");
        }
        foreach (var property in actualValue.EnumerateObject())
            if (!expected.Value.TryGetProperty(property.Name, out _))
                discrepancies.Add($"{property.Name}: Expected: <absent>; Actual: {property.Value}.");
        Assert.True(discrepancies.Count == 0,
            "IMAGE RECORD ASSERTION FAILED\n" + string.Join("\n", discrepancies));
    }
}
