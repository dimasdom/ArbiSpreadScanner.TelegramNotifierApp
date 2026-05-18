using ArbiScanner.TelegramNotifierApp.Abstractions.Interfaces.Services;
using ArbiScannerWeb.Domain.Models;
using FluentResults;
using Microsoft.EntityFrameworkCore;

namespace ArbiScanner.TelegramNotifierApp.Application.Services;
public class TelegramUserService : ITelegramUserService
{
    private readonly AppDbContext _dbContext;

    public TelegramUserService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Result> LinkTelegramUserWithAccount(string userName, long chatId, string linkId)
    {
        var linkGuid = Guid.Parse(linkId);
      TelegramLinkRequest? linkRequest = await _dbContext.TelegramLinkRequests.Where(r => r.Id == linkGuid).FirstOrDefaultAsync();

      if (linkRequest != null)
      {
          var userSettings = await _dbContext.UserSettings.Where(u => u.AccountId == linkRequest.AccountId).FirstOrDefaultAsync();
          if (userSettings != null)
          {
              userSettings.ChatId = chatId;
              userSettings.UserName = userName;
              userSettings.HaveAccess = true;
              _dbContext.UserSettings.Update(userSettings);
              await _dbContext.SaveChangesAsync();
          }
          else
          {
              return Result.Fail("No user found for the provided link code.");
          }
      }
        else
        {
            return Result.Fail("Invalid link code.");
        }
        return Result.Ok();
    }

    public async Task<Result> UnlinkTelegramUser(long chatId)
    {
        var userSettings = await _dbContext.UserSettings.Where(u => u.ChatId == chatId).FirstOrDefaultAsync();
        if (userSettings != null)
        {
            userSettings.ChatId = 0;
            userSettings.UserName = null;
            _dbContext.UserSettings.Update(userSettings);
            await _dbContext.SaveChangesAsync();
            return Result.Ok();
        }
        else
        {
            return Result.Fail("No user found for the provided chat ID.");
        }
    }

    public async Task<Result> UpdateTelegramUserActiveStatus(long chatId, bool active)
    {
        var userSettings = await _dbContext.UserSettings.Where(u => u.ChatId == chatId).FirstOrDefaultAsync();
        if (userSettings != null)
        {
            userSettings.Active = active;
            _dbContext.UserSettings.Update(userSettings);
            await _dbContext.SaveChangesAsync();
            return Result.Ok();
        }
        else
        {
            return Result.Fail("No user found for the provided chat ID.");
        }
    }
}