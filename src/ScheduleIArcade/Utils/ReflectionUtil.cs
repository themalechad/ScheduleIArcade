using System.Reflection;

namespace ScheduleIArcade.Utils;

internal static class ReflectionUtil
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static object? GetMemberValue(object? target, string name)
    {
        if (target == null)
            return null;

        try
        {
            var type = target.GetType();
            var property = type.GetProperty(name, Flags);
            if (property != null)
                return property.GetValue(target);

            var field = type.GetField(name, Flags);
            return field?.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    public static bool GetBool(object? target, string name, bool fallback = false)
    {
        var value = GetMemberValue(target, name);
        return value is bool b ? b : fallback;
    }

    public static string GetString(object? target, string name)
        => GetMemberValue(target, name)?.ToString() ?? string.Empty;


    public static bool TrySetMemberValue(object? target, string name, object? value)
    {
        if (target == null)
            return false;

        try
        {
            var type = target.GetType();
            var property = type.GetProperty(name, Flags);
            if (property != null && property.CanWrite)
            {
                property.SetValue(target, value);
                return true;
            }

            var field = type.GetField(name, Flags);
            if (field == null)
                return false;

            field.SetValue(target, value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryInvokeWithDefaultEnumValue(object? target, string methodName, int enumValue)
    {
        if (target == null)
            return false;

        try
        {
            var methods = target.GetType().GetMethods(Flags);
            foreach (var method in methods)
            {
                if (method.Name != methodName)
                    continue;

                var parameters = method.GetParameters();
                if (parameters.Length != 1 || !parameters[0].ParameterType.IsEnum)
                    continue;

                object arg = Enum.ToObject(parameters[0].ParameterType, enumValue);
                method.Invoke(target, new[] { arg });
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    public static bool TryInvokeWithDefaultBoolValue(object? target, string methodName, bool value)
    {
        if (target == null)
            return false;

        try
        {
            var methods = target.GetType().GetMethods(Flags);
            foreach (var method in methods)
            {
                if (method.Name != methodName)
                    continue;

                var parameters = method.GetParameters();
                if (parameters.Length != 1 || parameters[0].ParameterType != typeof(bool))
                    continue;

                method.Invoke(target, new object[] { value });
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    public static bool TryInvoke(object? target, string methodName)
    {
        if (target == null)
            return false;

        try
        {
            var method = target.GetType().GetMethod(methodName, Flags, null, Type.EmptyTypes, null);
            if (method == null)
                return false;

            method.Invoke(target, null);
            return true;
        }
        catch
        {
            return false;
        }
    }
    public static bool TryInvoke(object? target, string methodName, object? argument)
    {
        if (target == null)
            return false;

        try
        {
            var methods = target.GetType().GetMethods(Flags);
            foreach (var method in methods)
            {
                if (method.Name != methodName)
                    continue;

                var parameters = method.GetParameters();
                if (parameters.Length != 1)
                    continue;

                method.Invoke(target, new[] { argument });
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

}
