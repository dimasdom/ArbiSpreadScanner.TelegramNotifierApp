using ArbiScanner.TelegramNotifierApp.Application.Services;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Xunit;

namespace ArbiScanner.TelegramNotifierApp.Tests;

public class TelegramSendResiliencePipelineTests
{
    [Theory]
    [InlineData(403)] // Forbidden: bot blocked/kicked/chat deactivated
    [InlineData(400)] // Bad Request: chat not found, malformed message, etc.
    public void IsPermanent_KnownNonRecoverableErrorCodes_ReturnsTrue(int errorCode)
    {
        var ex = new ApiRequestException("simulated", errorCode);

        Assert.True(TelegramSendResiliencePipeline.IsPermanent(ex));
    }

    [Fact]
    public void IsPermanent_TooManyRequests_ReturnsFalse()
    {
        var ex = new ApiRequestException("Too Many Requests", 429);

        Assert.False(TelegramSendResiliencePipeline.IsPermanent(ex));
    }

    [Fact]
    public void IsPermanent_NonApiException_ReturnsFalse()
    {
        Assert.False(TelegramSendResiliencePipeline.IsPermanent(new HttpRequestException("network down")));
    }

    [Theory]
    [InlineData(403)]
    [InlineData(400)]
    public void IsTransientFailure_KnownNonRecoverableErrorCodes_ReturnsFalse(int errorCode)
    {
        Assert.False(TelegramSendResiliencePipeline.IsTransientFailure(new ApiRequestException("simulated", errorCode)));
    }

    [Fact]
    public void IsTransientFailure_TooManyRequests_ReturnsTrue()
    {
        Assert.True(TelegramSendResiliencePipeline.IsTransientFailure(new ApiRequestException("Too Many Requests", 429)));
    }

    [Fact]
    public void IsTransientFailure_BrokenCircuit_ReturnsFalse()
    {
        // Retrying instantly into an already-open circuit is pointless - fail fast instead.
        Assert.False(TelegramSendResiliencePipeline.IsTransientFailure(new BrokenCircuitException()));
    }

    [Fact]
    public void IsTransientFailure_RateLimiterRejected_ReturnsFalse()
    {
        // The bucket queue is already saturated; retrying immediately would only add pressure.
        Assert.False(TelegramSendResiliencePipeline.IsTransientFailure(new RateLimiterRejectedException()));
    }

    [Fact]
    public void IsTransientFailure_OperationCanceled_ReturnsFalse()
    {
        // Respects cooperative cancellation (e.g. worker shutdown) instead of retrying anyway.
        Assert.False(TelegramSendResiliencePipeline.IsTransientFailure(new OperationCanceledException()));
    }

    [Fact]
    public void IsTransientFailure_UnknownException_ReturnsTrue()
    {
        Assert.True(TelegramSendResiliencePipeline.IsTransientFailure(new HttpRequestException("network down")));
    }

    [Fact]
    public async Task Create_TransientFailureThenSuccess_RetriesAndSucceeds()
    {
        var pipeline = TelegramSendResiliencePipeline.Create();
        var attempts = 0;

        await pipeline.ExecuteAsync(async ct =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new HttpRequestException("transient network blip");
            }
            await Task.CompletedTask;
        });

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Create_ApiRequestExceptionWithRetryAfter_HonorsRetryAfterDelay()
    {
        var pipeline = TelegramSendResiliencePipeline.Create();
        var attempts = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await pipeline.ExecuteAsync(async ct =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new ApiRequestException("Too Many Requests", 429, new ResponseParameters { RetryAfter = 1 });
            }
            await Task.CompletedTask;
        });

        stopwatch.Stop();
        Assert.Equal(2, attempts);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(900),
            $"expected the pipeline to honor Telegram's RetryAfter hint, waited only {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Create_PermanentFailure_DoesNotRetry()
    {
        var pipeline = TelegramSendResiliencePipeline.Create();
        var attempts = 0;

        await Assert.ThrowsAsync<ApiRequestException>(async () =>
            await pipeline.ExecuteAsync(async ct =>
            {
                attempts++;
                await Task.CompletedTask;
                throw new ApiRequestException("Forbidden: bot was blocked by the user", 403);
            }));

        Assert.Equal(1, attempts);
    }
}
