namespace ArbiScanner.TelegramNotifierApp.Domain.Settings;
public class TelegramSettings
{
    public string BotToken { get; set; } = default!;
    public string ChatId { get; set; } = default!;
}