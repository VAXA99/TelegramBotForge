using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.Payments;
using TelegramBotForge.Controllers;
using TelegramBotForge.Hosting;
using TelegramBotForge.Payments;
using TelegramBotForge.Results;
using TelegramBotForge.Testing;

namespace TelegramBotForge.Tests;

public sealed class StarsScopeProbe
{
    public ConcurrentBag<ScopedStarsProducts> Providers { get; } = [];
    public ConcurrentBag<(string Stage, IStarsProducts Provider, StarsScopedDependency Dependency, BotContext Context)> Observations { get; } = [];
    public ConcurrentBag<(ScopedStarsProducts Provider, BotContext Context, CancellationToken Token, string Culture)> Calls { get; } = [];
    public int? Price { get; set; } = 50;
    public string? Failure { get; set; }
    public CancellationTokenSource? Cancellation { get; set; }
    public bool Parallel { get; set; }
    public Exception Error { get; } = new InvalidOperationException("Product lookup failed");
    private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrivals;

    public async Task Meet(CancellationToken ct)
    {
        if (!Parallel)
            return;
        if (Interlocked.Increment(ref _arrivals) == 2)
            _both.TrySetResult();
        await _both.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }
}

public sealed class StarsScopedDependency : IDisposable
{
    public bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;
}

public sealed class ScopedStarsProducts : IStarsProducts, IAsyncDisposable
{
    private readonly BotContext _context;
    private readonly StarsScopeProbe _probe;
    public StarsScopedDependency Dependency { get; }
    public bool Disposed { get; private set; }

    public ScopedStarsProducts(StarsScopedDependency dependency, BotContext context, StarsScopeProbe probe)
    {
        Dependency = dependency;
        _context = context;
        _probe = probe;
        probe.Providers.Add(this);
    }

    public async ValueTask<StarsProduct?> FindAsync(string code, CancellationToken ct)
    {
        Assert.False(Disposed);
        Assert.False(Dependency.Disposed);
        Assert.Same(this, _context.Services.GetRequiredService<IStarsProducts>());
        Assert.Same(Dependency, _context.Services.GetRequiredService<StarsScopedDependency>());
        Assert.Equal(_context.CancellationToken, ct);
        await Task.Yield();
        _probe.Calls.Add((this, _context, ct, CultureInfo.CurrentUICulture.Name));
        if (_probe.Failure == "error")
            throw _probe.Error;
        if (_probe.Failure == "cancel")
        {
            _probe.Cancellation!.Cancel();
            throw new OperationCanceledException(ct);
        }
        return _probe.Price is { } price ? new StarsProduct(code, "Scoped product", CultureInfo.CurrentUICulture.Name, price) : null;
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        Disposed = true;
    }
}

public sealed class SingletonStarsProducts : IStarsProducts
{
    public ValueTask<StarsProduct?> FindAsync(string code, CancellationToken ct) =>
        ValueTask.FromResult<StarsProduct?>(new StarsProduct(code, "Singleton", "Singleton product", 50));
}

public sealed class StarsScopeController<TMarker>(IStarsProducts products, StarsScopedDependency dependency, StarsScopeProbe probe) : BotController
{
    [Command("scope_invoice")]
    public async Task<IBotResult> Open(CancellationToken ct)
    {
        Observe("invoice");
        await probe.Meet(ct);
        return Invoice("scope_product").OnDelivered((_, _) =>
        {
            Observe("delivered");
            return Task.CompletedTask;
        }).Finally(() => Observe("finally"));
    }

    [PreCheckout("scope_product")]
    public IBotResult Checkout(StarsOrder order)
    {
        Observe("checkout");
        Assert.Equal(50, order.Product.Price);
        return AcceptCheckout();
    }

    [Paid("scope_product")]
    public IBotResult Paid(StarsPayment payment)
    {
        Observe("paid");
        Assert.Equal("scope_product", payment.ProductCode);
        return Reply(payment.Product is null ? "Paid removed product" : "Scoped paid");
    }

    private void Observe(string stage)
    {
        Assert.Same(products, Context.Services.GetRequiredService<IStarsProducts>());
        Assert.Same(dependency, Context.Services.GetRequiredService<StarsScopedDependency>());
        Assert.False(dependency.Disposed);
        probe.Observations.Add((stage, products, dependency, Context));
    }
}

public sealed class ScopedStarsProductsTests
{
    private static BotTestHost Create(StarsScopeProbe probe, Action<TelegramBotBuilder>? configure = null) =>
        BotTestHost.Create<StarsMiddleware>(services =>
        {
            services.AddSingleton(probe);
            services.AddScoped<StarsScopedDependency>();
        }, builder =>
        {
            builder.AddStars().UseStarsProducts<ScopedStarsProducts>(ServiceLifetime.Scoped);
            builder.BuiltInControllers.Add(typeof(StarsScopeController<object>));
            builder.UseLocalization(options =>
            {
                options.DefaultCulture = "en-US";
                options.SupportedCultures = ["en-US", "ru"];
            });
            configure?.Invoke(builder);
        });

