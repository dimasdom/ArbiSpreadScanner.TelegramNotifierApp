using ArbiScanner.TelegramNotifierApp.Application.Services;
using ArbiScanner.TelegramNotifierApp.Infrastructure.DbContext;
using ArbiScannerWeb.Domain.Models;
using Xunit;

namespace ArbiScanner.TelegramNotifierApp.Tests;

public class TelegramUserServiceTests : IDisposable
{
    private readonly AppDbContext _db = DbContextFactory.CreateInMemory();
    private readonly TelegramUserService _sut;

    public TelegramUserServiceTests()
    {
        _sut = new TelegramUserService(_db);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task LinkTelegramUser_ValidLink_UpdatesUserAndReturnsOk()
    {
        var accountId = "acc-1";
        var linkId = Guid.NewGuid();
        _db.TelegramLinkRequests.Add(new TelegramLinkRequest { Id = linkId, AccountId = accountId });
        _db.UserSettings.Add(new UserSettingsModel { AccountId = accountId, ChatId = 0 });
        await _db.SaveChangesAsync();

        var result = await _sut.LinkTelegramUserWithAccount("john", 99999L, linkId.ToString());

        Assert.True(result.IsSuccess);
        var user = _db.UserSettings.Single(u => u.AccountId == accountId);
        Assert.Equal(99999L, user.ChatId);
        Assert.Equal("john", user.UserName);
        Assert.True(user.HaveAccess);
        Assert.Empty(_db.TelegramLinkRequests);
    }

    [Fact]
    public async Task LinkTelegramUser_InvalidLinkId_ReturnsFail()
    {
        var result = await _sut.LinkTelegramUserWithAccount("john", 1L, Guid.NewGuid().ToString());

        Assert.True(result.IsFailed);
    }

    [Fact]
    public async Task LinkTelegramUser_LinkExistsButNoUserSettings_ReturnsFail()
    {
        var linkId = Guid.NewGuid();
        _db.TelegramLinkRequests.Add(new TelegramLinkRequest { Id = linkId, AccountId = "ghost" });
        await _db.SaveChangesAsync();

        var result = await _sut.LinkTelegramUserWithAccount("x", 1L, linkId.ToString());

        Assert.True(result.IsFailed);
    }

    [Fact]
    public async Task UnlinkTelegramUser_ExistingUser_ClearsChatIdAndReturnsOk()
    {
        _db.UserSettings.Add(new UserSettingsModel { AccountId = "acc-2", ChatId = 777L, UserName = "jane" });
        await _db.SaveChangesAsync();

        var result = await _sut.UnlinkTelegramUser(777L);

        Assert.True(result.IsSuccess);
        var user = _db.UserSettings.Single(u => u.AccountId == "acc-2");
        Assert.Equal(0L, user.ChatId);
        Assert.Null(user.UserName);
    }

    [Fact]
    public async Task UnlinkTelegramUser_NonExistingUser_ReturnsFail()
    {
        var result = await _sut.UnlinkTelegramUser(99999L);
        Assert.True(result.IsFailed);
    }

    [Fact]
    public async Task UpdateActiveStatus_ExistingUser_UpdatesAndReturnsOk()
    {
        _db.UserSettings.Add(new UserSettingsModel { AccountId = "acc-3", ChatId = 555L, Active = false });
        await _db.SaveChangesAsync();

        var result = await _sut.UpdateTelegramUserActiveStatus(555L, true);

        Assert.True(result.IsSuccess);
        Assert.True(_db.UserSettings.Single(u => u.ChatId == 555L).Active);
    }

    [Fact]
    public async Task UpdateActiveStatus_NonExistingUser_ReturnsFail()
    {
        var result = await _sut.UpdateTelegramUserActiveStatus(99999L, true);
        Assert.True(result.IsFailed);
    }
}
