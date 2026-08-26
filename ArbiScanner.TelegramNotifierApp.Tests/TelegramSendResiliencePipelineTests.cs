using ArbiScanner.TelegramNotifierApp.Application.Services;
using Telegram.Bot.Exceptions;
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

    [Fact]
    public void Create_ReturnsUsablePipeline()
    {
        var pipeline = TelegramSendResiliencePipeline.Create();

        Assert.NotNull(pipeline);
    }
}
