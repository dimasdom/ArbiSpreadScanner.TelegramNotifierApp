using System.Diagnostics.Metrics;
using ArbiScanner.TelegramNotifierApp.Abstractions.Interfaces.Services;
using ArbiScannerWeb.Abstractions.Messaging;
using ArbiScannerWeb.Domain.Models;
using ArbiScannerWeb.Infrastructure.Messaging;
using ArbiScannerWeb.Infrastructure.Settings;
using ArbiScannerWeb.Messaging.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArbiScanner.TelegramNotifierApp.Worker.Worker.MessageBroker;

/// <summary>
/// The live Kafka consumer of the Telegram notifier. Only <c>Open</c> events do anything (a user alert); <c>Update</c> is ignored
/// and <c>Close</c> is only logged, exactly as before the move to Kafka.
/// </summary>
/// <remarks>
/// Alerts are user-visible and cannot be undone, so two guards apply before one is sent: a duplicate of an already-processed
/// message-id is skipped, and an Open older than <see cref="MessagingConsumerSettings.MaxOpenAlertAgeMinutes"/> is dropped, so a long
/// outage or an offset reset never re-alerts users about spreads that no longer exist.
/// </remarks>
public sealed class TelegramSpreadKafkaHandler(
    ISpreadService spreads,
    IIdempotencyStore idempotency,
    IOptions<MessagingOptions> options,
    ILogger<TelegramSpreadKafkaHandler> logger,
    TimeProvider? clock = null) : IKafkaMessageHandler
{
    private static readonly TimeSpan ClaimWait = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ClaimPoll = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task HandleAsync(ConsumedMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var model = Decode(message);

        switch (model.ActionType)
        {
            case MarketPositionAction.Open:
                await HandleOpenAsync(model, message, cancellationToken);
                break;

            case MarketPositionAction.Update:
                logger.LogDebug("Ignoring update for spread {Guid}", model.Guid);
                break;

            case MarketPositionAction.Close:
                await spreads.HandleCloseSpread(model);
                break;

            default:
                throw new NonRetryableMessageException($"Unknown action type {model.ActionType}.");
        }
    }

    private async Task HandleOpenAsync(TradeOpportunityModel model, ConsumedMessage message, CancellationToken cancellationToken)
    {
        var messageId = message.Metadata.MessageId
            ?? throw new NonRetryableMessageException("The message-id header is required to alert users idempotently.");
        var service = new KeyValuePair<string, object?>("service", options.Value.ServiceName);

        var age = _clock.GetUtcNow() - new DateTimeOffset(message.TimestampUtc, TimeSpan.Zero);
        var maxAge = TimeSpan.FromMinutes(options.Value.Consumer.MaxOpenAlertAgeMinutes);
        if (age > maxAge)
        {
            ConsumerMetrics.StaleAlertsSkipped.Add(1, service);
            logger.LogWarning("Not alerting on spread {Guid}: its Open event is {Age:g} old (limit {Limit:g})", model.Guid, age, maxAge);
            return;
        }

        var ran = await IdempotentExecutor.RunOnceAsync(
            idempotency,
            $"spread-dedupe:{options.Value.ServiceName}:{messageId}",
            ct => spreads.HandleNewSpread(model, ct),
            ClaimWait,
            ClaimPoll,
            cancellationToken);

        if (!ran)
        {
            ConsumerMetrics.DuplicatesSkipped.Add(1, service);
            logger.LogInformation("Duplicate delivery skipped: message-id {MessageId}", messageId);
        }
    }

    private static TradeOpportunityModel Decode(ConsumedMessage message)
    {
        try
        {
            return TradeOpportunitySerializer.Deserialize(message.Value);
        }
        catch (Exception ex)
        {
            throw new NonRetryableMessageException("Payload is not a decodable TradeOpportunityModel.", ex);
        }
    }
}
