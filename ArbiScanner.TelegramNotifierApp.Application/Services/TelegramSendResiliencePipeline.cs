using System.Threading.RateLimiting;
using Polly;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Retry;
using Polly.Timeout;
using Telegram.Bot.Exceptions;

namespace ArbiScanner.TelegramNotifierApp.Application.Services;

// Shared, singleton pipeline for every outbound Telegram send: a token-bucket rate limiter
// caps throughput under Telegram's ~30 msg/sec per-bot flood-control limit, a circuit breaker
// stops hammering the API once it's clearly degraded, retry absorbs transient hiccups (honoring
// Telegram's own RetryAfter hint on 429s), and a per-attempt timeout bounds a single hung call.
// Must be a singleton: the rate limiter's token bucket only means anything if every caller
// shares the same instance.
public static class TelegramSendResiliencePipeline
{
    public const int MaxMessagesPerSecond = 25;
    private const int MaxRetryAttempts = 3;
    private static readonly TimeSpan PerAttemptTimeout = TimeSpan.FromSeconds(10);

    public static ResiliencePipeline Create()
    {
        var rateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = MaxMessagesPerSecond,
            TokensPerPeriod = MaxMessagesPerSecond,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 50_000,
            AutoReplenishment = true
        });

        return new ResiliencePipelineBuilder()
            .AddRateLimiter(rateLimiter)
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransientFailure),
                MaxRetryAttempts = MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(500),
                DelayGenerator = args => new ValueTask<TimeSpan?>(
                    args.Outcome.Exception is ApiRequestException { Parameters.RetryAfter: { } retryAfterSeconds }
                        ? TimeSpan.FromSeconds(retryAfterSeconds)
                        : null)
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransientFailure),
                FailureRatio = 0.5,
                MinimumThroughput = 20,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(30)
            })
            .AddTimeout(PerAttemptTimeout)
            .Build();
    }

    // Decides both what the retry loop attempts again and what counts toward tripping the
    // circuit breaker - a flood of "user blocked the bot" 403s at scale is expected and must
    // not look like Telegram itself degrading. Public (like IsPermanent) so the classification
    // itself is unit-testable without waiting through real Polly retry/backoff timing.
    public static bool IsTransientFailure(Exception ex) => ex switch
    {
        ApiRequestException { ErrorCode: 403 or 400 } => false,
        BrokenCircuitException => false,
        RateLimiterRejectedException => false,
        OperationCanceledException => false,
        _ => true
    };

    public static bool IsPermanent(Exception ex) =>
        ex is ApiRequestException { ErrorCode: 403 or 400 };
}
