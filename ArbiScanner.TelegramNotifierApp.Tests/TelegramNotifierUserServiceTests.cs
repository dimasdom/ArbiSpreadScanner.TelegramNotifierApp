using ArbiScanner.TelegramNotifierApp.Abstractions.Errors;
using ArbiScanner.TelegramNotifierApp.Application.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Polly;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Xunit;

namespace ArbiScanner.TelegramNotifierApp.Tests;

public class TelegramNotifierUserServiceTests
{
    private readonly ITelegramBotClient _botClient = Substitute.For<ITelegramBotClient>();
    private readonly TestLogger<TelegramNotifierUserService> _logger = new();
    private readonly TelegramNotifierUserService _sut;

    // ResiliencePipeline.Empty is a no-op: no retry delay, no rate-limit wait, no circuit
    // breaker state, so unit tests stay fast and only exercise NotifyUser's own logic. The
    // pipeline's retry/backoff/circuit-breaker/rate-limiter composition itself is timing-based
    // Polly wiring covered by TelegramSendResiliencePipelineTests' classification tests instead
    // of full end-to-end timing tests.
    public TelegramNotifierUserServiceTests()
    {
        _botClient
            .SendRequest(Arg.Any<IRequest<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Message>(null!));
        _sut = new TelegramNotifierUserService(_botClient, ResiliencePipeline.Empty, _logger);
    }

    [Fact]
    public async Task NotifyUser_ValidMessage_LogsInformationAndReturnsSuccess()
    {
        var result = await _sut.NotifyUser(123456, "hello from test");

        Assert.True(_logger.HasLevel(LogLevel.Information));
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task NotifyUser_EmptyMessage_LogsErrorAndReturnsPermanentFailure()
    {
        var result = await _sut.NotifyUser(123456, "");

        Assert.True(_logger.HasLevel(LogLevel.Error));
        Assert.True(result.IsFailed);
        Assert.True(result.HasError<PermanentDeliveryError>());
    }

    [Fact]
    public async Task NotifyUser_WhitespaceMessage_LogsErrorAndReturnsPermanentFailure()
    {
        var result = await _sut.NotifyUser(123456, "   ");

        Assert.True(_logger.HasLevel(LogLevel.Error));
        Assert.True(result.IsFailed);
        Assert.True(result.HasError<PermanentDeliveryError>());
    }

    [Fact]
    public async Task NotifyUser_BotClientThrowsGenericException_LogsErrorAndReturnsTransientFailure()
    {
        _botClient
            .SendRequest(Arg.Any<IRequest<Message>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("network error"));

        var result = await _sut.NotifyUser(123456, "valid message");

        Assert.True(_logger.HasLevel(LogLevel.Error));
        Assert.True(result.IsFailed);
        Assert.True(result.HasError<TransientDeliveryError>());
    }

    [Fact]
    public async Task NotifyUser_BotBlockedByUser_LogsWarningAndReturnsPermanentFailure()
    {
        _botClient
            .SendRequest(Arg.Any<IRequest<Message>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiRequestException("Forbidden: bot was blocked by the user", 403));

        var result = await _sut.NotifyUser(123456, "valid message");

        Assert.True(_logger.HasLevel(LogLevel.Warning));
        Assert.False(_logger.HasLevel(LogLevel.Error));
        Assert.True(result.IsFailed);
        Assert.True(result.HasError<PermanentDeliveryError>());
    }

    [Fact]
    public async Task NotifyUser_ValidMessage_DoesNotLogError()
    {
        var result = await _sut.NotifyUser(123456, "hello");

        Assert.False(_logger.HasLevel(LogLevel.Error));
        Assert.True(result.IsSuccess);
    }
}
