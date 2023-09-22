using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.HostedServices;
using BTCPayServer.Plugins.LNbank.Data.Models;
using BTCPayServer.Plugins.LNbank.Services.Wallets;
using LNURL;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.Altcoins.Elements;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Transaction = BTCPayServer.Plugins.LNbank.Data.Models.Transaction;

namespace BTCPayServer.Plugins.LNbank.Services;

public class BoltCardService : EventHostedServiceBase
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly LNbankPluginDbContextFactory _dbContextFactory;
    private readonly IMemoryCache _memoryCache;
    private readonly WalletService _walletService;

    public BoltCardService(
        ISettingsRepository settingsRepository,
        EventAggregator eventAggregator,
        ILogger<BoltCardService> logger,
        LNbankPluginDbContextFactory dbContextFactory,
        IMemoryCache memoryCache,
        WalletService walletService) : base(eventAggregator, logger)
    {
        _settingsRepository = settingsRepository;
        _dbContextFactory = dbContextFactory;
        _memoryCache = memoryCache;
        _walletService = walletService;
    }

    
    private readonly SemaphoreSlim _settingsSemaphore = new(1, 1);
    private async Task<BoltCardSettings> GetSettings()
    {
        await _settingsSemaphore.WaitAsync();
        var settings = await _settingsRepository.GetSettingAsync<BoltCardSettings>(nameof(BoltCardSettings));
        settings ??= new BoltCardSettings();
        if (settings.MasterSeed is null)
        {
            settings.MasterSeed = Convert.ToHexString(NBitcoin.RandomUtils.GetBytes(64));
            settings.LastIndexUsed = 0;
            settings.GroupSize = await ComputeGroupSize();
            
            
            await _settingsRepository.UpdateSetting(settings, nameof(BoltCardSettings));
        }
        _settingsSemaphore.Release();

        return settings;
    }

    private async Task<int> ComputeGroupSize()
    {
        int OneSecondOfCompute()
        {
            var sw = new Stopwatch();
            sw.Start();
            var attempts = 0;
            while (sw.Elapsed.Seconds < 1)
            {
                try
                {

                    var key1 = RandomUtils.GetBytes(16);
                    var result1 = BoltCardHelper.ExtractBoltCardFromRequest(
                        new Uri("https://test.com?p=4E2E289D945A66BB13377A728884E867&c=E19CCB1FED8892CE"),
                        key1, out var _);
                }
                catch (Exception e)
                {
                }

                attempts++;
            }

            return attempts;
        }

        return (int) (await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(OneSecondOfCompute)))).Average();
    }

    public async Task<string> GetWipeContent(int index, string cardIdentifier)
    {
        var settings = await GetSettings();
        var slip21Node = settings.Slip21Node();
        return JObject.FromObject(new
        {
            version = 1,
            action = "wipe",
            k0 = Convert.ToHexString(
                slip21Node.DeriveChild(index + "k0").Key.ToBytes().Take(16).ToArray()),
            k1 = Convert.ToHexString(
                slip21Node.DeriveChild(index + "k1").Key.ToBytes().Take(16).ToArray()),
            k2 = Convert.ToHexString(
                slip21Node.DeriveChild(index + "k2").Key.ToBytes().Take(16).ToArray()),
            k3 = Convert.ToHexString(
                slip21Node.DeriveChild(index + "k3").Key.ToBytes().Take(16).ToArray()),
            k4 = Convert.ToHexString(
                slip21Node.DeriveChild(index + "k4").Key.ToBytes().Take(16).ToArray())
        }).ToString();

    }

    record IncrementDerivationIndexEvt(TaskCompletionSource<int> tcs);
    
    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        // we use sequential processing of these to avoid race conditions such as two cards being issued with the same index or a counter check failing
        if (evt is IncrementDerivationIndexEvt incrementDerivationIndexEvt)
        {
            var settings = await GetSettings();
            
            settings.LastIndexUsed++;
            await _settingsRepository.UpdateSetting(settings, nameof(BoltCardSettings));
            incrementDerivationIndexEvt.tcs.SetResult(settings.LastIndexUsed);
        }
        await  base.ProcessEvent(evt, cancellationToken);
    }

    private async Task<int> IncrementDerivationIndex(BoltCardSettings settings)
    {
        var tcs = new TaskCompletionSource<int>();
        PushEvent(new IncrementDerivationIndexEvt(tcs));
        return await tcs.Task;
    }

    public async Task MarkForReactivation(string code)
    {
        await using var dbContext = _dbContextFactory.CreateContext();
        var card = await dbContext.BoltCards.FindAsync( code);
        if (card is null)
        {
            throw new Exception("Card not found");
        }
        card.Status = BoltCardStatus.PendingActivation;
        card.CardIdentifier = null;
        card.Counter = -1;
        await dbContext.SaveChangesAsync();
    }

    public async Task<string> CreateCard(string withdrawConfigId)
    {
        await using var dbContext = _dbContextFactory.CreateContext();
        var withdrawConfig = await dbContext.WithdrawConfigs.FindAsync(withdrawConfigId);
        if (withdrawConfig is null)
        {
            throw new Exception("Withdraw config not found");
        }

        var boltCard = new BoltCard()
        {
            Counter = -1,
            WithdrawConfigId = withdrawConfigId,
            Status = BoltCardStatus.PendingActivation
        };
        await dbContext.BoltCards.AddAsync(boltCard);
        await dbContext.SaveChangesAsync();
        return boltCard.BoltCardId;
    }

    public async Task<(BoltCard card, Slip21Node masterSeed, int group)> IssueCard(string activationCode)
    {
        await using var dbContext = _dbContextFactory.CreateContext();

        var card = await dbContext.BoltCards
            .Include(boltCard => boltCard.WithdrawConfig)
            .SingleOrDefaultAsync(boltCard => boltCard.BoltCardId == activationCode &&
                                              boltCard.Status == BoltCardStatus.PendingActivation);
        if (card is null)
            throw new Exception("Card not found or already activated");

        var settings = await GetSettings();
        card.Index ??= await IncrementDerivationIndex(settings);
        card.Status = BoltCardStatus.Active;
        
        await dbContext.SaveChangesAsync();
        var index = (int) card.Index;
        var groupSize = settings.GroupSize;
        var groupNumber = index / groupSize;
        return (card, settings.Slip21Node(), groupNumber);
    }
    

    private ConcurrentDictionary<int, SemaphoreSlim> _verificationSemaphores = new();

    
    public async Task<(BoltCard, string authorizationCode)> VerifyTap(string url, int group, CancellationToken cancellationToken)
    {
        var settings = await GetSettings();
        var slipNode = settings.Slip21Node();
        var lowerBound = group * settings.GroupSize;
        var upperBound = lowerBound + settings.GroupSize - 1;
        
        
        (string uid, uint counter, byte[] rawUid, byte[] rawCtr, byte[] c)? boltcardMatch = null;
        int i;
        for (i = lowerBound; i <= upperBound; i++)
        {
            var k1 = slipNode.DeriveChild(i + "k1").Key.ToBytes().Take(16)
                .ToArray();
            boltcardMatch =
                BoltCardHelper.ExtractBoltCardFromRequest(new Uri(url), k1, out var error);
            if (error is null && boltcardMatch is not null)
                break;
            cancellationToken.ThrowIfCancellationRequested();
        }
        
        if(boltcardMatch is null)
            throw new Exception("No matching card found");
        
        var semaphore = _verificationSemaphores.GetOrAdd(i, new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);  // Wait for the semaphore if it's locked by another task
        BoltCard matchedCard;
        try
        {
            await using var dbContext = _dbContextFactory.CreateContext();

            matchedCard = await dbContext.BoltCards.Where(card => card.Index == i).Include(card => card.WithdrawConfig).FirstOrDefaultAsync(cancellationToken: cancellationToken);

            if (matchedCard is null)
                throw new Exception("No matching card found", null);

            if(matchedCard.Status != BoltCardStatus.Active)
                throw new Exception("Card is not active", null);
            
            if(matchedCard.Counter >= boltcardMatch.Value.counter)
                throw new Exception("Counter is too low", null);

            matchedCard.CardIdentifier ??= boltcardMatch.Value.uid;
            if(matchedCard.CardIdentifier != boltcardMatch.Value.uid)
                throw new Exception("Card mismatch", null);
            
            var k2 =  slipNode.DeriveChild(i + "k2").Key.ToBytes().Take(16)
                .ToArray();

            if (!BoltCardHelper.CheckCmac(boltcardMatch.Value.rawUid, boltcardMatch.Value.rawCtr, k2,
                    boltcardMatch.Value.c, out var error2))
            {
                throw new Exception($"C invalid: {error2}", null);
            }
            matchedCard.Counter = (int)boltcardMatch.Value.counter;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
        
        var authorizationCode = Guid.NewGuid().ToString();
        _memoryCache.CreateEntry("BoltCardAuthorizationCode_" + authorizationCode).SetValue(matchedCard).AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
        return (matchedCard, authorizationCode);
    }
    public async Task<Transaction> HandleTapPayment(string authorizationCode, string paymentRequest)
    {
        var card = _memoryCache.Get<BoltCard>("BoltCardAuthorizationCode_" + authorizationCode);
        if (card is null)
            throw new Exception("Invalid authorization code");
        return await _walletService.Send(card.WithdrawConfig, paymentRequest);
    }
}

public class BoltCard
{
    
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public string BoltCardId { get; set; }
    public string CardIdentifier { get; set; }
    public int? Index { get; set; }
    public BoltCardStatus Status { get; set; }
    public string WithdrawConfigId { get; set; }
    public WithdrawConfig WithdrawConfig { get; set; }
    public long Counter { get; set; }

    public static void OnModelCreating(ModelBuilder builder)
    {
        builder
            .Entity<BoltCard>()
            .HasIndex(o => o.WithdrawConfigId);

        builder
            .Entity<BoltCard>()
            .HasOne(o => o.WithdrawConfig)
            .WithOne(w => w.BoltCard)
            .OnDelete(DeleteBehavior.Cascade);
        
        
    }
}

public enum BoltCardStatus
{
    PendingActivation,
    Active,
    Inactive
}