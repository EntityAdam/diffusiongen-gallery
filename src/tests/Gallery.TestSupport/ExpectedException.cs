using System.Collections.Immutable;
using System.Net;
using Gallery.Core;

namespace Gallery.Tests;

internal sealed record ExpectedException
{
    public string Behavior { get; }
    public ImmutableArray<string> MessageParts { get; }
    public ImmutableArray<string> ForbiddenMessageParts { get; init; } = [];
    public HttpStatusCode? HttpStatusCode { get; init; }
    public Severity? Severity { get; init; }
    public string? Reason { get; init; }
    public string? LogEventValue { get; init; }
    public ImmutableDictionary<string, object> ContextualDataByName { get; init; } = ImmutableDictionary<string, object>.Empty;
    public Exception? PropagatedInstance { get; init; }

    public ExpectedException(string behavior, params string[] messageParts)
    {
        Behavior = behavior;
        MessageParts = [.. messageParts];
    }
}
