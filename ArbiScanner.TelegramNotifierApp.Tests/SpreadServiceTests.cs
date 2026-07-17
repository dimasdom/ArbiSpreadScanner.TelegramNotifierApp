using ArbiScanner.TelegramNotifierApp.Application.Services;
using ArbiScanner.TelegramNotifierApp.Abstractions.Interfaces.Services;
using ArbiScannerWeb.Domain.Models;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace ArbiScanner.TelegramNotifierApp.Tests;

public class SpreadServiceTests
{
    private readonly SpreadService _sut;

    public SpreadServiceTests()
    {
        var factory = Substitute.For<IDbContextFactory<AppDbContext>>();
        var notifier = Substitute.For<ITelegramNotifierUserService>();
        var logger = new TestLogger<SpreadService>();
        _sut = new SpreadService(factory, notifier, logger);
    }

    private static TradeOpportunityModel MakeSpotModel(double spread) => new()
    {
        Symbol = "BTC/USDT",
        Spread = spread,
        PossibleProfit = 1.5,
        StartSpread = spread,
        ExchangeRateA = new ExchangeRateModel { Symbol = "BTC/USDT", Exchange = "Binance" },
        ExchangeRateB = new ExchangeRateModel { Symbol = "BTC/USDT", Exchange = "OKX" },
        ExchangeLong  = new ExchangeRateModel { Symbol = "BTC/USDT", Exchange = "Binance", ExchangeRate = 50000, SlippageShort = 0.05 },
        ExchangeShort = new ExchangeRateModel { Symbol = "BTC/USDT", Exchange = "OKX",     ExchangeRate = 51000, SlippageLong  = 0.05 },
    };

    [Fact]
    public void CalculateSpreadFor_PositiveDifference_ReturnsPositivePercent()
    {
        var result = SpreadService.CalculateSpreadFor(110, 100);
        Assert.Equal(10.0, result, precision: 10);
    }

    [Fact]
    public void CalculateSpreadFor_NegativeDifference_ReturnsNegativePercent()
    {
        var result = SpreadService.CalculateSpreadFor(90, 100);
        Assert.Equal(-10.0, result, precision: 10);
    }

    [Fact]
    public void CalculateSpreadFor_EqualPrices_ReturnsZero()
    {
        Assert.Equal(0.0, SpreadService.CalculateSpreadFor(100, 100));
    }

    [Fact]
    public void MessageConstructorSpot_ContainsCoinSymbolAndExchanges()
    {
        var msg = _sut.MessageConstructorSpot(MakeSpotModel(2.5));
        Assert.Contains("BTC/USDT", msg);
        Assert.Contains("Binance", msg);
        Assert.Contains("OKX", msg);
        Assert.Contains("Spot Spread:", msg);
    }

    [Fact]
    public void MessageConstructorSpot_WithFundingRate_ContainsFundingLine()
    {
        var model = MakeSpotModel(2.5);
        model.ExchangeShort.FundingRateValue = 0.01;
        var msg = _sut.MessageConstructorSpot(model);
        Assert.Contains("Funding:", msg);
    }

    [Fact]
    public void MessageConstructorSpot_NoFundingRate_OmitsFundingLine()
    {
        var msg = _sut.MessageConstructorSpot(MakeSpotModel(2.5));
        Assert.DoesNotContain("Funding:", msg);
    }
}
