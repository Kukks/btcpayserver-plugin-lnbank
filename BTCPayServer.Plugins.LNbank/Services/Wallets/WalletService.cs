#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LNbank.Data.Models;
using BTCPayServer.Plugins.LNbank.Exceptions;
using BTCPayServer.Plugins.LNbank.Hubs;
using LNURL;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Transaction = BTCPayServer.Plugins.LNbank.Data.Models.Transaction;

namespace BTCPayServer.Plugins.LNbank.Services.Wallets;

public class WalletService
{
    public const float MaxFeePercentDefault = 3;
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(21);
    public static readonly TimeSpan ExpiryDefault = TimeSpan.FromDays(1);
    private readonly BTCPayService _btcpayService;
    private readonly LNbankPluginDbContextFactory _dbContextFactory;
    private readonly LNURLService _lnurlService;
    private readonly ILogger _logger;
    private readonly Network _network;
    private readonly IHubContext<TransactionHub> _transactionHub;
    private readonly WalletRepository _walletRepository;

    public WalletService(
        ILogger<WalletService> logger,
        IHubContext<TransactionHub> transactionHub,
        BTCPayService btcpayService,
        BTCPayNetworkProvider btcPayNetworkProvider,
        LNbankPluginDbContextFactory dbContextFactory,
        WalletRepository walletRepository,
        LNURLService lnurlService)
    {
        _logger = logger;
        _btcpayService = btcpayService;
        _transactionHub = transactionHub;
        _walletRepository = walletRepository;
        _dbContextFactory = dbContextFactory;
        _lnurlService = lnurlService;
        _network = btcPayNetworkProvider.GetNetwork<BTCPayNetwork>(BTCPayService.CryptoCode).NBitcoinNetwork;
    }

    public async Task<bool> IsPaid(string paymentHash)
    {
        var transaction = await _walletRepository.GetTransaction(new TransactionQuery
        {
            PaymentHash = paymentHash
        });
        if (transaction != null)
            return transaction.IsPaid;

        var payment = await _btcpayService.GetLightningPayment(paymentHash);
        return payment?.Status == LightningPaymentStatus.Complete;
    }

