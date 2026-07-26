using System.Reflection;
using ArbiScanner.TelegramNotifierApp.Abstractions.Interfaces.Services;
using ArbiScanner.TelegramNotifierApp.Worker.Worker.MessageBroker;
using ArbiScannerWeb.Abstractions.Interfaces;
using ArbiScannerWeb.Domain.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace ArbiScanner.TelegramNotifierApp.Tests;

public class SpreadsMessageBrokerTests
{
    private readonly IRabbitMqService _rabbitMqService = Substitute.For<IRabbitMqService>();
    private readonly ISpreadService _spreadService = Substitute.For<ISpreadService>();
    private readonly TestLogger<SpreadsMessageBroker> _logger = new();
    private readonly SpreadsMessageBroker _sut;

    public SpreadsMessageBrokerTests()
    {
        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(ISpreadService)).Returns(_spreadService);

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(scopedProvider);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        _sut = new SpreadsMessageBroker(_rabbitMqService, scopeFactory, _logger);
    }

    // The two methods under test here are protected/private on this hosted service, so
    // reflection is used to invoke them directly without spinning up the real hosted-service
    // lifecycle or waiting out the production retry delays.
    private static Task InvokeExecuteAsync(SpreadsMessageBroker sut, CancellationToken token)
    {
        var method = typeof(SpreadsMessageBroker).GetMethod("ExecuteAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(sut, [token])!;
    }

    private static Task InvokeProcessMessageAsync(SpreadsMessageBroker sut, TradeOpportunityModel model)
    {
        var method = typeof(SpreadsMessageBroker).GetMethod("ProcessMessageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(sut, [model])!;
    }

    private static TradeOpportunityModel MakeModel(MarketPositionAction action) => new()
    {
        Guid = Guid.NewGuid(),
        ActionType = action,
        Symbol = "BTC/USDT",
        ExchangeRateA = new ExchangeRateModel { Symbol = "BTC/USDT", Exchange = "Binance" },
        ExchangeRateB = new ExchangeRateModel { Symbol = "BTC/USDT", Exchange = "OKX" },
        ExchangeLong = new ExchangeRateModel { Symbol = "BTC/USDT", Exchange = "Binance" },
        ExchangeShort = new ExchangeRateModel { Symbol = "BTC/USDT", Exchange = "OKX" },
    };

    [Fact]
    public async Task ExecuteAsync_TokenAlreadyCancelled_StopsConsumingWithoutStarting()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await InvokeExecuteAsync(_sut, cts.Token);

        await _rabbitMqService.DidNotReceive().StartConsumingAsync(Arg.Any<CancellationToken>());
        await _rabbitMqService.Received(1).StopConsumingAsync();
        Assert.Contains(_logger.Entries, e => e.Message.Contains("Stopped consuming"));
    }

    [Fact]
    public async Task ExecuteAsync_TokenAlreadyCancelled_StopConsumingThrows_LogsError()
    {
        _rabbitMqService.StopConsumingAsync().Throws(new InvalidOperationException("boom"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await InvokeExecuteAsync(_sut, cts.Token);

        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Error stopping"));
    }

    [Fact]
    public async Task ExecuteAsync_StartConsumingSucceedsThenCancelled_RetriesAndPropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        _rabbitMqService.StartConsumingAsync(Arg.Any<CancellationToken>())
            .Returns(async _ => await cts.CancelAsync());

        var executeTask = InvokeExecuteAsync(_sut, cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executeTask);
        await _rabbitMqService.Received(1).StartConsumingAsync(Arg.Any<CancellationToken>());
        await _rabbitMqService.Received(1).StopConsumingAsync();
        Assert.Contains(_logger.Entries, e => e.Message.Contains("Cancellation requested"));
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("RabbitMQ consuming failed"));
    }

    [Theory]
    [InlineData(MarketPositionAction.Open)]
    [InlineData(MarketPositionAction.Close)]
    public async Task ProcessMessageAsync_OpenOrClose_InvokesMatchingSpreadServiceMethod(MarketPositionAction action)
    {
        var model = MakeModel(action);

        await InvokeProcessMessageAsync(_sut, model);

        if (action == MarketPositionAction.Open)
        {
            await _spreadService.Received(1).HandleNewSpread(model);
            await _spreadService.DidNotReceive().HandleCloseSpread(Arg.Any<TradeOpportunityModel>());
        }
        else
        {
            await _spreadService.Received(1).HandleCloseSpread(model);
            await _spreadService.DidNotReceive().HandleNewSpread(Arg.Any<TradeOpportunityModel>());
        }
    }

    [Fact]
    public async Task ProcessMessageAsync_UpdateAction_LogsOnly()
    {
        var model = MakeModel(MarketPositionAction.Update);

        await InvokeProcessMessageAsync(_sut, model);

        await _spreadService.DidNotReceive().HandleNewSpread(Arg.Any<TradeOpportunityModel>());
        await _spreadService.DidNotReceive().HandleCloseSpread(Arg.Any<TradeOpportunityModel>());
        Assert.Contains(_logger.Entries, e => e.Message.Contains("Received update for spread"));
    }

    [Fact]
    public async Task ProcessMessageAsync_UnknownAction_LogsWarningAndDoesNotCallSpreadService()
    {
        var model = MakeModel((MarketPositionAction)999);

        await InvokeProcessMessageAsync(_sut, model);

        await _spreadService.DidNotReceive().HandleNewSpread(Arg.Any<TradeOpportunityModel>());
        await _spreadService.DidNotReceive().HandleCloseSpread(Arg.Any<TradeOpportunityModel>());
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Unknown action type"));
    }
}