    [Fact]
    public async Task Startup_does_not_resolve_scoped_products_and_invoice_checkout_paid_share_their_operation_scope()
    {
        var probe = new StarsScopeProbe();
        await using var bot = Create(probe);
        Assert.Empty(probe.Providers);
        var user = bot.PrivateChat(languageCode: "ru");
        using var cancellation = new CancellationTokenSource();
        var invoice = await user.SendText("/scope_invoice", cancellation.Token);
        Assert.Equal("ru", Assert.Single(invoice.Of<SendInvoiceRequest>()).Description);
        var payment = await user.PayInvoice("scoped-charge", cancellation.Token);
        Assert.True(payment.Accepted);
        Assert.Equal("Scoped paid", payment.Payment!.Text);
        Assert.Equal(3, probe.Providers.Count);
        Assert.Equal(3, probe.Calls.Count);
        Assert.All(probe.Calls, call =>
        {
            Assert.Equal("ru", call.Culture);
            Assert.Equal(cancellation.Token, call.Token);
            var observations = probe.Observations.Where(observation => ReferenceEquals(observation.Context, call.Context)).ToArray();
            Assert.NotEmpty(observations);
            Assert.All(observations, observation =>
            {
                Assert.Same(call.Provider, observation.Provider);
                Assert.Same(call.Provider.Dependency, observation.Dependency);
            });
        });
        Assert.Equal(3, probe.Providers.Select(provider => provider.Dependency).Distinct().Count());
        Assert.All(probe.Providers, provider =>
        {
            Assert.True(provider.Disposed);
            Assert.True(provider.Dependency.Disposed);
        });

        var duplicate = await user.PayInvoice("scoped-charge");
        Assert.True(duplicate.Payment!.IsEmpty);
        Assert.Single(probe.Observations, observation => observation.Stage == "paid");
    }

    [Fact]
    public async Task Parallel_updates_isolate_providers_and_scoped_dependencies()
    {
        var probe = new StarsScopeProbe { Parallel = true };
        await using var bot = Create(probe);
        var dispatcher = bot.Services.GetRequiredService<UpdateDispatcher>();
        Update Incoming(long id) => new()
        {
            Message = new Message
            {
                Id = 1, Chat = new Chat { Id = id, Type = ChatType.Private },
                From = new User { Id = id, FirstName = "Buyer" }, Text = "/scope_invoice"
            }
        };
        await Task.WhenAll(dispatcher.DispatchAsync(Incoming(1), false, CancellationToken.None),
            dispatcher.DispatchAsync(Incoming(2), false, CancellationToken.None));
        Assert.Equal(2, probe.Providers.Count);
        Assert.Equal(2, probe.Calls.Count);
        Assert.Equal(2, probe.Calls.Select(call => call.Context).Distinct().Count());
        Assert.Equal(2, probe.Providers.Select(provider => provider.Dependency).Distinct().Count());
        Assert.Equal(2, bot.Telegram.RequestsOf<SendInvoiceRequest>().Count);
        Assert.All(probe.Providers, provider => Assert.True(provider.Disposed && provider.Dependency.Disposed));
    }

    [Theory]
    [InlineData("error", false)]
    [InlineData("cancel", false)]
    [InlineData("error", true)]
    [InlineData("cancel", true)]
    public async Task Provider_and_dependencies_are_disposed_on_lookup_error_and_cancellation(string failure, bool checkout)
    {
        using var cancellation = new CancellationTokenSource();
        var probe = new StarsScopeProbe { Cancellation = cancellation };
        await using var bot = Create(probe);
        var user = bot.PrivateChat();
        if (checkout)
            await user.SendText("/scope_invoice");
        probe.Failure = failure;
        if (checkout)
            await user.PayInvoice(ct: cancellation.Token);
        else
            await user.SendText("/scope_invoice", cancellation.Token);
        Assert.All(probe.Providers, provider => Assert.True(provider.Disposed && provider.Dependency.Disposed));
        Assert.Equal(checkout ? 2 : 1, probe.Calls.Count);
        Assert.Equal(failure == "cancel", cancellation.IsCancellationRequested);
        Assert.Single(probe.Observations, observation => observation.Stage == "finally");
        if (!checkout)
            Assert.Empty(bot.Telegram.RequestsOf<SendInvoiceRequest>());
    }

    [Theory]
    [InlineData(51)]
    [InlineData(null)]
    public async Task Changed_price_or_missing_product_declines_precheckout(int? price)
    {
        var probe = new StarsScopeProbe();
        await using var bot = Create(probe);
        var user = bot.PrivateChat();
        await user.SendText("/scope_invoice");
        probe.Price = price;
        var payment = await user.PayInvoice();
        Assert.False(payment.Accepted);
        Assert.False(payment.Checkout.CheckoutAnswer!.Ok);
        Assert.NotEmpty(payment.Checkout.CheckoutAnswer.ErrorMessage!);
        Assert.Null(payment.Payment);
        Assert.Equal(2, probe.Calls.Count);
        Assert.DoesNotContain(probe.Observations, observation => observation.Stage == "checkout");
    }

