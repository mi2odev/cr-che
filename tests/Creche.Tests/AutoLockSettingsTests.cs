using Creche.Application.Services;
using Creche.Application.Settings;
using Creche.Domain.Entities;
using Creche.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Creche.Tests;

public class AutoLockSettingsTests
{
    [Fact]
    public async Task Auto_lock_is_on_by_default()
    {
        using var host = await TestHost.CreateAsync();
        var settings = await host.Get<ISettingsService>().GetAsync();
        Assert.Equal(CrecheSettings.DefaultAutoLockMinutes, settings.AutoLockMinutes);
    }

    [Fact]
    public async Task Settings_saved_before_auto_lock_existed_get_the_default()
    {
        using var host = await TestHost.CreateAsync();
        await using (var db = host.Db())
        {
            db.Settings.RemoveRange(await db.Settings.ToListAsync());
            db.Settings.Add(new SettingEntry { Key = "creche", Value = """{"Name":"Old crèche","Currency":"DA"}""" });
            await db.SaveChangesAsync();
        }
        var service = host.Get<ISettingsService>();
        ((SettingsService)service).Invalidate();   // read again from the database, not the cache

        var settings = await service.GetAsync();
        Assert.Equal("Old crèche", settings.Name);
        Assert.Equal(CrecheSettings.DefaultAutoLockMinutes, settings.AutoLockMinutes);
    }

    [Theory]
    [InlineData(0, 0)]          // off
    [InlineData(-5, 0)]
    [InlineData(15, 15)]
    [InlineData(100000, CrecheSettings.MaxAutoLockMinutes)]
    public async Task Auto_lock_minutes_are_kept_in_range(int entered, int stored)
    {
        using var host = await TestHost.CreateAsync();
        await host.UpdateSettingsAsync(s => s.AutoLockMinutes = entered);
        var service = host.Get<ISettingsService>();
        ((SettingsService)service).Invalidate();   // read again from the database, not the cache
        Assert.Equal(stored, (await service.GetAsync()).AutoLockMinutes);
    }
}
