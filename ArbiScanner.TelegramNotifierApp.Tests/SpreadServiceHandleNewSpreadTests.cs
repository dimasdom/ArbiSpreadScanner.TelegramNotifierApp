using ArbiScanner.TelegramNotifierApp.Abstractions.Errors;
using ArbiScanner.TelegramNotifierApp.Abstractions.Interfaces.Services;
using ArbiScanner.TelegramNotifierApp.Application.Services;
using ArbiScanner.TelegramNotifierApp.Infrastructure.DbContext;
using ArbiScannerWeb.Domain.Models;
using FluentResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.Threading;
using Xunit;

namespace ArbiScanner.TelegramNotifierApp.Tests;

public class SpreadServiceHandleNewSpreadTests
{
    private readonly ITelegramNotifierUserService _notifier = Substitute.For<ITelegramNotifierUserService>();
    private readonly TestLogger<SpreadService> _logger = new();

    public SpreadServiceHandleNewSpreadTests()
    {
        _notifier.NotifyUser(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok());
    }

    // CreateDbContext() is called once per HandleNewSpread invocation; every call returns
    // a fresh AppDbContext bound to the same named in-memory database so seeded data is visible.
    // Options are returned too so tests can open their own fresh context afterwards to verify
    // what HandleNewSpread persisted (e.g. a user deactivated after a permanent failure).
    private (SpreadService Sut, AppDbContext SeedContext, DbContextOptions<AppDbContext> Options) CreateSut()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var factory = Substitute.For<IDbContextFactory<AppDbContext>>();
        factory.CreateDbContext().Returns(_ => new AppDbContext(options));

