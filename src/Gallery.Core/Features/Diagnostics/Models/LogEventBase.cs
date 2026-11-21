using System.Collections.Immutable;

namespace Gallery.Core.Exceptions;

public abstract class LogEventBase(string value)
{
    public string Value { get; } = value;
}

public sealed record ContextualData(ImmutableDictionary<string, object> ValuesByName);
public sealed record ExceptionCause(string ExceptionType, string Message, string? StackTrace);

public enum Severity { Verbose = 0, Information = 1, Warning = 2, Error = 3, Critical = 4 }
