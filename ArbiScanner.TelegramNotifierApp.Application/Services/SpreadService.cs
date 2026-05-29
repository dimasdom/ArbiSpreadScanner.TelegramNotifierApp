using ArbiScanner.TelegramNotifierApp.Abstractions.Interfaces.Services;
using ArbiScannerWeb.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
namespace ArbiScanner.TelegramNotifierApp.Application.Services;
public class SpreadService : ISpreadService
{
    public readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ITelegramNotifierUserService _telegramNotifierUserService;
    private readonly ILogger<SpreadService> _logger;
    public SpreadService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ITelegramNotifierUserService telegramNotifierUserService,
        ILogger<SpreadService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _telegramNotifierUserService = telegramNotifierUserService;
    }

    public async Task HandleNewSpread(TradeOpportunityModel tradeOpportunityModel)
    {
        try
            {
                using var context = _dbContextFactory.CreateDbContext();
                var usersWhoMatchCriteria = context.UserSettings
                .AsNoTracking()
                .Include(x => x.Exchanges)
                    .ThenInclude(ue => ue.Exchange)
                .Where(x => 
                    x.Exchanges.Any(ue => tradeOpportunityModel.ExchangeRateA.Exchange.StartsWith(ue.Exchange.Name)) &&
                    x.Exchanges.Any(ue => tradeOpportunityModel.ExchangeRateB.Exchange.StartsWith(ue.Exchange.Name)) &&
                    x.SpreadSize <= Math.Abs(tradeOpportunityModel.StartSpread) &&
                x.Active &&
                tradeOpportunityModel.ExchangeRateA.VolumeAsk > (x.PositionSize * 3) &&
                tradeOpportunityModel.ExchangeRateA.VolumeBid > (x.PositionSize * 3) &&
                tradeOpportunityModel.ExchangeRateB.VolumeAsk > (x.PositionSize * 3) &&
                tradeOpportunityModel.ExchangeRateB.VolumeBid > (x.PositionSize * 3)
                );
                string message = string.Empty;
                switch (tradeOpportunityModel.Type)
                {
                    case SpreadType.Futures:
                        {
                            usersWhoMatchCriteria = usersWhoMatchCriteria.Where(x => x.FuturesSpread);
                            message = MessageConstructorFutures(tradeOpportunityModel);
                        }
                        break;
                    case SpreadType.Funding:
                        {
                            usersWhoMatchCriteria = usersWhoMatchCriteria.Where(x => x.FundingSpread);
                            message = MessageConstructorFunding(tradeOpportunityModel);
                        }
                        break;
                    case SpreadType.Spot:
                        {
                            usersWhoMatchCriteria = usersWhoMatchCriteria.Where(x => x.SpotSpread);
                            message = MessageConstructorSpot(tradeOpportunityModel);
                        }
                        break;
                    default: return;
                }
                var usersChats = await usersWhoMatchCriteria.Select(x => x.ChatId).ToListAsync();
                var tasksSending = usersChats.Select(x => _telegramNotifierUserService.NotifyUser(x, message)).ToList();
                Task.WaitAll(tasksSending);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling new spread: {Message}", ex.Message);
            }
    }

        private string MessageConstructorFutures(TradeOpportunityModel possiblePosition)
        {
            try
            {
                string riskLevel = "";
                if (possiblePosition.Volatility > 0)
                {
                    riskLevel = $"Volatility(30m): {possiblePosition.Volatility.ToString("0.00")}%\nRisk Level - ";
                    double volatilityRatio = Math.Abs(possiblePosition.Volatility / possiblePosition.Spread * 100);

                    if (volatilityRatio <= 15)
                        riskLevel += "Safe";
                    else if (volatilityRatio <= 30)
                        riskLevel += "Medium";
                    else if (volatilityRatio <= 50)
                        riskLevel += "Risky";
                    else
                        riskLevel += "Dangerous";
                }
                string fundingForLong = "";
                string fundingForShort = "";
                try
                {
                    if (possiblePosition.ExchangeLong.FundingRateValue.HasValue)
                    {
                        double longFunding = possiblePosition.ExchangeLong.FundingRateValue.Value * 100;
                        fundingForLong = $"\nFunding {possiblePosition.ExchangeLong.Exchange}:{longFunding.ToString("0.00")}%";
                    }
                    if (possiblePosition.ExchangeShort.FundingRateValue.HasValue)
                    {
                        double shortFunding = possiblePosition.ExchangeShort.FundingRateValue.Value * 100;
                        fundingForShort = $"\nFunding {possiblePosition.ExchangeShort.Exchange}:{shortFunding.ToString("0.00")}%";
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling funding rates: {Message}", ex.Message);
                }
                var message = $"Coin: {possiblePosition.ExchangeRateA.Symbol}\nSpread: {possiblePosition.Spread.ToString("0.00")}%\nLong: {possiblePosition.ExchangeLong.Exchange}({possiblePosition.ExchangeLong.ExchangeRate}$)\nShort: {possiblePosition.ExchangeShort.Exchange}({possiblePosition.ExchangeShort.ExchangeRate}$)\nSlippage {possiblePosition.ExchangeLong.Exchange}: {possiblePosition.ExchangeLong.SlippageShort.ToString("0.00")}%\nSlippage {possiblePosition.ExchangeShort.Exchange}: {possiblePosition.ExchangeShort.SlippageLong.ToString("0.00")}%\n{riskLevel}{fundingForLong}{fundingForShort}";
                return message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error constructing message for possible position: {Message}", ex.Message);
            }
            return string.Empty;
        }
        private string MessageConstructorFunding(TradeOpportunityModel possiblePosition)
        {
            var rateSpread = CalculateSpreadFor(possiblePosition.ExchangeRateA.ExchangeRate, possiblePosition.ExchangeRateB.ExchangeRate);
            try
            {
                string riskLevel = "";
                if (possiblePosition.Volatility > 0)
                {
                    riskLevel = $"\nVolatility(30m): {possiblePosition.Volatility.ToString("0.00")}%\nRisk Level - ";
                    double volatilityRatio = Math.Abs(possiblePosition.Volatility / possiblePosition.Spread * 100);

                    if (volatilityRatio <= 15)
                        riskLevel += "Safe";
                    else if (volatilityRatio <= 30)
                        riskLevel += "Medium";
                    else if (volatilityRatio <= 50)
                        riskLevel += "Risky";
                    else
                        riskLevel += "Dangerous";
                }
                string fundingForLong = "";
                string fundingForShort = "";
                try
                {
                    if (possiblePosition.ExchangeLong.FundingRateValue.HasValue)
                    {
                        double longFunding = possiblePosition.ExchangeLong.FundingRateValue.Value * 100;
                        fundingForLong = $"\nFunding {possiblePosition.ExchangeLong.Exchange}:{longFunding.ToString("0.00")}%";
                    }
                    if (possiblePosition.ExchangeShort.FundingRateValue.HasValue)
                    {
                        double shortFunding = possiblePosition.ExchangeShort.FundingRateValue.Value * 100;
                        fundingForShort = $"\nFunding {possiblePosition.ExchangeShort.Exchange}:{shortFunding.ToString("0.00")}%";
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling funding rates: {Message}", ex.Message);
                }
                string message = $"Coin: {possiblePosition.ExchangeRateA.Symbol}\nFunding Spread: {possiblePosition.TotalFunding.ToString("0.00")}%\nRate spread: {rateSpread.ToString("0.00")}%\nLong: {possiblePosition.ExchangeLong.Exchange}({possiblePosition.ExchangeLong.ExchangeRate}$)\nShort: {possiblePosition.ExchangeShort.Exchange}({possiblePosition.ExchangeShort.ExchangeRate}$)\nSlippage {possiblePosition.ExchangeLong.Exchange}: {possiblePosition.ExchangeLong.SlippageShort.ToString("0.00")}%\nSlippage {possiblePosition.ExchangeShort.Exchange}: {possiblePosition.ExchangeShort.SlippageLong.ToString("0.00")}%{riskLevel}{fundingForLong}{fundingForShort}\nPossible Profit:{possiblePosition.PossibleProfit.ToString("0.00")}%";
                return message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error constructing message for possible position: {Message}", ex.Message);
            }
            return string.Empty;
        }
        public string MessageConstructorSpot(TradeOpportunityModel possiblePosition)
        {
            try
            {
                string riskLevel = "";
                if (possiblePosition.Volatility > 0)
                {
                    riskLevel = $"\nVolatility(30m): {possiblePosition.Volatility.ToString("0.00")}%\nRisk Level - ";
                    double volatilityRatio = Math.Abs(possiblePosition.Volatility / possiblePosition.Spread * 100);

                    if (volatilityRatio <= 15)
                        riskLevel += "Safe";
                    else if (volatilityRatio <= 30)
                        riskLevel += "Medium";
                    else if (volatilityRatio <= 50)
                        riskLevel += "Risky";
                    else
                        riskLevel += "Dangerous";
                }
                string fundingForShort = "";
                try
                {
                    if (possiblePosition.ExchangeShort.FundingRateValue.HasValue)
                    {
                        double shortFunding = possiblePosition.ExchangeShort.FundingRateValue.Value * 100;
                        fundingForShort = $"\nFunding:{shortFunding.ToString("0.00")}%";
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling funding rates: {Message}", ex.Message);
                }
                string message = $"Coin: {possiblePosition.ExchangeRateA.Symbol}\nSpot Spread: {possiblePosition.Spread.ToString("0.00")}%\n{possiblePosition.ExchangeLong.Exchange} Spot: ({possiblePosition.ExchangeLong.ExchangeRate}$)\n{possiblePosition.ExchangeShort.Exchange} Futures: ({possiblePosition.ExchangeShort.ExchangeRate}$)\nSlippage Spot: {possiblePosition.ExchangeLong.SlippageShort.ToString("0.00")}%\nSlippage Futures: {possiblePosition.ExchangeShort.SlippageLong.ToString("0.00")}%{riskLevel}{fundingForShort}\nPossible Profit:{possiblePosition.PossibleProfit.ToString("0.00")}%";
                return message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error constructing message for possible position: {Message}", ex.Message);
            }
            return String.Empty;
        }
        public static double CalculateSpreadFor(double priceA, double priceB)
        {
            return (priceA - priceB) / priceB * 100;
        }

    public Task HandleCloseSpread(TradeOpportunityModel tradeOpportunityModel)
    {
        Console.WriteLine($"Closed spread: {tradeOpportunityModel.Guid}");
        return Task.CompletedTask;
    }
}