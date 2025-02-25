using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Plugins.LNbank.Data.Models;
using BTCPayServer.Plugins.LNbank.Services.Wallets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.LNbank.Pages.Wallets;

[AllowAnonymous]
public class ShareLNURLModel : BasePageModel
{
    public Wallet Wallet { get; set; }

    public ShareLNURLModel(
        UserManager<ApplicationUser> userManager,
        WalletRepository walletRepository,
        WalletService walletService) : base(userManager, walletRepository, walletService) { }

    public async Task<IActionResult> OnGet(string walletId)
    {
        Wallet = await WalletRepository.GetWallet(new WalletsQuery
        {
            WalletId = new[] { walletId },
            IsServerAdmin = User.IsInRole(Roles.ServerAdmin)
        });

        if (Wallet == null)
            return NotFound();

        return Page();
    }
}