        var sut = new SpreadService(factory, _notifier, _logger);
        return (sut, new AppDbContext(options), options);
    }

    private static UserSettingsModel MakeUser(long chatId, string accountId, bool active = true) => new()
    {
        AccountId = accountId,
        ChatId = chatId,
        Active = active,
        SpotSpread = true,
        FuturesSpread = true,
        FundingSpread = true,
        SpreadSize = 1.0,
        PositionSize = 10,
        Exchanges =
        [
            new UserExchangeModel { Exchange = new ExchangeModel { Name = "Binance" } },
            new UserExchangeModel { Exchange = new ExchangeModel { Name = "OKX" } },
        ],
    };

    private static TradeOpportunityModel MakeModel(SpreadType type) => new()
    {
        Guid = Guid.NewGuid(),
        Type = type,
        Symbol = "BTC/USDT",
        Spread = 2.5,
        StartSpread = 2.0,
        TotalFunding = 1.2,
        PossibleProfit = 1.5,
        ExchangeRateA = new ExchangeRateModel { Symbol = "BTC/USDT", Exchange = "Binance", VolumeAsk = 1000, VolumeBid = 1000 },
        ExchangeRateB = new ExchangeRateModel { Symbol = "BTC/USDT", Exchange = "OKX", VolumeAsk = 1000, VolumeBid = 1000 },
        ExchangeLong = new ExchangeRateModel { Symbol = "BTC/USDT", Exchange = "Binance", ExchangeRate = 50000, SlippageShort = 0.05 },
        ExchangeShort = new ExchangeRateModel { Symbol = "BTC/USDT", Exchange = "OKX", ExchangeRate = 51000, SlippageLong = 0.05 },
    };

    [Theory]
    [InlineData(SpreadType.Spot, "Spot Spread:")]
    [InlineData(SpreadType.Futures, "Long:")]
    [InlineData(SpreadType.Funding, "Funding Spread:")]
    public async Task HandleNewSpread_MatchingActiveUser_NotifiesWithConstructedMessage(SpreadType type, string expectedFragment)
    {
        var (sut, seed, _) = CreateSut();
        using (seed)
        {
            seed.UserSettings.Add(MakeUser(chatId: 111, accountId: "acc-1"));
            await seed.SaveChangesAsync();
        }

        await sut.HandleNewSpread(MakeModel(type));

        await _notifier.Received(1).NotifyUser(111, Arg.Is<string>(m => m.Contains(expectedFragment)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleNewSpread_UserInactive_DoesNotNotify()
    {
        var (sut, seed, _) = CreateSut();
        using (seed)
        {
            seed.UserSettings.Add(MakeUser(chatId: 222, accountId: "acc-2", active: false));
            await seed.SaveChangesAsync();
        }

        await sut.HandleNewSpread(MakeModel(SpreadType.Spot));

        await _notifier.DidNotReceive().NotifyUser(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleNewSpread_SpreadSizeThresholdNotMet_DoesNotNotify()
    {
        var (sut, seed, _) = CreateSut();
        var user = MakeUser(chatId: 333, accountId: "acc-3");
        user.SpreadSize = 10.0; // stricter than the model's 2.0 StartSpread
        using (seed)
        {
            seed.UserSettings.Add(user);
            await seed.SaveChangesAsync();
        }

        await sut.HandleNewSpread(MakeModel(SpreadType.Spot));

        await _notifier.DidNotReceive().NotifyUser(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleNewSpread_VolumeBelowRequiredPosition_DoesNotNotify()
    {
        var (sut, seed, _) = CreateSut();
        var user = MakeUser(chatId: 444, accountId: "acc-4");
        user.PositionSize = 1_000_000; // far larger than the model's volumes
        using (seed)
        {
            seed.UserSettings.Add(user);
            await seed.SaveChangesAsync();
        }

        await sut.HandleNewSpread(MakeModel(SpreadType.Spot));

        await _notifier.DidNotReceive().NotifyUser(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleNewSpread_UnknownSpreadType_ReturnsWithoutNotifying()
    {
        var (sut, seed, _) = CreateSut();
        using (seed)
        {
            seed.UserSettings.Add(MakeUser(chatId: 555, accountId: "acc-5"));
            await seed.SaveChangesAsync();
        }

        await sut.HandleNewSpread(MakeModel((SpreadType)999));

        await _notifier.DidNotReceive().NotifyUser(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // DB/query setup failures must propagate (not be swallowed) so the RabbitMQ-level retry
    // pipeline and dead-letter queue - which only engage on an exception from the message
    // handler - actually get a chance to redeliver or DLQ the message. A per-recipient send
    // failure, by contrast, must never surface as an exception here (see the permanent/transient
    // failure tests below): only genuine batch-setup failures should.
    [Fact]
    public async Task HandleNewSpread_DbContextFactoryThrows_PropagatesException()
    {
        var factory = Substitute.For<IDbContextFactory<AppDbContext>>();
        factory.CreateDbContext().Throws(new InvalidOperationException("db unavailable"));
        var sut = new SpreadService(factory, _notifier, _logger);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.HandleNewSpread(MakeModel(SpreadType.Spot)));
    }

    [Theory]
    [InlineData(SpreadType.Futures)]
    [InlineData(SpreadType.Funding)]
    public async Task HandleNewSpread_FundingRatesPresent_IncludesFundingLineInMessage(SpreadType type)
    {
        var (sut, seed, _) = CreateSut();
        using (seed)
        {
            seed.UserSettings.Add(MakeUser(chatId: 666, accountId: "acc-6"));
            await seed.SaveChangesAsync();
        }
        var model = MakeModel(type);
        model.ExchangeLong.FundingRateValue = 0.01;
        model.ExchangeShort.FundingRateValue = -0.02;

        await sut.HandleNewSpread(model);

        await _notifier.Received(1).NotifyUser(666, Arg.Is<string>(m => m.Contains("Funding")), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(SpreadType.Futures)]
    [InlineData(SpreadType.Funding)]
    public async Task HandleNewSpread_MessageConstructionThrows_LogsErrorsAndNotifiesWithEmptyMessage(SpreadType type)
    {
        var (sut, seed, _) = CreateSut();
        using (seed)
        {
            seed.UserSettings.Add(MakeUser(chatId: 777, accountId: "acc-7"));
            await seed.SaveChangesAsync();
        }
        var model = MakeModel(type);
        model.ExchangeLong = null!;

        await sut.HandleNewSpread(model);

        Assert.Contains(_logger.Entries, e => e.Message.Contains("Error handling funding rates"));
        Assert.Contains(_logger.Entries, e => e.Message.Contains("Error constructing message for possible position"));
        await _notifier.Received(1).NotifyUser(777, string.Empty, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleNewSpread_PermanentDeliveryFailure_DeactivatesUser()
    {
        var (sut, seed, options) = CreateSut();
        using (seed)
        {
            seed.UserSettings.Add(MakeUser(chatId: 888, accountId: "acc-8"));
            await seed.SaveChangesAsync();
        }
        _notifier.NotifyUser(888, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail(new PermanentDeliveryError("Forbidden: bot was blocked by the user")));

        await sut.HandleNewSpread(MakeModel(SpreadType.Spot));

        using var verifyContext = new AppDbContext(options);
        var user = await verifyContext.UserSettings.SingleAsync(u => u.ChatId == 888);
        Assert.False(user.Active);
        Assert.Contains(_logger.Entries, e => e.Message.Contains("Deactivated") && e.Message.Contains("permanent Telegram delivery failures"));
    }

    [Fact]
    public async Task HandleNewSpread_UserAlreadyDeactivatedConcurrently_DeactivationIsNoOp()
    {
        // Simulates two permanent failures for the same user racing: by the time this run's
        // cleanup pass queries for still-active permanently-failed users, another in-flight
        // HandleNewSpread call has already flipped Active to false. The "&& u.Active" guard in
        // DeactivateUnreachableUsersAsync should make the second pass a no-op, not a duplicate log.
        var (sut, seed, options) = CreateSut();
        using (seed)
        {
            seed.UserSettings.Add(MakeUser(chatId: 1111, accountId: "acc-11"));
            await seed.SaveChangesAsync();
        }
        _notifier.NotifyUser(1111, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                using var racingContext = new AppDbContext(options);
                var user = await racingContext.UserSettings.SingleAsync(u => u.ChatId == 1111);
                user.Active = false;
                await racingContext.SaveChangesAsync();
                return Result.Fail(new PermanentDeliveryError("Forbidden: bot was blocked by the user"));
            });

        await sut.HandleNewSpread(MakeModel(SpreadType.Spot));

        Assert.DoesNotContain(_logger.Entries, e => e.Message.Contains("Deactivated") && e.Message.Contains("permanent Telegram delivery failures"));
    }

    [Fact]
    public async Task HandleNewSpread_TransientDeliveryFailure_LogsWarningAndKeepsUserActive()
    {
        var (sut, seed, _) = CreateSut();
        using (seed)
        {
            seed.UserSettings.Add(MakeUser(chatId: 999, accountId: "acc-9"));
            await seed.SaveChangesAsync();
        }
        _notifier.NotifyUser(999, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail(new TransientDeliveryError("network error")));

        await sut.HandleNewSpread(MakeModel(SpreadType.Spot));

        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Failed to deliver"));
    }

    [Fact]
    public async Task HandleNewSpread_LargeUserBatch_BoundsConcurrentNotifications()
    {
        var (sut, seed, _) = CreateSut();
        const int userCount = 200;
        using (seed)
        {
            for (var i = 0; i < userCount; i++)
            {
                seed.UserSettings.Add(MakeUser(chatId: 10_000 + i, accountId: $"acc-conc-{i}"));
            }
            await seed.SaveChangesAsync();
        }

        var current = 0;
        var maxObserved = 0;
        _notifier.NotifyUser(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                var now = Interlocked.Increment(ref current);
                InterlockedMax(ref maxObserved, now);
                await Task.Delay(20);
                Interlocked.Decrement(ref current);
                return Result.Ok();
            });

        await sut.HandleNewSpread(MakeModel(SpreadType.Spot));

        Assert.True(maxObserved > 1, "expected notification sends to run concurrently, not one at a time");
        Assert.True(maxObserved <= 20, $"expected concurrency to stay capped at 20, observed {maxObserved}");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int initial, computed;
        do
        {
            initial = target;
            computed = Math.Max(initial, value);
        } while (Interlocked.CompareExchange(ref target, computed, initial) != initial);
    }

    [Fact]
    public async Task HandleCloseSpread_CompletesSuccessfully()
    {
        var factory = Substitute.For<IDbContextFactory<AppDbContext>>();
        var sut = new SpreadService(factory, _notifier, _logger);

        var task = sut.HandleCloseSpread(MakeModel(SpreadType.Spot));
        await task;

        Assert.True(task.IsCompletedSuccessfully);
    }
}
