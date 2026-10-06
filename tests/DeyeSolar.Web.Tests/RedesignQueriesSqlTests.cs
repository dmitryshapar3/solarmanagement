using System.Security.Claims;
using DeyeSolar.Domain.Models;
using DeyeSolar.Domain.Options;
using DeyeSolar.Domain.Services;
using DeyeSolar.Web.Auth;
using DeyeSolar.Web.Data;
using DeyeSolar.Web.Integrations;
using DeyeSolar.Web.Redesign;
using DeyeSolar.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeyeSolar.Web.Tests;

public sealed class RedesignQueriesSqlTests
{
    private static readonly DateTimeOffset Now = new(2026,10,6,12,0,0,TimeSpan.Zero);
    private sealed class Clock:TimeProvider { public override DateTimeOffset GetUtcNow()=>Now; }
    private sealed class Factory(DbContextOptions<DeyeSolarDbContext> options,string tenant):IDbContextFactory<DeyeSolarDbContext>
    {
        public DeyeSolarDbContext CreateDbContext()=>new(options,tenant);
        public Task<DeyeSolarDbContext> CreateDbContextAsync(CancellationToken ct=default)=>Task.FromResult(CreateDbContext());
    }
    private sealed class Owner:IInstallationAccessAuthorizer
    {
        public Task<InstallationMembership> CheckAsync(ClaimsPrincipal actor,string installationId,InstallationPermission permission,CancellationToken ct=default)
            =>Task.FromResult(new InstallationMembership {InstallationId=installationId,UserId="owner",Role="Owner"});
    }
    private static InteractiveSecurityContext Security(string tenant)
    {
        var current=new CurrentInstallation();current.BindOnce(tenant);
        return new(new Owner(),current,new HttpContextAccessor {HttpContext=new DefaultHttpContext()});
    }
    private static RedesignQueries Queries(Factory factory,string tenant,SolarManagement.Integrations.Contracts.IIntegrationProviderCatalog? catalog=null)
    {
        var settings=new AppSettingsService(factory,new ConfigurationBuilder().Build());var devices=new DeviceStatusSnapshot();
        return new(factory,new RuleRepository(factory),new Clock(),Security(tenant),devices,
            new DeviceNameService(new AppSettingsDeviceLabelStore(settings,settings),devices),null!,catalog);
    }
    private static Task<SqlServerTestDatabase> Database()=>SqlServerTestDatabase.CreateAsync("RedesignQueries",SqlTestSchema.Model,seed:async db=>
    {db.Installations.AddRange(new Installation {Id="a"},new Installation {Id="b"});await db.SaveChangesAsync();});

