using ArbiScanner.TelegramNotifierApp.Abstractions.Interfaces.Services;
using ArbiScanner.TelegramNotifierApp.Infrastructure.DbContext;
using ArbiScannerWeb.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
namespace ArbiScanner.TelegramNotifierApp.Application.Services;
public class SpreadService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ITelegramNotifierUserService telegramNotifierUserService,
    ILogger<SpreadService> logger) : ISpreadService
{
    public readonly IDbContextFactory<AppDbContext> _dbContextFactory = dbContextFactory;
    private readonly ITelegramNotifierUserService _telegramNotifierUserService = telegramNotifierUserService;
    private readonly ILogger<SpreadService> _logger = logger;
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
                string fundingForLong = "";
                string fundingForShort = "";
                try
                {
                    if (possiblePosition.ExchangeLong.FundingRateValue.HasValue)
                    {
                        double longFunding = possiblePosition.ExchangeLong.FundingRateValue.Value * 100;
                        fundingForLong = $"\nFunding {possiblePosition.ExchangeLong.Exchange}:{longFunding:0.00}%";
                    }
                    if (possiblePosition.ExchangeShort.FundingRateValue.HasValue)
                    {
                        double shortFunding = possiblePosition.ExchangeShort.FundingRateValue.Value * 100;
                        fundingForShort = $"\nFunding {possiblePosition.ExchangeShort.Exchange}:{shortFunding:0.00}%";
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling funding rates: {Message}", ex.Message);
                }
                var message = $"Coin: {possiblePosition.ExchangeRateA.Symbol}\nSpread: {possiblePosition.Spread:0.00}%\nLong: {possiblePosition.ExchangeLong.Exchange}({possiblePosition.ExchangeLong.ExchangeRate}$)\nShort: {possiblePosition.ExchangeShort.Exchange}({possiblePosition.ExchangeShort.ExchangeRate}$)\nSlippage {possiblePosition.ExchangeLong.Exchange}: {possiblePosition.ExchangeLong.SlippageShort:0.00}%\nSlippage {possiblePosition.ExchangeShort.Exchange}: {possiblePosition.ExchangeShort.SlippageLong:0.00}%\n{fundingForLong}{fundingForShort}";
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
                string fundingForLong = "";
                string fundingForShort = "";
                try
                {
                    if (possiblePosition.ExchangeLong.FundingRateValue.HasValue)
                    {
                        double longFunding = possiblePosition.ExchangeLong.FundingRateValue.Value * 100;
                        fundingForLong = $"\nFunding {possiblePosition.ExchangeLong.Exchange}:{longFunding:0.00}%";
                    }
                    if (possiblePosition.ExchangeShort.FundingRateValue.HasValue)
                    {
                        double shortFunding = possiblePosition.ExchangeShort.FundingRateValue.Value * 100;
                        fundingForShort = $"\nFunding {possiblePosition.ExchangeShort.Exchange}:{shortFunding:0.00}%";
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling funding rates: {Message}", ex.Message);
                }
                string message = $"Coin: {possiblePosition.ExchangeRateA.Symbol}\nFunding Spread: {possiblePosition.TotalFunding:0.00}%\nRate spread: {rateSpread:0.00}%\nLong: {possiblePosition.ExchangeLong.Exchange}({possiblePosition.ExchangeLong.ExchangeRate}$)\nShort: {possiblePosition.ExchangeShort.Exchange}({possiblePosition.ExchangeShort.ExchangeRate}$)\nSlippage {possiblePosition.ExchangeLong.Exchange}: {possiblePosition.ExchangeLong.SlippageShort:0.00}%\nSlippage {possiblePosition.ExchangeShort.Exchange}: {possiblePosition.ExchangeShort.SlippageLong:0.00}%{fundingForLong}{fundingForShort}";
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
                string fundingForShort = "";
                try
                {
                    if (possiblePosition.ExchangeShort.FundingRateValue.HasValue)
                    {
                        double shortFunding = possiblePosition.ExchangeShort.FundingRateValue.Value * 100;
                        fundingForShort = $"\nFunding:{shortFunding:0.00}%";
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling funding rates: {Message}", ex.Message);
                }
                string message = $"Coin: {possiblePosition.ExchangeRateA.Symbol}\nSpot Spread: {possiblePosition.Spread:0.00}%\n{possiblePosition.ExchangeLong.Exchange} Spot: ({possiblePosition.ExchangeLong.ExchangeRate}$)\n{possiblePosition.ExchangeShort.Exchange} Futures: ({possiblePosition.ExchangeShort.ExchangeRate}$)\nSlippage Spot: {possiblePosition.ExchangeLong.SlippageShort:0.00}%\nSlippage Futures: {possiblePosition.ExchangeShort.SlippageLong:0.00}%{fundingForShort}\nPossible Profit:{possiblePosition.PossibleProfit:0.00}%";
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