    public async Task<Transaction> Receive(Wallet wallet, CreateLightningInvoiceRequest req, string? memo = null,
        CancellationToken cancellationToken = default)
    {
        if (req.Amount < 0)
            throw new ArgumentException("Amount should be a non-negative value", nameof(req.Amount));

        var data = await _btcpayService.CreateLightningInvoice(req);

        await using var dbContext = _dbContextFactory.CreateContext();
        var bolt11 = ParsePaymentRequest(data.BOLT11);
        var entry = await dbContext.Transactions.AddAsync(
            new Transaction
            {
                WalletId = wallet.WalletId,
                InvoiceId = data.Id,
                Amount = data.Amount,
                ExpiresAt = data.ExpiresAt,
                PaymentRequest = data.BOLT11,
                PaymentHash = bolt11.PaymentHash?.ToString(),
                Description = memo
            }, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        return entry.Entity;
    }

    public async Task<Transaction> Send(Wallet wallet, string paymentRequest)
    {
        var bolt11 = ParsePaymentRequest(paymentRequest);
        return await Send(wallet, bolt11, bolt11.ShortDescription);
    }

    public async Task<Transaction> Send(WithdrawConfig withdrawConfig, string paymentRequest)
    {
        var bolt11 = ParsePaymentRequest(paymentRequest);
        var remaining = withdrawConfig.GetRemainingBalance();

        if (bolt11.MinimumAmount > remaining)
            throw new PaymentRequestValidationException($"Payment request amount ({bolt11.MinimumAmount.ToUnit(LightMoneyUnit.Satoshi)} sats) was more than the remaining limit ({remaining.ToUnit(LightMoneyUnit.Satoshi)} sats)");

        return await Send(withdrawConfig.Wallet, bolt11, bolt11.ShortDescription, withdrawConfigId: withdrawConfig.WithdrawConfigId);
    }

    public async Task<Transaction> Send(Wallet wallet, BOLT11PaymentRequest bolt11, string? description,
        LightMoney? explicitAmount = null, string? withdrawConfigId = null, float maxFeePercent = MaxFeePercentDefault, CancellationToken cancellationToken = default)
    {
        if (bolt11.ExpiryDate <= DateTimeOffset.UtcNow)
            throw new PaymentRequestValidationException($"Payment request already expired at {bolt11.ExpiryDate}.");

        // check balance
        var amount = bolt11.MinimumAmount == LightMoney.Zero ? explicitAmount : bolt11.MinimumAmount;
        if (amount == null)
            throw new ArgumentException("Amount must be defined.", nameof(amount));
        if (wallet.Balance < amount)
            throw new InsufficientBalanceException(
                $"Insufficient balance: {Sats(wallet.Balance)} — tried to send {Sats(amount)}.");

        // check if the invoice exists already
        var paymentRequest = bolt11.ToString();
        var receivingTransaction = await ValidatePaymentRequest(paymentRequest);
        var isInternal = !string.IsNullOrEmpty(receivingTransaction?.InvoiceId);

        var sendingTransaction = new Transaction
        {
            WalletId = wallet.WalletId,
            PaymentRequest = paymentRequest,
            PaymentHash = bolt11.PaymentHash?.ToString(),
            ExpiresAt = bolt11.ExpiryDate,
            Description = description,
            Amount = amount,
            AmountSettled = new LightMoney(amount.MilliSatoshi * -1),
            WithdrawConfigId = withdrawConfigId
        };

        return await (isInternal && receivingTransaction != null
            ? SendInternal(sendingTransaction, receivingTransaction, cancellationToken)
            : SendExternal(sendingTransaction, amount, wallet.Balance, maxFeePercent, cancellationToken));
    }

    private async Task<Transaction> SendInternal(Transaction sendingTransaction, Transaction receivingTransaction,
        CancellationToken cancellationToken = default)
    {
        var transaction = sendingTransaction;
        await using var dbContext = _dbContextFactory.CreateContext();
        var executionStrategy = dbContext.Database.CreateExecutionStrategy();
        var isSettled = false;

        await executionStrategy.ExecuteAsync(async () =>
        {
            await using IDbContextTransaction dbTransaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                var now = DateTimeOffset.UtcNow;

                var receiveEntry = dbContext.Entry(receivingTransaction);
                var sendingEntry = await dbContext.Transactions.AddAsync(sendingTransaction, cancellationToken);

                sendingEntry.Entity.SetSettled(sendingTransaction.Amount, sendingTransaction.AmountSettled, null, now, null);
                receiveEntry.Entity.SetSettled(sendingTransaction.Amount, sendingTransaction.Amount, null, now, null);
                receiveEntry.State = EntityState.Modified;
                await dbContext.SaveChangesAsync(cancellationToken);
                await dbTransaction.CommitAsync(cancellationToken);

                _logger.LogInformation("Settled transaction {TransactionId} internally. Paid by {SendingTransactionId}",
                    receivingTransaction.TransactionId, sendingTransaction.TransactionId);

                transaction = sendingEntry.Entity;
                isSettled = transaction.IsSettled;
            }
            catch (Exception)
            {
                await dbTransaction.RollbackAsync(cancellationToken);

                _logger.LogInformation("Settling transaction {TransactionId} internally failed",
                    receivingTransaction.TransactionId);

                throw;
            }
        });

        if (isSettled)
        {
            await BroadcastTransactionUpdate(sendingTransaction, Transaction.StatusSettled);
            await BroadcastTransactionUpdate(receivingTransaction, Transaction.StatusSettled);
        }

        return transaction;
    }

