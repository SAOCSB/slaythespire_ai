using System.Collections.Concurrent;
using System.Reflection;

namespace SpirePilot.Infra;

/// <summary>
/// Cached, failure-tolerant access to non-public members (game internals change between patches).
/// Every accessor returns default/false instead of throwing when the member is missing.
/// </summary>
internal static class Reflect
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly ConcurrentDictionary<(Type, string), MemberInfo?> Members = new();
    private static readonly ConcurrentDictionary<string, bool> Warned = new();

    private static MemberInfo? Find(Type type, string name) =>
        Members.GetOrAdd((type, name), key =>
        {
            for (var t = key.Item1; t != null; t = t.BaseType)
            {
                var f = t.GetField(key.Item2, All | BindingFlags.DeclaredOnly);
                if (f != null) return f;
                var p = t.GetProperty(key.Item2, All | BindingFlags.DeclaredOnly);
                if (p != null) return p;
            }
            return null;
        });

    /// <summary>Reads a field or property (public or not) by name.</summary>
    public static T? Get<T>(object? target, string name)
    {
        if (target == null) return default;
        var member = Find(target.GetType(), name);
        try
        {
            object? value = member switch
            {
                FieldInfo f => f.GetValue(f.IsStatic ? null : target),
                PropertyInfo p when p.GetMethod != null => p.GetValue(p.GetMethod.IsStatic ? null : target),
                _ => null,
            };
            if (member == null) WarnOnce(target.GetType(), name);
            return value is T t ? t : default;
        }
        catch (Exception)
        {
            return default;
        }
    }

    /// <summary>
    /// Like <see cref="Get{T}"/> for optional members that only some subclasses (or game versions) declare: a missing
    /// member is not reported as missing. Returns false when the member does not exist or could not be read.
    /// </summary>
    public static bool TryGetQuiet<T>(object? target, string name, out T? value)
    {
        value = default;
        if (target == null) return false;
        var member = Find(target.GetType(), name);
        try
        {
            object? raw = member switch
            {
                FieldInfo f => f.GetValue(f.IsStatic ? null : target),
                PropertyInfo p when p.GetMethod != null => p.GetValue(p.GetMethod.IsStatic ? null : target),
                _ => null,
            };
            if (member == null) return false;
            if (raw is T t) value = t;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static T? GetStatic<T>(Type type, string name)
    {
        var member = Find(type, name);
        try
        {
            object? value = member switch
            {
                FieldInfo f => f.GetValue(null),
                PropertyInfo p => p.GetValue(null),
                _ => null,
            };
            if (member == null) WarnOnce(type, name);
            return value is T t ? t : default;
        }
        catch (Exception)
        {
            return default;
        }
    }

    public static bool Set(object? target, string name, object? value)
    {
        if (target == null) return false;
        var member = Find(target.GetType(), name);
        try
        {
            switch (member)
            {
                case FieldInfo f: f.SetValue(f.IsStatic ? null : target, value); return true;
                case PropertyInfo p when p.SetMethod != null: p.SetValue(target, value); return true;
                default: WarnOnce(target.GetType(), name); return false;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Invokes a (possibly non-public) method by name and argument count.</summary>
    public static bool TryCall(object? target, string name, out object? result, params object?[] args)
    {
        result = null;
        if (target == null) return false;
        var method = FindMethod(target.GetType(), name, args.Length);
        if (method == null)
        {
            WarnOnce(target.GetType(), name + "()");
            return false;
        }
        try
        {
            result = method.Invoke(method.IsStatic ? null : target, args);
            return true;
        }
        catch (TargetInvocationException ex)
        {
            ModLog.Warn($"{target.GetType().Name}.{name} threw: {ex.InnerException?.Message}");
            return false;
        }
        catch (Exception ex)
        {
            ModLog.Warn($"{target.GetType().Name}.{name} failed: {ex.Message}");
            return false;
        }
    }

    public static MethodInfo? FindMethod(Type type, string name, int argCount)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var m = t.GetMethods(All | BindingFlags.DeclaredOnly).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == argCount);
            if (m != null) return m;
        }
        return null;
    }

    private static void WarnOnce(Type type, string name)
    {
        var key = type.FullName + "." + name;
        if (Warned.TryAdd(key, true)) ModLog.Warn("reflection: member not found " + key + " (game version changed?)");
    }

    /// <summary>Non-public members that were looked up but do not exist in the running game (for diagnostics).</summary>
    public static IReadOnlyCollection<string> MissingMembers => Warned.Keys.ToList();
}
