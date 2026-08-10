using ArbiScanner.TelegramNotifierApp.Abstractions.Interfaces.Services;
using ArbiScanner.TelegramNotifierApp.Application.Services;
using ArbiScanner.TelegramNotifierApp.Infrastructure.DbContext;
using ArbiScannerWeb.Domain.Models;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace ArbiScanner.TelegramNotifierApp.Tests;

public class SpreadServiceHandleNewSpreadTests
{
    private readonly ITelegramNotifierUserService _notifier = Substitute.For<ITelegramNotifierUserService>();
    private readonly TestLogger<SpreadService> _logger = new();

    // CreateDbContext() is called once per HandleNewSpread invocation; every call returns
    // a fresh AppDbContext bound to the same named in-memory database so seeded data is visible.
    private (SpreadService Sut, AppDbContext SeedContext) CreateSut()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var factory = Substitute.For<IDbContextFactory<AppDbContext>>();
        factory.CreateDbContext().Returns(_ => new AppDbContext(options));

        var sut = new SpreadService(factory, _notifier, _logger);
        return (sut, new AppDbContext(options));
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
        var (sut, seed) = CreateSut();
        using (seed)
        {
            seed.UserSettings.Add(MakeUser(chatId: 111, accountId: "acc-1"));
            await seed.SaveChangesAsync();
        }

        await sut.HandleNewSpread(MakeModel(type));

        await _notifier.Received(1).NotifyUser(111, Arg.Is<string>(m => m.Contains(expectedFragment)));
    }

    [Fact]
    public async Task HandleNewSpread_UserInactive_DoesNotNotify()
    {
        var (sut, seed) = CreateSut();
        using (seed)
        {
            seed.UserSettings.Add(MakeUser(chatId: 222, accountId: "acc-2", active: false));
            await seed.SaveChangesAsync();
        }

        await sut.HandleNewSpread(MakeModel(SpreadType.Spot));

        await _notifier.DidNotReceive().NotifyUser(Arg.Any<long>(), Arg.Any<string>());
    }

    [Fact]
    public async Task HandleNewSpread_SpreadSizeThresholdNotMet_DoesNotNotify()
    {
        var (sut, seed) = CreateSut();
        var user = MakeUser(chatId: 333, accountId: "acc-3");
        user.SpreadSize = 10.0; // stricter than the model's 2.0 StartSpread
        using (seed)
        {
            seed.UserSettings.Add(user);
            await seed.SaveChangesAsync();
        }

        await sut.HandleNewSpread(MakeModel(SpreadType.Spot));

        await _notifier.DidNotReceive().NotifyUser(Arg.Any<long>(), Arg.Any<string>());
    }

    [Fact]
    public async Task HandleNewSpread_VolumeBelowRequiredPosition_DoesNotNotify()
    {
        var (sut, seed) = CreateSut();
        var user = MakeUser(chatId: 444, accountId: "acc-4");
        user.PositionSize = 1_000_000; // far larger than the model's volumes
        using (seed)
        {
            seed.UserSettings.Add(user);
            await seed.SaveChangesAsync();
        }

        await sut.HandleNewSpread(MakeModel(SpreadType.Spot));

        await _notifier.DidNotReceive().NotifyUser(Arg.Any<long>(), Arg.Any<string>());
    }

    [Fact]
    public async Task HandleNewSpread_UnknownSpreadType_ReturnsWithoutNotifying()
    {
        var (sut, seed) = CreateSut();
        using (seed)
        {
            seed.UserSettings.Add(MakeUser(chatId: 555, accountId: "acc-5"));
            await seed.SaveChangesAsync();
        }

        await sut.HandleNewSpread(MakeModel((SpreadType)999));

        await _notifier.DidNotReceive().NotifyUser(Arg.Any<long>(), Arg.Any<string>());
    }

    [Fact]
    public async Task HandleNewSpread_DbContextFactoryThrows_LogsErrorAndSwallowsException()
    {
        var factory = Substitute.For<IDbContextFactory<AppDbContext>>();
        factory.CreateDbContext().Throws(new InvalidOperationException("db unavailable"));
        var sut = new SpreadService(factory, _notifier, _logger);

        await sut.HandleNewSpread(MakeModel(SpreadType.Spot));

        Assert.Contains(_logger.Entries, e => e.Message.Contains("Error handling new spread"));
    }

    [Theory]
    [InlineData(SpreadType.Futures)]
    [InlineData(SpreadType.Funding)]
    public async Task HandleNewSpread_FundingRatesPresent_IncludesFundingLineInMessage(SpreadType type)
    {
        var (sut, seed) = CreateSut();
        using (seed)
        {
            seed.UserSettings.Add(MakeUser(chatId: 666, accountId: "acc-6"));
            await seed.SaveChangesAsync();
        }
        var model = MakeModel(type);
        model.ExchangeLong.FundingRateValue = 0.01;
        model.ExchangeShort.FundingRateValue = -0.02;

        await sut.HandleNewSpread(model);

        await _notifier.Received(1).NotifyUser(666, Arg.Is<string>(m => m.Contains("Funding")));
    }

    [Theory]
    [InlineData(SpreadType.Futures)]
    [InlineData(SpreadType.Funding)]
    public async Task HandleNewSpread_MessageConstructionThrows_LogsErrorsAndNotifiesWithEmptyMessage(SpreadType type)
    {
        var (sut, seed) = CreateSut();
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
        await _notifier.Received(1).NotifyUser(777, string.Empty);
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
