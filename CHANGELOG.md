# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

[![Support this plugin](./docs/img/support.png)](lightning:LNURL1DP68GURN8GHJ7AMPD3KX2AR0VEEKZAR0WD5XJTNRDAKJ7TNHV4KXCTTTDEHHWM30D3H82UNVWQHKXUN0WAJX2ER9V9E8G6PN8QSKVTEZ)

## [1.6.3]

### Added

- Add Lightning Address Identifier for LNbank wallets.

## [1.6.2]

### Fixed

- Account for fees in LNURL withdraw request data. (#35)

### Changed

- Add `lightning:` prefix to QR code data, so that generic readers can use them. (#36)

## [1.6.1]

### Fixed

- Fix API docs.

## [1.6.0]

### Added

- Incorporate "Hide Sensitive Info" setting from BTCPay Server.
- Add wallet balance graph.

### Fixed

- Payments would sometimes not be detected by BTCPay Server. (#33) @NicolasDorier
- Fix updating wallet name and route hint default. (btcpayserver/btcpayserver#5009)
- Prevent wallet lock out of owner caused by improper access key. (btcpayserver/btcpayserver#5026)

### Changed

- Improve logging of errors and debug messages.

## [1.5.1]

### Added

- Display warnings if LNbank liabilities exceed Lightning node balance. (#21)

### Changed

- Allow sweeping transaction if the amount is below threshold and empties the wallet. (#32)

## [1.5.0]

### Added

- Configure LNURL withdraw access per wallet. (#18)
- API: Add send and receive endpoints.
- API: Add transactions endpoints.
- API: Add LNURL-Pay to wallet data.

## [1.4.2]

### Fixed

- Fix crash on admin overview if a user does not exist anymore.

## [1.4.1]

### Changed

- Minor UI updates.

## [1.4.0]

### Added

- Admin overview: See how many wallets there are and what balances they have. (#16)
- Admin can run revalidation for transactions marked as expired, cancelled or invalid.
- Admin can delete transactions.

### Changed

- Open LNURL share page in separate tab. (#27)
- Update QR boxes.
- Improve logging and increase retries before invalidation.

## [1.3.12]

### Added

- Wallet: Setting for adding route hints by default.

### Fixed

- Make links compatible with `rootpath` setting. (If BTCPay is deployed in a subdirectory)

## [1.3.11]

### Fixed

- Lookup invoices by either invoice ID or payment hash. (#22)
- Fix attaching the description.

### Added

- Add invoice ID to transaction details view.

## [1.3.10]

### Added

- Add retries before invalidating invoices and payments.

## [1.3.9]

### Changed

- Require an internal Lightning node to be configured.
- Add transaction details (payment hash, preimage) to view.
- LNDhub API: Add additional node info.
- Handle sub 1 sat (millisat) balances better. (btcpayserver/btcpayserver#4517)

### Fixed

- Fix Lightning Addresses for LNbank-backed stores. (btcpayserver/btcpayserver#4496)

## [1.3.8] - 2022-12-23

### Fixed

- Invoices were not generated after upgrade to BTCPay Server v1.7.2. (btcpayserver/btcpayserver#4458)

### Changed

- Updates for BTCPay Server v1.7.3

## [1.3.7] - 2022-12-19

### Added

- Parse payment URLs.

### Fixed

- Allow invalidating pending invoices. (#23)

## [1.3.6] - 2022-12-15

### Changed

- Updates for BTCPay Server v1.7.2

## [1.3.5] - 2022-09-30

### Changed

- Date display improvements.

## [1.3.4] - 2022-09-30

### Changed

- Improve LNURL payment flow.
- Improve invoice canceling and invalidating.
- Minor Send and Receive view improvements.

## [1.3.3] - 2022-09-29

### Changed

- Handle invalid transactions in background watcher.

### Fixed

- LNDhub API: Fix send error.
- Lightning setup: Fix setting LNbank wallet in WebKit-based browsers. (btcpayserver/btcpayserver-plugins#44)

## [1.3.2] - 2022-09-27

### Added

- Invoices API for BTCPay Lightning client. (btcpayserver/BTCPayServer.Lightning#99)

### Fixed

- Fix missing icon in sidebar.
- LNDhub-API: Fix invoices list. (btcpayserver/btcpayserver#4168)

## [1.3.1] - 2022-09-26

### Fixed

- Fix LNURL metadata. (btcpayserver/btcpayserver#4165)
- Fix migration. (dennisreimann/btcpayserver#22)

## [1.3.0] - 2022-09-26

### Added

- LNDhub-compatible API: Wallets are usable with BlueWallet, Zeus and Alby.
- Wallet access keys: Share wallet access, supporting different access levels.
- Send: LNURL-Pay and Lightning Address support.
- Receive: Add custom invoice expiry as advanced option.

### Changed

- Handle expired invoices in background watcher.
- More logging in background watcher.

## [1.2.3] - 2022-07-08

### Changed

- Improve send error handling.
- Improve API responses.
- Allow creation of zero amount invoices.

## [1.2.2] - 2022-05-30

### Added

- Public wallet LNURL page for sharing.

### Changed

- Distinguish original invoice amount and actual amount settled.
- Improve hold invoice handling.

### Fixed

- Allow specifying explicit amount for zero amount invoices.

## [1.2.1] - 2022-04-30

### Added

- Refresh transactions list on update.
- Log exceptions in background watcher.
- Handling for hold invoices.
- Autofocus input fields.

### Fixed

- Allow for empty description when creating invoices.
- Handle cancelled invoices in background watcher.

## [1.2.0] - 2022-04-01

### Added

- LNURL-Pay for receiving transactions.
- API for accessing, updating and deleting LNbank wallets.
- Export wallet transactions for accounting (CSV and JSON).

## [1.1.1] - 2022-03-09

### Added

- API for creating LNbank wallets.

### Changed

- Use store invoice expiry time.
- Soft delete wallets (only mark as deleted).

### Fixed

- Websocket connection to update transaction states.
- Handle crashes in background service.
- Fix redirects.

## [1.1.0] - 2022-02-21

### Added

- Toggle for attaching description to pay request when receiving.
- Allow for empty description when receiving.
- Customize description when sending.
- Prevent deletion of wallet with balance.

### Changed

- Proper redirects on homepage (create wallet if none exists).
- Separate wallet list and wallet details views.
- Common wallet header for all views.

### Fixed

- Fee handling: User pays routing fee and needs to have a fee reserve when sending.
- Prevent paying payment requests multiple times.

## [1.0.4] - 2022-02-10

### Added

- Support for private route hints: Will be enabled if the connected store has the required setting or if the toggle on
  the receive page is activated.

### Changed

- Lowercase page paths.
- Remove button icons, improve wallet view.

### Fixed

- Logo link on Share page.

## [1.0.3] - 2022-02-01

### Added

- Form validation

### Changed

- Improve create wallet for LN node connection case.

### Fixed

- Non-admins cannot send and receive when using the internal node.
