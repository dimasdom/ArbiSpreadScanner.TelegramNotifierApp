using System.Collections.Concurrent;
using ArbiScanner.TelegramNotifierApp.Abstractions.Errors;
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
    // Caps how many Telegram sends this single spread notification has in flight at once.
    // The resilience pipeline's rate limiter already bounds total bot-wide throughput to
    // Telegram's flood-control limit; this bounds concurrent sockets/threads per batch so a
    // 10k-user match doesn't try to open 10k connections simultaneously.
    private const int MaxConcurrentNotifications = 20;

    public readonly IDbContextFactory<AppDbContext> _dbContextFactory = dbContextFactory;
    private readonly ITelegramNotifierUserService _telegramNotifierUserService = telegramNotifierUserService;
    private readonly ILogger<SpreadService> _logger = logger;
    public async Task HandleNewSpread(TradeOpportunityModel tradeOpportunityModel, CancellationToken cancellationToken = default)
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
        var usersChats = await usersWhoMatchCriteria.Select(x => x.ChatId).ToListAsync(cancellationToken);
        if (usersChats.Count == 0)
        {
            return;
        }

        var permanentlyFailedChatIds = new ConcurrentBag<long>();
        var transientFailureCount = 0;

        await Parallel.ForEachAsync(
            usersChats,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentNotifications, CancellationToken = cancellationToken },
            async (chatId, ct) =>
            {
                var result = await _telegramNotifierUserService.NotifyUser(chatId, message, ct);
                if (result.IsFailed)
                {
                    if (result.HasError<PermanentDeliveryError>())
                    {
                        permanentlyFailedChatIds.Add(chatId);
                    }
                    else
                    {
                        Interlocked.Increment(ref transientFailureCount);
                    }
                }
            });

        if (transientFailureCount > 0)
        {
            _logger.LogWarning(
                "Failed to deliver {FailedCount}/{TotalCount} notifications for spread {Guid} after retries",
                transientFailureCount, usersChats.Count, tradeOpportunityModel.Guid);
        }

        if (!permanentlyFailedChatIds.IsEmpty)
        {
            await DeactivateUnreachableUsersAsync(context, permanentlyFailedChatIds, cancellationToken);
        }
    }

    // Best-effort cleanup: a user Telegram confirmed as unreachable (bot blocked, chat gone)
    // gets flagged inactive so future spreads stop wasting a call and retry budget on them.
    // Failure here must never fail the whole batch - the notifications themselves already went
    // out (or definitively couldn't), this is just bookkeeping.
    private async Task DeactivateUnreachableUsersAsync(AppDbContext context, IReadOnlyCollection<long> chatIds, CancellationToken cancellationToken)
    {
        try
        {
            var usersToDeactivate = await context.UserSettings
                .Where(u => chatIds.Contains(u.ChatId) && u.Active)
                .ToListAsync(cancellationToken);

            if (usersToDeactivate.Count == 0)
            {
                return;
            }

            foreach (var user in usersToDeactivate)
            {
                user.Active = false;
            }

            await context.SaveChangesAsync(cancellationToken);
            _logger.LogWarning(
                "Deactivated {Count} user(s) after permanent Telegram delivery failures (bot blocked or chat not found)",
                usersToDeactivate.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deactivate users after permanent delivery failures");
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