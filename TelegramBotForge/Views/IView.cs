using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace TelegramBotForge.Views;

/// <summary>
/// Marks a class as a view. Derive from <see cref="View"/> or <see cref="View{TModel}"/> instead of implementing this directly.
/// Views are found at startup and registered in the DI container, so controllers get them through their constructor.
/// </summary>
public interface IBotView
{
}

/// <summary>
/// A view that needs no data. Usually written as a <see cref="View"/>; implement this interface directly only
/// when you need to build a <see cref="BotMessage"/> from raw Telegram types yourself.
/// </summary>
public interface IView : IBotView
{
    /// <summary>
    /// Builds the message.
    /// </summary>
    BotMessage Render();
}

/// <summary>
/// A view that turns a model into a message. Usually written as a <see cref="View{TModel}"/>; implement this interface directly
/// only when you need to build a <see cref="BotMessage"/> from raw Telegram types yourself.
/// </summary>
/// <typeparam name="TModel">The data the view shows.</typeparam>
public interface IView<in TModel> : IBotView
{
    /// <summary>
    /// Builds the message for the model.
    /// </summary>
    BotMessage Render(TModel model);
}

/// <summary>
/// Optional settings of a view. Without it a view is scoped and sends normal messages.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ViewAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the lifetime in the DI container. The default is <see cref="ServiceLifetime.Scoped"/>, one instance per update,
    /// which is always safe. Use <see cref="ServiceLifetime.Singleton"/> for views without per-update dependencies.
    /// </summary>
    public ServiceLifetime Lifetime { get; set; } = ServiceLifetime.Scoped;

    /// <summary>
    /// Gets or sets a value indicating whether the view sends a Telegram rich message:
    /// its buttons are placed inside the message, and they can be disabled. Rich messages cannot have media
    /// or buttons under the input field.
    /// </summary>
    public bool Rich { get; set; }
}

/// <summary>
/// Finds views in an assembly and registers them.
/// </summary>
internal static class ViewRegistration
{
    public static void AddViews(IServiceCollection services, Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            var attribute = type.GetCustomAttribute<ViewAttribute>();
            var isView = typeof(IBotView).IsAssignableFrom(type);

            if (attribute is not null && !isView)
                throw new InvalidOperationException($"{type.FullName} has [View] but is not a view. Derive it from View or View<TModel>.");

            if (!isView || !type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition)
                continue;

            services.Add(new ServiceDescriptor(type, type, attribute?.Lifetime ?? ServiceLifetime.Scoped));
        }
    }
}
