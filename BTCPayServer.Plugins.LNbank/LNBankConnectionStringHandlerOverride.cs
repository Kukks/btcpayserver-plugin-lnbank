using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Lightning.LNbank;
using NBitcoin;

namespace BTCPayServer.Plugins.LNbank;

public class LNBankConnectionStringHandlerOverride : ILightningConnectionStringHandler
{
    private readonly LNbankConnectionStringHandler _inner;

    public LNBankConnectionStringHandlerOverride(HttpClient client = null)
    {
        _inner = new LNbankConnectionStringHandler(client);
    }

    public ILightningClient Create(string connectionString, Network network, out string error)
    {
        var result =  _inner.Create(connectionString, network, out error);
        if (result is not null)
        {
            return new LightningClientWrapper(connectionString,result);
        }
        return result;
    }

    public class LightningClientWrapper : ILightningClient
    {
        private readonly string _connectionString;
        private readonly ILightningClient _inner;

        public LightningClientWrapper(string connectionString, ILightningClient inner)
        {
            _connectionString = connectionString;
            _inner = inner;
        }

        public override string ToString()
        {
            return _connectionString;
        }

        public async Task<LightningInvoice> GetInvoice(string invoiceId, CancellationToken cancellation = new CancellationToken())
        {
            return await _inner.GetInvoice(invoiceId, cancellation);
        }

        public async Task<LightningInvoice> GetInvoice(uint256 paymentHash, CancellationToken cancellation = new CancellationToken())
        {
            return await _inner.GetInvoice(paymentHash, cancellation);
        }

        public async Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = new CancellationToken())
        {
            return await _inner.ListInvoices(cancellation);
        }

        public async Task<LightningInvoice[]> ListInvoices(ListInvoicesParams request, CancellationToken cancellation = new CancellationToken())
        {
            return await _inner.ListInvoices(request, cancellation);
        }

        public async Task<LightningPayment> GetPayment(string paymentHash, CancellationToken cancellation = new CancellationToken())
        {
            return await _inner.GetPayment(paymentHash, cancellation);
        }

        public async Task<LightningPayment[]> ListPayments(CancellationToken cancellation = new CancellationToken())
        {
            return await _inner.ListPayments(cancellation);
        }

        public async Task<LightningPayment[]> ListPayments(ListPaymentsParams request, CancellationToken cancellation = new CancellationToken())
        {
            return await _inner.ListPayments(request, cancellation);
        }

        public async Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry,
            CancellationToken cancellation = new CancellationToken())
        {
            return await _inner.CreateInvoice(amount, description, expiry, cancellation);
        }

        public async Task<LightningInvoice> CreateInvoice(CreateInvoiceParams createInvoiceRequest, CancellationToken cancellation = new CancellationToken())
        {
            return await _inner.CreateInvoice(createInvoiceRequest, cancellation);
        }

        public async Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = new CancellationToken())
        {
            return await _inner.Listen(cancellation);
        }

        public async Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = new CancellationToken())
        {
            return await _inner.GetInfo(cancellation);
        }

        public async Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = new CancellationToken())
        {
            return await _inner.GetBalance(cancellation);
        }

        public async Task<PayResponse> Pay(PayInvoiceParams payParams, CancellationToken cancellation = new CancellationToken())
        {
            return await _inner.Pay(payParams, cancellation);
        }

        public async Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams, CancellationToken cancellation = new CancellationToken())
        {
            return await _inner.Pay(bolt11, payParams, cancellation);
        }

        public async Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = new CancellationToken())
        {
            return await _inner.Pay(bolt11, cancellation);
        }

        public async Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest, CancellationToken cancellation = new CancellationToken())
        {
            return await _inner.OpenChannel(openChannelRequest, cancellation);
        }

        public async Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = new CancellationToken())
        {
            return await _inner.GetDepositAddress(cancellation);
        }

        public async Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo, CancellationToken cancellation = new CancellationToken())
        {
            return await _inner.ConnectTo(nodeInfo, cancellation);
        }

        public async Task CancelInvoice(string invoiceId, CancellationToken cancellation = new CancellationToken())
        {
            await _inner.CancelInvoice(invoiceId, cancellation);
        }

        public async Task<LightningChannel[]> ListChannels(CancellationToken cancellation = new CancellationToken())
        {
            return await _inner.ListChannels(cancellation);
        }
    }
}