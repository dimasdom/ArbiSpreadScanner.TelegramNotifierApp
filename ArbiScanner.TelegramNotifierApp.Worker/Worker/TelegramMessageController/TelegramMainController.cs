namespace ArbiScanner.TelegramNotifierApp.Worker.Worker.TelegramMessageController;
using ArbiScanner.TelegramNotifierApp.Application.Services;
using ArbiScanner.TelegramNotifierApp.Abstractions.Interfaces.Services;
using ArbiScanner.TelegramNotifierApp.Domain.Settings;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
public class MainController(IServiceProvider serviceProvider)
{
    private const string ButtonResume = "▶️ Resume";
    private const string ButtonPause  = "⏸ Pause";

    private static readonly ReplyKeyboardMarkup MainKeyboard = new(
    [
        new KeyboardButton[] { new(ButtonResume), new(ButtonPause) }
    ])
    {
        ResizeKeyboard  = true,
        OneTimeKeyboard = false
    };

    private readonly IServiceProvider _serviceProvider = serviceProvider;

    public async Task Index(ITelegramBotClient botClient, Update update, CancellationToken token)
    {
        if (update.Type == UpdateType.Message && update.Message?.Type == MessageType.Text)
        {
            await HandleTextMessageAsync(botClient, update.Message, token);
        }
    }


    private async Task HandleTextMessageAsync(
        ITelegramBotClient botClient,
        Message message,
        CancellationToken token)
    {
        var chatId  = message.Chat.Id;
        var text    = message.Text?.Trim() ?? string.Empty;
        var userName = message.From?.Username ?? string.Empty;

        if (text.Equals(ButtonResume, StringComparison.OrdinalIgnoreCase))
        {
            await SetActiveStatusAsync(botClient, chatId, active: true, token);
            return;
        }

        if (text.Equals(ButtonPause, StringComparison.OrdinalIgnoreCase))
        {
            await SetActiveStatusAsync(botClient, chatId, active: false, token);
            return;
        }

        if (Guid.TryParse(text, out _))
        {
            await LinkAccountAsync(botClient, chatId, userName, linkId: text, token);
            return;
        }

        await botClient.SendMessage(
            chatId,
            "Send your link code to connect your account, or use the buttons below to control notifications.",
            replyMarkup: MainKeyboard,
            cancellationToken: token);
    }


    private async Task SetActiveStatusAsync(
        ITelegramBotClient botClient,
        long chatId,
        bool active,
        CancellationToken token)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var userService = scope.ServiceProvider.GetRequiredService<ITelegramUserService>();

        await userService.UpdateTelegramUserActiveStatus(chatId, active);

        var statusText = active ? "Notifications resumed ▶️" : "Notifications paused ⏸";
        await botClient.SendMessage(
            chatId,
            statusText,
            replyMarkup: MainKeyboard,
            cancellationToken: token);
    }

    private async Task LinkAccountAsync(
        ITelegramBotClient botClient,
        long chatId,
        string userName,
        string linkId,
        CancellationToken token)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var userService = scope.ServiceProvider.GetRequiredService<ITelegramUserService>();

        var res = await userService.LinkTelegramUserWithAccount(userName, chatId, linkId);
        if (res.IsFailed)
        {
            await botClient.SendMessage(
                chatId,
                $"Failed to link account: {res.Errors.FirstOrDefault()?.Message ?? "Unknown error"}",
                replyMarkup: new ReplyKeyboardRemove(),
                cancellationToken: token);
            return;
        }
        await botClient.SendMessage(
            chatId,
            "Your account has been successfully linked! You will now receive notifications here.",
            replyMarkup: MainKeyboard,
            cancellationToken: token);
    }

    public static Task HandleErrorAsync(Exception ex)
    {
        Console.WriteLine("Error: " + ex.Message);
        return Task.CompletedTask;
    }

}