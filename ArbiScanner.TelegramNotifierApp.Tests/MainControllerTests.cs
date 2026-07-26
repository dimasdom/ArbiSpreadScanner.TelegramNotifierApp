using ArbiScanner.TelegramNotifierApp.Abstractions.Interfaces.Services;
using ArbiScanner.TelegramNotifierApp.Worker.Worker.TelegramMessageController;
using FluentResults;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Telegram.Bot;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Xunit;

namespace ArbiScanner.TelegramNotifierApp.Tests;

public class MainControllerTests
{
    private readonly ITelegramUserService _userService = Substitute.For<ITelegramUserService>();
    private readonly ITelegramBotClient _botClient = Substitute.For<ITelegramBotClient>();
    private readonly MainController _sut;

    public MainControllerTests()
    {
        _botClient
            .SendRequest(Arg.Any<IRequest<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Message>(null!));

        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(ITelegramUserService)).Returns(_userService);

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(scopedProvider);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IServiceScopeFactory)).Returns(scopeFactory);

        _sut = new MainController(serviceProvider);
    }

    private static Update TextUpdate(long chatId, string text, string userName = "john") => new()
    {
        Message = new Message
        {
            Chat = new Chat { Id = chatId },
            From = new User { Username = userName },
            Text = text,
        },
    };

    private async Task<SendMessageRequest> AssertSentMessage(long chatId)
    {
        var call = _botClient.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(ITelegramBotClient.SendRequest));
        var request = Assert.IsType<SendMessageRequest>(call.GetArguments()[0]);
        Assert.Equal(chatId, request.ChatId!.Identifier);
        return await Task.FromResult(request);
    }

    [Fact]
    public async Task Index_NonMessageUpdate_DoesNothing()
    {
        var update = new Update();

        await _sut.Index(_botClient, update, CancellationToken.None);

        await _botClient.DidNotReceive().SendRequest(Arg.Any<IRequest<Message>>(), Arg.Any<CancellationToken>());
        await _userService.DidNotReceive().UpdateTelegramUserActiveStatus(Arg.Any<long>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Index_MessageWithoutText_DoesNothing()
    {
        var update = new Update { Message = new Message { Chat = new Chat { Id = 1 } } };

        await _sut.Index(_botClient, update, CancellationToken.None);

        await _botClient.DidNotReceive().SendRequest(Arg.Any<IRequest<Message>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Index_ResumeButtonText_ActivatesUserAndSendsConfirmation()
    {
        const long chatId = 555;
        _userService.UpdateTelegramUserActiveStatus(chatId, true).Returns(Task.FromResult(Result.Ok()));

        await _sut.Index(_botClient, TextUpdate(chatId, "▶️ Resume"), CancellationToken.None);

        await _userService.Received(1).UpdateTelegramUserActiveStatus(chatId, true);
        var request = await AssertSentMessage(chatId);
        Assert.Contains("resumed", request.Text);
    }

    [Fact]
    public async Task Index_PauseButtonText_DeactivatesUserAndSendsConfirmation()
    {
        const long chatId = 556;
        _userService.UpdateTelegramUserActiveStatus(chatId, false).Returns(Task.FromResult(Result.Ok()));

        await _sut.Index(_botClient, TextUpdate(chatId, "⏸ Pause"), CancellationToken.None);

        await _userService.Received(1).UpdateTelegramUserActiveStatus(chatId, false);
        var request = await AssertSentMessage(chatId);
        Assert.Contains("paused", request.Text);
    }

    [Fact]
    public async Task Index_ValidGuidText_SuccessfulLink_SendsSuccessMessage()
    {
        const long chatId = 557;
        var linkId = Guid.NewGuid();
        _userService.LinkTelegramUserWithAccount("john", chatId, linkId.ToString())
            .Returns(Task.FromResult(Result.Ok()));

        await _sut.Index(_botClient, TextUpdate(chatId, linkId.ToString()), CancellationToken.None);

        await _userService.Received(1).LinkTelegramUserWithAccount("john", chatId, linkId.ToString());
        var request = await AssertSentMessage(chatId);
        Assert.Contains("successfully linked", request.Text);
    }

    [Fact]
    public async Task Index_ValidGuidText_FailedLink_SendsFailureMessageWithErrorText()
    {
        const long chatId = 558;
        var linkId = Guid.NewGuid();
        _userService.LinkTelegramUserWithAccount("john", chatId, linkId.ToString())
            .Returns(Task.FromResult(Result.Fail("link expired")));

        await _sut.Index(_botClient, TextUpdate(chatId, linkId.ToString()), CancellationToken.None);

        var request = await AssertSentMessage(chatId);
        Assert.Contains("link expired", request.Text);
    }

    [Fact]
    public async Task Index_ArbitraryText_SendsDefaultPrompt()
    {
        const long chatId = 559;

        await _sut.Index(_botClient, TextUpdate(chatId, "hello there"), CancellationToken.None);

        await _userService.DidNotReceive().LinkTelegramUserWithAccount(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<string>());
        var request = await AssertSentMessage(chatId);
        Assert.Contains("Send your link code", request.Text);
    }

    [Fact]
    public async Task HandleErrorAsync_CompletesSuccessfully()
    {
        var task = MainController.HandleErrorAsync(new InvalidOperationException("boom"));

        await task;

        Assert.True(task.IsCompletedSuccessfully);
    }
}
