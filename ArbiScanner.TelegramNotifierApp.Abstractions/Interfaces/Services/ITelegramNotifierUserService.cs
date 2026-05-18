namespace ArbiScanner.TelegramNotifierApp.Abstractions.Interfaces.Services;

public interface ITelegramNotifierUserService
{
    Task NotifyUser(long chatId, string message);
}
