using FluentResults;

namespace ArbiScanner.TelegramNotifierApp.Abstractions.Interfaces.Services;

public interface ITelegramNotifierUserService
{
    Task<Result> NotifyUser(long chatId, string message, CancellationToken cancellationToken = default);
}
