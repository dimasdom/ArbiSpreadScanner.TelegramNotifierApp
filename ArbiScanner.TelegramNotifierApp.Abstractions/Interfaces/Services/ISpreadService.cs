using ArbiScannerWeb.Domain.Models;

namespace ArbiScanner.TelegramNotifierApp.Abstractions.Interfaces.Services;

public interface ISpreadService
{
    Task HandleNewSpread(TradeOpportunityModel tradeOpportunityModel, CancellationToken cancellationToken = default);
    Task HandleCloseSpread(TradeOpportunityModel tradeOpportunityModel);
}
