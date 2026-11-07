using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TelegramBotForge.Configurations;
using TelegramBotForge.Routing;
using TelegramBotForge.Views;

namespace TelegramBotForge.Payments;

/// <summary>
/// Turns on Telegram Stars payments.
/// </summary>
public static class StarsBuilderExtensions
{
    /// <summary>
    /// Turns on Stars payments: products, invoices with <c>Invoice("code")</c>, <c>[PreCheckout]</c> and <c>[Paid]</c> actions,
    /// refunds, duplicate protection and the <c>/paysupport</c> answer Telegram requires.
    /// Can be called more than once; the products are added together.
    /// </summary>
    /// <remarks>
    /// At startup the bot checks that <c>Stars:PaySupportMessage</c> is set (unless you have your own <c>/paysupport</c> action),
    /// that every product has a <c>[Paid]</c> action, and that every product code in an attribute exists.
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddTelegramBot(builder.Configuration)
    ///     .AddStars(stars => stars
    ///         .Product("premium_month", "Premium", "30 days of Premium", price: 100)
    ///         .Product("boost", "Boost", "Your profile first for a day", price: 25));
    /// </code>
    /// </example>
    public static TelegramBotBuilder AddStars(this TelegramBotBuilder builder, Action<StarsCatalog>? products = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!builder.StarsEnabled)
        {
            builder.StarsEnabled = true;
            builder.Services.TryAddSingleton(builder.StarsCatalog);
            builder.Services.TryAddSingleton<IStarsProducts>(provider => provider.GetRequiredService<StarsCatalog>());
            builder.Services.TryAddSingleton<IProcessedPayments, InMemoryProcessedPayments>();
            builder.Services.TryAddSingleton<IStarsPayments, TelegramStarsPayments>();
            builder.Services.TryAddSingleton<StarsMiddleware>();
            builder.BuiltInControllers.Add(typeof(PaySupportController));
            builder.StartupChecks.Add((provider, registry) => CheckStars(builder, provider, registry));
        }

        products?.Invoke(builder.StarsCatalog);
        return builder;
    }

    /// <summary>
    /// Replaces where products come from, for example a database. Prices are read again in the check before payment,
    /// so a price change between the invoice and the payment declines the order.
    /// </summary>
    public static TelegramBotBuilder UseStarsProducts<TProducts>(this TelegramBotBuilder builder) where TProducts : class, IStarsProducts
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Replace(ServiceDescriptor.Singleton<IStarsProducts, TProducts>());
        return builder;
    }

    /// <summary>
    /// Replaces where processed payments are remembered, for example a database, so duplicates are also caught after a restart.
    /// </summary>
    public static TelegramBotBuilder UseProcessedPayments<TStore>(this TelegramBotBuilder builder) where TStore : class, IProcessedPayments
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Replace(ServiceDescriptor.Singleton<IProcessedPayments, TStore>());
        return builder;
    }

    private static void CheckStars(TelegramBotBuilder builder, IServiceProvider provider, ActionRegistry registry)
    {
        var errors = new List<string>();
        var options = provider.GetRequiredService<IOptions<TelegramBotConfiguration>>().Value.Stars;

        var builtInSupport = registry.Actions.Any(action => action.ControllerType == typeof(PaySupportController));
        if (builtInSupport && string.IsNullOrWhiteSpace(options.PaySupportMessage) && builder.FindOverrides(typeof(PaySupportView)).Count == 0)
        {
            errors.Add("Telegram requires bots that sell with Stars to answer /paysupport. Set " +
                       $"{TelegramBotConfiguration.SectionName}:Stars:PaySupportMessage, redefine PaySupportView, " +
                       "or write your own [Command(\"paysupport\")] action.");
        }

        var paid = registry.Actions.SelectMany(action => action.Routes.OfType<PaidAttribute>()).ToList();
        var paidForAll = paid.Any(route => route.ProductCode is null);
        if (paid.Count == 0)
            errors.Add("No action handles payments. Add a [Paid] action, or [Paid(\"code\")] for every product.");

        // Product codes can only be checked when the products are known at startup.
        if (provider.GetService<IStarsProducts>() is StarsCatalog catalog)
        {
            var codes = catalog.Products.Select(product => product.Code).ToHashSet(StringComparer.Ordinal);

            if (paid.Count > 0 && !paidForAll)
            {
                foreach (var code in codes.Where(code => paid.All(route => route.ProductCode != code)))
                    errors.Add($"Stars product \"{code}\" has no [Paid(\"{code}\")] action, so a user could pay and get nothing.");
            }

            var used = registry.Actions.SelectMany(action => action.Routes.Select(route => (action.DisplayName, Code: route switch
            {
                PaidAttribute p => p.ProductCode,
                PreCheckoutAttribute c => c.ProductCode,
                _ => null
            })));

            foreach (var (action, code) in used.Where(entry => entry.Code is not null && !codes.Contains(entry.Code)))
                errors.Add($"{action} handles Stars product \"{code}\", which is not one of the products given to AddStars.");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Stars payments are not set up correctly:" + Environment.NewLine +
                                                string.Join(Environment.NewLine, errors.Select(error => " - " + error)));
        }
    }
}
