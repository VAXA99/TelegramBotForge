using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace TelegramBotForge.Views;

/// <summary>
/// Optional view discovery marker, implemented by the View bases. Annotated classes can also be discovered without this interface.
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
/// Marks an ordinary class for discovery and configures a view's defaults. View subclasses are discovered without it;
/// their default lifetime is scoped and their default mode is standard.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class ViewAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the lifetime in the DI container. The default is <see cref="ServiceLifetime.Scoped"/>, one instance per update,
    /// which is always safe. Use <see cref="ServiceLifetime.Singleton"/> for views without per-update dependencies.
    /// </summary>
    public ServiceLifetime Lifetime { get; set; } = ServiceLifetime.Scoped;

    /// <summary>
    /// Gets or sets a value indicating whether the view sends a Telegram rich message:
    /// its buttons and structured photo/video media are embedded in the content. Rich messages cannot have
    /// buttons under the input field. Method annotations override this default.
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

            if ((!isView && attribute is null) || !type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition)
                continue;

            ViewMetadata.Validate(type);
            services.Add(new ServiceDescriptor(type, type, attribute?.Lifetime ?? ServiceLifetime.Scoped));
        }
    }
}
