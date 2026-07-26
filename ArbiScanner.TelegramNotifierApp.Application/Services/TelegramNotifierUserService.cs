using ArbiScanner.TelegramNotifierApp.Abstractions.Interfaces.Services;
using Microsoft.Extensions.Logging;
using Telegram.Bot;

namespace ArbiScanner.TelegramNotifierApp.Application.Services;
public class TelegramNotifierUserService(
    ITelegramBotClient telegramBotClient,
    ILogger<TelegramNotifierUserService> logger) : ITelegramNotifierUserService
{
    private readonly ITelegramBotClient _telegramBotClient = telegramBotClient;
    private readonly ILogger<TelegramNotifierUserService> _logger = logger;

    public async Task NotifyUser(long chatId, string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                throw new ArgumentException("Message cannot be empty.", nameof(message));
            }

            await _telegramBotClient.SendMessage(chatId, message);
            _logger.LogInformation("Telegram message sent to chatId {ChatId}", chatId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Telegram message to chatId {ChatId}", chatId);
        }
        
    }
}