    private async Task<Transaction> SendExternal(Transaction sendingTransaction, LightMoney amount,
        LightMoney walletBalance, float maxFeePercent, CancellationToken cancellationToken = default)
    {
        // Account for fees
        var amountInSats = amount.ToUnit(LightMoneyUnit.Satoshi);
        var maxFeeAmount = LightMoney.Satoshis(amountInSats * (decimal)maxFeePercent / 100);
        var amountWithFee = amount + maxFeeAmount;
        if (walletBalance < amountWithFee)
        {
            // allow sweeping transaction if the amount is below threshold and empties the wallet
            if (amountInSats == walletBalance.ToUnit(LightMoneyUnit.Satoshi) && amountInSats < 10000)
            {
                amountWithFee = walletBalance;
                maxFeeAmount = LightMoney.Zero;
            }
            else
            {
                throw new InsufficientBalanceException(
                    $"Insufficient balance: {Sats(walletBalance)} — tried to send {Sats(amount)} and need to keep a fee reserve of {Millisats(maxFeeAmount)}.");
            }
        }

        await using var dbContext = _dbContextFactory.CreateContext();

        // Create preliminary transaction entry - if something fails afterwards, the LightningInvoiceWatcher will handle cleanup
        sendingTransaction.Amount = amount;
        sendingTransaction.AmountSettled = new LightMoney(amountWithFee.MilliSatoshi * -1);
        sendingTransaction.RoutingFee = maxFeeAmount;
        sendingTransaction.ExplicitStatus = Transaction.StatusPending;
        var sendingEntry = await dbContext.Transactions.AddAsync(sendingTransaction, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            // Pass explicit amount only for zero amount invoices, because the implementations might throw an exception otherwise
            var bolt11 = ParsePaymentRequest(sendingTransaction.PaymentRequest);
            var request = new PayLightningInvoiceRequest
            {
                BOLT11 = sendingTransaction.PaymentRequest,
                MaxFeePercent = maxFeePercent,
                Amount = bolt11.MinimumAmount == LightMoney.Zero ? amount : null,
                SendTimeout = SendTimeout
            };

            var result = await _btcpayService.PayLightningInvoice(request, cancellationToken);

            // Check result
            if (result.TotalAmount == null)
                throw new PaymentRequestValidationException("Payment request has already been paid.");

            // Set amounts according to actual amounts paid, including fees
            LightMoney settledAmount = new (result.TotalAmount * -1);
            var originalAmount = result.TotalAmount - result.FeeAmount;

            await Settle(sendingEntry.Entity, originalAmount, settledAmount, result.FeeAmount, DateTimeOffset.UtcNow, result.Preimage);
        }
        catch (GreenfieldAPIException ex)
        {
            switch (ex.APIError.Code)
            {
                case "could-not-find-route":
                case "generic-error":
                    // Remove preliminary transaction entry, payment could not be sent
                    dbContext.Transactions.Remove(sendingTransaction);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    break;
            }

            // Rethrow to inform about the error up in the stack
            throw;
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            // Timeout, potentially caused by hold invoices
            // Payment will be saved as pending, the LightningInvoiceWatcher will handle settling/cancelling
            _logger.LogInformation("Sending transaction {TransactionId} timed out. Saved as pending",
                sendingEntry.Entity.TransactionId);
        }

        return sendingEntry.Entity;
    }

    public bool ValidateDescriptionHash(string paymentRequest, string metadata)
    {
        return ParsePaymentRequest(paymentRequest).VerifyDescriptionHash(metadata);
    }

    public async Task<Transaction?> ValidatePaymentRequest(string paymentRequest)
    {
        var transaction = await _walletRepository.GetTransaction(new TransactionQuery
        {
            PaymentRequest = paymentRequest
        });

        return transaction switch
        {
            { IsExpired: true } => throw new PaymentRequestValidationException(
                $"Payment request already expired at {transaction.ExpiresAt}."),
            { IsSettled: true } => throw new PaymentRequestValidationException(
                "Payment request has already been settled."),
            { IsPaid: true } => throw new PaymentRequestValidationException("Payment request has already been paid."),
            _ => transaction
        };
    }

    public BOLT11PaymentRequest ParsePaymentRequest(string payReq)
    {
        return BOLT11PaymentRequest.Parse(payReq.Trim(), _network);
    }

    public async Task<BOLT11PaymentRequest> GetBolt11(LNURLPayRequest lnurlPay, LightMoney? amount = null,
        string? comment = null)
    {
        return await _lnurlService.GetBolt11(lnurlPay, amount, comment);
    }

    public async Task<(BOLT11PaymentRequest? bolt11, LNURLPayRequest? lnurlPay)> GetPaymentRequests(string destination)
    {
        var index = destination.IndexOf("lightning=", StringComparison.InvariantCultureIgnoreCase);
        var dest = index == -1
            ? destination.Replace("lightning:", "", StringComparison.InvariantCultureIgnoreCase)
            : destination.Substring(index + 10);
        try
        {
            var bolt11 = ParsePaymentRequest(dest);
            return (bolt11, null);
        }
        catch (Exception)
        {
            var lnurlPay = await _lnurlService.GetPaymentRequest(dest);
            return (null, lnurlPay);
        }
    }

