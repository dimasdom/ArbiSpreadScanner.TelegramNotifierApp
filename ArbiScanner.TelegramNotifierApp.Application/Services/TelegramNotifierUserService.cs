using ArbiScanner.TelegramNotifierApp.Abstractions.Errors;
using ArbiScanner.TelegramNotifierApp.Abstractions.Interfaces.Services;
using FluentResults;
using Microsoft.Extensions.Logging;
using Polly;
using Telegram.Bot;

namespace ArbiScanner.TelegramNotifierApp.Application.Services;

public class TelegramNotifierUserService(
    ITelegramBotClient telegramBotClient,
    ResiliencePipeline telegramSendPipeline,
    ILogger<TelegramNotifierUserService> logger) : ITelegramNotifierUserService
{
    private readonly ITelegramBotClient _telegramBotClient = telegramBotClient;
    private readonly ResiliencePipeline _telegramSendPipeline = telegramSendPipeline;
    private readonly ILogger<TelegramNotifierUserService> _logger = logger;

    public async Task<Result> NotifyUser(long chatId, string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            _logger.LogError("Cannot notify chatId {ChatId}: message is empty", chatId);
            return Result.Fail(new PermanentDeliveryError("Message cannot be empty."));
        }

        try
        {
            await _telegramSendPipeline.ExecuteAsync(
                async ct => await _telegramBotClient.SendMessage(chatId, message, cancellationToken: ct),
                cancellationToken);

            _logger.LogInformation("Telegram message sent to chatId {ChatId}", chatId);
            return Result.Ok();
        }
        catch (Exception ex) when (TelegramSendResiliencePipeline.IsPermanent(ex))
        {
            _logger.LogWarning(ex, "Permanently undeliverable to chatId {ChatId}: {Message}", chatId, ex.Message);
            return Result.Fail(new PermanentDeliveryError(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Telegram message to chatId {ChatId} after retries", chatId);
            return Result.Fail(new TransientDeliveryError(ex.Message));
        }
    }
}
