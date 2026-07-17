using ArbiScanner.TelegramNotifierApp.Application.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Telegram.Bot;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Xunit;

namespace ArbiScanner.TelegramNotifierApp.Tests;

public class TelegramNotifierUserServiceTests
{
    private readonly ITelegramBotClient _botClient = Substitute.For<ITelegramBotClient>();
    private readonly TestLogger<TelegramNotifierUserService> _logger = new();
    private readonly TelegramNotifierUserService _sut;

    public TelegramNotifierUserServiceTests()
    {
        _botClient
            .SendRequest(Arg.Any<IRequest<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Message>(null!));
        _sut = new TelegramNotifierUserService(_botClient, _logger);
    }

    [Fact]
    public async Task NotifyUser_ValidMessage_LogsInformation()
    {
        await _sut.NotifyUser(123456, "hello from test");
        Assert.True(_logger.HasLevel(LogLevel.Information));
    }

    [Fact]
    public async Task NotifyUser_EmptyMessage_LogsError()
    {
        await _sut.NotifyUser(123456, "");
        Assert.True(_logger.HasLevel(LogLevel.Error));
    }

    [Fact]
    public async Task NotifyUser_WhitespaceMessage_LogsError()
    {
        await _sut.NotifyUser(123456, "   ");
        Assert.True(_logger.HasLevel(LogLevel.Error));
    }

    [Fact]
    public async Task NotifyUser_BotClientThrows_LogsError()
    {
        _botClient
            .SendRequest(Arg.Any<IRequest<Message>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("network error"));

        await _sut.NotifyUser(123456, "valid message");

        Assert.True(_logger.HasLevel(LogLevel.Error));
    }

    [Fact]
    public async Task NotifyUser_ValidMessage_DoesNotLogError()
    {
        await _sut.NotifyUser(123456, "hello");
        Assert.False(_logger.HasLevel(LogLevel.Error));
    }
}
