using ArbiScanner.TelegramNotifierApp.Abstractions.Interfaces.Services;
using ArbiScanner.TelegramNotifierApp.Worker.Worker.MessageBroker;
using ArbiScannerWeb.Abstractions.Messaging;
using ArbiScannerWeb.Domain.Models;
using ArbiScannerWeb.Infrastructure.Messaging;
using ArbiScannerWeb.Infrastructure.Settings;
using ArbiScannerWeb.Messaging.Kafka;
using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace ArbiScanner.TelegramNotifierApp.Tests;

public class TelegramSpreadKafkaHandlerTests
{
    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MemoryStore : IIdempotencyStore
    {
        private readonly Dictionary<string, string> _state = [];

        public int Calls { get; private set; }

        public IReadOnlyCollection<string> Keys => _state.Keys;

        public Task<ClaimResult> TryBeginAsync(string key, CancellationToken cancellationToken)
        {
            Calls++;
            if (_state.TryAdd(key, "processing"))
            {
                return Task.FromResult(ClaimResult.Started);
            }

            return Task.FromResult(_state[key] == "done" ? ClaimResult.AlreadyDone : ClaimResult.InProgress);
        }

        public Task CompleteAsync(string key)
        {
            _state[key] = "done";
            return Task.CompletedTask;
        }

        public Task AbandonAsync(string key)
        {
            _state.Remove(key);
            return Task.CompletedTask;
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly ISpreadService _spreads = Substitute.For<ISpreadService>();
    private readonly MemoryStore _store = new();

    private TelegramSpreadKafkaHandler Sut(int maxAgeMinutes = 10) => new(
        _spreads,
        _store,
        Options.Create(new MessagingOptions { ServiceName = "telegram", Consumer = new MessagingConsumerSettings { MaxOpenAlertAgeMinutes = maxAgeMinutes } }),
        NullLogger<TelegramSpreadKafkaHandler>.Instance,
        new ManualClock(Now));

    private static TradeOpportunityModel Spread(MarketPositionAction action) => new()
    {
        Guid = Guid.NewGuid(),
        ActionType = action,
        Symbol = "BTC/USDT",
        Type = SpreadType.Futures,
        ExchangeRateA = new ExchangeRateModel { Symbol = "BTC/USDT", Exchange = "Binance" },
        ExchangeRateB = new ExchangeRateModel { Symbol = "BTC/USDT", Exchange = "OKX" },
        ExchangeLong = new ExchangeRateModel { Symbol = "BTC/USDT", Exchange = "Binance" },
        ExchangeShort = new ExchangeRateModel { Symbol = "BTC/USDT", Exchange = "OKX" },
    };

    private static ConsumedMessage Message(TradeOpportunityModel model, string? messageId = "m-1", TimeSpan? age = null) =>
        Raw(TradeOpportunitySerializer.Serialize(model), messageId, age);

    private static ConsumedMessage Raw(byte[] payload, string? messageId, TimeSpan? age = null)
    {
        var headers = new Headers();
        if (messageId is not null)
        {
            headers.Add(MessageHeaders.MessageId, System.Text.Encoding.UTF8.GetBytes(messageId));
        }

        var producedAt = (Now - (age ?? TimeSpan.FromSeconds(1))).UtcDateTime;
        return new ConsumedMessage("spreads", 0, 1, "k", payload, headers, MessageHeaders.Read(headers), producedAt);
    }

    [Fact]
    public async Task An_open_event_alerts_users_once()
    {
        var model = Spread(MarketPositionAction.Open);

        await Sut().HandleAsync(Message(model), CancellationToken.None);

        await _spreads.Received(1).HandleNewSpread(Arg.Is<TradeOpportunityModel>(m => m.Guid == model.Guid), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_redelivered_open_does_not_alert_users_twice()
    {
        var message = Message(Spread(MarketPositionAction.Open));

        await Sut().HandleAsync(message, CancellationToken.None);
        await Sut().HandleAsync(message, CancellationToken.None);

        await _spreads.Received(1).HandleNewSpread(Arg.Any<TradeOpportunityModel>(), Arg.Any<CancellationToken>());
        Assert.Equal(["spread-dedupe:telegram:m-1"], _store.Keys);
    }

    [Fact]
    public async Task An_open_older_than_the_replay_window_is_not_alerted_and_not_claimed()
    {
        await Sut(maxAgeMinutes: 10).HandleAsync(Message(Spread(MarketPositionAction.Open), age: TimeSpan.FromMinutes(11)), CancellationToken.None);

        await _spreads.DidNotReceiveWithAnyArgs().HandleNewSpread(default!, default);
        Assert.Equal(0, _store.Calls);
    }

    [Fact]
    public async Task An_open_inside_the_replay_window_is_alerted()
    {
        await Sut(maxAgeMinutes: 10).HandleAsync(Message(Spread(MarketPositionAction.Open), age: TimeSpan.FromMinutes(9)), CancellationToken.None);

        await _spreads.Received(1).HandleNewSpread(Arg.Any<TradeOpportunityModel>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_update_does_nothing_and_does_not_touch_the_idempotency_store()
    {
        await Sut().HandleAsync(Message(Spread(MarketPositionAction.Update)), CancellationToken.None);

        await _spreads.DidNotReceiveWithAnyArgs().HandleNewSpread(default!, default);
        await _spreads.DidNotReceiveWithAnyArgs().HandleCloseSpread(default!);
        Assert.Equal(0, _store.Calls);
    }

    [Fact]
    public async Task A_close_is_forwarded()
    {
        var model = Spread(MarketPositionAction.Close);

        await Sut().HandleAsync(Message(model), CancellationToken.None);

        await _spreads.Received(1).HandleCloseSpread(Arg.Is<TradeOpportunityModel>(m => m.Guid == model.Guid));
    }

    [Fact]
    public async Task A_failure_releases_the_claim_so_the_retry_can_alert()
    {
        _spreads.HandleNewSpread(Arg.Any<TradeOpportunityModel>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db down"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Sut().HandleAsync(Message(Spread(MarketPositionAction.Open)), CancellationToken.None));

        Assert.Empty(_store.Keys);
    }

    [Fact]
    public async Task An_open_without_a_message_id_is_non_retryable()
    {
        await Assert.ThrowsAsync<NonRetryableMessageException>(() => Sut().HandleAsync(Message(Spread(MarketPositionAction.Open), messageId: null), CancellationToken.None));

        await _spreads.DidNotReceiveWithAnyArgs().HandleNewSpread(default!, default);
    }

    [Fact]
    public async Task An_undecodable_payload_is_non_retryable()
    {
        await Assert.ThrowsAsync<NonRetryableMessageException>(() => Sut().HandleAsync(Raw([0xFF, 0xFF, 0xFF, 0xFF, 0xFF], "m-1"), CancellationToken.None));
    }

    [Fact]
    public async Task An_unknown_action_is_non_retryable()
    {
        var model = Spread(MarketPositionAction.Open);
        model.ActionType = (MarketPositionAction)42;

        await Assert.ThrowsAsync<NonRetryableMessageException>(() => Sut().HandleAsync(Message(model), CancellationToken.None));
    }
}