    [Fact]
    public async Task Missing_product_during_invoice_creation_does_not_send_and_still_finalizes_and_disposes()
    {
        var probe = new StarsScopeProbe { Price = null };
        await using var bot = Create(probe);
        var result = await bot.PrivateChat().SendText("/scope_invoice");
        Assert.True(result.IsEmpty);
        Assert.Single(probe.Calls);
        Assert.Single(probe.Observations, observation => observation.Stage == "finally");
        Assert.DoesNotContain(probe.Observations, observation => observation.Stage == "delivered");
        var provider = Assert.Single(probe.Providers);
        Assert.True(provider.Disposed && provider.Dependency.Disposed);
    }

    [Fact]
    public void Scoped_products_do_not_disable_startup_payment_route_checks()
    {
        var probe = new StarsScopeProbe();
        var error = Assert.Throws<InvalidOperationException>(() => BotTestHost.Create<StarsMiddleware>(
            services =>
            {
                services.AddSingleton(probe);
                services.AddScoped<StarsScopedDependency>();
            }, builder => builder.AddStars().UseStarsProducts<ScopedStarsProducts>(ServiceLifetime.Scoped)));
        Assert.Contains("No action handles payments", error.Message);
        Assert.Empty(probe.Providers);
    }

    [Fact]
    public async Task Successful_payment_still_reaches_action_when_product_was_removed_and_duplicates_are_skipped()
    {
        var probe = new StarsScopeProbe();
        await using var bot = Create(probe);
        var user = bot.PrivateChat();
        var invoice = Assert.Single((await user.SendText("/scope_invoice")).Of<SendInvoiceRequest>());
        probe.Price = null;
        Task<UpdateResult> Paid() => user.SendMessage(message => message.SuccessfulPayment = new SuccessfulPayment
        {
            Currency = "XTR", TotalAmount = 50, InvoicePayload = invoice.Payload,
            TelegramPaymentChargeId = "removed-charge", ProviderPaymentChargeId = ""
        });
        Assert.Equal("Paid removed product", (await Paid()).Text);
        Assert.True((await Paid()).IsEmpty);
        Assert.Single(probe.Observations, observation => observation.Stage == "paid");
    }

    [Fact]
    public async Task Default_registration_keeps_singleton_and_static_catalog_behavior()
    {
        await using var singleton = Create(new StarsScopeProbe(), builder => builder.UseStarsProducts<SingletonStarsProducts>());
        var expected = singleton.Services.GetRequiredService<IStarsProducts>();
        await using (var scope = singleton.Services.CreateAsyncScope())
            Assert.Same(expected, scope.ServiceProvider.GetRequiredService<IStarsProducts>());
        Assert.Single((await singleton.PrivateChat().SendText("/scope_invoice")).Of<SendInvoiceRequest>());

        await using var catalog = BotTestHost.Create<StarsMiddleware>(configureBot: builder =>
        {
            builder.AddStars(products => products.Product("scope_product", "Static", "Static catalog", 12));
            builder.BuiltInControllers.Add(typeof(StaticStarsController<object>));
        });
        Assert.IsType<StarsCatalog>(catalog.Services.GetRequiredService<IStarsProducts>());
        Assert.Equal(12, Assert.Single(Assert.Single((await catalog.PrivateChat().SendText("/static_invoice")).Of<SendInvoiceRequest>()).Prices).Amount);
        Assert.Throws<InvalidOperationException>(() => BotTestHost.Create<StarsMiddleware>(configureBot: builder =>
        {
            builder.AddStars(products => products.Product("unhandled", "Static", "Static catalog", 12));
            builder.BuiltInControllers.Add(typeof(StaticStarsController<object>));
        }));
    }

    [Fact]
    public async Task Transient_is_explicitly_supported_scoped_dependencies_are_owned_by_scope_and_invalid_lifetimes_throw()
    {
        var probe = new StarsScopeProbe();
        await using var bot = Create(probe, builder =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => builder.UseStarsProducts<ScopedStarsProducts>((ServiceLifetime)99));
            builder.UseStarsProducts<ScopedStarsProducts>(ServiceLifetime.Transient);
        });
        Assert.Empty(probe.Providers);
        await using (var scope = bot.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<BotContextScope>().Initialize(new BotContext(new Update(), bot.Telegram, scope.ServiceProvider, CancellationToken.None));
            var first = Assert.IsType<ScopedStarsProducts>(scope.ServiceProvider.GetRequiredService<IStarsProducts>());
            var second = Assert.IsType<ScopedStarsProducts>(scope.ServiceProvider.GetRequiredService<IStarsProducts>());
            Assert.NotSame(first, second);
            Assert.Same(first.Dependency, second.Dependency);
        }
        Assert.Equal(2, probe.Providers.Count);
        Assert.All(probe.Providers, provider => Assert.True(provider.Disposed && provider.Dependency.Disposed));
    }
}

public sealed class StaticStarsController<TMarker> : BotController
{
    [Command("static_invoice")]
    public IBotResult Open() => Invoice("scope_product");
    [Paid("scope_product")]
    public void Paid() { }
}