    private sealed class Catalog(string digest):SolarManagement.Integrations.Contracts.IIntegrationProviderCatalog
    {
        public Task<SolarManagement.Integrations.Contracts.IntegrationProviderDescriptor> GetAsync(string id,string? version,CancellationToken ct)=>Task.FromResult(new SolarManagement.Integrations.Contracts.IntegrationProviderDescriptor(id,version!,digest,"descriptor","Trusted human provider name",1,1,[],[],[]));
        public Task<IReadOnlyList<SolarManagement.Integrations.Contracts.IntegrationProviderDescriptor>> GetProvidersAsync(CancellationToken ct)=>throw new NotSupportedException();
    }
    [SqlServerFact]
    public async Task ActivityActorDisplayNamesOnlyResolveReturnedCurrentInstallationMembers()
    {
        await using var database=await Database();var factory=new Factory(database.Options,"a");
        await using(var db=factory.CreateDbContext())
        {
            db.Users.AddRange(new IdentityUser {Id="member-a",UserName="a"},new IdentityUser {Id="member-b",UserName="b"});
            db.InstallationMemberships.AddRange(new InstallationMembership {InstallationId="a",UserId="member-a",Role="Owner"},new InstallationMembership {InstallationId="b",UserId="member-b",Role="Owner"});
            db.UserClaims.AddRange(new IdentityUserClaim<string> {UserId="member-a",ClaimType=AccountManagementService.DisplayNameClaim,ClaimValue="Alice Solar"},new IdentityUserClaim<string> {UserId="member-b",ClaimType=AccountManagementService.DisplayNameClaim,ClaimValue="Private neighbour name"});
            db.ActivityEvents.AddRange(new ActivityEvent {Kind="command.manual",ActorUserId="member-a",OccurredAt=Now.AddMinutes(-1).UtcDateTime},new ActivityEvent {Kind="command.manual",ActorUserId="member-b",OccurredAt=Now.AddMinutes(-2).UtcDateTime});
            await db.SaveChangesAsync();
        }
        var result=await Queries(factory,"a").ActivityAsync();
        Assert.Equal("Alice Solar",Assert.Single(result.Items.Where(item=>item.ActorUserId=="member-a")).ActorDisplayName);
        Assert.Null(Assert.Single(result.Items.Where(item=>item.ActorUserId=="member-b")).ActorDisplayName);
        var neighbour=await Queries(new Factory(database.Options,"b"),"b").ActivityAsync();Assert.Empty(neighbour.Items);
    }
    [SqlServerFact]
    public async Task DeviceProviderNameRequiresExactTrustedPackagePinAndRemainsTenantScoped()
    {
        await using var database=await Database();var factory=new Factory(database.Options,"a");var instance=Guid.NewGuid();var device=Guid.NewGuid();
        await using(var db=factory.CreateDbContext())
        {
            db.IntegrationInstances.Add(new(){Id=instance,ProviderId="opaque.provider.id",PackageVersion="1.0.0",PackageDigest="package",DescriptorDigest="descriptor",Name="My connection"});
            db.IntegrationDeviceBindings.Add(new(){Id=device,InstanceId=instance,Kind="socket",RemoteId="remote",Name="My device"});await db.SaveChangesAsync();
        }
        var known=await Queries(factory,"a",new Catalog("package")).DeviceAsync(device.ToString("D"));Assert.Equal("Trusted human provider name",known!.ProviderDisplayName);Assert.Equal("opaque.provider.id",known.ProviderId);
        Assert.Null((await Queries(factory,"a",new Catalog("other-version")).DeviceAsync(device.ToString("D")))!.ProviderDisplayName);
        Assert.Null((await Queries(factory,"a").DeviceAsync(device.ToString("D")))!.ProviderDisplayName);
        Assert.Null(await Queries(new Factory(database.Options,"b"),"b",new Catalog("package")).DeviceAsync(device.ToString("D")));
    }
    [SqlServerFact]
    public async Task ReadingsProviderRequiresTheDisplayedInverterAndAnExactTrustedPin()
    {
        await using var database=await Database();var factory=new Factory(database.Options,"a");var instance=Guid.NewGuid();var inverter=Guid.NewGuid();
        await using(var db=factory.CreateDbContext())
        {
            db.IntegrationInstances.Add(new(){Id=instance,ProviderId="opaque.provider.id",PackageVersion="1.0.0",PackageDigest="package",DescriptorDigest="descriptor",Name="My connection",State="enabled"});
            db.IntegrationDeviceBindings.Add(new(){Id=inverter,InstanceId=instance,Kind="inverter",RemoteId="remote",Name="My inverter",Enabled=true});await db.SaveChangesAsync();
        }
        Assert.Equal("Trusted human provider name",await Queries(factory,"a",new Catalog("package")).ReadingsProviderAsync(inverter));
        Assert.Null(await Queries(factory,"a",new Catalog("wrong-pin")).ReadingsProviderAsync(inverter));
        Assert.Null(await Queries(new Factory(database.Options,"b"),"b",new Catalog("package")).ReadingsProviderAsync(inverter));
        Assert.Null(await Queries(factory,"a",new Catalog("package")).ReadingsProviderAsync(Guid.NewGuid()));
    }
    [SqlServerFact]
    public async Task FlowThresholdsFollowExplicitSocketAndDefaultSourcesAndNeverOtherTenants()
    {
        await using var database=await Database();var factory=new Factory(database.Options,"a");var instance=Guid.NewGuid();var primary=Guid.NewGuid();var other=Guid.NewGuid();var socket=Guid.NewGuid();
        await using(var db=factory.CreateDbContext())
        {
            db.IntegrationInstances.Add(new(){Id=instance,ProviderId="provider",PackageVersion="1.0.0",Name="Connection",State="enabled"});
            db.IntegrationDeviceBindings.AddRange(new(){Id=primary,InstanceId=instance,Kind="inverter",RemoteId="primary",Name="Primary",Enabled=true,IsDefault=true},new(){Id=other,InstanceId=instance,Kind="inverter",RemoteId="other",Name="Other",Enabled=true},new(){Id=socket,InstanceId=instance,Kind="socket",RemoteId="plug",Name="Plug",Enabled=true,MetadataJson="{}"});
            await db.SaveChangesAsync();
            var binding=await db.IntegrationDeviceBindings.SingleAsync(b=>b.Id==socket);binding.MetadataJson=IntegrationSocketAssociation.Write(binding,other,1);await db.SaveChangesAsync();
        }
        TriggerRule[] rules=[new(){Id=1,Enabled=true,SourceInverterId=primary},new(){Id=2,Enabled=true,EntityId="legacy-plug"},new(){Id=3,Enabled=true,EntityId=socket.ToString("D")},new(){Id=4,Enabled=false,SourceInverterId=primary},new(){Id=5,Enabled=true,SourceInverterId=Guid.NewGuid()}];
        Assert.Equal(new[]{1,2},(await Queries(factory,"a").FlowRulesAsync(rules,primary)).Select(rule=>rule.Id));
        Assert.Equal(3,Assert.Single(await Queries(factory,"a").FlowRulesAsync(rules,other)).Id);
        Assert.Empty(await Queries(new Factory(database.Options,"b"),"b").FlowRulesAsync(rules,primary));
        Assert.Empty(await Queries(factory,"a").FlowRulesAsync(rules,null));
    }
    [SqlServerFact]
    public async Task FiveMinuteAveragesKeepInvalidMetricsNullAndNeverMixSourceEpochsOrTenants()
    {
        await using var database=await Database();var factory=new Factory(database.Options,"a");var source=Guid.NewGuid();
        await using(var db=factory.CreateDbContext())
        {
            db.Readings.AddRange(
                new Reading {Timestamp=Now.AddMinutes(-3).UtcDateTime,InverterId=source,RuntimeGeneration=1,SolarProduction=0,SolarPowerValid=true,BatterySoc=99,BatterySocValid=false},
                new Reading {Timestamp=Now.AddMinutes(-2).UtcDateTime,InverterId=source,RuntimeGeneration=1,SolarProduction=1000,SolarPowerValid=true,BatterySoc=0,BatterySocValid=true},
                new Reading {Timestamp=Now.AddMinutes(-1).UtcDateTime,InverterId=source,RuntimeGeneration=2,SolarProduction=9000,SolarPowerValid=false,BatterySoc=90,BatterySocValid=true});
            await db.SaveChangesAsync();
        }
        await using(var db=new Factory(database.Options,"b").CreateDbContext())
        {db.Readings.Add(new Reading {Timestamp=Now.AddMinutes(-1).UtcDateTime,SolarProduction=100000,SolarPowerValid=true});await db.SaveChangesAsync();}
        var result=await Queries(factory,"a").ReadingsAsync(1,"5m");
        Assert.Equal(2,result.Items.Count);
        var first=Assert.Single(result.Items.Where(r=>r.RuntimeGeneration==1));Assert.Equal(500,first.SolarProduction);Assert.Equal(0,first.BatterySoc);
        Assert.Null(Assert.Single(result.Items.Where(r=>r.RuntimeGeneration==2)).SolarProduction);
        Assert.Contains(result.Gaps,g=>g.ReasonCode=="source_changed");Assert.True(result.Partial);
        var raw=await Queries(factory,"a").ReadingsAsync(1);Assert.Null(raw.Items.Last().BatterySoc);Assert.Equal(0,raw.Items.Last().SolarProduction);
    }
    [SqlServerFact]
    public async Task ReadingsCursorFreezesWindowAndSnapshotWhileNewRowsArrive()
    {
        await using var database=await Database();var factory=new Factory(database.Options,"a");
        await using(var db=factory.CreateDbContext())
        {db.Readings.AddRange(Enumerable.Range(0,510).Select(i=>new Reading {Timestamp=Now.AddSeconds(-i-1).UtcDateTime,BatterySocValid=true,SolarPowerValid=true}));await db.SaveChangesAsync();}
        var queries=Queries(factory,"a");var first=await queries.ReadingsAsync(1);Assert.Equal(500,first.Items.Count);Assert.NotNull(first.NextCursor);
        await using(var db=factory.CreateDbContext()) {db.Readings.Add(new Reading {Timestamp=Now.AddSeconds(-505).UtcDateTime});await db.SaveChangesAsync();}
        var next=await queries.ReadingsAsync(1,"raw",first.NextCursor);Assert.Equal(10,next.Items.Count);Assert.Null(next.NextCursor);
        Assert.Empty(first.Items.Select(r=>r.Id).Intersect(next.Items.Select(r=>r.Id)));Assert.Equal(first.Start,next.Start);Assert.Equal(first.End,next.End);
        await Assert.ThrowsAsync<ArgumentException>(()=>queries.ReadingsAsync(6,"raw",first.NextCursor));
    }
    [SqlServerFact]
    public async Task ActivityGroupsActualChecksWhileSummaryIncludesAllPagesAndTenantScope()
    {
        await using var database=await Database();var factory=new Factory(database.Options,"a");
        long group;
        await using(var db=factory.CreateDbContext())
        {
            var first=new ActivityEvent {Kind="rule.checked",RuleId=42,ReasonCode="SocBelowTurnOnThreshold",OccurredAt=Now.AddMinutes(-5).UtcDateTime,BatterySoc=50};
            db.ActivityEvents.Add(first);await db.SaveChangesAsync();group=first.Id;
            db.ActivityEvents.Add(new ActivityEvent {Kind="rule.checked",RuleId=42,GroupId=group,ReasonCode=first.ReasonCode,OccurredAt=Now.AddMinutes(-4).UtcDateTime,BatterySoc=52});
            db.ActivityEvents.AddRange(Enumerable.Range(0,101).Select(i=>new ActivityEvent {Kind="command.result",ReasonCode="acknowledged",ActorUserId="owner",OccurredAt=Now.AddSeconds(-i-1).UtcDateTime}));await db.SaveChangesAsync();
        }
        await using(var db=new Factory(database.Options,"b").CreateDbContext())
        {db.ActivityEvents.Add(new ActivityEvent {Kind="command.result",ReasonCode="acknowledged",OccurredAt=Now.AddMinutes(-1).UtcDateTime});await db.SaveChangesAsync();}
        var queries=Queries(factory,"a");var feed=await queries.ActivityAsync();Assert.Equal(100,feed.Items.Count);Assert.Equal(101,feed.Summary.Switches);Assert.Equal(101,feed.Summary.ConfirmedCommands);Assert.NotNull(feed.NextCursor);
        var older=await queries.ActivityAsync(cursor:feed.NextCursor);var grouped=Assert.Single(older.Items.Where(r=>r.Id==group));Assert.Equal(2,grouped.CheckCount);Assert.Equal(50,grouped.SocMin);Assert.Equal(52,grouped.SocMax);
        Assert.Equal(feed.Summary,older.Summary);Assert.Null(feed.Summary.OnSeconds);Assert.True(feed.Summary.Partial);
        Assert.Equal(2,(await queries.ChecksAsync(group)).Items.Count);Assert.Empty((await Queries(new Factory(database.Options,"b"),"b").ChecksAsync(group)).Items);
    }
    [SqlServerFact]
    public async Task InstallationFormPreservesSecretsAndAdvancedOptionsAndRejectsStaleSaveAtomically()
    {
        await using var database=await Database();var factory=new Factory(database.Options,"a");
        await using(var db=factory.CreateDbContext())
        {db.AppSettings.AddRange(new AppSetting {Section="SolarEstimate",Key="ApiKey",Value="synthetic-keep"},new AppSetting {Section="SolarEstimate",Key="InverterEfficiency",Value="0.92"});await db.SaveChangesAsync();}
        var settings=new AppSettingsService(factory,new ConfigurationBuilder().Build());
        var service=new InstallationSettingsService(factory,settings,Security("a"),new IntegrationChangeNotifier(NullLogger<IntegrationChangeNotifier>.Instance));
        var current=await service.LoadAsync();var change=new InstallationSettingsChange(current.Site with {SolarEstimate=current.Site.SolarEstimate with {LocationLabel="My home"}},new(60),current.Display,null,current.Version);
        var saved=await service.SaveAsync(change);Assert.Equal(60,saved.Polling.IntervalSeconds);Assert.Equal("My home",saved.Site.SolarEstimate.LocationLabel);Assert.NotEqual(current.Version,saved.Version);
        await Assert.ThrowsAsync<IntegrationRequestException>(()=>service.SaveAsync(change with {Polling=new(120)}));
        await using var check=factory.CreateDbContext();Assert.Equal("synthetic-keep",(await check.AppSettings.SingleAsync(s=>s.Key=="ApiKey")).Value);Assert.Equal("0.92",(await check.AppSettings.SingleAsync(s=>s.Key=="InverterEfficiency")).Value);
        Assert.Equal(60,(await service.LoadAsync()).Polling.IntervalSeconds);
    }
    [SqlServerFact]
    public async Task PrimaryInverterChangeIsGuardedAtomicAndKeepsEncryptedConfigurationAndOtherTenants()
    {
        await using var database=await Database();var factory=new Factory(database.Options,"a");
        var oldInstance=Guid.NewGuid();var nextInstance=Guid.NewGuid();var oldBinding=Guid.NewGuid();var nextBinding=Guid.NewGuid();
        await using(var db=factory.CreateDbContext())
        {
            foreach(var id in new[]{oldInstance,nextInstance})
            {
                db.IntegrationInstances.Add(new(){Id=id,ProviderId="fixture",Name="fixture",State="enabled",PackageVersion="1.0.0",PackageDigest="digest",DescriptorDigest="descriptor"});
                db.IntegrationConfigurations.Add(new(){InstanceId=id,Revision=1,SecretsCiphertext="synthetic-encrypted-keep",ValuesJson="{}"});
            }
            db.IntegrationDeviceBindings.AddRange(new(){Id=oldBinding,InstanceId=oldInstance,Kind="inverter",IsDefault=true},new(){Id=nextBinding,InstanceId=nextInstance,Kind="inverter"});
            await db.SaveChangesAsync();
        }
        var settings=new AppSettingsService(factory,new ConfigurationBuilder().Build());
        var service=new InstallationSettingsService(factory,settings,Security("a"),new IntegrationChangeNotifier(NullLogger<IntegrationChangeNotifier>.Instance));
        var current=await service.LoadAsync();
        var draft=new InstallationSettingsChange(current.Site,current.Polling with {IntervalSeconds=90},current.Display,nextBinding,current.Version);
        await Assert.ThrowsAsync<IntegrationRequestException>(()=>service.SaveAsync(draft));
        Assert.Equal(oldBinding,(await service.LoadAsync()).PrimaryInverterId);
        var saved=await service.SaveAsync(draft with {ExpectedIntegrationVersions=current.IntegrationVersions.ToDictionary(p=>p.Key,p=>p.Value)});
        Assert.Equal(nextBinding,saved.PrimaryInverterId);Assert.Equal(90,saved.Polling.IntervalSeconds);
        await using var check=factory.CreateDbContext();
        Assert.Equal(nextBinding,(await check.IntegrationDeviceBindings.SingleAsync(b=>b.IsDefault)).Id);
        Assert.All(await check.IntegrationInstances.ToListAsync(),i=>{Assert.Equal(2,i.Generation);Assert.Equal(1,i.Revision);});
        Assert.All(await check.IntegrationConfigurations.ToListAsync(),c=>Assert.Equal("synthetic-encrypted-keep",c.SecretsCiphertext));
        await Assert.ThrowsAsync<ArgumentException>(()=>service.SaveAsync(new(saved.Site,saved.Polling,saved.Display,Guid.NewGuid(),saved.Version,saved.IntegrationVersions.ToDictionary(p=>p.Key,p=>p.Value))));
        Assert.Equal(nextBinding,(await service.LoadAsync()).PrimaryInverterId);
    }

    [SqlServerFact]
    public async Task ActivitySummaryCountsADeviceWithoutObservationsAsUnknown()
    {
        await using var database=await Database();var factory=new Factory(database.Options,"a");var instance=Guid.NewGuid();var observed=Guid.NewGuid();var unseen=Guid.NewGuid();
        await using(var db=factory.CreateDbContext())
        {
            db.IntegrationInstances.Add(new(){Id=instance,ProviderId="fixture",State="enabled"});
            db.IntegrationDeviceBindings.AddRange(new(){Id=observed,InstanceId=instance,Kind="socket",RemoteId="observed"},new(){Id=unseen,InstanceId=instance,Kind="socket",RemoteId="unseen"});
            db.ActivityEvents.Add(new(){Kind="device.observed",DeviceId=observed.ToString("D"),State=true,OccurredAt=Now.AddMinutes(-6).UtcDateTime});
            await db.SaveChangesAsync();
        }
        var summary=(await Queries(factory,"a").ActivityAsync(Now.AddMinutes(-5),Now)).Summary;
        Assert.Equal(300,summary.KnownSeconds);Assert.Equal(300,summary.OnSeconds);Assert.Equal(600,summary.ExpectedSeconds);Assert.True(summary.Partial);
    }
}
