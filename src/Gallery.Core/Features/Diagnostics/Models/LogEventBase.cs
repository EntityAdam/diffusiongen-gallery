using System.Collections.Immutable;

namespace Gallery.Core.Exceptions;

public abstract class LogEventBase(string value)
{
    public string Value { get; } = value;
}

/// <summary>Scalar diagnostic values; flatten or serialize nested data before adding it.</summary>
public sealed class ContextualData
{
    private ImmutableDictionary<string, object> valuesByName;

    public ContextualData() => valuesByName = ImmutableDictionary<string, object>.Empty;
    internal ContextualData(ContextualData source) => valuesByName = source.valuesByName;

    public ImmutableDictionary<string, object> ValuesByName => valuesByName;

    public ContextualData Add(string name, string value) => With(name, value ?? throw new ArgumentNullException(nameof(value)));
    public ContextualData Add(string name, int value) => With(name, value);
    public ContextualData Add(string name, long value) => With(name, value);
    public ContextualData Add(string name, float value) => With(name, value);
    public ContextualData Add(string name, double value) => With(name, value);
    public ContextualData Add(string name, decimal value) => With(name, value);
    public ContextualData Add(string name, bool value) => With(name, value);
    public ContextualData Add(string name, DateTime value) => With(name, value);

    private ContextualData With(string name, object value)
    {
        valuesByName = valuesByName.SetItem(name, value);
        return this;
    }
}
public sealed record ExceptionCause(string ExceptionType, string Message, string? StackTrace);

public enum Severity { Verbose = 0, Information = 1, Warning = 2, Error = 3, Critical = 4 }
