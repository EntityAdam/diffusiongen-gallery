using System.Reflection;

namespace Gallery.Tests;

// Explicit paths keep integration tests focused on the current state owner.
internal static class ComponentTestAccess
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static (object Owner, string Name) Resolve(object target, string path)
    {
        var parts = path.Split('.');
        foreach (var part in parts[..^1]) target = Get<object>(target, part);
        return (target, parts[^1]);
    }
    public static T Get<T>(object target, string path)
    {
        var (owner, name) = Resolve(target, path);
        var type = owner.GetType();
        if (type.GetProperty(name, Members) is { } property) return (T)property.GetValue(owner)!;
        var field = type.GetField(name, Members) ?? throw new MissingMemberException(type.Name, name);
        return (T)field.GetValue(owner)!;
    }
    public static void Set(object target, string path, object? value)
    {
        var (owner, name) = Resolve(target, path);
        if (owner.GetType().GetProperty(name, Members) is { } property) property.SetValue(owner, value);
        else owner.GetType().GetField(name, Members)!.SetValue(owner, value);
    }
    public static object? Call(object target, string path, params object?[] args)
    {
        var (owner, name) = Resolve(target, path);
        return owner.GetType().GetMethod(name, Members)!.Invoke(owner, args);
    }
}
