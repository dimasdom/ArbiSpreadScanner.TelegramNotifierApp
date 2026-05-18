using FluentResults;

namespace ArbiScanner.TelegramNotifierApp.Abstractions.Interfaces.Services;

public interface ITelegramUserService
{
    Task<Result> LinkTelegramUserWithAccount(string userName, long chatId, string linkId);
    Task<Result> UnlinkTelegramUser(long chatId);
    Task<Result> UpdateTelegramUserActiveStatus(long chatId, bool active);
}
