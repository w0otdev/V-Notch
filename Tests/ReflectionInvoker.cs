using System;
using System.Reflection;

namespace VNotch.Tests;

internal static class ReflectionInvoker
{
    // MethodBase.Invoke does not apply C# default values, so a production method that later gains an
    // optional parameter would break every reflection call that relied on the shorter signature.
    // Trailing parameters the caller omitted are filled with their declared defaults.
    public static object? InvokeWithDefaults(object target, MethodInfo method, object?[] args)
    {
        var parameters = method.GetParameters();
        if (args.Length >= parameters.Length)
            return method.Invoke(target, args);

        var full = new object?[parameters.Length];
        Array.Copy(args, full, args.Length);
        for (int i = args.Length; i < parameters.Length; i++)
        {
            if (!parameters[i].HasDefaultValue)
                throw new ArgumentException($"{method.Name} requires a value for parameter '{parameters[i].Name}'.");
            full[i] = parameters[i].DefaultValue;
        }

        return method.Invoke(target, full);
    }
}
