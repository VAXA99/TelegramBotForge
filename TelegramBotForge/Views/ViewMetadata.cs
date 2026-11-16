using System.Collections.Concurrent;
using System.Reflection;

namespace TelegramBotForge.Views;

/// <summary>Selects rich mode for a screen, overriding the view default and inherited method annotations.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = true)]
public sealed class RichAttribute : Attribute;

/// <summary>Selects standard mode for a screen, overriding the view default and inherited method annotations.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = true)]
public sealed class StandardAttribute : Attribute;

internal static class ViewMetadata
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, bool>> Screens = new();

    internal static void Validate(Type type) => _ = Screens.GetOrAdd(type, Read);

    internal static bool For(Type type, string screen)
    {
        var screens = Screens.GetOrAdd(type, Read);
        return screens.TryGetValue(screen, out var rich) ? rich : throw new InvalidOperationException(
            $"View {type.FullName}.{screen}: Message() must be called directly by a public, uniquely named screen returning TelegramBotForge.Views.Message. Helpers append to an existing message.");
    }

    private static IReadOnlyDictionary<string, bool> Read(Type type)
    {
        var result = new Dictionary<string, bool>();
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.ReturnType == typeof(Message));
        foreach (var group in methods.GroupBy(method => method.Name))
        {
            if (group.Count() != 1 || type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Count(method => method.Name == group.Key) != 1)
                throw new InvalidOperationException($"View {type.FullName}.{group.Key}: ambiguous screen name. Screen methods cannot be overloaded or hidden; use unique names.");
            var method = group.Single();
            for (var declaring = type; declaring is not null; declaring = declaring.BaseType)
            {
                var hidden = declaring.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Any(candidate => candidate.Name == method.Name && candidate.GetBaseDefinition() != method.GetBaseDefinition());
                if (hidden)
                    throw new InvalidOperationException($"View {type.FullName}.{method.Name}: ambiguous hidden screen name. Use unique screen names.");
            }
            bool? mode = null;
            for (var declaring = method.DeclaringType; declaring is not null; declaring = declaring.BaseType)
            {
                var candidate = declaring.GetMethod(method.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                    null, method.GetParameters().Select(parameter => parameter.ParameterType).ToArray(), null);
                if (candidate is null || candidate.GetBaseDefinition() != method.GetBaseDefinition())
                    continue;
                var rich = candidate.IsDefined(typeof(RichAttribute), false);
                var standard = candidate.IsDefined(typeof(StandardAttribute), false);
                if (rich && standard)
                    throw new InvalidOperationException($"View {type.FullName}.{method.Name}: conflicting [Rich] and [Standard] annotations.");
                if (mode is null && (rich || standard))
                    mode = rich;
            }
            result.Add(method.Name, mode ?? type.GetCustomAttribute<ViewAttribute>(true)?.Rich ?? false);
        }
        return result;
    }
}