    public async Task<bool> Cancel(string invoiceId)
    {
        var transaction = await _walletRepository.GetTransaction(new TransactionQuery { InvoiceId = invoiceId });

        return await Cancel(transaction);
    }

    public async Task<bool> Expire(Transaction transaction)
    {
        var status = transaction.Status;
        var result = transaction.SetExpired();
        if (result)
        {
            await _walletRepository.UpdateTransaction(transaction);
            await BroadcastTransactionUpdate(transaction, Transaction.StatusCancelled);
        }

        _logger.LogInformation(
            // ReSharper disable once TemplateIsNotCompileTimeConstantProblem
            (result ? "Expired transaction {TransactionId}" : "Expiring transaction {TransactionId} failed") +
            " (previous state: {Status})",
            transaction.TransactionId, status);

        return true;
    }

    public async Task<bool> Cancel(Transaction transaction)
    {
        var status = transaction.Status;
        var result = transaction.SetCancelled();
        if (result)
        {
            await _walletRepository.UpdateTransaction(transaction);
            await BroadcastTransactionUpdate(transaction, Transaction.StatusCancelled);
        }

        _logger.LogInformation(
            // ReSharper disable once TemplateIsNotCompileTimeConstantProblem
            (result ? "Cancelled transaction {TransactionId}" : "Cancelling transaction {TransactionId} failed") +
            " (previous state: {Status})",
            transaction.TransactionId, status);

        return true;
    }

    public async Task<bool> Invalidate(Transaction transaction)
    {
        var status = transaction.Status;
        var result = transaction.SetInvalid();
        if (result)
        {
            await _walletRepository.UpdateTransaction(transaction);
            await BroadcastTransactionUpdate(transaction, Transaction.StatusInvalid);
        }

        _logger.LogInformation(
            // ReSharper disable once TemplateIsNotCompileTimeConstantProblem
            (result ? "Invalidated transaction {TransactionId}" : "Invalidating transaction {TransactionId} failed") +
            " (previous state: {Status})",
            transaction.TransactionId, status);

        return result;
    }

    public async Task<bool> Revalidate(Transaction transaction)
    {
        var status = transaction.Status;
        var result = transaction.QueueForRevalidation();
        if (result)
        {
            await _walletRepository.UpdateTransaction(transaction);
            await BroadcastTransactionUpdate(transaction, Transaction.StatusRevalidating);
        }

        _logger.LogInformation(
            // ReSharper disable once TemplateIsNotCompileTimeConstantProblem
            (result ? "Revalidating transaction {TransactionId}" : "Revalidating transaction {TransactionId} failed") +
            " (previous state: {Status})",
            transaction.TransactionId, status);

        return result;
    }

    public async Task<bool> Settle(Transaction transaction, LightMoney amount, LightMoney amountSettled,
        LightMoney routingFee, DateTimeOffset date, string preimage)
    {
        var status = transaction.Status;
        var result = transaction.SetSettled(amount, amountSettled, routingFee, date, preimage);
        if (result)
        {
            await _walletRepository.UpdateTransaction(transaction);
            await BroadcastTransactionUpdate(transaction, Transaction.StatusSettled);
        }

        _logger.LogInformation(
            // ReSharper disable once TemplateIsNotCompileTimeConstantProblem
            (result ? "Settled transaction {TransactionId}" : "Settling transaction {TransactionId} failed") +
            " (previous state: {Status})",
            transaction.TransactionId, status);

        return result;
    }

    public async Task<LightMoney> GetLiabilitiesTotal()
    {
        var total = await _walletRepository.GetLiabilitiesTotal();
        return new LightMoney(total);
    }

    private async Task BroadcastTransactionUpdate(Transaction transaction, string eventName)
    {
        await _transactionHub.Clients.All.SendAsync("transaction-update",
            new
            {
                transaction.TransactionId,
                transaction.InvoiceId,
                transaction.WalletId,
                transaction.Status,
                transaction.IsPaid,
                transaction.IsExpired,
                transaction.PaymentHash,
                Event = eventName
            });
    }

    private static string Sats(LightMoney amount)
    {
        return $"{Math.Round(amount.ToUnit(LightMoneyUnit.Satoshi))} sats";
    }

    private static string Millisats(LightMoney amount)
    {
        return $"{amount.ToUnit(LightMoneyUnit.MilliSatoshi)} millisats";
    }
}
