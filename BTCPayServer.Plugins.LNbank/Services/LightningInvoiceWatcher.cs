using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LNbank.Data.Models;
using BTCPayServer.Plugins.LNbank.Services.Wallets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.LNbank.Services;

public class LightningInvoiceWatcher : BackgroundService
{
    private static readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(5);

    // grace period before starting to check a pending transaction, which is inflight
    // and might get handled in the request context that initiated the payment
    private static readonly TimeSpan _inflightDelay = WalletService.SendTimeout + _checkInterval;
    private readonly BTCPayService _btcpayService;
    private readonly ILogger<LightningInvoiceWatcher> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly WalletRepository _walletRepository;
    private readonly Dictionary<string, int> _retries = new ();

    public LightningInvoiceWatcher(
        BTCPayService btcpayService,
        WalletRepository walletRepository,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<LightningInvoiceWatcher> logger)
    {
        _logger = logger;
        _btcpayService = btcpayService;
        _walletRepository = walletRepository;
        _serviceScopeFactory = serviceScopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting");

        cancellationToken.Register(() => _logger.LogInformation("Stop request via cancellation"));

        while (!cancellationToken.IsCancellationRequested)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var walletService = scope.ServiceProvider.GetRequiredService<WalletService>();

            var transactions = await _walletRepository.GetPendingTransactions();
            var list = transactions.ToList();
            var count = list.Count;

            if (count > 0)
            {
                _logger.LogDebug("Processing {Count} transactions", count);

                try
                {
                    await Task.WhenAll(list.Select(transaction =>
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        cts.CancelAfter(_checkInterval);
                        return CheckPendingTransaction(walletService, transaction, cts.Token);
                    }));
                }
                catch (Exception exception) when (exception is TaskCanceledException or OperationCanceledException)
                {
                    _logger.LogInformation("Checking pending transactions canceled after time out");
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Checking pending transactions failed: {Message}", exception.Message);
                }
            }

            await Task.Delay(_checkInterval, cancellationToken);
        }

        _logger.LogInformation("Ending, cancellation requested");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping");

        await Task.CompletedTask;
    }

    private async Task CheckPendingTransaction(WalletService walletService, Transaction transaction,
        CancellationToken cancellationToken = default)
    {
        await (string.IsNullOrEmpty(transaction.InvoiceId)
            ? CheckPayment(walletService, transaction, cancellationToken)
            : CheckInvoice(walletService, transaction, cancellationToken));
    }

    private async Task CheckInvoice(WalletService walletService, Transaction transaction,
        CancellationToken cancellationToken = default)
    {
        LightningInvoiceData invoice = null;
        string errorDetails = null;
        var invalidate = false;

        try
        {
            invoice = await _btcpayService.GetLightningInvoice(transaction.PaymentHash, cancellationToken);
        }
        catch (GreenfieldAPIException apiException) when (apiException.APIError.Code == "invoice-not-found")
        {
            errorDetails = apiException.Message;
            invalidate = InvalidateAfterRetries(transaction.PaymentHash, 12);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception,
                "Unable to resolve invoice (Invoice Id = {InvoiceId}) for transaction {TransactionId}",
                transaction.InvoiceId, transaction.TransactionId);
            return;
        }

        if (invoice == null)
        {
            _logger.LogWarning(
                "Unable to resolve invoice (Invoice Id = {InvoiceId}) for transaction {TransactionId}{Details}",
                transaction.InvoiceId, transaction.TransactionId,
                string.IsNullOrEmpty(errorDetails) ? "" : $": {errorDetails}");
            if (invalidate)
                await walletService.Invalidate(transaction);
            return;
        }

        switch (invoice.Status)
        {
            case LightningInvoiceStatus.Paid:
                {
                    var paidAt = invoice.PaidAt ?? DateTimeOffset.Now;
                    var amount = invoice.Amount ?? invoice.AmountReceived; // Zero amount invoices have amount as null value
                    var feeAmount = amount - invoice.AmountReceived;
                    await walletService.Settle(transaction, amount, invoice.AmountReceived, feeAmount, paidAt, transaction.Preimage);
                    break;
                }
            case LightningInvoiceStatus.Expired:
                await walletService.Expire(transaction);
                break;
        }
    }

    private async Task CheckPayment(WalletService walletService, Transaction transaction,
        CancellationToken cancellationToken = default)
    {
        LightningPaymentData payment = null;
        string errorDetails = null;
        var invalidate = false;

        try
        {
            payment = await _btcpayService.GetLightningPayment(transaction.PaymentHash, cancellationToken);
        }
        catch (GreenfieldAPIException apiException) when (apiException.APIError.Code == "payment-not-found")
        {
            errorDetails = apiException.Message;
            invalidate = InvalidateAfterRetries(transaction.PaymentHash, 60);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception,
                "Unable to resolve payment (Payment Hash = {PaymentHash}) for transaction {TransactionId}",
                transaction.PaymentHash, transaction.TransactionId);
            return;
        }

        if (payment == null)
        {
            var isInflight = transaction.IsPending && transaction.CreatedAt > DateTimeOffset.Now - _inflightDelay;
            if (!isInflight)
            {
                invalidate = InvalidateAfterRetries(transaction.PaymentHash, 12);
                _logger.LogWarning(
                    "Unable to resolve payment (Payment Hash = {PaymentHash}) for transaction {TransactionId}{Details}",
                    transaction.PaymentHash, transaction.TransactionId,
                    string.IsNullOrEmpty(errorDetails) ? "" : $": {errorDetails}");
            }

            if (invalidate)
                await walletService.Invalidate(transaction);
            return;
        }

        switch (payment.Status)
        {
            case LightningPaymentStatus.Complete:
                {
                    var paidAt = payment.CreatedAt ?? DateTimeOffset.Now;
                    var originalAmount = payment.TotalAmount - payment.FeeAmount;
                    await walletService.Settle(transaction, originalAmount, payment.TotalAmount * -1, payment.FeeAmount,
                        paidAt, payment.Preimage);
                    break;
                }
            case LightningPaymentStatus.Failed:
                _logger.LogWarning(
                    "Failed payment (Payment Hash = {PaymentHash}) for transaction {TransactionId} - invalidating transaction",
                    transaction.PaymentHash, transaction.TransactionId);
                await walletService.Invalidate(transaction);
                break;
            case LightningPaymentStatus.Unknown:
            case LightningPaymentStatus.Pending:
                _logger.LogInformation("Transaction {TransactionId} status: {Status}",
                    transaction.TransactionId, payment.Status.ToString());
                break;
        }
    }

    private bool InvalidateAfterRetries(string id, int max)
    {
        if (!_retries.ContainsKey(id))
            _retries.Add(id, 1);
        else
            _retries[id] += 1;

        return _retries[id] >= max;
    }
}
