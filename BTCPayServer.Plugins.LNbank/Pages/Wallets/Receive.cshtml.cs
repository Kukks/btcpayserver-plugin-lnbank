using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LNbank.Authentication;
using BTCPayServer.Plugins.LNbank.Data.Models;
using BTCPayServer.Plugins.LNbank.Services.Wallets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.Extensions.Logging;
using AuthenticationSchemes = BTCPayServer.Abstractions.Constants.AuthenticationSchemes;

namespace BTCPayServer.Plugins.LNbank.Pages.Wallets;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = LNbankPolicies.CanCreateInvoices)]
public class ReceiveModel : BasePageModel
{
    private readonly ILogger _logger;

    [BindProperty]
    public string Description { get; set; }

    [BindProperty]
    [DisplayName("Attach description to payment request")]
    public bool AttachDescription { get; set; }

    [BindProperty]
    [DisplayName("Amount")]
    [Required]
    [Range(0, 2100000000000)]
    public long Amount { get; set; }

    [BindProperty]
    [DisplayName("Add routing hints for private channels")]
    public bool PrivateRouteHints { get; set; }

    [BindProperty]
    [DisplayName("Custom invoice expiry")]
    public int? Expiry { get; set; }

    public ReceiveModel(
        ILogger<ReceiveModel> logger,
        UserManager<ApplicationUser> userManager,
        WalletRepository walletRepository,
        WalletService walletService) : base(userManager, walletRepository, walletService)
    {
        _logger = logger;
    }

    public IActionResult OnGet(string walletId)
    {
        if (CurrentWallet == null)
            return NotFound();

        PrivateRouteHints = CurrentWallet.PrivateRouteHintsByDefault;

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string walletId)
    {
        if (CurrentWallet == null)
            return NotFound();
        if (!ModelState.IsValid)
            return Page();

        try
        {
            var amount = LightMoney.Satoshis(Amount).MilliSatoshi;
            var memo = !string.IsNullOrEmpty(Description) ? Description : null;
            var expiry = Expiry is > 0 ? TimeSpan.FromMinutes(Expiry.Value) : WalletService.ExpiryDefault;
            var req = new CreateLightningInvoiceRequest
            {
                Amount = amount,
                Expiry = expiry,
                Description = AttachDescription && !string.IsNullOrEmpty(memo) ? memo : null,
                PrivateRouteHints = PrivateRouteHints
            };

            var transaction = await WalletService.Receive(CurrentWallet, req, memo);
            return RedirectToPage("/Transactions/Details", new { walletId, transaction.TransactionId });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Invoice creation failed: {Message}", exception.Message);

            TempData[WellKnownTempData.ErrorMessage] = $"Invoice creation failed: {exception.Message}";
        }

        return Page();
    }
}
