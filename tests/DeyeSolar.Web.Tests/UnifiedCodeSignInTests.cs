using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DeyeSolar.Web.Tests;

public sealed partial class AccountManagementTests
{
    private static UnifiedCodeSignIn SignIn(IServiceProvider services)=>new(
        services.GetRequiredService<OneTimeVerificationService>(),services.GetRequiredService<AccountIdentityService>(),
        services.GetRequiredService<UserManager<IdentityUser>>(),services.GetRequiredService<AuthProviderOptions>());

    [Fact]
    public async Task UnifiedCodeCreatesOnePrivateTrialAndExistingVerifiedIdentitySignsIntoIt()
    {
        await using var f=new Fixture();await f.InitAsync();using var scope=f.Services.CreateScope();var services=scope.ServiceProvider;var signin=SignIn(services);
        var existing=await services.GetRequiredService<AccountIdentityService>().RegisterAsync(new("email","known@example.test"),Password,default);
        var known=await signin.StartAsync("email","known@example.test",default);var unknown=await signin.StartAsync("email","new@example.test",default);
        Assert.Equal(known.VerificationId.Length,unknown.VerificationId.Length);Assert.Equal(known.RetryAfterSeconds,unknown.RetryAfterSeconds);
        Assert.Equal(existing.Id,(await signin.CompleteAsync(known.VerificationId,f.Delivery.Codes["known@example.test"],default)).Id);
        var created=await signin.CompleteAsync(unknown.VerificationId,f.Delivery.Codes["new@example.test"],default);
        Assert.True(created.EmailConfirmed);Assert.False(await services.GetRequiredService<UserManager<IdentityUser>>().HasPasswordAsync(created));
        await using var db=new DeyeSolarDbContext(f.Options);Assert.Equal(2,await db.Users.CountAsync());Assert.Equal(2,await db.BillingAccounts.CountAsync());Assert.Equal(2,await db.Installations.CountAsync());
        var owner=Assert.Single(await db.InstallationMemberships.Where(m=>m.UserId==created.Id).ToListAsync());Assert.Equal("Owner",owner.Role);
        var billing=await db.BillingAccounts.SingleAsync(b=>b.UserId==created.Id);Assert.NotEqual(Guid.Empty,billing.AppAccountToken);Assert.True(billing.TrialEndsAt>billing.TrialStartedAt);
        Assert.Equal("invalid_code",(await Assert.ThrowsAsync<AccountIdentityException>(()=>signin.CompleteAsync(unknown.VerificationId,f.Delivery.Codes["new@example.test"],default))).Code);
    }
    [Fact]
    public async Task ClosedRegistrationStillAllowsKnownSignInAndLeavesUnknownDestinationUncreated()
    {
        await using var f=new Fixture();await f.InitAsync();using var scope=f.Services.CreateScope();var services=scope.ServiceProvider;
        var existing=await services.GetRequiredService<AccountIdentityService>().RegisterAsync(new("email","known@example.test"),Password,default);
        var signin=new UnifiedCodeSignIn(services.GetRequiredService<OneTimeVerificationService>(),services.GetRequiredService<AccountIdentityService>(),
            services.GetRequiredService<UserManager<IdentityUser>>(),new AuthProviderOptions {RegistrationEnabled=false});
        var known=await signin.StartAsync("email","known@example.test",default);Assert.Equal(existing.Id,(await signin.CompleteAsync(known.VerificationId,f.Delivery.Codes["known@example.test"],default)).Id);
        var unknown=await signin.StartAsync("email","closed@example.test",default);
        Assert.Equal("registration_disabled",(await Assert.ThrowsAsync<AccountIdentityException>(()=>signin.CompleteAsync(unknown.VerificationId,f.Delivery.Codes["closed@example.test"],default))).Code);
        await using var db=new DeyeSolarDbContext(f.Options);Assert.Single(await db.Users.ToListAsync());Assert.Single(await db.BillingAccounts.ToListAsync());
    }
    [Fact]
    public async Task UnifiedCodeCannotMergeAnUnverifiedCollisionOrBypassLockout()
    {
        await using var f=new Fixture();await f.InitAsync();using var scope=f.Services.CreateScope();var services=scope.ServiceProvider;var users=services.GetRequiredService<UserManager<IdentityUser>>();
        var collision=new IdentityUser {UserName="unverified",Email="taken@example.test",EmailConfirmed=false};Assert.True((await users.CreateAsync(collision,Password)).Succeeded);
        var signin=SignIn(services);var start=await signin.StartAsync("email","taken@example.test",default);
        Assert.Equal("link_required",(await Assert.ThrowsAsync<AccountIdentityException>(()=>signin.CompleteAsync(start.VerificationId,f.Delivery.Codes["taken@example.test"],default))).Code);
        var locked=await services.GetRequiredService<AccountIdentityService>().RegisterAsync(new("email","locked@example.test"),Password,default);
        Assert.True((await users.SetLockoutEnabledAsync(locked,true)).Succeeded);Assert.True((await users.SetLockoutEndDateAsync(locked,DateTimeOffset.UtcNow.AddHours(1))).Succeeded);
        var challenge=await signin.StartAsync("email","locked@example.test",default);
        Assert.Equal("account_unavailable",(await Assert.ThrowsAsync<AccountIdentityException>(()=>signin.CompleteAsync(challenge.VerificationId,f.Delivery.Codes["locked@example.test"],default))).Code);
        await using var db=new DeyeSolarDbContext(f.Options);Assert.Equal(2,await db.Users.CountAsync());
    }
}
