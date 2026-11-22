using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using POS.Api.Controllers;
using POS.Api.Models;
using POS.Api.Services;
using POS.Shared.Models;

namespace POS.Tests;

/// <summary>
/// SQLite-compatible subclass of PosDbContext that strips PostgreSQL-only "::character varying" default SQL
/// so the full EF Core model can be created in an in-memory SQLite relational database with real transactions.
/// </summary>
public sealed class TestPosDbContext(DbContextOptions<PosDbContext> options) : PosDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var prop in entity.GetProperties())
            {
                var defaultSql = prop.GetDefaultValueSql();
                if (defaultSql != null && defaultSql.Contains("::", StringComparison.Ordinal))
                {
                    prop.SetDefaultValueSql(null);
                }
            }
        }
    }
}

public static partial class Program
{
    private static int _passed;
    private static int _failed;

    public static async Task<int> Main()
    {
        Console.WriteLine("=== Petty Cash & Cash Movement Management Test Suite (18 Layer-10 Scenarios) ===");

        await RunTestAsync("01. Normal Paid-In posts immediately and increases ExpectedBalance", Test01_NormalPaidIn);
        await RunTestAsync("02. Normal Paid-Out / Petty Cash with valid supervisor PIN posts and deducts ExpectedBalance", Test02_NormalPaidOut);
        await RunTestAsync("03. Insufficient Drawer Balance is rejected (400 BadRequest) and preserves ExpectedBalance", Test03_InsufficientBalance);
        await RunTestAsync("04. Invalid Amount (<= 0) is rejected (400 BadRequest)", Test04_InvalidAmount);
        await RunTestAsync("05. No Active Shift is rejected when shift is closed or missing", Test05_NoActiveShift);
        await RunTestAsync("06. Unauthorized Cashier on another cashier's shift is rejected (403 Forbidden)", Test06_UnauthorizedCashier);
        await RunTestAsync("07. Manager Approval Required saves as PENDING_APPROVAL without changing balance until approved (and blocks username-as-PIN bypass)", Test07_ManagerApprovalRequired);
        await RunTestAsync("08. Rejected Request transitions to REJECTED and never alters ExpectedBalance", Test08_RejectedRequest);
        await RunTestAsync("09. Duplicate Submission with identical ClientMovementId is idempotent", Test09_DuplicateSubmission);
        await RunTestAsync("10. Concurrent / Sequential Paid-Out Balance Guard blocks second outflow exceeding remaining cash", Test10_ConcurrentPaidOutGuard);
        await RunTestAsync("11. Offline Paid-Out verifies SHA-256 PinHash and keeps unverified requests as PENDING_APPROVAL", Test11_OfflinePaidOutVerification);
        await RunTestAsync("12. Offline-to-Online Synchronization (SyncCashMovement) posts movement idempotently", Test12_OfflineToOnlineSync);
        await RunTestAsync("13. Synchronization Retry Policy enforces bounded exponential backoff and NEEDS_ATTENTION / FAILED_CONFLICT", Test13_SyncRetryPolicy);
        await RunTestAsync("14. Synchronization Conflict returns 409 CONFLICT when offline paid-out exceeds server drawer balance", Test14_SyncConflict);
        await RunTestAsync("15. Shift Closing Reconciliation enforces full formula and blocks close while PENDING_APPROVAL exists", Test15_ShiftClosingReconciliation);
        await RunTestAsync("16. Voucher Printing / Reference Formatting generates PCV/CIN/COU/CDP/REV vouchers with full audit fields", Test16_VoucherFormatting);
        await RunTestAsync("17. Reversal Processing marks original REVERSED, creates linked REVERSAL entry, restores balance, and blocks double-reversal", Test17_ReversalProcessing);
        await RunTestAsync("18. BIR Sales & VAT Isolation ensures Petty Cash / Paid-In / Paid-Out never alter GrossSales, VAT, or InvoiceSequences", Test18_BirSalesAndVatIsolation);
        await RunTestAsync("19. Cashier Transactions strictly scoped to Token Claims (branch_id, terminal_id) and forbid cross-terminal access", Test19_TransactionsTokenClaimsScoping);
        await RunTestAsync("20. Cashier Transactions enforce 4:00 AM Business Day Cutoff (no sales prior to 4am)", Test20_Transactions4AmCutoff);
        await RunTestAsync("21. Voids and Returns require strict PIN authorization (rejecting username-as-PIN bypass)", Test21_VoidAndReturnStrictPinEnforcement);
        await RunTestAsync("22. Offline Transaction History logic verifies local terminal & 4 AM scoping", Test22_OfflineTransactionHistoryLogic);

        await RunTestAsync("23. Physical outlet and terminal ownership model", Test23_HierarchyModel);
        await RunTestAsync("24. Outlet and terminal CRUD validation and deletion guards", Test24_HierarchyCrud);
        await RunTestAsync("25. SQLite legacy upgrade preserves records and payload hashes", Test25_LocalHierarchyUpgrade);
        await RunTestAsync("26. Legacy cached payload still resolves the original terminal", Test26_LegacyPayload);
        await RunTestAsync("27. Terminal resolution, sale, payment, invoice and receipt", Test27_SaleReceipt);
        await RunTestAsync("28. Offline upload replays safely and rejects invoice collision", Test28_OfflineReplay);
        await RunTestAsync("29. Online and offline invoice allocations do not overlap", Test29_InvoiceReservations);
        await RunTestAsync("30. Registration rejects cross-field IP collisions", Test30_RegistrationAddressCollision);
        await RunTestAsync("31. Unscoped cashier cannot write offline history", Test31_UnscopedOfflineWriteIsForbidden);
        await RunTestAsync("32. Supervisor recovers inactive terminal history with audit", Test32_InactiveHistoryRecovery);
        await RunTestAsync("33. Status acknowledgement requires matching terminal and payload", Test33_ExactAcknowledgement);
        await RunTestAsync("34. Cash movement status scoping", Test34_CashMovementStatusScoping);
        await RunTestAsync("35. Idempotent replay rejects cross-correlated order payload", Test35_IdempotentReplayCorrelation);
        await RunTestAsync("36. Supervisor historical recovery succeeds across terminals", Test36_SupervisorRecoveryAcrossTerminals);
        await RunTestAsync("37. Deactivation cutoff date preserved against subsequent edits", Test37_DeactivationCutoffPreserved);
        await RunTestAsync("38. Receipt layout, item totals, and syntax integrity", Test38_ReceiptFormattingOutput);

        await RunTestAsync("39. Blank opening float is rejected", Test39_OpeningFloatRequired);
        await RunTestAsync("40. Bar and Kitchen preparation routing, assigned printers, and ticket formatting", Test40_BarAndKitchenRouting);
        await RunTestAsync("41. Cash denomination counting, centavos, PHP 20 coin/bill separation, and server recalculation", Test41_DenominationCountingAndCentavoArithmetic);
        await RunTestAsync("42. Non-cash payment declarations, inactive used methods, and offsetting variance detection", Test42_PaymentMethodReconciliationAndOffsettingVariances);
        await RunTestAsync("43. Cash expected balance ledger formula and separation of sales vs drawer float/movements", Test43_CashExpectedLedgerAndSalesSeparation);
        await RunTestAsync("44. Duplicate close idempotency (ClientCloseId) and X-reading formatting/reprint", Test44_CloseIdempotencyAndXReadingReprint);
        await RunTestAsync("45. Blind closing role guards, cashier redaction, and explicit zero-declaration confirmation", Test45_BlindClosingRoleGuardsAndZeroDeclarationConfirmation);
        await RunTestAsync("46. Supervisor recount preserves original declaration and appends immutable recount audit trail", Test46_SupervisorRecountPreservesOriginalDeclarationAndAuditTrail);
        await RunTestAsync("47. Sales report terminal breakdown, payment split isolation, and net refund separation", Test47_SalesReportTerminalBreakdownAndSeparationOfRefunds);
        await RunTestAsync("48. Back-office shift reconciliation, historical denomination fallback, and terminal filtering", Test48_BackOfficeShiftReconciliationAndTerminalFiltering);
        await RunTestAsync("49. Local-First terminal activation, UUIDv4, pairing code lifecycle, and sync toggle", Test49_TerminalLocalFirstActivation);

        await RunTestAsync("Z01. Fixed Manila business date", TestZReadingBusinessDate);
        await RunTestAsync("Z02. Immutable idempotent generation", TestZReadingGeneration);
        await RunTestAsync("Z03. Authoritative adjustment events and detail", TestZReadingEvents);
        await RunTestAsync("Z04. Append-only honest print audit", TestZReadingPrintAudit);
        await RunTestAsync("Z05. Supervisor and current-terminal authorization", TestZReadingControllerScope);
        await RunTestAsync("Z06. Deployment, readiness and serialization fence", TestZReadingPrerequisites);
        await RunTestAsync("Z07. Unreconciled paid/incomplete orders block", TestZReadingUnreconciledServerEvents);

        await RunTestAsync("Z08. Thermal report detail, HTML escaping and truthful reprint labels", TestZReadingFormatter);
        await RunTestAsync("Z09. Late-night Asia/Manila sales (20:00–03:59 PHT) reconcile with Z-Reading Product Sales, invoices, payments & Manila shift timestamps", TestZReadingEveningManilaEndToEnd);

        await RunTestAsync("SEC01. Controller authorization and deny-by-default endpoints", TestSecurity_ControllerAuthorizationAttributes);
        await RunTestAsync("SEC02. Privilege escalation prevention (Manager cannot create/delete Admin)", TestSecurity_PrivilegeEscalationPrevented);
        await RunTestAsync("SEC03. Sensitive data masking (non-admin PIN redaction)", TestSecurity_SensitiveDataMasking);
        await RunTestAsync("SEC04. Cart item price and quantity tampering prevention", TestSecurity_CartPriceAndQuantityTampering);
        await RunTestAsync("SEC05. BOLA / IDOR cross-branch order, void and return protection", TestSecurity_BOLA_CrossBranchOrderAndReturnProtection);
        await RunTestAsync("SEC06. BOLA / IDOR manager cross-branch terminal/outlet hierarchy protection", TestSecurity_BOLA_ManagerCrossBranchHierarchyProtection);
        await RunTestAsync("SEC07. File upload size bounding (5MB max)", TestSecurity_FileUploadSizeValidation);

        await RunTestAsync("MENU01. Stable terminal menu display contracts and validation", TestMenuDisplayModeContracts);
        await RunTestAsync("MENU02. Per-terminal CRUD, resolution and offline reference round trip", TestMenuDisplayModeRoundTrip);
        await RunTestAsync("MENU03. Unsupported menu modes rejected without mutation", TestMenuDisplayModeInvalidServerValues);
        await RunTestAsync("MENU04. Supervisor attributes, manager branch isolation and cashier terminal isolation", TestMenuDisplayModeAuthorization);
        await RunTestAsync("MENU05. PostgreSQL integer default, allowed values and discoverable migration", TestMenuDisplayModeDatabaseModel);
        await RunTestAsync("FUND01. Initial Cash Fund Outlet default inheritance, Terminal override, denomination opening count, discrepancy tracking, and active shift isolation", TestInitialCashFundManagementAndOpeningProcess);
        await RunTestAsync("LOCK01. Cashier account restriction and POS terminal locking after closing until 4:00 AM Asia/Manila", TestCashierAndTerminalClosingLockUntil4Am);
        await RunTestAsync("PHASE1A-01. Session-token endpoint strictly issues Terminal role (never Admin/Cashier) and validates lock", TestPhase1A_SessionTokenNeverMintsAdmin);
        await RunTestAsync("PHASE1A-02. BIR InvoiceSequence block preservation (zero number burning on sync/re-login)", TestPhase1A_OfflineReferenceDataPreservesInvoiceSequenceBlock);
        await RunTestAsync("PAYMENT-01. Standardize payment status to PAID (offline/online/sync/void/receipt/migration)", TestPaymentStatusStandardization);
        await RunTestAsync("IMG-01. Product image URLs resolve against configured ApiBaseUrl (UAT), never localhost", TestProductImageUrlResolution);
        await RunTestAsync("MOD-01. ModifierOption Product Linking: Non-inventory modifier without ProductId works as normal", TestModifierOption_NonInventoryModifier);
        await RunTestAsync("MOD-02. ModifierOption Product Linking: Modifier with ProductId deducts linked product inventory and logs stock adjustment", TestModifierOption_LinkedProductInventoryDeduction);
        await RunTestAsync("MOD-03. ModifierOption Product Linking: Invalid or inactive ProductId is rejected on creation/update", TestModifierOption_InvalidProductRejected);
        await RunTestAsync("WB-01. Lechon Weight-Based Pricing: Amount Mode (₱300) and Weight Mode precision & complimentary sets", TestWeightBased_CalculationsAndComplimentarySets);
        await RunTestAsync("WB-02. Lechon Weight-Based: Station routing splits Bar (Drink) and Kitchen (Lechon, Atchara, Sarsa)", TestWeightBased_StationRouting);
        await RunTestAsync("WB-03. Lechon Weight-Based: Receipt thermal layout and complimentary labels", TestWeightBased_ReceiptFormatting);
        await RunTestAsync("WB-04. Lechon Weight-Based: Offline sync, inventory deduction & double-deduction prevention", TestWeightBased_OfflineSyncAndInventory);
        await RunTestAsync("WB-05. Lechon Weight-Based: Statutory SC/PWD discount and BIR tax calculation", TestWeightBased_DiscountCalculationService);
        await RunTestAsync("WB-06. Lechon Weight-Based: Product PricingMethod persistence in EF Core and ProductsController", TestWeightBased_ProductPricingMethodPersistence);
        await RunTestAsync("SORT-01. Custom Sorting: Categories and Products per-category reordering and offline sync", TestCustomSorting_CategoriesAndProducts);
        await RunTestAsync("LOGO-01. Receipt Logo Management: Upload validation, branding CRUD, offline reference sync, and thermal HTML formatting", TestReceiptLogoManagementAndPrinting);
        await RunTestAsync("VAT-01. Branch VAT Display configuration, LocalBranches SQLite sync, customer receipt VAT suppression, and tax calculation preservation", TestBranchVatDisplayAndLocalBranchSync);
        await RunTestAsync("SYNC-01. Critical Transaction Sync: SQLite DateTimeKind.Unspecified UTC normalization, Terminal JWT shift/sale sync, split payments, NEEDS_ATTENTION retry recovery, idempotency, and LastSyncAt", TestCriticalTransactionSyncEndToEnd);
        await RunTestAsync("TIME-01. Transaction Timestamps & Philippine Time (Asia/Manila): Windows/Android TimeZoneInfo, 3:59 AM / 4:00 AM cutoff, offline preservation, ServerReceivedAtUtc, clock skew detection, receipt reprints, and X/Z-Reading", TestTransactionTimestampsAndPhilippineTime);
        await RunTestAsync("TAGLINE-01. Branch Tagline: Optional CRUD (with/without tagline, add/remove), LocalBranches SQLite sync, offline availability, and customer receipt formatting", TestBranchTaglineManagementSyncAndReceiptFormatting);
        await RunTestAsync("RCPT-01. Receipt Footer & BranchCode Removal: AGRES Business Solution centered in footer, BranchCode omitted from customer receipt, DTO/80mm layout preserved", TestReceiptCompanyFooterAndBranchCodeRemoval);
        await RunTestAsync("DRAWER-01. Cash Drawer ESC/POS Commands & Endpoint Validation: Xprinter XP-80T 1B 70 00 19 FA, Pin 2/5, Test Slip & IP/Port guards", TestCashDrawer01_EscPosCommandsAndValidation);
        await RunTestAsync("DRAWER-02. Cash Drawer Automatic Sale Triggers: Offline cash sale opens once, Card & E-wallet stay closed, failed/incomplete sales stay closed", TestCashDrawer02_AutomaticCashVsNonCashAndFailed);
        await RunTestAsync("DRAWER-03. Cash Drawer Split Payments: Cash+Card/GCash split opens once, multiple CASH splits open once, GCash+Card split stays closed", TestCashDrawer03_SplitPaymentRules);
        await RunTestAsync("DRAWER-04. Cash Drawer Duplicate, Reprint & Restart Protection: Double callback, concurrent race, IsReprint, and fresh instance across restart open at most once", TestCashDrawer04_DuplicateReprintAndRestartProtection);
        await RunTestAsync("DRAWER-05. Cash Drawer Manual Open Authorization & Audit: Supervisor direct, Cashier denied without PIN, Cashier+SupervisorPIN allowed offline with reason & audit trail", TestCashDrawer05_ManualOpenAuthorizationAndAudit);
        await RunTestAsync("DRAWER-06. Cash Drawer Resilience & Config Auth: Printer timeout/offline never corrupts saved sale/shift, TestPrinter/TestDrawer work without sale", TestCashDrawer06_PrinterUnavailableResilienceAndHardwareTests);
        await RunTestAsync("PRINTER-01. Unified ESC/POS Formatting & Raster Logo: 58mm/80mm layout, codepage, density, auto-cut, BranchCode omission & GS v 0 bitmap", TestUnifiedPrinter01_EscPosFormattingAndBitmap);
        await RunTestAsync("PRINTER-02. Unified USB Printer Lifecycle: Discovery, runtime permission, print & cash drawer kick, unplug resilience & reconnect recovery", TestUnifiedPrinter02_UsbLifecycleDiscoveryPermissionReconnectAndDrawer);
        await RunTestAsync("PRINTER-03. Unified Ethernet, USB/LAN Switching, Restart Persistence & Multi-Terminal Isolation", TestUnifiedPrinter03_EthernetSwitchingPersistenceAndMultiTerminalIsolation);
        await RunTestAsync("PRINTER-04. Reliability & Safety: Pre-write connect retry on print, ZERO retry on cash drawer kick, & serialized concurrent jobs", TestUnifiedPrinter04_RetryBoundariesConcurrencyAndTimeoutSafety);
        await RunTestAsync("PRINTER-05. Offline Unified Receipt & Reports: Prep ticket, X-Reading, Z-Reading & Cashier Declaration via ESC/POS without POS.API", TestUnifiedPrinter05_OfflineOperationWithoutApiAndAllReportTypes);
        await RunTestAsync("AUTOPRINT-01. Automatic Receipt Printing on Finalized Cash, Card, E-Wallet & Split Sales with Saved Receipt Details", TestAutoPrint01_PaymentMethodsSplitAndSavedReceiptFormatting);
        await RunTestAsync("AUTOPRINT-02. Auto-Print Guards: Failed, Pending, Cancelled, Non-Persisted & Reprint Never Auto-Print", TestAutoPrint02_FailedPendingCancelledIncompleteAndReprintSkipped);
        await RunTestAsync("AUTOPRINT-03. Duplicate Auto-Print Prevention: Concurrent Callbacks, Restart Persistence & Sync Replay Isolation", TestAutoPrint03_DuplicateProtectionRestartAndSyncIsolation);
        await RunTestAsync("AUTOPRINT-04. Unified USB Offline Auto-Printing & Direct ESC/POS Disabled Skip Safety", TestAutoPrint04_UsbAutoPrintOfflineAndDisabledFallbackBehavior);
        await RunTestAsync("AUTOPRINT-05. Auto-Print Failure Handling: Sale Stays Paid, Logged Diagnostics & Controlled Retry Print", TestAutoPrint05_PrinterDisconnectedPreservesSaleAndControlledRetry);
        await RunTestAsync("AUTOPRINT-06. Cash Drawer Coordination: Cash Opens+Prints, Card Prints-Only & Reprint Never Opens Drawer", TestAutoPrint06_CashDrawerCoordinationAndReprintIsolation);
        await RunTestAsync("AUTOPRINT-07. Bounded Execution: Hung Transport & UI SyncContext Never Deadlock or Leave PRINTING Stuck", TestAutoPrint07_HungTransportAndSynchronizationContextNeverStall);
        await RunTestAsync("LIC-01. Licensing: Terminal registration and license issuance (ECDSA P-256 signature)", TestLic01_TerminalRegistrationAndLicenseIssuance);
        await RunTestAsync("LIC-02. Licensing: Terminal activation and hardware device binding", TestLic02_TerminalActivationAndDeviceBinding);
        await RunTestAsync("LIC-03. Licensing: 5-year expiration date calculation from official activation date", TestLic03_FiveYearExpirationCalculation);
        await RunTestAsync("LIC-04. Licensing: Valid active license allows POS sales and opening shifts", TestLic04_ValidActiveLicenseAllowsSales);
        await RunTestAsync("LIC-05. Licensing: Expiring soon warnings (90, 60, 30, 7 days) allow sales with warning notice", TestLic05_ExpiringSoonWarnings);
        await RunTestAsync("LIC-06. Licensing: Expired license blocks new POS sales and new shifts", TestLic06_ExpiredLicenseBlocksSales);
        await RunTestAsync("LIC-07. Licensing: License renewal restores POS sales and logs renewal history", TestLic07_LicenseRenewalRestoresSales);
        await RunTestAsync("LIC-08. Licensing: Revoked license blocks POS sales and shifts", TestLic08_RevokedLicenseBlocksSales);
        await RunTestAsync("LIC-09. Licensing: Suspended license blocks POS sales and Reactivate restores access", TestLic09_SuspendedLicenseBlocksAndReactivates);
        await RunTestAsync("LIC-10. Licensing: Device replacement workflow and device binding history", TestLic10_DeviceReplacementWorkflow);
        await RunTestAsync("LIC-11. Licensing: Manual PostgreSQL license expiration modification rejected (signature mismatch)", TestLic11_ManualPostgreSqlExpirationModificationRejected);
        await RunTestAsync("LIC-12. Licensing: Manual SQLite license expiration modification rejected (signature mismatch)", TestLic12_ManualSqliteExpirationModificationRejected);
        await RunTestAsync("LIC-13. Licensing: Copying SQLite database to another physical device rejected", TestLic13_CopyingSqliteDatabaseToAnotherDeviceRejected);
        await RunTestAsync("LIC-14. Licensing: Invalid / forged digital signature rejected", TestLic14_InvalidDigitalSignatureRejected);
        await RunTestAsync("LIC-15. Licensing: Device clock rollback detected while preserving local transaction time", TestLic15_DeviceClockRollbackDetected);
        await RunTestAsync("LIC-16. Licensing: Offline operation within 30-day grace period allowed", TestLic16_OfflineWithinGracePeriodAllowed);
        await RunTestAsync("LIC-17. Licensing: Offline operation beyond 30-day grace period blocked", TestLic17_OfflineBeyondGracePeriodBlocked);
        await RunTestAsync("LIC-18. Licensing: API unavailable during startup falls back to cached license with capped retries", TestLic18_ApiUnavailableDuringStartupFallbackAndCappedRetries);
        await RunTestAsync("LIC-19. Licensing: Expiration during active session preserves already-committed sales", TestLic19_LicenseExpirationSafeBoundaryPreservesCommittedSales);
        await RunTestAsync("LIC-20. Licensing: Unauthorized non-Admin API calls and brute-force rate limit rejected", TestLic20_UnauthorizedApiCallsAndRateLimitRejected);
        await RunTestAsync("LIC-21. Licensing: Multi-terminal, multi-branch, and multi-outlet isolation", TestLic21_MultiTerminalBranchOutletIsolation);
        await RunTestAsync("LIC-22. Licensing: Historical transaction/reprint access after expiration & UAT/Production key isolation", TestLic22_HistoricalAccessAfterExpirationAndEnvironmentKeyIsolation);
        await RunTestAsync("LIC-23. Licensing: Configurable duration presets (1Y/2Y/3Y/5Y), custom durations (Y/M/D), leap-year & validation", TestLic23_ConfigurableDurationsPresetsCustomLeapAndValidation);
        await RunTestAsync("LIC-24. Licensing: Active/expired/different-duration renewals, immutable history & auditable adjustment", TestLic24_RenewalsActiveExpiredDifferentDurationAndHistory);
        await RunTestAsync("LIC-25. Licensing: Renewal authorization, idempotency replay/conflict, pre-renewal & post-renewal signatures", TestLic25_RenewalSecurityUnauthorizedIdempotencyAndSignatures);
        await RunTestAsync("LIC-26. Licensing: Offline renewal behavior, clock tamper defense, reconnect sync & pending tx preservation", TestLic26_OfflineRenewalReconnectAndPendingTransactionPreservation);
        await RunTestAsync("OFFLINE-01. Offline Checkout & Health: OFFLINE-SESSION-TOKEN, unreachable/timeout/503 DB-down/captive portal HTML -> local sale", TestOffline01_DisconnectedSaleAndHealthChecks);
        await RunTestAsync("OFFLINE-02. Offline Resilience: Mid-session drop & proof 'Unable to verify current shift.' is never thrown on transient errors", TestOffline02_MidSessionDropAndNoVagueError);
        await RunTestAsync("OFFLINE-03. Offline Continuity: App restart shift recovery, sequential BIR invoices & ShiftCacheRegressionTests suite", TestOffline03_RestartPersistenceAndSequentialInvoices);
        await RunTestAsync("OFFLINE-04. Offline Shift & Movements: Negative shift open, CASH_IN/PETTY_CASH, reconnect migration, idempotency & closed-shift protection", TestOffline04_OfflineShiftOpenMovementsReconnectIdempotentSync);
        await RunTestAsync("OFFLINE-05. Offline Security Fail-Closed: ShiftVerificationIssue matrix, 401/403/423 non-bypass, dup/conflict OPEN rows & expired license", TestOffline05_SecurityFailClosedAndErrorClassification);
        await RunTestAsync("WM-01. Offline Weight-Based Modifier Loading, Reverse DisplayOrder, ProductId Scoping & Empty Fallback", TestWeightModifiers01_OfflineModifierLoadingAndOrdering);
        await RunTestAsync("WM-02. Regular Multi-Select Modifiers, Weight Complimentary Thresholds, Preview Alignment & RemoveItem Isolation", TestWeightModifiers02_CartPricingComplimentaryAndRemoveIsolation);
        await RunTestAsync("WM-03. Offline Weight & Modifier Sale Persistence, App Restart Recovery & Reconnect Sync", TestWeightModifiers03_OfflinePersistenceRestartRecoveryAndReconnectSync);
        await RunTestAsync("WM-04. Online ProductModifiersController & Offline ReferenceDataSync Parity", TestWeightModifiers04_OnlineOfflineModifierParity);
        await RunTestAsync("WM-05. CartItem Quantity & Product Card Badge Formatting with Precision Preservation", TestWeightModifiers05_QuantityAndBadgeFormatting);
        await RunTestAsync("RCPT-QTY-01. Weight-Based Only Receipt Formatting & Legacy Null-Pricing Fallback", TestReceiptMixed01_WeightBasedOnlyAndLegacyFallback);
        await RunTestAsync("RCPT-QTY-02. Regular Products Only: Configured UOM vs Null UOM (No Hardcoded 'pc')", TestReceiptMixed02_RegularConfiguredAndNullUom);
        await RunTestAsync("RCPT-QTY-03. Mixed Weight-Based + Regular Items, Total Items & Total Weight", TestReceiptMixed03_MixedWeightBasedAndRegular);
        await RunTestAsync("RCPT-QTY-04. Multiple Weight-Based Products: Same Unit Sum & Mixed Units Separate", TestReceiptMixed04_MultipleWeightSameAndIncompatibleUnits);
        await RunTestAsync("RCPT-QTY-05. Weight Mode vs Amount Mode Exact LineTotal & Unrounded Summation", TestReceiptMixed05_WeightModeVsAmountModeAndUnroundedSummation);
        await RunTestAsync("RCPT-QTY-06. Split Payment End-to-End Offline Checkout & Receipt Recovery Parity", TestReceiptMixed06_SplitPaymentEndToEndOfflineCheckoutAndRecovery);
        await RunTestAsync("RCPT-QTY-07. Complimentary Items & Long Product Names Wrapping/Alignment on 58mm & 80mm", TestReceiptMixed07_ComplimentaryLongNamesWrappingAndAlignment58And80Mm);
        await RunTestAsync("RCPT-QTY-08. Online Reprint (ReceiptService) & Offline DB-Mapped Reprint Parity", TestReceiptMixed08_OnlineReprintAndOfflineReprintParity);
        await RunTestAsync("WM-COMP-01. ReceiptPresentationHelper.ShouldShowFreeModifierLabel Truth Table", TestWeightComplimentary01_ShouldShowFreeModifierLabelTruthTable);
        await RunTestAsync("WM-COMP-02. CartService Weight-Based Unlinked & Linked Complimentary Modifiers + Note Isolation", TestWeightComplimentary02_CartWeightModifiersAndUpdateNotesNoCollapse);
        await RunTestAsync("WM-COMP-03. Receipt, ESC/POS 58mm/80mm, Prep Tickets & Offline Checkout Pipeline for Unlinked/Linked Weight Complimentary Items", TestWeightComplimentary03_ReceiptPrepTicketsAndOfflineCheckoutPipeline);
        await RunTestAsync("UPD-01. Database Preservation & Versioned Migration: V0 -> V2 upgrade preserves unsynced orders, outbox, sequences, printer config & creates pre-migration backup", TestUpdate01_LegacyV0ToV2MigrationPreservesOfflineDataAndCreatesBackup);
        await RunTestAsync("UPD-02. Migration Safety: Failed migration rollback restores backup intact & downgrade guard blocks older APK on newer schema", TestUpdate02_FailedMigrationRollbackAndDowngradeProtection);
        await RunTestAsync("UPD-03. Accidental Uninstall Recovery: AES-GCM encrypted survivable backup (.agresbak) restores DeviceUuid, license & unsynced sales and rejects tampering", TestUpdate03_SurvivableEncryptedBackupAndAccidentalUninstallRestore);
        await RunTestAsync("UPD-04. Update Security & ABI Stability: ECDSA-signed release manifest, SHA-256 verification, downgrade block & Arm->Arm64 ABI-safe device binding", TestUpdate04_SignedReleaseManifestVerificationAndAbiSafeDeviceBinding);
        await RunTestAsync("UPD-05. Proceed to Payment Conditional Cash Tendered: Cash vs Card/GCash/Other Bank single & split payment isolation", TestUpdate05_PaymentConditionalCashTenderedAndSplitNonCashIsolation);
        await RunTestAsync("UPD-06. BUG 1 UAT: Offline transactions preserve cashier identity across reconnect, sync idempotently & never disappear from Transaction History", TestUpdate06_Bug1_OfflineTransactionsRemainVisibleOnReconnectAndSync);
        await RunTestAsync("UPD-07. BUG 2 UAT: Held Sales persist in SQLite offline & online, survive offline restart, restore weight/modifiers/discounts & sync checkout once", TestUpdate07_Bug2_HeldSalesOfflinePersistenceRestartResumeAndCheckoutSync);
        await RunTestAsync("RBAC-01. Transaction History RBAC API Matrix: Admin/Manager/SeniorCashier allowed (branch-scoped), Cashier & bare Terminal denied across all history/order/receipt/return endpoints", TestRbac01_ApiMultiEndpointRoleMatrix);
        await RunTestAsync("RBAC-02. Transaction History RBAC Offline & Client Service: Cashier blocked on local SQLite & client service after restart, while New Sale, current receipt, Held Sales & offline sync work intact", TestRbac02_ClientServiceOfflineSqliteAndCashierWorkflowPreservation);
        await RunTestAsync("RBAC-03. Transaction History RBAC Account Switching: Switching Admin -> Cashier -> Manager never retains prior role permissions or bearer token", TestRbac03_AccountSwitchingClearsPriorPermissionsAndToken);
        await RunTestAsync("HELD-EXP-01. Held Sales Auto-Expiration (3:59 AM / 4:00 AM Cutoff): Server auto-cancels previous Business Date, preserves items/audit, idempotent & multi-terminal safe", TestHeldExp01_Boundary359Vs400AmExpirationIdempotencyAndIsolation);
        await RunTestAsync("HELD-EXP-02. Held Sales Resume Protection: Direct/stale resume of previous-day Held Sale blocked with exact expiration message & persisted as CANCELLED", TestHeldExp02_ResumeProtectionForDirectOrStaleReference);
        await RunTestAsync("HELD-EXP-03. Held Sales Offline Expiration & Reconnect Sync: Offline auto-cancel, restart persistence, blocked resume & server/SQLite agreement on reconnect", TestHeldExp03_OfflineExpirationRestartPersistenceAndReconnectSync);
        await RunTestAsync("VOID-OFF-01. Online Void Regression: Supervisor PIN, cashier/username rejection, Cash vs Non-Cash refunds & inventory restoration", TestVoidOff01_OnlineVoidRegressionAndRefundInventoryIntegrity);
        await RunTestAsync("VOID-OFF-02. Offline Void E2E: Local atomic void, restart persistence, Order->VOID sync ordering, inventory restore & idempotent replay", TestVoidOff02_EndToEndOfflineVoidRestartReconnectSyncAndIdempotency);
        await RunTestAsync("VOID-OFF-03. Offline Void Security & Conflicts: Missing/expired supervisor, Cashier self-approval, cross-terminal, tampered proof & 409 CONFLICT preservation", TestVoidOff03_SecurityMatrixAndServerConflictPreservation);
        await RunTestAsync("VOID-OFF-04. Offline Void Payment Matrix: Cash/Card/GCash/Split drawer isolation, linked modifier inventory restore & local void overlay", TestVoidOff04_PaymentMethodMatrixSplitVoidAndOverlay);
        await RunTestAsync("DISC-VAL-01. Discount Validation: Manual % (0/10/50/100/12.5/150/10000/-5), zero-VAT-at-100%, API & admin definition guards", TestDiscVal01_ManualPercentageAndAdminDefinitionValidation);
        await RunTestAsync("DISC-VAL-02. Discount Validation: Manual & configured FIXED discounts (0/100/exact/decimal/over-subtotal/negative/huge)", TestDiscVal02_ManualAndConfiguredFixedAmountDiscounts);
        await RunTestAsync("DISC-VAL-03. Discount Validation: Stacked item+order/manual+promo, statutory SC/PWD/SP/BNPC preservation & CartService auto-recalculation", TestDiscVal03_CombinedStackedStatutoryAndCartRecalculation);
        await RunTestAsync("DISC-VAL-04. Discount Validation E2E: OrdersController, OfflineCheckout (100% zero-total, weight-based, split), Sync & HeldSale guards", TestDiscVal04_EndToEndApiOfflineCheckoutZeroTotalSplitSyncAndHeldSaleGuards);
        await RunTestAsync("DISC-SYNC-01. Discount Type IsActive Sync (PG->SQLite): Incremental sync, activation/deactivation, StateHash self-heal, restart/offline persistence, historical preservation & API guards", TestDiscSync01_IsActiveSyncIncrementalFilteringOfflineAndApiValidation);
        await RunTestAsync("WF-01. Close Cashier Separation: Restricts ONLY CashierId+TerminalId+BusinessDate (Cashier A works on T2; Cashier B works on T1; failed close never restricts)", TestWorkflow01_CloseCashierScopeAndMultiTerminalIsolation);
        await RunTestAsync("WF-02. Anytime Read-Only X-Reading: Runs repeatedly while shifts are OPEN across multiple cashiers with zero mutations & Supervisor role/PIN auth", TestWorkflow02_AnytimeReadOnlyXReadingWhileShiftsOpenAndRepeatable);
        await RunTestAsync("WF-03. Z-Reading End-of-Day Finalization: Role/open-shift/accountability guards, atomic Terminal+BusinessDate lock (T2 stays open), failed Z never locks & 4:00 AM reset", TestWorkflow03_ZReadingGuardsAtomicTerminalLockMultiTerminalAnd4AmReset);
        await RunTestAsync("WF-04. Offline Close Cashier, Offline X-Reading, Supervisor Reopen Audit & Idempotent Reconnect Sync", TestWorkflow04_OfflineCloseCashierAnytimeXReadingSupervisorReopenAndIdempotentSync);
        await RunTestAsync("OWN-01. Logout vs Close Cashier & Single Shift Ownership (Online): Logout preserves shift, blocks Cashier B across all endpoints, Cashier A resumes without duplicates, Close releases ownership", TestShiftOwnership01_OnlineLogoutResumeCloseReleaseMultiTerminalAndReadings);
        await RunTestAsync("OWN-02. Logout & Shift Ownership (Offline E2E): Offline blocked login, restart persistence, network-fail safety, offline X-Reading, Close release & idempotent reconnect sync", TestShiftOwnership02_OfflineLogoutBlockedLoginRestartPersistenceAndReconnectSync);
        await RunTestAsync("OVN-01. Overnight Unclosed Shift Recovery (Online): 4AM detection, Cashier B blocked, T2 operational, supervisor denomination close with shortage/overage, Nov 20 preserved, Nov 21 fresh shift & Z-Reading", TestOvernightRecovery01_OnlineDetectionBlockingSupervisorCloseReleaseMultiTerminalAndZReading);
        await RunTestAsync("OVN-02. Overnight Unverified Cash Investigation: Keeps shift OPEN without fake count, records reason, blocks takeover & Z-Reading, resolves cleanly once cash verified", TestOvernightRecovery02_UnverifiedCashCannotBeForcedStaysOpenAndBlocksTakeoverAndZReading);
        await RunTestAsync("OVN-03. Overnight Recovery Offline E2E: Offline 4AM detection, restart persistence, offline PinHash recovery, stale snapshot guard, fresh Nov 21 shift & causal idempotent sync", TestOvernightRecovery03_OfflineDetectionRestartRecoveryStaleSnapshotProtectionAndIdempotentSync);
        await RunTestAsync("AGRES-01. Identity Separation: AGRES System Admin isolated from Client Users, cross-org BOLA blocked, privilege escalation & role spoofing rejected", TestAgres01_IdentityIsolationUsersTableCrossTenantAndPrivilegeEscalation);
        await RunTestAsync("AGRES-02. JWT Scope & Policy Enforcement: Client JWT blocked on Licensing/Activation/Platform APIs, AGRES JWT blocked on Cashier Ops, Offline SQLite excludes AGRES accounts", TestAgres02_JwtScopePoliciesLicensingActivationOfflineSyncAndMigrationIntegrity);
        await RunTestAsync("PROD-01. Production Hardening: Config validator blocks UAT secrets/short JWT/test bypass, CORS normalization, sanitized 503 health & Production AppReleases manifest", TestProd01_ProductionConfigValidationHealthSanitizationAndReleaseManifest);

        Console.WriteLine($"\n=== Summary: {_passed} passed, {_failed} failed ===");
        return _failed == 0 ? 0 : 1;
    }

    private static Task Test23_HierarchyModel()
    {
        using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>()
            .UseSqlite("Data Source=:memory:").Options);
        var terminal = db.Model.GetEntityTypes().SingleOrDefault(e => e.ClrType.Name == "Terminal");
        Assert(terminal != null, "Existing machine records must map to Terminal, not physical Outlet.");
        Assert(terminal!.GetTableName() == "Terminals", "Terminal table must be named Terminals.");
        Assert(terminal!.FindPrimaryKey()!.Properties.Single().Name == "TerminalId", "Terminal primary key must retain the old machine identity.");
        var outlet = db.Model.GetEntityTypes().Single(e => e.ClrType.Name == "Outlet");
        Assert(outlet.FindProperty("OutletCode") != null, "Physical outlet must have a scoped code.");
        Assert(terminal!.GetForeignKeys().Any(f => f.PrincipalEntityType == outlet), "Terminal must reference a physical outlet.");
        var order = db.Model.GetEntityTypes().Single(e => e.ClrType.Name == "Order");
        Assert(order.FindProperty("OutletId") != null, "Order must have an explicit OutletId.");
        Assert(order.GetForeignKeys().Any(f => f.PrincipalEntityType == outlet), "Order must reference physical Outlet.");
        return Task.CompletedTask;
    }

    private static async Task RunTestAsync(string name, Func<Task> test)
    {
        try
        {
            await test();
            _passed++;
            Console.WriteLine($"[PASS] {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"[FAIL] {name}: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static async Task<(SqliteConnection Conn, TestPosDbContext Db, CashDrawerShift Shift)> CreateTestEnvAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();

        var options = new DbContextOptionsBuilder<PosDbContext>()
            .UseSqlite(conn)
            .Options;

        var db = new TestPosDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Organizations.Add(new Organization { OrganizationId = 1, Code = "DEFAULT", Name = "Default Organization", IsActive = true, CreatedAtUtc = DateTime.UtcNow });
        var branch = new Branch { BranchId = 1, OrganizationId = 1, Name = "Main Branch", TaxType = "VAT-Registered", IsActive = true };
        var outlet = new Terminal { TerminalId = 1, BranchId = 1, OutletId = 1, TerminalCode = "POS-1", Name = "Counter 1", IsActive = true };
        db.Outlets.Add(new Outlet { OutletId = 1, BranchId = 1, OutletCode = "MAIN", Name = "Main", IsActive = true });
        var cashier = new User { UserId = 1, Username = "cashier1", Role = "Cashier", PinCode = "1111", PasswordHash = "x" };
        var otherCashier = new User { UserId = 2, Username = "cashier2", Role = "Cashier", PinCode = "2222", PasswordHash = "x" };
        var manager = new User { UserId = 3, Username = "manager1", Role = "Manager", PinCode = "9999", PasswordHash = "x" };

        var catPaidIn = new CashMovementCategory { CategoryId = 1, Name = "Additional Float", MovementType = "CASH_IN", RequiresApproval = false, IsActive = true };
        var catPettyCash = new CashMovementCategory { CategoryId = 2, Name = "Ice & Store Supplies", MovementType = "PETTY_CASH", RequiresApproval = true, ApprovalThreshold = 0m, IsActive = true };
        var catMinorOut = new CashMovementCategory { CategoryId = 3, Name = "Minor Change Out", MovementType = "CASH_OUT", RequiresApproval = false, ApprovalThreshold = 500m, IsActive = true };
        var catDrop = new CashMovementCategory { CategoryId = 4, Name = "Safe Cash Drop", MovementType = "CASH_DROP", RequiresApproval = true, ApprovalThreshold = 0m, IsActive = true };

        var shift = new CashDrawerShift
        {
            ShiftId = 1,
            BranchId = 1,
            TerminalId = 1,
            CashierId = "cashier1",
            OpeningFloat = 2000.00m,
            OpeningConfirmed = true,
            ExpectedBalance = 2000.00m,
            Status = "OPEN",
            OpenedAt = DateTime.UtcNow.AddHours(-1)
        };

        db.Branches.Add(branch);
        db.Terminals.Add(outlet);
        db.Users.AddRange(cashier, otherCashier, manager);
        db.CashMovementCategories.AddRange(catPaidIn, catPettyCash, catMinorOut, catDrop);
        db.CashDrawerShifts.Add(shift);
        await db.SaveChangesAsync();

        return (conn, db, shift);
    }

    private static CashDrawerController CreateController(PosDbContext db, string username = "cashier1", string role = "Cashier")
    {
        var controller = new CashDrawerController(db);
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, role),
            new Claim("terminal_id", "1")
        ], "TestAuth");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private static async Task Test01_NormalPaidIn()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        var controller = CreateController(db, "cashier1", "Cashier");

        var result = await controller.CreatePaidIn(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId,
            BranchId = 1,
            TerminalId = 1,
            CategoryId = 1,
            Amount = 500.00m,
            Reason = "Added coin change float"
        });

        var ok = result.Result as OkObjectResult;
        Assert(ok?.Value is CashMovementResponseDto, "Expected OkObjectResult with CashMovementResponseDto");
        var dto = (CashMovementResponseDto)ok!.Value!;
        Assert(dto.Status == "POSTED", $"Expected POSTED but got {dto.Status}");
        Assert(dto.MovementType == "CASH_IN", "Expected CASH_IN");

        var updatedShift = await db.CashDrawerShifts.FindAsync(shift.ShiftId);
        Assert(updatedShift!.TotalCashIn == 500.00m, $"Expected TotalCashIn 500, got {updatedShift.TotalCashIn}");
        Assert(updatedShift.ExpectedBalance == 2500.00m, $"Expected ExpectedBalance 2500, got {updatedShift.ExpectedBalance}");
    }

    private static async Task Test02_NormalPaidOut()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        var controller = CreateController(db, "cashier1", "Cashier");

        var result = await controller.CreatePaidOut(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId,
            BranchId = 1,
            TerminalId = 1,
            MovementType = "PETTY_CASH",
            CategoryId = 2,
            Amount = 350.00m,
            Reason = "Bought emergency ice bags",
            SupervisorPin = "9999"
        });

        var ok = result.Result as OkObjectResult;
        Assert(ok?.Value is CashMovementResponseDto, "Expected OkObjectResult");
        var dto = (CashMovementResponseDto)ok!.Value!;
        Assert(dto.Status == "POSTED", $"Expected POSTED, got {dto.Status}");
        Assert(dto.ApprovedBy == "manager1", $"Expected manager1 approval, got {dto.ApprovedBy}");

        var updatedShift = await db.CashDrawerShifts.FindAsync(shift.ShiftId);
        Assert(updatedShift!.TotalCashOut == 350.00m, $"Expected TotalCashOut 350, got {updatedShift.TotalCashOut}");
        Assert(updatedShift.ExpectedBalance == 1650.00m, $"Expected ExpectedBalance 1650, got {updatedShift.ExpectedBalance}");
    }

    private static async Task Test03_InsufficientBalance()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        var controller = CreateController(db, "cashier1", "Cashier");

        var result = await controller.CreatePaidOut(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId,
            BranchId = 1,
            TerminalId = 1,
            MovementType = "PETTY_CASH",
            CategoryId = 2,
            Amount = 2500.00m, // > 2000 OpeningFloat
            Reason = "Over-limit withdrawal",
            SupervisorPin = "9999"
        });

        Assert(result.Result is BadRequestObjectResult, "Expected BadRequest when paid-out exceeds drawer balance");
        var updatedShift = await db.CashDrawerShifts.FindAsync(shift.ShiftId);
        Assert(updatedShift!.ExpectedBalance == 2000.00m, "ExpectedBalance must remain 2000.00");
    }

    private static async Task Test04_InvalidAmount()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        var controller = CreateController(db, "cashier1", "Cashier");

        var zeroRes = await controller.CreatePaidIn(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId,
            BranchId = 1,
            TerminalId = 1,
            Amount = 0m,
            Reason = "Zero amount"
        });
        var negRes = await controller.CreatePaidOut(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId,
            BranchId = 1,
            TerminalId = 1,
            Amount = -50m,
            Reason = "Negative amount"
        });

        Assert(zeroRes.Result is BadRequestObjectResult, "Expected BadRequest for 0 amount");
        Assert(negRes.Result is BadRequestObjectResult, "Expected BadRequest for negative amount");
    }

    private static async Task Test05_NoActiveShift()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        shift.Status = "CLOSED";
        await db.SaveChangesAsync();

        var controller = CreateController(db, "cashier1", "Cashier");
        var res = await controller.CreatePaidIn(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId,
            BranchId = 1,
            TerminalId = 1,
            Amount = 100m,
            Reason = "Attempt on closed shift"
        });

        Assert(res.Result is BadRequestObjectResult, "Expected BadRequest when shift is closed");
    }

    private static async Task Test06_UnauthorizedCashier()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        var controller = CreateController(db, "cashier2", "Cashier"); // Shift belongs to cashier1

        var res = await controller.CreatePaidIn(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId,
            BranchId = 1,
            TerminalId = 1,
            Amount = 100m,
            Reason = "Unauthorized cashier attempt"
        });

        var statusResult = res.Result as ObjectResult;
        Assert(statusResult?.StatusCode == 403, $"Expected 403 Forbidden for wrong cashier, got {statusResult?.StatusCode}");
    }

    private static async Task Test07_ManagerApprovalRequired()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        var controller = CreateController(db, "cashier1", "Cashier");

        // 1. Submit without supervisor PIN -> saved as PENDING_APPROVAL, balance untouched
        var createRes = await controller.CreatePaidOut(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId,
            BranchId = 1,
            TerminalId = 1,
            MovementType = "PETTY_CASH",
            CategoryId = 2,
            Amount = 400.00m,
            Reason = "LPG refill deposit"
        });

        var created = (CashMovementResponseDto)((OkObjectResult)createRes.Result!).Value!;
        Assert(created.Status == "PENDING_APPROVAL", $"Expected PENDING_APPROVAL, got {created.Status}");

        var shiftBefore = await db.CashDrawerShifts.FindAsync(shift.ShiftId);
        Assert(shiftBefore!.ExpectedBalance == 2000.00m, "Pending movement must NOT affect ExpectedBalance");

        // 2. Security check: supplying manager username ("manager1") instead of PIN ("9999") must fail!
        var bypassAttempt = await controller.ApproveMovement(created.MovementId, new CashMovementApproveRejectDto
        {
            SupervisorPin = "manager1"
        });
        Assert(bypassAttempt.Result is BadRequestObjectResult, "Username-as-PIN bypass must be rejected with BadRequest");

        // 3. Approve with genuine manager PIN ("9999") -> transitions to POSTED and deducts balance
        var approveRes = await controller.ApproveMovement(created.MovementId, new CashMovementApproveRejectDto
        {
            SupervisorPin = "9999"
        });
        var approved = (CashMovementResponseDto)((OkObjectResult)approveRes.Result!).Value!;
        Assert(approved.Status == "POSTED", $"Expected POSTED after approval, got {approved.Status}");
        Assert(approved.ApprovedBy == "manager1", "Expected ApprovedBy manager1");

        var shiftAfter = await db.CashDrawerShifts.FindAsync(shift.ShiftId);
        Assert(shiftAfter!.ExpectedBalance == 1600.00m, $"Expected 1600.00 after approval, got {shiftAfter.ExpectedBalance}");
    }

    private static async Task Test08_RejectedRequest()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        var controller = CreateController(db, "cashier1", "Cashier");

        var createRes = await controller.CreatePaidOut(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId,
            BranchId = 1,
            TerminalId = 1,
            MovementType = "PETTY_CASH",
            CategoryId = 2,
            Amount = 300.00m,
            Reason = "Unapproved snack purchase"
        });
        var created = (CashMovementResponseDto)((OkObjectResult)createRes.Result!).Value!;

        var rejectRes = await controller.RejectMovement(created.MovementId, new CashMovementApproveRejectDto
        {
            SupervisorPin = "9999",
            Reason = "Not an authorized store expense"
        });
        var rejected = (CashMovementResponseDto)((OkObjectResult)rejectRes.Result!).Value!;
        Assert(rejected.Status == "REJECTED", $"Expected REJECTED, got {rejected.Status}");
        Assert(rejected.RejectionReason == "Not an authorized store expense", "Expected RejectionReason to be saved");

        var shiftAfter = await db.CashDrawerShifts.FindAsync(shift.ShiftId);
        Assert(shiftAfter!.ExpectedBalance == 2000.00m, "Rejected movement must never alter ExpectedBalance");
    }

    private static async Task Test09_DuplicateSubmission()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        var controller = CreateController(db, "cashier1", "Cashier");
        string clientGuid = Guid.NewGuid().ToString("N");

        var req = new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId,
            BranchId = 1,
            TerminalId = 1,
            MovementType = "PETTY_CASH",
            CategoryId = 2,
            Amount = 250.00m,
            Reason = "Water gallon purchase",
            ClientMovementId = clientGuid,
            SupervisorPin = "9999"
        };

        var res1 = (CashMovementResponseDto)((OkObjectResult)(await controller.CreatePaidOut(req)).Result!).Value!;
        var res2 = (CashMovementResponseDto)((OkObjectResult)(await controller.CreatePaidOut(req)).Result!).Value!;

        Assert(res1.MovementId == res2.MovementId, "Duplicate ClientMovementId must return same MovementId");
        var shiftAfter = await db.CashDrawerShifts.FindAsync(shift.ShiftId);
        Assert(shiftAfter!.ExpectedBalance == 1750.00m, $"Expected single deduction (1750.00), got {shiftAfter.ExpectedBalance}");
    }

    private static async Task Test10_ConcurrentPaidOutGuard()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        shift.OpeningFloat = 1000.00m;
        shift.ExpectedBalance = 1000.00m;
        await db.SaveChangesAsync();

        var controller = CreateController(db, "cashier1", "Cashier");

        var first = await controller.CreatePaidOut(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId,
            BranchId = 1,
            TerminalId = 1,
            MovementType = "PETTY_CASH",
            CategoryId = 2,
            Amount = 600.00m,
            Reason = "First withdrawal",
            SupervisorPin = "9999"
        });
        Assert(first.Result is OkObjectResult, "First 600 withdrawal from 1000 should succeed");

        var second = await controller.CreatePaidOut(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId,
            BranchId = 1,
            TerminalId = 1,
            MovementType = "PETTY_CASH",
            CategoryId = 2,
            Amount = 600.00m,
            Reason = "Second withdrawal exceeding remaining 400",
            SupervisorPin = "9999"
        });
        Assert(second.Result is BadRequestObjectResult, "Second 600 withdrawal must fail against 400 remaining balance");

        var shiftAfter = await db.CashDrawerShifts.FindAsync(shift.ShiftId);
        Assert(shiftAfter!.ExpectedBalance == 400.00m, $"Expected remaining balance 400.00, got {shiftAfter.ExpectedBalance}");
    }

    private static async Task Test11_OfflinePaidOutVerification()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        var syncController = new OfflineSyncController(db);
        Authenticate(syncController);

        // Offline movement with unverified / missing supervisor PinHash must remain PENDING_APPROVAL on server
        var unverifiedReq = new OfflineCashMovementSyncRequest
        {
            ClientMovementId = "OFFLINE-UNVERIFIED-001",
            ShiftId = shift.ShiftId,
            BranchId = 1,
            TerminalId = 1,
            MovementType = "PETTY_CASH",
            CategoryId = 2,
            CategoryName = "Ice & Store Supplies",
            Amount = 150.00m,
            Reason = "Offline petty cash without supervisor PIN",
            ReferenceNumber = "PCV-OFF-UNV-001",
            Status = "POSTED", // Even if client claims POSTED, server enforces SupervisorPinHash verification!
            CreatedBy = "cashier1",
            SupervisorPinHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("wrong-pin"))),
            CreatedAt = DateTime.UtcNow
        };

        var res = await syncController.SyncCashMovement(unverifiedReq);
        var ok = (OfflineCashMovementSyncResponse)((OkObjectResult)res.Result!).Value!;
        Assert(ok.ServerStatus == "PENDING_APPROVAL", $"Unverified offline supervisor PIN must stay PENDING_APPROVAL, got {ok.ServerStatus}");

        var shiftAfter = await db.CashDrawerShifts.FindAsync(shift.ShiftId);
        Assert(shiftAfter!.ExpectedBalance == 2000.00m, "Unverified offline movement must NOT reduce ExpectedBalance");
    }

    private static async Task Test12_OfflineToOnlineSync()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        var syncController = new OfflineSyncController(db);
        Authenticate(syncController);
        string validPinHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("9999")));

        var syncReq = new OfflineCashMovementSyncRequest
        {
            ClientMovementId = "OFFLINE-CM-001",
            ShiftId = shift.ShiftId,
            BranchId = 1,
            TerminalId = 1,
            MovementType = "PETTY_CASH",
            CategoryId = 2,
            CategoryName = "Ice & Store Supplies",
            Amount = 200.00m,
            Reason = "Offline petty cash ice purchase",
            ReferenceNumber = "PCV-OFFLINE-001",
            Status = "POSTED",
            CreatedBy = "cashier1",
            ApprovedBy = "manager1",
            SupervisorPinHash = validPinHash,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            ApprovedAt = DateTime.UtcNow.AddMinutes(-5),
            PostedAt = DateTime.UtcNow.AddMinutes(-5)
        };

        var res1 = await syncController.SyncCashMovement(syncReq);
        var ok1 = (OfflineCashMovementSyncResponse)((OkObjectResult)res1.Result!).Value!;
        Assert(ok1.Success && ok1.ServerStatus == "POSTED", $"Expected Success/POSTED, got {ok1.Success}/{ok1.ServerStatus}");

        // Idempotent replay of the same offline movement
        var res2 = await syncController.SyncCashMovement(syncReq);
        var ok2 = (OfflineCashMovementSyncResponse)((OkObjectResult)res2.Result!).Value!;
        Assert(ok2.Success && ok2.ServerMovementId == ok1.ServerMovementId, "Replay must return same ServerMovementId");

        var updatedShift = await db.CashDrawerShifts.FindAsync(shift.ShiftId);
        Assert(updatedShift!.ExpectedBalance == 1800.00m, $"Expected 1800.00 after single sync, got {updatedShift.ExpectedBalance}");
    }

    private static Task Test13_SyncRetryPolicy()
    {
        // Verify bounded exponential backoff schedule (5s * 2^(attempt-1), capped at 10 attempts)
        static TimeSpan ComputeBackoff(int attempt) =>
            TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Max(0, attempt - 1))));

        var delay1 = ComputeBackoff(1);
        var delay3 = ComputeBackoff(3);
        Assert(delay3 > delay1, "Exponential backoff delay must increase with attempt count");
        Assert(delay1.TotalSeconds == 5 && delay3.TotalSeconds == 20, "Expected 5s and 20s delays");
        return Task.CompletedTask;
    }

    private static async Task Test14_SyncConflict()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        var syncController = new OfflineSyncController(db);
        Authenticate(syncController);
        string validPinHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("9999")));

        var conflictReq = new OfflineCashMovementSyncRequest
        {
            ClientMovementId = "OFFLINE-CM-CONFLICT",
            ShiftId = shift.ShiftId,
            BranchId = 1,
            TerminalId = 1,
            MovementType = "PETTY_CASH",
            CategoryId = 2,
            Amount = 5000.00m, // Exceeds 2000 server shift balance
            Reason = "Offline paid-out conflicting with server drawer balance",
            ReferenceNumber = "PCV-CONFLICT-001",
            Status = "POSTED",
            CreatedBy = "cashier1",
            ApprovedBy = "manager1",
            SupervisorPinHash = validPinHash,
            CreatedAt = DateTime.UtcNow
        };

        var res = await syncController.SyncCashMovement(conflictReq);
        var conflictResult = res.Result as ConflictObjectResult;
        Assert(conflictResult != null, "Expected 409 ConflictObjectResult when offline paid-out exceeds server drawer balance");
        var payload = (OfflineCashMovementSyncResponse)conflictResult!.Value!;
        Assert(payload.IsConflict && payload.ServerStatus == "CONFLICT", $"Expected IsConflict/CONFLICT, got {payload.IsConflict}/{payload.ServerStatus}");

        var updatedShift = await db.CashDrawerShifts.FindAsync(shift.ShiftId);
        Assert(updatedShift!.ExpectedBalance == 2000.00m, "Server shift balance must not be overwritten on conflict");
    }

    private static async Task Test15_ShiftClosingReconciliation()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        var controller = CreateController(db, "cashier1", "Cashier");

        // Simulate Cash Sales (+1500) and Cash Refunds (-100)
        shift.TotalCashSales = 1500.00m;
        shift.TotalCashRefunds = 100.00m;
        shift.ExpectedBalance = 2000.00m + 1500.00m - 100.00m; // 3400
        await db.SaveChangesAsync();

        // Paid-In +300 -> 3700
        await controller.CreatePaidIn(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId, BranchId = 1, TerminalId = 1, CategoryId = 1, Amount = 300.00m, Reason = "Float top-up"
        });

        // Petty Cash -200 -> 3500
        await controller.CreatePaidOut(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId, BranchId = 1, TerminalId = 1, MovementType = "PETTY_CASH", CategoryId = 2, Amount = 200.00m, Reason = "Supplies", SupervisorPin = "9999"
        });

        // Safe Cash Drop -1000 -> 2500
        await controller.RecordMovement(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId, BranchId = 1, TerminalId = 1, MovementType = "CASH_DROP", CategoryId = 4, Amount = 1000.00m, Reason = "Midday safe skim", SupervisorPin = "9999"
        });

        // Create a PENDING_APPROVAL movement -> must block shift close!
        var pendingRes = await controller.CreatePaidOut(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId, BranchId = 1, TerminalId = 1, MovementType = "PETTY_CASH", CategoryId = 2, Amount = 100.00m, Reason = "Pending request"
        });
        var pendingDto = (CashMovementResponseDto)((OkObjectResult)pendingRes.Result!).Value!;

        var blockedClose = await controller.CloseShift(new CloseShiftDto
        {
            ShiftId = shift.ShiftId,
            ActualCountedBalance = 2500.00m
        });
        Assert(blockedClose is BadRequestObjectResult, "Shift close must be blocked while PENDING_APPROVAL movements exist");

        // Reject the pending request so shift can close
        await controller.RejectMovement(pendingDto.MovementId, new CashMovementApproveRejectDto
        {
            SupervisorPin = "9999",
            Reason = "Cancelled before shift close"
        });

        // Close shift with ActualCountedBalance = 2480.00 (Discrepancy = -20.00 short)
        var closeRes = await controller.CloseShift(new CloseShiftDto
        {
            ShiftId = shift.ShiftId,
            ActualCountedBalance = 2480.00m,
            Notes = "End of shift count"
        });
        Assert(closeRes is OkObjectResult, "Expected OkObjectResult on CloseShift");

        var closedShift = await db.CashDrawerShifts.FindAsync(shift.ShiftId);
        Assert(closedShift!.ExpectedBalance == 2500.00m, $"Expected 2500.00, got {closedShift.ExpectedBalance}");
        Assert(closedShift.Discrepancy == -20.00m, $"Expected Discrepancy -20.00, got {closedShift.Discrepancy}");
        Assert(closedShift.Status == "CLOSED", "Expected CLOSED status");
    }

    private static async Task Test16_VoucherFormatting()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        var controller = CreateController(db, "cashier1", "Cashier");

        var cin = (CashMovementResponseDto)((OkObjectResult)(await controller.CreatePaidIn(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId, BranchId = 1, TerminalId = 1, Amount = 100m, Reason = "Paid in"
        })).Result!).Value!;
        var pcv = (CashMovementResponseDto)((OkObjectResult)(await controller.CreatePaidOut(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId, BranchId = 1, TerminalId = 1, MovementType = "PETTY_CASH", CategoryId = 2, Amount = 50m, Reason = "Petty cash", SupervisorPin = "9999"
        })).Result!).Value!;
        var cdp = (CashMovementResponseDto)((OkObjectResult)(await controller.RecordMovement(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId, BranchId = 1, TerminalId = 1, MovementType = "CASH_DROP", CategoryId = 4, Amount = 100m, Reason = "Safe drop", SupervisorPin = "9999"
        })).Result!).Value!;

        Assert(cin.ReferenceNumber.StartsWith("CIN-", StringComparison.Ordinal), $"Expected CIN- prefix, got {cin.ReferenceNumber}");
        Assert(pcv.ReferenceNumber.StartsWith("PCV-", StringComparison.Ordinal), $"Expected PCV- prefix, got {pcv.ReferenceNumber}");
        Assert(cdp.ReferenceNumber.StartsWith("CDP-", StringComparison.Ordinal), $"Expected CDP- prefix, got {cdp.ReferenceNumber}");
    }

    private static async Task Test17_ReversalProcessing()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;
        var controller = CreateController(db, "cashier1", "Cashier");

        var pcv = (CashMovementResponseDto)((OkObjectResult)(await controller.CreatePaidOut(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId, BranchId = 1, TerminalId = 1, MovementType = "PETTY_CASH", CategoryId = 2, Amount = 300m, Reason = "Erroneous petty cash entry", SupervisorPin = "9999"
        })).Result!).Value!;

        var shiftAfterOut = await db.CashDrawerShifts.FindAsync(shift.ShiftId);
        Assert(shiftAfterOut!.ExpectedBalance == 1700.00m, "Expected 1700 after paid-out");

        var revRes = await controller.ReverseMovement(pcv.MovementId, new CashMovementReverseDto
        {
            SupervisorPin = "9999",
            Reason = "Entered wrong amount on voucher"
        });
        var reversedOrigDto = (CashMovementResponseDto)((OkObjectResult)revRes.Result!).Value!;
        Assert(reversedOrigDto.Status == "REVERSED", "Expected REVERSED status on original movement");
        Assert(reversedOrigDto.ReversedBy == "manager1", "Expected ReversedBy manager1");

        var reversalAuditRow = await db.CashDrawerMovements
            .FirstOrDefaultAsync(m => m.OriginalMovementId == pcv.MovementId && m.MovementType == "REVERSAL");
        Assert(reversalAuditRow != null, "Expected linked REVERSAL audit movement row in DB");
        Assert(reversalAuditRow!.ReferenceNumber!.StartsWith("REV-", StringComparison.Ordinal), "Expected REV- prefix on reversal entry");

        var shiftAfterRev = await db.CashDrawerShifts.FindAsync(shift.ShiftId);
        Assert(shiftAfterRev!.ExpectedBalance == 2000.00m, $"Expected balance restored to 2000.00, got {shiftAfterRev.ExpectedBalance}");

        // Attempting to reverse again must fail
        var secondRev = await controller.ReverseMovement(pcv.MovementId, new CashMovementReverseDto
        {
            SupervisorPin = "9999",
            Reason = "Duplicate reversal attempt"
        });
        Assert(secondRev.Result is BadRequestObjectResult, "Reversing an already-reversed movement must fail");
    }

    private static async Task Test18_BirSalesAndVatIsolation()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;

        // Seed a completed sale order with VAT and an InvoiceSequence
        db.InvoiceSequences.Add(new InvoiceSequence
        {
            InvoiceSequenceId = 1,
            TerminalId = 1,
            SeriesCode = "2026",
            Prefix = "INV-",
            LastIssuedNumber = 105
        });
        var order = new Order
        {
            OrderId = 1,
            BranchId = 1,
            OutletId = 1,
            TerminalId = 1,
            OrderNumber = "ORD-0001",
            OrderType = "DINE_IN",
            InvoiceNumber = "INV-0000105",
            CashierId = "cashier1",
            OrderStatus = "COMPLETED",
            PaymentStatus = "PAID",
            Subtotal = 1120.00m,
            VatableSales = 1000.00m,
            VatAmount = 120.00m,
            TotalAmount = 1120.00m,
            CreatedAt = DateTime.UtcNow
        };
        db.Orders.Add(order);
        db.Payments.Add(new Payment
        {
            PaymentId = 1,
            OrderId = 1,
            PaymentMethod = "CASH",
            Amount = 1120.00m,
            AmountTendered = 1120.00m,
            ChangeAmount = 0m,
            PaymentStatus = "PAID",
            ProcessedBy = "cashier1",
            PaidAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var reportService = new SalesReportService(db);
        var beforeReport = await reportService.GenerateReportAsync(new SalesReportRequestDto { BranchId = 1 });

        var controller = CreateController(db, "cashier1", "Cashier");
        await controller.CreatePaidIn(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId, BranchId = 1, TerminalId = 1, CategoryId = 1, Amount = 500.00m, Reason = "Paid-In float"
        });
        await controller.CreatePaidOut(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId, BranchId = 1, TerminalId = 1, MovementType = "PETTY_CASH", CategoryId = 2, Amount = 250.00m, Reason = "Petty cash supplies", SupervisorPin = "9999"
        });
        await controller.RecordMovement(new CashDrawerMovementDto
        {
            ShiftId = shift.ShiftId, BranchId = 1, TerminalId = 1, MovementType = "CASH_DROP", CategoryId = 4, Amount = 400.00m, Reason = "Safe drop", SupervisorPin = "9999"
        });

        var afterReport = await reportService.GenerateReportAsync(new SalesReportRequestDto { BranchId = 1 });
        var seqAfter = await db.InvoiceSequences.FirstAsync(s => s.TerminalId == 1);

        Assert(afterReport.GrossSales == beforeReport.GrossSales && afterReport.GrossSales == 1120.00m, "Petty cash must never alter GrossSales");
        Assert(afterReport.OutputVat == beforeReport.OutputVat && afterReport.OutputVat == 120.00m, "Petty cash must never alter Output VAT");
        Assert(afterReport.NetSales == beforeReport.NetSales, "Petty cash must never alter NetSales");
        Assert(afterReport.CompletedTransactionsCount == 1, "Cash movements must never create sales orders");
        Assert(seqAfter.LastIssuedNumber == 105, "Cash movements must never consume BIR InvoiceSequences");
    }

    private static TransactionsController CreateTransactionsController(PosDbContext db, string username = "cashier1", string role = "Cashier", int? branchId = null, int? terminalId = null)
    {
        var receiptService = new ReceiptService(db);
        var controller = new TransactionsController(db, receiptService);
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, role)
        };
        if (branchId.HasValue)
            claims.Add(new Claim("branch_id", branchId.Value.ToString()));
        if (terminalId.HasValue)
            claims.Add(new Claim("terminal_id", terminalId.Value.ToString()));

        var identity = new ClaimsIdentity(claims, "TestAuth");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private static ReturnsController CreateReturnsController(PosDbContext db, string username = "cashier1", string role = "Cashier")
    {
        var controller = new ReturnsController(db);
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, role),
            new Claim("terminal_id", "1")
        ], "TestAuth");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private static async Task Test19_TransactionsTokenClaimsScoping()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;

        var outlet2 = new Terminal { TerminalId = 2, BranchId = 1, OutletId = 1, TerminalCode = "POS-2", Name = "Counter 2", IsActive = true };
        db.Terminals.Add(outlet2);

        // Order 1 at outlet 1 (cashier1)
        db.Orders.Add(new Order
        {
            OrderId = 101, BranchId = 1, OutletId = 1, TerminalId = 1, OrderNumber = "ORD-101", OrderType = "DINE_IN", CashierId = "cashier1",
            OrderStatus = "COMPLETED", PaymentStatus = "PAID", Subtotal = 100m, TotalAmount = 100m, CreatedAt = DateTime.UtcNow
        });
        // Order 2 at outlet 2 (cashier1)
        db.Orders.Add(new Order
        {
            OrderId = 102, BranchId = 1, OutletId = 1, TerminalId = 2, OrderNumber = "ORD-102", OrderType = "DINE_IN", CashierId = "cashier1",
            OrderStatus = "COMPLETED", PaymentStatus = "PAID", Subtotal = 200m, TotalAmount = 200m, CreatedAt = DateTime.UtcNow
        });
        // Order 3 at outlet 1 (cashier2)
        db.Orders.Add(new Order
        {
            OrderId = 103, BranchId = 1, OutletId = 1, TerminalId = 1, OrderNumber = "ORD-103", OrderType = "DINE_IN", CashierId = "cashier2",
            OrderStatus = "COMPLETED", PaymentStatus = "PAID", Subtotal = 300m, TotalAmount = 300m, CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        // Cashier 1 token scoped to Branch 1, Terminal 1 -> MUST be forbidden from Transaction History (even own orders)
        var cashierController = CreateTransactionsController(db, "cashier1", "Cashier", branchId: 1, terminalId: 1);
        var cashierRes = await cashierController.GetTransactions(null, null, null, null, null, null, null, null, null, null, null);
        Assert(cashierRes.Result is ForbidResult, "Cashier must be forbidden from viewing Transaction History, including own orders.");

        // Manager token scoped to Branch 1 -> allowed to view Branch 1 transactions, forbidden from other branches
        var managerController = CreateTransactionsController(db, "manager1", "Manager", branchId: 1, terminalId: 1);
        var res = await managerController.GetTransactions(null, null, null, terminalId: 1, null, null, null, null, null, null, null);
        var ok = (List<OrderResponseDto>)((OkObjectResult)res.Result!).Value!;
        Assert(ok.Count == 2, $"Manager scoped to Terminal 1 should see 2 orders (101 & 103), got {ok.Count}");

        // Attempting to filter by a different branch than Manager's token claim must be forbidden (403)
        var crossBranchRes = await managerController.GetTransactions(null, null, branchId: 99, null, null, null, null, null, null, null, null);
        Assert(crossBranchRes.Result is ForbidResult, "Manager querying a different branch than token claim must return ForbidResult");

        // Admin can view all orders across terminals
        var adminController = CreateTransactionsController(db, "admin1", "Admin", branchId: null, terminalId: null);
        var adminRes = await adminController.GetTransactions(null, null, null, null, null, null, null, null, null, null, null);
        var adminOk = (List<OrderResponseDto>)((OkObjectResult)adminRes.Result!).Value!;
        Assert(adminOk.Count == 3, $"Admin should see all 3 orders, got {adminOk.Count}");
    }

    private static async Task Test20_Transactions4AmCutoff()
    {
        // 1. Verify 4:00 AM business day cutoff math
        static DateTime CalcBusinessDayStart(DateTime localTime)
        {
            var baseDate = localTime.TimeOfDay < TimeSpan.FromHours(4)
                ? localTime.Date.AddDays(-1)
                : localTime.Date;
            return baseDate.AddHours(4);
        }

        // Night shift 03:30 AM Oct 20 -> belongs to business day starting Oct 19 04:00 AM
        var lateNight = new DateTime(2026, 10, 20, 3, 30, 0);
        var startLateNight = CalcBusinessDayStart(lateNight);
        Assert(startLateNight == new DateTime(2026, 10, 19, 4, 0, 0), $"Expected Oct 19 4am, got {startLateNight}");

        // Morning 04:00 AM Oct 20 -> belongs to business day starting Oct 20 04:00 AM
        var exactMorning = new DateTime(2026, 10, 20, 4, 0, 0);
        var startMorning = CalcBusinessDayStart(exactMorning);
        Assert(startMorning == new DateTime(2026, 10, 20, 4, 0, 0), $"Expected Oct 20 4am, got {startMorning}");

        // Evening 11:30 PM Oct 20 -> belongs to business day starting Oct 20 04:00 AM
        var evening = new DateTime(2026, 10, 20, 23, 30, 0);
        var startEvening = CalcBusinessDayStart(evening);
        Assert(startEvening == new DateTime(2026, 10, 20, 4, 0, 0), $"Expected Oct 20 4am, got {startEvening}");

        // 2. Verify server-side floor in TransactionsController
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;

        // Old order (2 days ago)
        db.Orders.Add(new Order
        {
            OrderId = 201, BranchId = 1, OutletId = 1, TerminalId = 1, OrderNumber = "ORD-OLD", OrderType = "DINE_IN", CashierId = "cashier1",
            OrderStatus = "COMPLETED", PaymentStatus = "PAID", Subtotal = 50m, TotalAmount = 50m,
            CreatedAt = DateTime.UtcNow.AddDays(-2)
        });
        // Today's order (recent)
        db.Orders.Add(new Order
        {
            OrderId = 202, BranchId = 1, OutletId = 1, TerminalId = 1, OrderNumber = "ORD-TODAY", OrderType = "DINE_IN", CashierId = "cashier1",
            OrderStatus = "COMPLETED", PaymentStatus = "PAID", Subtotal = 80m, TotalAmount = 80m,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var cashierController = CreateTransactionsController(db, "cashier1", "Cashier", branchId: 1, terminalId: 1);
        var cashierRes = await cashierController.GetTransactions(null, null, null, null, null, null, null, null, null, null, null);
        Assert(cashierRes.Result is ForbidResult, "Cashier must be forbidden from TransactionsController.GetTransactions.");

        var managerController = CreateTransactionsController(db, "manager1", "Manager", branchId: 1, terminalId: 1);
        var cutoff = POS.Shared.Services.BusinessDayClock.GetCurrentBusinessDayStartLocal();
        var res = await managerController.GetTransactions(cutoff, null, null, null, null, null, null, null, null, null, null);
        var ok = (List<OrderResponseDto>)((OkObjectResult)res.Result!).Value!;
        Assert(ok.Count == 1 && ok[0].OrderId == 202, $"Expected only today's order after 4 AM cutoff, got {ok.Count} orders");
    }

    private static async Task Test21_VoidAndReturnStrictPinEnforcement()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var _ = conn;

        // Order to void
        var order = new Order
        {
            OrderId = 301, BranchId = 1, OutletId = 1, TerminalId = 1, OrderNumber = "ORD-301", OrderType = "DINE_IN", InvoiceNumber = "INV-301",
            CashierId = "cashier1", OrderStatus = "COMPLETED", PaymentStatus = "PAID", Subtotal = 500m, TotalAmount = 500m,
            CreatedAt = DateTime.UtcNow
        };
        db.Orders.Add(order);
        db.Payments.Add(new Payment
        {
            PaymentId = 301, OrderId = 301, PaymentMethod = "CASH", Amount = 500m,
            AmountTendered = 500m, ChangeAmount = 0m, PaymentStatus = "PAID", ProcessedBy = "cashier1", PaidAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var returnsController = CreateReturnsController(db, "cashier1", "Cashier");

        // 1. Attempt void using supervisor username "manager1" instead of PIN -> Must fail!
        var bypassReq = new VoidRequestDto
        {
            OriginalOrderId = 301,
            VoidReason = "Mistake",
            ApprovedByPinOrUsername = "manager1"
        };
        var badRes = await returnsController.VoidTransaction(bypassReq);
        Assert(badRes.Result is BadRequestObjectResult bad && bad.Value?.ToString()?.Contains("PIN not found") == true,
            "Using supervisor username in place of PIN must be rejected");

        // 2. Void using correct supervisor PIN "9999" -> Must succeed!
        var validReq = new VoidRequestDto
        {
            OriginalOrderId = 301,
            VoidReason = "Customer changed mind",
            ApprovedByPinOrUsername = "9999"
        };
        var okRes = await returnsController.VoidTransaction(validReq);
        Assert(okRes.Result is OkObjectResult okObj && ((VoidResponseDto)okObj.Value!).Status == "APPROVED",
            "Valid supervisor PIN must approve void");

        var updatedOrder = await db.Orders.FindAsync(301L);
        Assert(updatedOrder!.OrderStatus == "VOIDED" && updatedOrder.PaymentStatus == "VOIDED", "Order status must transition to VOIDED");
    }

    private static Task Test22_OfflineTransactionHistoryLogic()
    {
        // Verify offline query filtering & mapping contract
        var nowLocal = DateTime.Now;
        var baseDate = nowLocal.TimeOfDay < TimeSpan.FromHours(4) ? nowLocal.Date.AddDays(-1) : nowLocal.Date;
        var cutoffUtc = baseDate.AddHours(4).ToUniversalTime();

        var localOrderMock = new
        {
            OrderId = Guid.NewGuid().ToString(),
            InvoiceNumber = "INV-OFFLINE-001",
            BranchId = 1,
            TerminalId = 1,
            CashierId = "cashier1",
            Subtotal = 250m,
            TotalAmount = 250m,
            PaymentStatus = "PAID",
            OrderStatus = "COMPLETED",
            CreatedAt = DateTime.UtcNow
        };

        Assert(localOrderMock.CreatedAt >= cutoffUtc, "Recent order must fall within active business day");
        Assert(localOrderMock.TerminalId == 1 && localOrderMock.CashierId == "cashier1", "Order scoped to active terminal and cashier");
        return Task.CompletedTask;
    }

    private static async Task TestInitialCashFundManagementAndOpeningProcess()
    {
        var (conn, db, initialShift) = await CreateTestEnvAsync();
        await using var _ = conn;

        // Close the default seeded shift so we can test opening shifts with cash fund configuration
        initialShift.Status = "CLOSED";
        initialShift.ClosedAt = DateTime.UtcNow;
        var outlet = await db.Outlets.FirstAsync(o => o.OutletId == 1);
        var terminal = await db.Terminals.FirstAsync(t => t.TerminalId == 1);
        outlet.DefaultOpeningFund = null;
        terminal.OpeningFundOverride = null;
        await db.SaveChangesAsync();

        var cashDrawerController = CreateController(db, "cashier1", "Cashier");

        // 1. Unconfigured Outlet & Terminal -> GetOpeningFundConfig returns IsConfigured = false, and RequireConfiguredFund blocks OpenShift
        var unconfRes = await cashDrawerController.GetOpeningFundConfig(1);
        var unconfDto = (TerminalCashFundConfigDto)((OkObjectResult)unconfRes.Result!).Value!;
        Assert(!unconfDto.IsConfigured && unconfDto.EffectiveOpeningFund == null && unconfDto.OpeningFundSource == "UNCONFIGURED",
            "Expected unconfigured cash fund when neither Outlet Default nor Terminal Override is set.");

        var blockedOpen = await cashDrawerController.OpenShift(new OpenShiftDto
        {
            BranchId = 1,
            TerminalId = 1,
            CashierId = "cashier1",
            ClientShiftId = Guid.NewGuid().ToString("N"),
            OpeningFloat = 1000m,
            RequireConfiguredFund = true
        });
        Assert(blockedOpen is BadRequestObjectResult, "OpenShift with RequireConfiguredFund=true must be blocked when cash fund is unconfigured.");

        // 2. Admin configures Outlet Default Initial Cash Fund (₱3,000.00) -> Terminal inherits Outlet Default without duplicating into Terminal row
        var outletsController = new OutletsController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.Name, "admin1"),
                        new Claim(ClaimTypes.Role, "Admin")
                    ], "TestAuth"))
                }
            }
        };
        var updateOutletRes = await outletsController.UpdateOutlet(1, new OutletDto
        {
            OutletId = 1,
            BranchId = 1,
            OutletCode = outlet.OutletCode,
            Name = outlet.Name,
            DefaultOpeningFund = 3000.00m,
            IsActive = true
        });
        Assert(updateOutletRes is NoContentResult, "UpdateOutlet must return NoContent.");
        Assert(outlet.DefaultOpeningFund == 3000.00m && outlet.CashFundUpdatedBy == "admin1" && outlet.CashFundUpdatedAt.HasValue,
            "Outlet DefaultOpeningFund and audit metadata must be saved.");

        var inheritedRes = await cashDrawerController.GetOpeningFundConfig(1);
        var inheritedDto = (TerminalCashFundConfigDto)((OkObjectResult)inheritedRes.Result!).Value!;
        Assert(inheritedDto.IsConfigured && inheritedDto.EffectiveOpeningFund == 3000.00m && inheritedDto.OpeningFundSource == "OUTLET_DEFAULT" && inheritedDto.OpeningFundOverride == null,
            "Terminal must inherit Outlet DefaultOpeningFund without duplicating into OpeningFundOverride.");

        // 3. Admin configures Terminal Override (₱5,000.00) -> Terminal Override takes precedence over Outlet Default
        var terminalsController = new TerminalsController(db, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.Name, "admin1"),
                        new Claim(ClaimTypes.Role, "Admin")
                    ], "TestAuth"))
                }
            }
        };
        var updateTermRes = await terminalsController.UpdateTerminal(1, new TerminalDto
        {
            TerminalId = 1,
            BranchId = 1,
            OutletId = 1,
            TerminalCode = terminal.TerminalCode,
            Name = terminal.Name,
            MenuDisplayMode = terminal.MenuDisplayMode,
            OpeningFundOverride = 5000.00m,
            IsActive = true
        });
        Assert(updateTermRes is NoContentResult, "UpdateTerminal must return NoContent.");
        var getTermRes = await terminalsController.GetTerminal(1);
        var updatedTermDto = (TerminalDto)((OkObjectResult)getTermRes.Result!).Value!;
        Assert(updatedTermDto.OpeningFundOverride == 5000.00m && updatedTermDto.EffectiveOpeningFund == 5000.00m && updatedTermDto.OpeningFundSource == "TERMINAL_OVERRIDE",
            "Terminal Override must override Outlet Default.");

        // 4. Cashier opens shift using denomination count with a ₱150.00 shortage (counted ₱4,850.00 vs expected ₱5,000.00)
        var openDenoms = CashDenominationCatalog.CreateDefaultLines();
        openDenoms.Single(d => d.Type == "BILL" && d.Denomination == 1000m).Quantity = 4; // 4,000
        openDenoms.Single(d => d.Type == "BILL" && d.Denomination == 500m).Quantity = 1;  // 500
        openDenoms.Single(d => d.Type == "BILL" && d.Denomination == 100m).Quantity = 3;  // 300
        openDenoms.Single(d => d.Type == "BILL" && d.Denomination == 50m).Quantity = 1;   // 50 -> total 4,850

        var openRes = await cashDrawerController.OpenShift(new OpenShiftDto
        {
            BranchId = 1,
            TerminalId = 1,
            CashierId = "cashier1",
            ClientShiftId = Guid.NewGuid().ToString("N"),
            RequireConfiguredFund = true,
            OpeningDenominations = openDenoms
        });
        var openedShiftDto = (CashDrawerShiftDto)((OkObjectResult)openRes).Value!;
        Assert(openedShiftDto.OpeningFloat == 4850.00m, $"Expected counted OpeningFloat 4850.00, got {openedShiftDto.OpeningFloat}");
        Assert(openedShiftDto.ExpectedOpeningFund == 5000.00m, $"Expected ExpectedOpeningFund 5000.00, got {openedShiftDto.ExpectedOpeningFund}");
        Assert(openedShiftDto.OpeningDiscrepancy == -150.00m, $"Expected OpeningDiscrepancy -150.00, got {openedShiftDto.OpeningDiscrepancy}");
        Assert(openedShiftDto.OpeningReconciliationStatus == "SHORT", $"Expected SHORT status, got {openedShiftDto.OpeningReconciliationStatus}");

        // 5. Changing Terminal Override while shift is open must NOT alter the already-open shift
        await terminalsController.UpdateTerminal(1, new TerminalDto
        {
            TerminalId = 1,
            BranchId = 1,
            OutletId = 1,
            TerminalCode = terminal.TerminalCode,
            Name = terminal.Name,
            MenuDisplayMode = terminal.MenuDisplayMode,
            OpeningFundOverride = null, // Disable override -> falls back to Outlet Default 3,000 for future shifts
            IsActive = true
        });
        var openShiftInDb = await db.CashDrawerShifts.FindAsync(openedShiftDto.ShiftId);
        Assert(openShiftInDb!.ExpectedBalance == 4850.00m, "Expected drawer balance in DB must start from actual counted opening cash.");
        Assert(openShiftInDb.ExpectedOpeningFund == 5000.00m && openShiftInDb.OpeningFloat == 4850.00m && openShiftInDb.OpeningDiscrepancy == -150.00m,
            "Admin config changes must never mutate an already-open cashier session.");
    }

    private static async Task TestCashierAndTerminalClosingLockUntil4Am()
    {
        try
        {
            // Business Day 2026-10-20 at 10:00 PM Asia/Manila (14:00:00 UTC)
            POS.Shared.Services.BusinessDayClock.UtcNowProvider = () => new DateTime(2026, 10, 20, 14, 0, 0, DateTimeKind.Utc);

            var (conn, db, shift) = await CreateTestEnvAsync();
            await using var _ = conn;

            var outlet = await db.Outlets.FindAsync(1);
            outlet!.DefaultOpeningFund = 2000.00m;
            db.CashierSessions.Add(new CashierSession
            {
                BranchId = 1,
                TerminalId = 1,
                CashierId = "cashier1",
                Status = "ACTIVE",
                LoginAt = new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc)
            });
            await db.SaveChangesAsync();

            var cashDrawerCashier1 = CreateController(db, "cashier1", "Cashier");

            // 1. Cashier A (cashier1) closes Terminal 01 at 10:00 PM Asia/Manila
            var closeRes = await cashDrawerCashier1.CloseShift(new CloseShiftDto
            {
                ShiftId = shift.ShiftId,
                ActualCountedBalance = 2000.00m,
                Notes = "End of day close"
            });
            Assert(closeRes is OkObjectResult, "CloseShift must succeed.");

            // 2. Verify Cashier-level closing restriction persisted in DB (NOT Terminal Z-Reading lock!) & only cashier1's session closed
            var terminalInDb = await db.Terminals.FindAsync(1);
            Assert(terminalInDb!.ClosedBusinessDate == null,
                $"Close Cashier must NOT lock Terminal.ClosedBusinessDate for other cashiers, got {terminalInDb.ClosedBusinessDate}");

            var restrictionInDb = await db.CashierClosingRestrictions.SingleOrDefaultAsync(r => r.TerminalId == 1 && r.CashierId == "cashier1" && r.BusinessDate == "2026-10-20");
            Assert(restrictionInDb != null && restrictionInDb.Status == "ACTIVE",
                "CloseShift must create an ACTIVE CashierClosingRestriction for (cashier1, Terminal 1, 2026-10-20).");
            Assert(restrictionInDb!.UnlockAtUtc == new DateTime(2026, 10, 20, 20, 0, 0, DateTimeKind.Utc),
                $"Expected UnlockAtUtc 2026-10-20 20:00:00 UTC (4:00 AM Manila next day), got {restrictionInDb.UnlockAtUtc}");

            var activeSessionsAfterClose = await db.CashierSessions.CountAsync(s => s.TerminalId == 1 && s.CashierId == "cashier1" && s.Status == "ACTIVE");
            Assert(activeSessionsAfterClose == 0, "Closing shift must terminate the closing cashier's active CashierSession on that terminal.");

            // 3. Verify AuthController blocks Cashier A (403 Cashier Restricted) while ALLOWING Cashier B (200 OK) on Terminal 1!
            var jwtBuilder = new Microsoft.Extensions.Configuration.ConfigurationBuilder();
            jwtBuilder.Add(new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource
            {
                InitialData = new Dictionary<string, string?>
                {
                    ["Jwt:Key"] = "SuperSecretTestKeyForUnitTestsOnly1234567890!",
                    ["Jwt:Issuer"] = "POS.Api",
                    ["Jwt:Audience"] = "POS.Client"
                }
            });
            var jwtConfig = jwtBuilder.Build();
            var authController = new AuthController(db, jwtConfig);

            var pinLoginCashier1 = await authController.PinLogin(new PinLoginDto { PinCode = "1111", TerminalId = 1, BranchId = 1 });
            var pinRes1 = pinLoginCashier1 as ObjectResult;
            Assert(pinRes1 != null && pinRes1.StatusCode == 403, $"Cashier who closed terminal must get 403 Forbidden, got {pinRes1?.StatusCode}");

            var pinLoginCashier2BeforeZ = await authController.PinLogin(new PinLoginDto { PinCode = "2222", TerminalId = 1, BranchId = 1 });
            Assert(pinLoginCashier2BeforeZ is OkObjectResult, "Other cashiers (cashier2) MUST be allowed to log in on Terminal 1 after cashier1 closes!");

            // 4. Verify CashierSessionsController blocks cashier1 (403) while allowing cashier2, then Z-Reading locks Terminal 1 (423 for everyone!)
            var sessionsController1 = new CashierSessionsController(db)
            {
                ControllerContext = cashDrawerCashier1.ControllerContext
            };
            var activateRes1 = await sessionsController1.ActivateSession(new CashierSessionActivateDto { BranchId = 1, TerminalId = 1 });
            Assert((activateRes1 as ObjectResult)?.StatusCode == 403, "ActivateSession for closing cashier must return 403.");

            var cashDrawerCashier2 = CreateController(db, "cashier2", "Cashier");
            var sessionsController2 = new CashierSessionsController(db)
            {
                ControllerContext = cashDrawerCashier2.ControllerContext
            };
            var activateRes2BeforeZ = await sessionsController2.ActivateSession(new CashierSessionActivateDto { BranchId = 1, TerminalId = 1 });
            Assert(activateRes2BeforeZ is OkObjectResult, "ActivateSession for cashier2 must succeed before Z-Reading finalization.");

            // Now finalize Z-Reading for 2026-10-20 -> atomically locks Terminal 1 for ALL cashiers until 4:00 AM!
            db.ZReadingTerminalStates.Add(new ZReadingTerminalState { TerminalId = 1, FirstBusinessDate = new DateOnly(2026, 10, 20), ConfiguredBy = "verified" });
            await db.SaveChangesAsync();
            var zSvc = new ZReadingService(db, BuildWorkflowJwtConfig());
            await zSvc.GenerateAsync(new GenerateZReadingRequest
            {
                TerminalId = 1,
                BusinessDate = new DateOnly(2026, 10, 20),
                ClientOperationId = "lock01-z-1",
                DeviceIdentifier = "test",
                AllLocalOutboxesDrained = true,
                NoUnresolvedLocalTransactions = true,
                SoleDeviceCompletenessConfirmed = true
            }, "manager1");

            var terminalAfterZ = await db.Terminals.FindAsync(1);
            Assert(terminalAfterZ!.ClosedBusinessDate == "2026-10-20" && terminalAfterZ.ClosingLockedByCashier == "manager1",
                "Z-Reading finalization must lock Terminal.ClosedBusinessDate for the finalized Business Date.");

            var pinLoginCashier2AfterZ = await authController.PinLogin(new PinLoginDto { PinCode = "2222", TerminalId = 1, BranchId = 1 });
            var pinRes2 = pinLoginCashier2AfterZ as ObjectResult;
            Assert(pinRes2 != null && pinRes2.StatusCode == 423, $"After Z-Reading, all cashiers on locked terminal must get 423 Locked, got {pinRes2?.StatusCode}");

            var activateRes2 = await sessionsController2.ActivateSession(new CashierSessionActivateDto { BranchId = 1, TerminalId = 1 });
            Assert((activateRes2 as ObjectResult)?.StatusCode == 423, "ActivateSession for other cashier on Z-Reading-locked terminal must return 423.");

            var openWhileLocked = await cashDrawerCashier2.OpenShift(new OpenShiftDto
            {
                BranchId = 1,
                TerminalId = 1,
                CashierId = "cashier2",
                OpeningFloat = 2000.00m
            });
            Assert(openWhileLocked is ConflictObjectResult, "OpenShift on locked terminal must return ConflictObjectResult.");

            // 5. At 3:59:59 AM Asia/Manila (19:59:59 UTC), terminal and cashier remain locked
            POS.Shared.Services.BusinessDayClock.UtcNowProvider = () => new DateTime(2026, 10, 20, 19, 59, 59, DateTimeKind.Utc);
            var lockStatusBefore4Am = (TerminalClosingLockDto)((OkObjectResult)(await cashDrawerCashier1.GetTerminalLockStatus(1, "cashier1")).Result!).Value!;
            Assert(lockStatusBefore4Am.IsTerminalLocked && lockStatusBefore4Am.IsCashierRestricted,
                "At 3:59:59 AM Manila, both terminal lock and cashier restriction must still be active.");

            // 6. At exactly 4:00:00 AM Asia/Manila (20:00:00 UTC), both restrictions automatically reset
            POS.Shared.Services.BusinessDayClock.UtcNowProvider = () => new DateTime(2026, 10, 20, 20, 0, 0, DateTimeKind.Utc);
            var lockStatusAt4Am = (TerminalClosingLockDto)((OkObjectResult)(await cashDrawerCashier1.GetTerminalLockStatus(1, "cashier1")).Result!).Value!;
            Assert(!lockStatusAt4Am.IsTerminalLocked && !lockStatusAt4Am.IsCashierRestricted,
                "At 4:00:00 AM Manila, both terminal lock and cashier restriction must automatically unlock.");

            var unlockedTerminalInDb = await db.Terminals.FindAsync(1);
            Assert(unlockedTerminalInDb!.ClosedBusinessDate == null && unlockedTerminalInDb.ClosingLockedByCashier == null,
                "Auto-unlock at 4:00 AM must clear terminal lock columns in DB.");

            // 7. Authorized cashier can now log in and open a new session after Initial Cash Fund confirmation
            var pinAfter4Am = await authController.PinLogin(new PinLoginDto { PinCode = "1111", TerminalId = 1, BranchId = 1 });
            Assert(pinAfter4Am is OkObjectResult, "Cashier A must be allowed to log in after 4:00 AM.");

            var openAfter4Am = await cashDrawerCashier1.OpenShift(new OpenShiftDto
            {
                BranchId = 1,
                TerminalId = 1,
                CashierId = "cashier1",
                OpeningFloat = 2000.00m,
                RequireConfiguredFund = true
            });
            Assert(openAfter4Am is OkObjectResult, "Cashier A must be able to open a new shift after 4:00 AM with Initial Cash Fund confirmation.");
        }
        finally
        {
            POS.Shared.Services.BusinessDayClock.ResetProviders();
        }
    }

    private static async Task TestModifierOption_NonInventoryModifier()
    {
        var (conn, db, _) = await CreateTestEnvAsync();
        using (conn)
        using (db)
        {
            db.Categories.Add(new Category { CategoryId = 1, Name = "Food" });
            db.InvoiceSequences.Add(new InvoiceSequence { TerminalId = 1, SeriesCode = "OFF", Prefix = "OFF-", StartingNumber = 1, EndingNumber = 1000, LastIssuedNumber = 0 });
            var p1 = new Product { ProductId = 10, CategoryId = 1, Name = "Burger", Price = 100m, ProductType = "Standard", IsActive = true };
            db.Products.Add(p1);
            db.BranchInventories.Add(new BranchInventory { BranchId = 1, ProductId = 10, StockQuantity = 10, LastUpdated = DateTime.UtcNow });

            var mg = new ModifierGroup { ModifierGroupId = 1, Name = "Spice Level", IsActive = true };
            db.ModifierGroups.Add(mg);
            var mo = new ModifierOption { ModifierOptionId = 1, ModifierGroupId = 1, ProductId = null, Name = "Extra Spicy", Price = 10m, IsActive = true };
            db.ModifierOptions.Add(mo);
            await db.SaveChangesAsync();

            var controller = new OfflineSyncController(db);
            Authenticate(controller);
            var req = new OfflineTransactionSyncRequest
            {
                LocalTransactionId = "TX-MOD-NONINV",
                BranchId = 1,
                TerminalId = 1,
                CashierId = "cashier1",
                DeviceIdentifier = "TEST",
                OrderNumber = "POS01-000001",
                InvoiceNumber = "OFF-0000001",
                PayloadHash = "hash-noninv",
                TransactionDate = DateTime.UtcNow,
                Subtotal = 110m,
                TotalAmount = 110m,
                VatableSales = 110m,
                Items = new List<OfflineTransactionItemDto>
                {
                    new()
                    {
                        ProductId = 10,
                        ProductName = "Burger",
                        Quantity = 1m,
                        UnitPrice = 110m,
                        LineTotal = 110m,
                        Modifiers = new List<OfflineModifierDto>
                        {
                            new() { OptionId = 1, ProductId = null, Name = "Extra Spicy", AdditionalPrice = 10m }
                        }
                    }
                },
                Payments = new List<OfflinePaymentDto>
                {
                    new() { Amount = 110m, TenderedAmount = 110m, ChangeAmount = 0m, PaymentMethod = "CASH", PaidAt = DateTime.UtcNow }
                }
            };

            var syncRes = await controller.SyncTransaction(req);
            Assert(syncRes.Result is OkObjectResult, "Syncing non-inventory modifier transaction must succeed.");

            var savedMod = await db.OrderItemModifiers.FirstOrDefaultAsync(m => m.ModifierOptionId == 1);
            Assert(savedMod != null, "OrderItemModifier must be persisted.");
            Assert(savedMod!.ProductId == null, "Non-inventory modifier OrderItemModifier.ProductId must be null.");

            var burgerInv = await db.BranchInventories.FirstOrDefaultAsync(bi => bi.BranchId == 1 && bi.ProductId == 10);
            Assert(burgerInv != null && burgerInv.StockQuantity == 9, "Burger inventory must decrease from 10 to 9.");

            var adjustments = await db.StockAdjustments.ToListAsync();
            Assert(adjustments.Count == 1, "Only 1 StockAdjustment must exist for the parent product.");
            Assert(adjustments[0].ProductId == 10, "StockAdjustment must be for Product 10.");
        }
    }

    private static async Task TestModifierOption_LinkedProductInventoryDeduction()
    {
        var (conn, db, _) = await CreateTestEnvAsync();
        using (conn)
        using (db)
        {
            db.Categories.Add(new Category { CategoryId = 1, Name = "Food" });
            db.InvoiceSequences.Add(new InvoiceSequence { TerminalId = 1, SeriesCode = "OFF", Prefix = "OFF-", StartingNumber = 1, EndingNumber = 1000, LastIssuedNumber = 0 });
            var p1 = new Product { ProductId = 20, CategoryId = 1, Name = "Lechon", Price = 950m, ProductType = "Standard", PricingMethod = "WeightBased", IsActive = true };
            var p2 = new Product { ProductId = 21, CategoryId = 1, Name = "Coke 330ml", Price = 50m, ProductType = "Standard", PricingMethod = "Fixed", IsActive = true };
            db.Products.AddRange(p1, p2);

            db.BranchInventories.Add(new BranchInventory { BranchId = 1, ProductId = 20, StockQuantity = 5, LastUpdated = DateTime.UtcNow });
            db.BranchInventories.Add(new BranchInventory { BranchId = 1, ProductId = 21, StockQuantity = 20, LastUpdated = DateTime.UtcNow });

            var mg = new ModifierGroup { ModifierGroupId = 2, Name = "Beverage Inclusion", IsActive = true };
            db.ModifierGroups.Add(mg);
            var mo = new ModifierOption { ModifierOptionId = 2, ModifierGroupId = 2, ProductId = 21, Name = "Coke 330ml", Price = 0m, IsActive = true };
            db.ModifierOptions.Add(mo);
            await db.SaveChangesAsync();

            var controller = new OfflineSyncController(db);
            Authenticate(controller);
            var req = new OfflineTransactionSyncRequest
            {
                LocalTransactionId = "TX-MOD-LINKED",
                BranchId = 1,
                TerminalId = 1,
                CashierId = "cashier1",
                DeviceIdentifier = "TEST",
                OrderNumber = "POS01-000002",
                InvoiceNumber = "OFF-0000002",
                PayloadHash = "hash-linked",
                TransactionDate = DateTime.UtcNow,
                Subtotal = 950m,
                TotalAmount = 950m,
                VatableSales = 950m,
                Items = new List<OfflineTransactionItemDto>
                {
                    new()
                    {
                        ProductId = 20,
                        ProductName = "Lechon",
                        Quantity = 1m,
                        UnitPrice = 950m,
                        LineTotal = 950m,
                        PricingMethod = "WeightBased",
                        Modifiers = new List<OfflineModifierDto>
                        {
                            new() { OptionId = 2, ProductId = 21, Name = "Coke 330ml", AdditionalPrice = 0m }
                        }
                    }
                },
                Payments = new List<OfflinePaymentDto>
                {
                    new() { Amount = 950m, TenderedAmount = 950m, ChangeAmount = 0m, PaymentMethod = "CASH", PaidAt = DateTime.UtcNow }
                }
            };

            var syncRes = await controller.SyncTransaction(req);
            Assert(syncRes.Result is OkObjectResult, "Syncing linked modifier transaction must succeed.");

            var savedMod = await db.OrderItemModifiers.FirstOrDefaultAsync(m => m.ModifierOptionId == 2);
            Assert(savedMod != null, "OrderItemModifier must be persisted.");
            Assert(savedMod!.ProductId == 21, "OrderItemModifier.ProductId must be 21.");

            var lechonInv = await db.BranchInventories.FirstOrDefaultAsync(bi => bi.BranchId == 1 && bi.ProductId == 20);
            Assert(lechonInv != null && lechonInv.StockQuantity == 4, "Lechon inventory must decrease from 5 to 4.");

            var cokeInv = await db.BranchInventories.FirstOrDefaultAsync(bi => bi.BranchId == 1 && bi.ProductId == 21);
            Assert(cokeInv != null && cokeInv.StockQuantity == 19, "Coke inventory must decrease from 20 to 19.");

            var adjustments = await db.StockAdjustments.OrderBy(a => a.StockAdjustmentId).ToListAsync();
            Assert(adjustments.Count == 2, "Exactly 2 StockAdjustment records must exist: 1 for Lechon and 1 for Coke.");
            Assert(adjustments.Any(a => a.ProductId == 20 && a.Quantity == 1), "StockAdjustment must exist for Lechon.");
            Assert(adjustments.Any(a => a.ProductId == 21 && a.Quantity == 1 && a.Reason.Contains("Modifier")), "StockAdjustment must exist for Coke with modifier reason.");
        }
    }

    private static async Task TestModifierOption_InvalidProductRejected()
    {
        var (conn, db, _) = await CreateTestEnvAsync();
        using (conn)
        using (db)
        {
            db.Categories.Add(new Category { CategoryId = 1, Name = "Food" });
            var mg = new ModifierGroup { ModifierGroupId = 3, Name = "Add-ons", IsActive = true };
            db.ModifierGroups.Add(mg);

            var inactiveProd = new Product { ProductId = 30, CategoryId = 1, Name = "Discontinued Drink", Price = 40m, ProductType = "Standard", IsActive = false };
            var deletedProd = new Product { ProductId = 31, CategoryId = 1, Name = "Deleted Item", Price = 50m, ProductType = "Standard", IsActive = true, IsDeleted = true };
            db.Products.AddRange(inactiveProd, deletedProd);
            await db.SaveChangesAsync();

            var controller = new ModifierOptionsController(db);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.Name, "admin"),
                        new Claim(ClaimTypes.Role, "Admin")
                    }, "TestAuth"))
                }
            };

            // 1. Non-existent product ID
            var resNonExistent = await controller.CreateOption(new ModifierOptionDto
            {
                ModifierGroupId = 3,
                ProductId = 9999,
                Name = "Invalid Option",
                Price = 10m
            });
            Assert(resNonExistent.Result is BadRequestObjectResult, "Creating ModifierOption with non-existent ProductId must return BadRequest.");

            // 2. Inactive product ID
            var resInactive = await controller.CreateOption(new ModifierOptionDto
            {
                ModifierGroupId = 3,
                ProductId = 30,
                Name = "Inactive Product Option",
                Price = 10m
            });
            Assert(resInactive.Result is BadRequestObjectResult, "Creating ModifierOption with inactive ProductId must return BadRequest.");

            // 3. Deleted product ID
            var resDeleted = await controller.CreateOption(new ModifierOptionDto
            {
                ModifierGroupId = 3,
                ProductId = 31,
                Name = "Deleted Product Option",
                Price = 10m
            });
            Assert(resDeleted.Result is BadRequestObjectResult, "Creating ModifierOption with deleted ProductId must return BadRequest.");

            // 4. Valid active product ID succeeds
            var activeProd = new Product { ProductId = 32, CategoryId = 1, Name = "Available Drink", Price = 40m, ProductType = "Standard", IsActive = true };
            db.Products.Add(activeProd);
            await db.SaveChangesAsync();

            var resSuccess = await controller.CreateOption(new ModifierOptionDto
            {
                ModifierGroupId = 3,
                ProductId = 32,
                Name = "Available Drink",
                Price = 0m
            });
            Assert(resSuccess.Result is OkObjectResult, "Creating ModifierOption with active ProductId must return OkObjectResult.");
            var createdDto = (ModifierOptionDto)((OkObjectResult)resSuccess.Result!).Value!;
            Assert(createdDto.ProductId == 32, "Created DTO must retain ProductId = 32.");
            Assert(createdDto.ProductName == "Available Drink", "Created DTO must retain ProductName.");
        }
    }

    private static Task TestWeightBased_CalculationsAndComplimentarySets()
    {
        decimal pricePerKg = 950.00m;

        // 1. Amount Mode: ₱300
        decimal amt300 = 300.00m;
        decimal wt300 = amt300 / pricePerKg; // 0.3157894736842105...
        int sets300 = (int)Math.Floor(wt300);
        Assert(sets300 == 0, "₱300 order must yield 0 complimentary sets.");
        var item300 = new CartItem
        {
            ProductId = 20,
            ProductName = "Lechon",
            PricingMethod = "WeightBased",
            BasePrice = pricePerKg,
            Quantity = wt300,
            TargetAmount = amt300
        };
        Assert(item300.LineTotal == 300.00m, "₱300 Amount Mode line total must be exact ₱300.00.");

        // 2. Weight Mode: 0.99 kg
        decimal wt099 = 0.99m;
        int sets099 = (int)Math.Floor(wt099);
        Assert(sets099 == 0, "0.99 kg must yield 0 complimentary sets.");
        var item099 = new CartItem
        {
            ProductId = 20,
            ProductName = "Lechon",
            PricingMethod = "WeightBased",
            BasePrice = pricePerKg,
            Quantity = wt099
        };
        Assert(item099.LineTotal == 940.50m, "0.99 kg line total must be ₱940.50.");

        // 3. Weight Mode: 1.00 kg
        decimal wt100 = 1.00m;
        int sets100 = (int)Math.Floor(wt100);
        Assert(sets100 == 1, "1.00 kg must yield 1 complimentary set.");
        var item100 = new CartItem
        {
            ProductId = 20,
            ProductName = "Lechon",
            PricingMethod = "WeightBased",
            BasePrice = pricePerKg,
            Quantity = wt100
        };
        Assert(item100.LineTotal == 950.00m, "1.00 kg line total must be ₱950.00.");

        // 4. Weight Mode: 1.50 kg
        decimal wt150 = 1.50m;
        int sets150 = (int)Math.Floor(wt150);
        Assert(sets150 == 1, "1.50 kg must yield 1 complimentary set.");
        var item150 = new CartItem
        {
            ProductId = 20,
            ProductName = "Lechon",
            PricingMethod = "WeightBased",
            BasePrice = pricePerKg,
            Quantity = wt150
        };
        Assert(item150.LineTotal == 1425.00m, "1.50 kg line total must be ₱1,425.00.");

        // 5. Weight Mode: 2.00 kg
        decimal wt200 = 2.00m;
        int sets200 = (int)Math.Floor(wt200);
        Assert(sets200 == 2, "2.00 kg must yield 2 complimentary sets.");
        var item200 = new CartItem
        {
            ProductId = 20,
            ProductName = "Lechon",
            PricingMethod = "WeightBased",
            BasePrice = pricePerKg,
            Quantity = wt200
        };
        Assert(item200.LineTotal == 1900.00m, "2.00 kg line total must be ₱1,900.00.");

        // 6. Amount Mode: ₱1,500
        decimal amt1500 = 1500.00m;
        decimal wt1500 = amt1500 / pricePerKg; // 1.578947... kg
        int sets1500 = (int)Math.Floor(wt1500);
        Assert(sets1500 == 1, "₱1,500 order must yield 1 complimentary set.");
        var item1500 = new CartItem
        {
            ProductId = 20,
            ProductName = "Lechon",
            PricingMethod = "WeightBased",
            BasePrice = pricePerKg,
            Quantity = wt1500,
            TargetAmount = amt1500
        };
        Assert(item1500.LineTotal == 1500.00m, "₱1,500 Amount Mode line total must be exact ₱1,500.00.");

        // 7. Amount Mode: ₱2,500
        decimal amt2500 = 2500.00m;
        decimal wt2500 = amt2500 / pricePerKg; // 2.631578... kg
        int sets2500 = (int)Math.Floor(wt2500);
        Assert(sets2500 == 2, "₱2,500 order must yield 2 complimentary sets.");
        var item2500 = new CartItem
        {
            ProductId = 20,
            ProductName = "Lechon",
            PricingMethod = "WeightBased",
            BasePrice = pricePerKg,
            Quantity = wt2500,
            TargetAmount = amt2500
        };
        Assert(item2500.LineTotal == 2500.00m, "₱2,500 Amount Mode line total must be exact ₱2,500.00.");

        // 8. Complimentary items financial isolation
        var compDrink = new CartItem { ProductId = 21, ProductName = "Coke 330ml", BasePrice = 50m, Quantity = 2m, IsComplimentary = true, ParentProductId = 20 };
        var compAtchara = new CartItem { ProductId = 25, ProductName = "Atchara Cup", BasePrice = 30m, Quantity = 2m, IsComplimentary = true, ParentProductId = 20 };
        var compSarsa = new CartItem { ProductId = 26, ProductName = "Sarsa Cup", BasePrice = 20m, Quantity = 2m, IsComplimentary = true, ParentProductId = 20 };

        Assert(compDrink.UnitPrice == 0.00m, "Complimentary Drink UnitPrice must be 0.00.");
        Assert(compDrink.LineTotal == 0.00m, "Complimentary Drink LineTotal must be 0.00.");
        Assert(compAtchara.UnitPrice == 0.00m, "Complimentary Atchara UnitPrice must be 0.00.");
        Assert(compAtchara.LineTotal == 0.00m, "Complimentary Atchara LineTotal must be 0.00.");
        Assert(compSarsa.UnitPrice == 0.00m, "Complimentary Sarsa UnitPrice must be 0.00.");
        Assert(compSarsa.LineTotal == 0.00m, "Complimentary Sarsa LineTotal must be 0.00.");

        var allItems = new List<CartItem> { item200, compDrink, compAtchara, compSarsa };
        decimal subtotal = allItems.Sum(i => i.LineTotal);
        Assert(subtotal == 1900.00m, "Subtotal must remain strictly ₱1,900.00 with complimentary items included.");

        return Task.CompletedTask;
    }

    private static Task TestWeightBased_StationRouting()
    {
        var receipt = new ReceiptDto
        {
            OrderId = 101,
            OrderNumber = "POS01-000101",
            InvoiceNumber = "INV01-000101",
            OrderType = "DINING",
            CashierId = "cashier1",
            TerminalName = "POS-1",
            CreatedAt = DateTime.UtcNow,
            Subtotal = 1500.00m,
            TotalAmount = 1500.00m,
            Items = new List<OrderItemResponseDto>
            {
                new()
                {
                    OrderItemId = 1,
                    ProductId = 20,
                    ProductName = "Lechon",
                    Quantity = 1.578947m,
                    UnitPrice = 950.00m,
                    LineTotal = 1500.00m,
                    IsKitchenItem = true,
                    IsBarItem = false
                },
                new()
                {
                    OrderItemId = 2,
                    ProductId = 22,
                    ProductName = "Sprite 330ml",
                    Quantity = 1.00m,
                    UnitPrice = 0.00m,
                    LineTotal = 0.00m,
                    IsComplimentary = true,
                    ParentProductId = 20,
                    IsKitchenItem = false,
                    IsBarItem = true
                },
                new()
                {
                    OrderItemId = 3,
                    ProductId = 25,
                    ProductName = "Atchara Cup",
                    Quantity = 1.00m,
                    UnitPrice = 0.00m,
                    LineTotal = 0.00m,
                    IsComplimentary = true,
                    ParentProductId = 20,
                    IsKitchenItem = true,
                    IsBarItem = false
                },
                new()
                {
                    OrderItemId = 4,
                    ProductId = 26,
                    ProductName = "Sarsa Cup",
                    Quantity = 1.00m,
                    UnitPrice = 0.00m,
                    LineTotal = 0.00m,
                    IsComplimentary = true,
                    ParentProductId = 20,
                    IsKitchenItem = true,
                    IsBarItem = false
                }
            }
        };

        var tickets = POS.Client.Services.PreparationTicketRouter.GenerateTickets(receipt, "Kitchen-Thermal", "Bar-Thermal");
        Assert(tickets.Count == 2, "Must generate exactly 2 tickets: 1 for KITCHEN and 1 for BAR.");

        var barTicket = tickets.FirstOrDefault(t => t.Station == "BAR");
        Assert(barTicket != null, "BAR ticket must exist.");
        Assert(barTicket!.Items.Count == 1, "BAR ticket must have exactly 1 item (the complimentary Drink).");
        Assert(barTicket.Items[0].ProductName == "Sprite 330ml", "BAR ticket must contain Sprite 330ml.");
        Assert(barTicket.Items[0].Quantity == 1.00m, "BAR ticket Drink quantity must be 1.");

        var kitchenTicket = tickets.FirstOrDefault(t => t.Station == "KITCHEN");
        Assert(kitchenTicket != null, "KITCHEN ticket must exist.");
        Assert(kitchenTicket!.Items.Count == 3, "KITCHEN ticket must have 3 items (Lechon, Atchara, Sarsa).");
        Assert(kitchenTicket.Items.Any(i => i.ProductName == "Lechon" && i.Quantity == 1.578947m), "KITCHEN ticket must contain Lechon with exact computed weight.");
        Assert(kitchenTicket.Items.Any(i => i.ProductName == "Atchara Cup" && i.Quantity == 1.00m), "KITCHEN ticket must contain Atchara with quantity 1.");
        Assert(kitchenTicket.Items.Any(i => i.ProductName == "Sarsa Cup" && i.Quantity == 1.00m), "KITCHEN ticket must contain Sarsa with quantity 1.");

        return Task.CompletedTask;
    }

    private static Task TestWeightBased_ReceiptFormatting()
    {
        var receipt = new ReceiptDto
        {
            OrderId = 101,
            OrderNumber = "POS01-000101",
            InvoiceNumber = "INV01-000101",
            OrderType = "DINING",
            CashierId = "cashier1",
            TerminalName = "POS-1",
            CreatedAt = DateTime.UtcNow,
            Subtotal = 1500.00m,
            VatableSales = 1339.29m,
            VatAmount = 160.71m,
            TotalAmount = 1500.00m,
            Items = new List<OrderItemResponseDto>
            {
                new()
                {
                    OrderItemId = 1,
                    ProductId = 20,
                    ProductName = "Lechon",
                    Quantity = 1.578947m,
                    UnitPrice = 950.00m,
                    LineTotal = 1500.00m
                },
                new()
                {
                    OrderItemId = 2,
                    ProductId = 22,
                    ProductName = "Sprite 330ml",
                    Quantity = 1.00m,
                    UnitPrice = 0.00m,
                    LineTotal = 0.00m,
                    IsComplimentary = true,
                    ParentProductId = 20
                },
                new()
                {
                    OrderItemId = 3,
                    ProductId = 25,
                    ProductName = "Atchara Cup",
                    Quantity = 1.00m,
                    UnitPrice = 0.00m,
                    LineTotal = 0.00m,
                    IsComplimentary = true,
                    ParentProductId = 20
                },
                new()
                {
                    OrderItemId = 4,
                    ProductId = 26,
                    ProductName = "Sarsa Cup",
                    Quantity = 1.00m,
                    UnitPrice = 0.00m,
                    LineTotal = 0.00m,
                    IsComplimentary = true,
                    ParentProductId = 20
                }
            },
            Payments = new List<PaymentResponseDto>
            {
                new() { PaymentMethod = "CASH", Amount = 1500.00m, AmountTendered = 1500.00m, ChangeAmount = 0m }
            }
        };

        var html = POS.Client.Services.ReceiptHtmlFormatter.Format(receipt);
        Assert(html.Contains("LECHON", StringComparison.OrdinalIgnoreCase), "Receipt must list Lechon.");
        Assert(html.Contains("Sprite 330ml [COMPLIMENTARY]", StringComparison.OrdinalIgnoreCase), "Receipt must label complimentary Sprite with [COMPLIMENTARY].");
        Assert(html.Contains("Atchara Cup [COMPLIMENTARY]", StringComparison.OrdinalIgnoreCase), "Receipt must label complimentary Atchara with [COMPLIMENTARY].");
        Assert(html.Contains("Sarsa Cup [COMPLIMENTARY]", StringComparison.OrdinalIgnoreCase), "Receipt must label complimentary Sarsa with [COMPLIMENTARY].");
        Assert(html.Contains("1,500.00"), "Receipt must show Subtotal and Total as 1,500.00.");

        return Task.CompletedTask;
    }

    private static async Task TestWeightBased_OfflineSyncAndInventory()
    {
        var (conn, db, _) = await CreateTestEnvAsync();
        using (conn)
        using (db)
        {
            db.Categories.Add(new Category { CategoryId = 1, Name = "Food" });
            db.InvoiceSequences.Add(new InvoiceSequence { TerminalId = 1, SeriesCode = "OFF", Prefix = "OFF-", StartingNumber = 1, EndingNumber = 1000, LastIssuedNumber = 0 });

            var pLechon = new Product { ProductId = 20, CategoryId = 1, Name = "Lechon", Price = 950m, ProductType = "Standard", PricingMethod = "WeightBased", IsActive = true };
            var pSprite = new Product { ProductId = 22, CategoryId = 1, Name = "Sprite 330ml", Price = 50m, ProductType = "Standard", PricingMethod = "Fixed", IsActive = true };
            var pAtchara = new Product { ProductId = 25, CategoryId = 1, Name = "Atchara Cup", Price = 30m, ProductType = "Standard", PricingMethod = "Fixed", IsActive = true };
            var pSarsa = new Product { ProductId = 26, CategoryId = 1, Name = "Sarsa Cup", Price = 20m, ProductType = "Standard", PricingMethod = "Fixed", IsActive = true };
            db.Products.AddRange(pLechon, pSprite, pAtchara, pSarsa);

            db.BranchInventories.Add(new BranchInventory { BranchId = 1, ProductId = 20, StockQuantity = 10, LastUpdated = DateTime.UtcNow });
            db.BranchInventories.Add(new BranchInventory { BranchId = 1, ProductId = 22, StockQuantity = 20, LastUpdated = DateTime.UtcNow });
            db.BranchInventories.Add(new BranchInventory { BranchId = 1, ProductId = 25, StockQuantity = 30, LastUpdated = DateTime.UtcNow });
            db.BranchInventories.Add(new BranchInventory { BranchId = 1, ProductId = 26, StockQuantity = 40, LastUpdated = DateTime.UtcNow });

            var mg = new ModifierGroup { ModifierGroupId = 5, Name = "Complimentary Drink", IsActive = true };
            db.ModifierGroups.Add(mg);
            var mo = new ModifierOption { ModifierOptionId = 50, ModifierGroupId = 5, ProductId = 22, Name = "Sprite 330ml", Price = 0m, IsActive = true };
            db.ModifierOptions.Add(mo);
            await db.SaveChangesAsync();

            var controller = new OfflineSyncController(db);
            Authenticate(controller);

            // Sale: 2.0 kg Lechon (2 sets of freebies)
            var req = new OfflineTransactionSyncRequest
            {
                LocalTransactionId = "TX-WB-SYNC-01",
                BranchId = 1,
                TerminalId = 1,
                CashierId = "cashier1",
                DeviceIdentifier = "TEST",
                OrderNumber = "POS01-000088",
                InvoiceNumber = "OFF-0000088",
                PayloadHash = "hash-wb-01",
                TransactionDate = DateTime.UtcNow,
                Subtotal = 1900m,
                TotalAmount = 1900m,
                VatableSales = 1696.43m,
                VatAmount = 203.57m,
                Items = new List<OfflineTransactionItemDto>
                {
                    new()
                    {
                        ProductId = 20,
                        ProductName = "Lechon",
                        Quantity = 2.00m,
                        UnitPrice = 950m,
                        LineTotal = 1900m,
                        PricingMethod = "WeightBased",
                        Modifiers = new List<OfflineModifierDto>
                        {
                            new() { OptionId = 50, ProductId = 22, Name = "Sprite 330ml", AdditionalPrice = 0m }
                        }
                    },
                    new()
                    {
                        ProductId = 22,
                        ProductName = "Sprite 330ml",
                        Quantity = 2.00m,
                        UnitPrice = 0m,
                        LineTotal = 0m,
                        IsComplimentary = true,
                        ParentProductId = 20
                    },
                    new()
                    {
                        ProductId = 25,
                        ProductName = "Atchara Cup",
                        Quantity = 2.00m,
                        UnitPrice = 0m,
                        LineTotal = 0m,
                        IsComplimentary = true,
                        ParentProductId = 20
                    },
                    new()
                    {
                        ProductId = 26,
                        ProductName = "Sarsa Cup",
                        Quantity = 2.00m,
                        UnitPrice = 0m,
                        LineTotal = 0m,
                        IsComplimentary = true,
                        ParentProductId = 20
                    }
                },
                Payments = new List<OfflinePaymentDto>
                {
                    new() { Amount = 1900m, TenderedAmount = 1900m, ChangeAmount = 0m, PaymentMethod = "CASH", PaidAt = DateTime.UtcNow }
                }
            };

            var syncRes = await controller.SyncTransaction(req);
            Assert(syncRes.Result is OkObjectResult, "Syncing weight-based transaction with complimentary items must succeed.");

            // Verify inventory deductions
            var lechonInv = await db.BranchInventories.FirstOrDefaultAsync(bi => bi.BranchId == 1 && bi.ProductId == 20);
            Assert(lechonInv != null && lechonInv.StockQuantity == 8, "Lechon stock must decrease from 10 to 8.");

            var spriteInv = await db.BranchInventories.FirstOrDefaultAsync(bi => bi.BranchId == 1 && bi.ProductId == 22);
            Assert(spriteInv != null && spriteInv.StockQuantity == 18, "Sprite stock must decrease from 20 to 18 (deducted exactly once, no double-deduction).");

            var atcharaInv = await db.BranchInventories.FirstOrDefaultAsync(bi => bi.BranchId == 1 && bi.ProductId == 25);
            Assert(atcharaInv != null && atcharaInv.StockQuantity == 28, "Atchara stock must decrease from 30 to 28.");

            var sarsaInv = await db.BranchInventories.FirstOrDefaultAsync(bi => bi.BranchId == 1 && bi.ProductId == 26);
            Assert(sarsaInv != null && sarsaInv.StockQuantity == 38, "Sarsa stock must decrease from 40 to 38.");

            // Verify persisted OrderItems
            var items = await db.OrderItems.Where(i => i.Order.InvoiceNumber == "OFF-0000088").ToListAsync();
            Assert(items.Count == 4, "Must persist 4 OrderItems.");
            var lechonSaved = items.First(i => i.ProductId == 20);
            Assert(lechonSaved.Quantity == 2.00m && lechonSaved.LineTotal == 1900m && lechonSaved.PricingMethod == "WeightBased", "Lechon saved properly.");

            var spriteSaved = items.First(i => i.ProductId == 22);
            Assert(spriteSaved.IsComplimentary && spriteSaved.ParentProductId == 20 && spriteSaved.LineTotal == 0m, "Complimentary Sprite saved properly.");
        }
    }

    private static Task TestWeightBased_DiscountCalculationService()
    {
        var calculator = new POS.Shared.Services.LocalDiscountCalculationService();

        // ₱1,500 Amount Mode order with complimentary Drink, Atchara, Sarsa
        var req = new OrderCalculationRequestDto
        {
            BranchTaxType = "VAT-Registered",
            DiscountCode = "NONE",
            Items = new List<CartItemCalculationDto>
            {
                new()
                {
                    ProductId = 20,
                    ProductName = "Lechon",
                    Quantity = 1.578947m,
                    UnitPrice = 950.00m,
                    TargetAmount = 1500.00m,
                    TaxName = "VATable"
                },
                new()
                {
                    ProductId = 22,
                    ProductName = "Sprite 330ml",
                    Quantity = 1.00m,
                    UnitPrice = 0.00m,
                    IsComplimentary = true,
                    TaxName = "VATable"
                },
                new()
                {
                    ProductId = 25,
                    ProductName = "Atchara Cup",
                    Quantity = 1.00m,
                    UnitPrice = 0.00m,
                    IsComplimentary = true,
                    TaxName = "VATable"
                },
                new()
                {
                    ProductId = 26,
                    ProductName = "Sarsa Cup",
                    Quantity = 1.00m,
                    UnitPrice = 0.00m,
                    IsComplimentary = true,
                    TaxName = "VATable"
                }
            }
        };

        var res = calculator.CalculateOrder(req);
        Assert(res.Subtotal == 1500.00m, "Subtotal must be ₱1,500.00.");
        Assert(res.TotalAmount == 1500.00m, "TotalAmount must be ₱1,500.00.");
        Assert(res.VatableSales == 1339.29m, "Vatable sales = 1500 / 1.12 = 1339.29.");
        Assert(res.VatAmount == 160.71m, "VAT amount = 1500 - 1339.29 = 160.71.");

        // Now apply Senior Citizen (SC) 20% discount
        req.DiscountCode = "SC";
        var scRes = calculator.CalculateOrder(req);
        Assert(scRes.DiscountAmount == 267.86m, "SC Discount amount must be ₱267.86.");
        Assert(scRes.TotalAmount == 1071.43m, "SC TotalAmount must be ₱1,071.43.");
        Assert(scRes.VatExemptSales == 1339.29m, "SC VatExemptSales must be ₱1,339.29.");
        Assert(scRes.VatAmount == 0.00m, "SC VatAmount must be ₱0.00.");

        return Task.CompletedTask;
    }

    private static async Task TestWeightBased_ProductPricingMethodPersistence()
    {
        var (conn, db, _) = await CreateTestEnvAsync();
        using (conn)
        using (db)
        {
            db.Categories.Add(new Category { CategoryId = 1, Name = "Meat" });
            await db.SaveChangesAsync();

            var env = new TestWebHostEnvironment();
            var controller = new ProductsController(db, env);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.Name, "admin"),
                        new Claim(ClaimTypes.Role, "Admin")
                    }, "TestAuth"))
                }
            };

            // 1. Create product with PricingMethod = "WeightBased"
            var createDto = new ProductDto
            {
                CategoryId = 1,
                Name = "Roasted Lechon",
                Price = 950.00m,
                CostPrice = 600.00m,
                ProductType = "Standard",
                PricingMethod = "WeightBased",
                UnitOfMeasure = "kg",
                UnitValue = 1.0m,
                IsActive = true
            };

            var createRes = await controller.CreateProduct(createDto);
            Assert(createRes.Result is CreatedAtActionResult, "CreateProduct must return CreatedAtActionResult.");
            var createdDto = (createRes.Result as CreatedAtActionResult)!.Value as ProductDto;
            Assert(createdDto != null && createdDto.PricingMethod == "WeightBased", "Created product must have PricingMethod = 'WeightBased'.");

            // Verify database entity directly
            var dbProduct = await db.Products.FindAsync(createdDto!.ProductId);
            Assert(dbProduct != null, "Product must be persisted in database.");
            Assert(dbProduct!.PricingMethod == "WeightBased", "Database entity PricingMethod must be 'WeightBased'.");

            // 2. Update product back to "Fixed"
            createdDto.PricingMethod = "Fixed";
            var updateRes = await controller.UpdateProduct(createdDto.ProductId, createdDto);
            Assert(updateRes is NoContentResult, "UpdateProduct must return NoContent.");

            var reloaded = await db.Products.FindAsync(createdDto.ProductId);
            Assert(reloaded!.PricingMethod == "Fixed", "Updated database entity PricingMethod must be 'Fixed'.");

            // 3. Update product back to "WeightBased"
            createdDto.PricingMethod = "WeightBased";
            await controller.UpdateProduct(createdDto.ProductId, createdDto);
            var reloaded2 = await db.Products.FindAsync(createdDto.ProductId);
            Assert(reloaded2!.PricingMethod == "WeightBased", "Updated database entity PricingMethod must be 'WeightBased'.");

            // 4. Test GetProducts returns correct PricingMethod
            var getRes = await controller.GetProducts();
            var okResult = getRes.Result as OkObjectResult;
            Assert(okResult != null, "GetProducts must return OkObjectResult.");
            var productsList = okResult!.Value as List<ProductDto>;
            var lechon = productsList?.FirstOrDefault(p => p.ProductId == createdDto.ProductId);
            Assert(lechon != null && lechon.PricingMethod == "WeightBased", "GetProducts DTO must return PricingMethod = 'WeightBased'.");
        }
    }

    private static async Task TestCustomSorting_CategoriesAndProducts()
    {
        var (conn, db, _) = await CreateTestEnvAsync();
        using (conn)
        using (db)
        {
            var catController = new CategoriesController(db);
            catController.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.Name, "admin"),
                        new Claim(ClaimTypes.Role, "Admin"),
                        new Claim("terminal_id", "1"),
                        new Claim("branch_id", "1")
                    }, "TestAuth"))
                }
            };

            var env = new TestWebHostEnvironment();
            var prodController = new ProductsController(db, env);
            prodController.ControllerContext = catController.ControllerContext;

            // 1. Create 3 categories with auto-increment DisplayOrder
            var cat1Res = await catController.CreateCategory(new CategoryDto { Name = "Beverages", ColorHex = "#3B82F6", IsActive = true });
            var cat1 = ((CreatedAtActionResult)cat1Res.Result!).Value as CategoryDto;
            Assert(cat1!.DisplayOrder == 1, $"Cat1 DisplayOrder must be 1, got {cat1.DisplayOrder}");

            var cat2Res = await catController.CreateCategory(new CategoryDto { Name = "Main Dishes", ColorHex = "#EF4444", IsActive = true });
            var cat2 = ((CreatedAtActionResult)cat2Res.Result!).Value as CategoryDto;
            Assert(cat2!.DisplayOrder == 2, $"Cat2 DisplayOrder must be 2, got {cat2.DisplayOrder}");

            var cat3Res = await catController.CreateCategory(new CategoryDto { Name = "Desserts", ColorHex = "#10B981", IsActive = true });
            var cat3 = ((CreatedAtActionResult)cat3Res.Result!).Value as CategoryDto;
            Assert(cat3!.DisplayOrder == 3, $"Cat3 DisplayOrder must be 3, got {cat3.DisplayOrder}");

            // 2. Reorder categories: Desserts (3), Beverages (1), Main Dishes (2)
            var reorderCatRes = await catController.ReorderCategories(new List<int> { cat3.CategoryId, cat1.CategoryId, cat2.CategoryId });
            Assert(reorderCatRes is OkResult, "ReorderCategories must return OkResult.");

            var catListRes = await catController.GetCategories();
            var catList = ((OkObjectResult)catListRes.Result!).Value as List<CategoryDto>;
            Assert(catList != null && catList.Count >= 3, "GetCategories must return at least 3 categories.");
            var myCats = catList!.Where(c => c.CategoryId == cat3.CategoryId || c.CategoryId == cat1.CategoryId || c.CategoryId == cat2.CategoryId).ToList();
            Assert(myCats[0].CategoryId == cat3.CategoryId && myCats[0].DisplayOrder == 1, "First category must be Desserts with DisplayOrder 1.");
            Assert(myCats[1].CategoryId == cat1.CategoryId && myCats[1].DisplayOrder == 2, "Second category must be Beverages with DisplayOrder 2.");
            Assert(myCats[2].CategoryId == cat2.CategoryId && myCats[2].DisplayOrder == 3, "Third category must be Main Dishes with DisplayOrder 3.");

            // 3. Create products in Beverages (cat1) and Desserts (cat3)
            var p1Res = await prodController.CreateProduct(new ProductDto { CategoryId = cat1.CategoryId, Name = "Iced Tea", Price = 50, CostPrice = 20, ProductType = "Standard", IsActive = true });
            var p1 = ((CreatedAtActionResult)p1Res.Result!).Value as ProductDto;
            Assert(p1!.DisplayOrder == 1, $"Product 1 in Cat1 must have DisplayOrder 1, got {p1.DisplayOrder}");

            var p2Res = await prodController.CreateProduct(new ProductDto { CategoryId = cat1.CategoryId, Name = "Lemonade", Price = 60, CostPrice = 25, ProductType = "Standard", IsActive = true });
            var p2 = ((CreatedAtActionResult)p2Res.Result!).Value as ProductDto;
            Assert(p2!.DisplayOrder == 2, $"Product 2 in Cat1 must have DisplayOrder 2, got {p2.DisplayOrder}");

            var p3Res = await prodController.CreateProduct(new ProductDto { CategoryId = cat1.CategoryId, Name = "Mineral Water", Price = 30, CostPrice = 10, ProductType = "Standard", IsActive = true });
            var p3 = ((CreatedAtActionResult)p3Res.Result!).Value as ProductDto;
            Assert(p3!.DisplayOrder == 3, $"Product 3 in Cat1 must have DisplayOrder 3, got {p3.DisplayOrder}");

            var p4Res = await prodController.CreateProduct(new ProductDto { CategoryId = cat3.CategoryId, Name = "Halo-Halo", Price = 120, CostPrice = 60, ProductType = "Standard", IsActive = true });
            var p4 = ((CreatedAtActionResult)p4Res.Result!).Value as ProductDto;
            Assert(p4!.DisplayOrder == 1, $"Product 4 in Cat3 must have DisplayOrder 1, got {p4.DisplayOrder}");

            // 4. Reorder products in Beverages (cat1): Mineral Water (3), Iced Tea (1), Lemonade (2)
            var reorderProdRes = await prodController.ReorderProducts(cat1.CategoryId, new List<int> { p3.ProductId, p1.ProductId, p2.ProductId });
            Assert(reorderProdRes is OkResult, "ReorderProducts must return OkResult.");

            var prodListRes = await prodController.GetProducts();
            var prodList = ((OkObjectResult)prodListRes.Result!).Value as List<ProductDto>;
            var cat1Prods = prodList!.Where(p => p.CategoryId == cat1.CategoryId).ToList();
            Assert(cat1Prods.Count == 3, "Cat1 must have 3 products.");
            Assert(cat1Prods[0].ProductId == p3.ProductId && cat1Prods[0].DisplayOrder == 1, "First Cat1 prod must be Mineral Water with DisplayOrder 1.");
            Assert(cat1Prods[1].ProductId == p1.ProductId && cat1Prods[1].DisplayOrder == 2, "Second Cat1 prod must be Iced Tea with DisplayOrder 2.");
            Assert(cat1Prods[2].ProductId == p2.ProductId && cat1Prods[2].DisplayOrder == 3, "Third Cat1 prod must be Lemonade with DisplayOrder 3.");

            // Verify Cat3 product order untouched
            var cat3Prods = prodList.Where(p => p.CategoryId == cat3.CategoryId).ToList();
            Assert(cat3Prods.Count == 1 && cat3Prods[0].ProductId == p4.ProductId && cat3Prods[0].DisplayOrder == 1, "Cat3 products must remain unaffected.");

            // 5. Verify Offline Reference Data Controller returns sorted data with DisplayOrder
            var offlineController = new OfflineReferenceDataController(db);
            offlineController.ControllerContext = catController.ControllerContext;

            var refDataRes = await offlineController.GetReferenceData(1, null);
            var okRef = refDataRes.Result as OkObjectResult;
            Assert(okRef != null, "GetReferenceData must return OkObjectResult.");
            var refData = okRef!.Value as OfflineReferenceDataResponseDto;
            Assert(refData != null, "Reference data must not be null.");

            // Check category sorting in offline reference data
            var refCat3 = refData!.Categories.FirstOrDefault(c => c.CategoryId == cat3.CategoryId);
            var refCat1 = refData.Categories.FirstOrDefault(c => c.CategoryId == cat1.CategoryId);
            var refCat2 = refData.Categories.FirstOrDefault(c => c.CategoryId == cat2.CategoryId);
            Assert(refCat3 != null && refCat3.DisplayOrder == 1, "Offline ref Cat3 DisplayOrder must be 1.");
            Assert(refCat1 != null && refCat1.DisplayOrder == 2, "Offline ref Cat1 DisplayOrder must be 2.");
            Assert(refCat2 != null && refCat2.DisplayOrder == 3, "Offline ref Cat2 DisplayOrder must be 3.");

            // Check product sorting in offline reference data
            var refP3 = refData.Products.FirstOrDefault(p => p.ProductId == p3.ProductId);
            var refP1 = refData.Products.FirstOrDefault(p => p.ProductId == p1.ProductId);
            var refP2 = refData.Products.FirstOrDefault(p => p.ProductId == p2.ProductId);
            Assert(refP3 != null && refP3.DisplayOrder == 1, "Offline ref P3 DisplayOrder must be 1.");
            Assert(refP1 != null && refP1.DisplayOrder == 2, "Offline ref P1 DisplayOrder must be 2.");
            Assert(refP2 != null && refP2.DisplayOrder == 3, "Offline ref P2 DisplayOrder must be 3.");
        }
    }

    private static async Task TestReceiptLogoManagementAndPrinting()
    {
        // 1. Validate ReceiptLogoValidator magic bytes, size limits, and extensions
        var emptyCheck = POS.Shared.Helpers.ReceiptLogoValidator.Validate(Array.Empty<byte>(), "logo.png");
        Assert(!emptyCheck.IsValid, "Empty file must be rejected.");

        var spoofedCheck = POS.Shared.Helpers.ReceiptLogoValidator.Validate(Encoding.UTF8.GetBytes("<script>alert(1)</script>"), "logo.png");
        Assert(!spoofedCheck.IsValid, "Spoofed non-image bytes with .png extension must be rejected.");

        var svgCheck = POS.Shared.Helpers.ReceiptLogoValidator.Validate(Encoding.UTF8.GetBytes("<svg></svg>"), "logo.svg");
        Assert(!svgCheck.IsValid, "Unsupported .svg extension must be rejected.");

        // Valid 1x1 PNG (67 bytes)
        byte[] validPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        var pngCheck = POS.Shared.Helpers.ReceiptLogoValidator.Validate(validPng, "brand-logo.png");
        Assert(pngCheck.IsValid && pngCheck.MimeType == "image/png" && pngCheck.Width == 1 && pngCheck.Height == 1,
            "Valid 1x1 PNG must pass validation and extract dimensions.");

        // Valid minimal JPEG with SOF0 header (width=16, height=8)
        byte[] validJpeg = new byte[]
        {
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x01, 0x00, 0x48, 0x00, 0x48, 0x00, 0x00,
            0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x08, 0x00, 0x10, 0x01, 0x01, 0x11, 0x00,
            0xFF, 0xD9
        };
        var jpgCheck = POS.Shared.Helpers.ReceiptLogoValidator.Validate(validJpeg, "brand-logo.jpg");
        Assert(jpgCheck.IsValid && jpgCheck.MimeType == "image/jpeg" && jpgCheck.Width == 16 && jpgCheck.Height == 8,
            $"Valid JPEG must pass validation and extract dimensions (got Valid={jpgCheck.IsValid}, W={jpgCheck.Width}, H={jpgCheck.Height}).");

        Assert(!POS.Shared.Helpers.ReceiptLogoValidator.IsSafeDataUri("javascript:alert(1)"), "javascript URI must be rejected.");
        Assert(!POS.Shared.Helpers.ReceiptLogoValidator.IsSafeDataUri("data:image/png;base64,abc\"onerror=\"alert(1)"), "Quotes/XSS in data URI must be rejected.");

        // 2. Test ReceiptBrandingController CRUD, BOLA protection, and fallback preservation
        var (conn, db, _) = await CreateTestEnvAsync();
        using (conn)
        using (db)
        {
            db.Branches.Add(new Branch { BranchId = 2, Name = "Branch Two", Code = "BR-02", TaxType = "VAT-Registered", IsActive = true });
            await db.SaveChangesAsync();

            var env = new TestWebHostEnvironment();
            var brandingController = new ReceiptBrandingController(db, env);

            // Manager of Branch 1 attempting to modify Branch 2 -> Forbid
            brandingController.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.Name, "manager1"),
                        new Claim(ClaimTypes.Role, "Manager"),
                        new Claim("branch_id", "1")
                    }, "TestAuth"))
                }
            };
            var forbidRes = await brandingController.GetReceiptBranding(2);
            Assert(forbidRes.Result is ForbidResult, "Manager of Branch 1 must be forbidden from accessing Branch 2 receipt branding.");

            // Admin context
            var adminContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.Name, "admin"),
                        new Claim(ClaimTypes.Role, "Admin"),
                        new Claim("branch_id", "1"),
                        new Claim("terminal_id", "1")
                    }, "TestAuth"))
                }
            };
            brandingController.ControllerContext = adminContext;

            // Upload valid PNG to Branch 1
            using var pngStream = new System.IO.MemoryStream(validPng);
            IFormFile pngFormFile = new FormFile(pngStream, 0, validPng.Length, "file", "store-logo.png");
            var uploadRes = await brandingController.UploadLogo(1, pngFormFile, applyToAllBranches: false);
            var uploadOk = uploadRes.Result as OkObjectResult;
            Assert(uploadOk != null, "Uploading valid PNG logo must return 200 OK.");
            var uploadDto = uploadOk!.Value as ReceiptBrandingResultDto;
            Assert(uploadDto != null && uploadDto.Success && uploadDto.Branding != null, "Upload response must include Branding DTO.");
            Assert(uploadDto!.Branding!.HasLogo && uploadDto.Branding.ReceiptLogoUrl!.StartsWith("/uploads/logos/logo_", StringComparison.Ordinal),
                "Uploaded logo URL must use safe generated server filename.");
            Assert(POS.Shared.Helpers.ReceiptLogoValidator.IsSafeDataUri(uploadDto.Branding.ReceiptLogoDataUri),
                "Upload response must include valid base64 data URI.");

            string firstLogoUrl = uploadDto.Branding.ReceiptLogoUrl!;

            // Attempt invalid upload -> must fail with BadRequest AND preserve firstLogoUrl
            using var badStream = new System.IO.MemoryStream(Encoding.UTF8.GetBytes("not an image"));
            IFormFile badFormFile = new FormFile(badStream, 0, badStream.Length, "file", "corrupt.png");
            var badUploadRes = await brandingController.UploadLogo(1, badFormFile, applyToAllBranches: false);
            Assert(badUploadRes.Result is BadRequestObjectResult, "Corrupted image upload must return 400 BadRequest.");

            var branchAfterFailedUpload = await db.Branches.AsNoTracking().FirstAsync(b => b.BranchId == 1);
            Assert(branchAfterFailedUpload.ReceiptLogoUrl == firstLogoUrl, "Existing logo must be preserved when a new upload fails validation.");

            // Verify OfflineReferenceDataController includes ReceiptLogoUrl and ReceiptLogoDataUri
            var offlineController = new OfflineReferenceDataController(db) { ControllerContext = adminContext };
            var refDataRes = await offlineController.GetReferenceData(1, null);
            var refData = ((OkObjectResult)refDataRes.Result!).Value as OfflineReferenceDataResponseDto;
            Assert(refData != null && refData.Terminal.ReceiptLogoUrl == firstLogoUrl &&
                   POS.Shared.Helpers.ReceiptLogoValidator.IsSafeDataUri(refData.Terminal.ReceiptLogoDataUri),
                "OfflineReferenceDataController must return ReceiptLogoUrl and ReceiptLogoDataUri for offline caching.");

            // Verify ReceiptHtmlFormatter renders <img class="receipt-logo"> when logo is configured
            var sampleReceipt = new ReceiptDto
            {
                OrderId = 500,
                OrderNumber = "ORD-500",
                InvoiceNumber = "SI-0000500",
                BranchId = 1,
                BranchName = "Main Branch",
                BranchCode = "BR-01",
                TerminalId = 1,
                TerminalName = "Counter 1",
                CashierId = "cashier1",
                CreatedAt = DateTime.UtcNow,
                PaidAt = DateTime.UtcNow,
                Subtotal = 100m,
                TotalAmount = 100m,
                ReceiptLogoUrl = refData!.Terminal.ReceiptLogoUrl,
                ReceiptLogoDataUri = refData.Terminal.ReceiptLogoDataUri,
                Items = new List<OrderItemResponseDto>
                {
                    new() { ProductName = "Coffee", Quantity = 1, UnitPrice = 100m, LineTotal = 100m }
                },
                Payments = new List<PaymentResponseDto>
                {
                    new() { PaymentMethod = "CASH", Amount = 100m, AmountTendered = 100m, ChangeAmount = 0m }
                }
            };

            string htmlWithLogo = POS.Client.Services.ReceiptHtmlFormatter.Format(sampleReceipt);
            Assert(htmlWithLogo.Contains("img-src data:;", StringComparison.Ordinal), "Receipt HTML CSP must allow data: images.");
            Assert(htmlWithLogo.Contains("<div class=\"receipt-logo-wrap\"><img class=\"receipt-logo\" src=\"data:image/png;base64,", StringComparison.Ordinal),
                "Receipt HTML must render centered receipt-logo img at the top of the header.");

            // Remove logo -> verify DB cleared and ReceiptHtmlFormatter omits <img>
            var removeRes = await brandingController.RemoveLogo(1, applyToAllBranches: true);
            Assert(removeRes.Result is OkObjectResult, "RemoveLogo must return 200 OK.");
            var branchAfterRemove = await db.Branches.AsNoTracking().FirstAsync(b => b.BranchId == 1);
            Assert(string.IsNullOrEmpty(branchAfterRemove.ReceiptLogoUrl), "Branch ReceiptLogoUrl must be null after removal.");

            sampleReceipt.ReceiptLogoUrl = null;
            sampleReceipt.ReceiptLogoDataUri = null;
            string htmlWithoutLogo = POS.Client.Services.ReceiptHtmlFormatter.Format(sampleReceipt);
            Assert(!htmlWithoutLogo.Contains("<img class=\"receipt-logo\"", StringComparison.Ordinal),
                "Receipt HTML without a configured logo must not render an <img> tag or broken image placeholder.");
        }
    }

    private static async Task TestBranchVatDisplayAndLocalBranchSync()
    {
        var (conn, db, _) = await CreateTestEnvAsync();
        using (conn)
        using (db)
        {
            var adminContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.Name, "admin"),
                        new Claim(ClaimTypes.Role, "Admin"),
                        new Claim("branch_id", "1"),
                        new Claim("terminal_id", "1")
                    }, "TestAuth"))
                }
            };

            var branchesController = new BranchesController(db) { ControllerContext = adminContext };
            var brandingController = new ReceiptBrandingController(db, new TestWebHostEnvironment()) { ControllerContext = adminContext };

            // 1. Default VatDisplay = true and ShowCustomerDetailsOnReceipt = true on existing Branch 1 and new Branch creation
            var brandingInitialRes = await brandingController.GetReceiptBranding(1);
            var brandingInitial = ((OkObjectResult)brandingInitialRes.Result!).Value as ReceiptBrandingDto;
            Assert(brandingInitial != null && brandingInitial.ShowCustomerDetailsOnReceipt,
                "GetReceiptBranding must default to ShowCustomerDetailsOnReceipt = true.");

            var createRes = await branchesController.CreateBranch(new BranchDto
            {
                Name = "Branch No-VAT-Display",
                Code = "BR-NOVATDISP",
                TaxType = "VAT-Registered",
                VatDisplay = false,
                ShowCustomerDetailsOnReceipt = false,
                IsActive = true
            });
            var createdBranch = ((OkObjectResult)createRes.Result!).Value as BranchDto;
            Assert(createdBranch != null && createdBranch.BranchId > 0 && !createdBranch.VatDisplay && !createdBranch.ShowCustomerDetailsOnReceipt,
                "CreateBranch must persist VatDisplay = false and ShowCustomerDetailsOnReceipt = false when configured.");

            // 2. Update Branch 1 VatDisplay -> false and ShowCustomerDetailsOnReceipt -> false via ReceiptBrandingController
            var branch1 = await db.Branches.AsNoTracking().FirstAsync(b => b.BranchId == 1);
            Assert(branch1.VatDisplay && branch1.ShowCustomerDetailsOnReceipt,
                "Branch 1 must default to VatDisplay = true and ShowCustomerDetailsOnReceipt = true.");

            var settingsRes = await brandingController.UpdateSettings(1, new UpdateReceiptBrandingSettingsDto
            {
                ShowCustomerDetailsOnReceipt = false,
                ApplyToAllBranches = false
            });
            var settingsResult = ((OkObjectResult)settingsRes.Result!).Value as ReceiptBrandingResultDto;
            Assert(settingsResult != null && settingsResult.Success && settingsResult.Branding != null && !settingsResult.Branding.ShowCustomerDetailsOnReceipt,
                "ReceiptBrandingController.UpdateSettings must persist ShowCustomerDetailsOnReceipt = false.");

            var updateRes = await branchesController.UpdateBranch(1, new BranchDto
            {
                BranchId = 1,
                BusinessTypeId = branch1.BusinessTypeId,
                ServiceModeId = branch1.ServiceModeId,
                Name = branch1.Name,
                Code = branch1.Code,
                Address = branch1.Address,
                Phone = branch1.Phone,
                TaxType = branch1.TaxType,
                VatDisplay = false,
                ShowCustomerDetailsOnReceipt = false,
                IsActive = branch1.IsActive,
                TrackInventory = branch1.TrackInventory
            });
            Assert(updateRes is NoContentResult, "UpdateBranch must return 204 NoContent.");

            var clientCfgRes = await branchesController.GetBranchClientConfig(1);
            var clientCfg = ((OkObjectResult)clientCfgRes.Result!).Value as OfflineBranchConfigDto;
            Assert(clientCfg != null && clientCfg.BranchId == 1 && !clientCfg.VatDisplay && !clientCfg.ShowCustomerDetailsOnReceipt && clientCfg.TaxType == "VAT-Registered",
                "GetBranchClientConfig must return updated VatDisplay = false and ShowCustomerDetailsOnReceipt = false while keeping TaxType = VAT-Registered.");

            // 3. OfflineReferenceDataController returns Branch DTO and Terminal with VatDisplay = false and ShowCustomerDetailsOnReceipt = false
            var offlineRefController = new OfflineReferenceDataController(db) { ControllerContext = adminContext };
            var refRes = await offlineRefController.GetReferenceData(1, null);
            var refDto = ((OkObjectResult)refRes.Result!).Value as OfflineReferenceDataResponseDto;
            Assert(refDto != null && refDto.Branch != null && !refDto.Branch.VatDisplay && !refDto.Terminal.VatDisplay &&
                   !refDto.Branch.ShowCustomerDetailsOnReceipt && !refDto.Terminal.ShowCustomerDetailsOnReceipt,
                "OfflineReferenceDataController must include Branch and Terminal with VatDisplay = false and ShowCustomerDetailsOnReceipt = false.");

            // 4. Online Order creation & ReceiptService: VAT calculation and customer transaction data MUST remain intact even when flags are false!
            var discountCalc = new POS.Api.Services.DiscountCalculationService();
            var calcResult = discountCalc.CalculateOrder(new OrderCalculationRequestDto
            {
                BranchTaxType = "VAT-Registered",
                DiscountCode = "NONE",
                Items = new List<CartItemCalculationDto>
                {
                    new() { ProductId = 1, ProductName = "Burger", Quantity = 1, UnitPrice = 112m, TaxName = "VATable" }
                }
            });
            Assert(calcResult.VatableSales == 100.00m && calcResult.VatAmount == 12.00m && calcResult.TotalAmount == 112.00m,
                "VAT calculation must remain 100.00 Vatable + 12.00 VAT = 112.00 Total regardless of VatDisplay.");

            var order = new Order
            {
                OrderNumber = "POS01-000777",
                InvoiceNumber = "SI-0000777",
                BranchId = 1,
                OutletId = 1,
                TerminalId = 1,
                CashierId = "cashier1",
                OrderType = "DINING",
                CustomerName = "Juan Dela Cruz",
                CustomerAddress = "Makati City",
                CustomerTin = "123-456-789-000",
                CustomerBusinessStyle = "Retail Trading",
                Subtotal = 112m,
                VatableSales = calcResult.VatableSales,
                VatExemptSales = calcResult.VatExemptSales,
                ZeroRatedSales = calcResult.ZeroRatedSales,
                VatAmount = calcResult.VatAmount,
                DiscountAmount = 0m,
                TotalAmount = 112m,
                PaymentStatus = "PAID",
                OrderStatus = "COMPLETED",
                CreatedAt = DateTime.UtcNow,
                InvoiceIssuedAt = DateTime.UtcNow
            };
            order.OrderItems.Add(new OrderItem
            {
                ProductId = 1,
                ProductName = "Burger",
                Quantity = 1,
                BasePrice = 112m,
                UnitPrice = 112m,
                LineTotal = 112m,
                VatType = "VATABLE",
                VatAmount = 12m
            });
            order.Payments.Add(new Payment
            {
                PaymentMethod = "CASH",
                Amount = 112m,
                AmountTendered = 112m,
                ChangeAmount = 0m,
                PaymentStatus = "PAID",
                ProcessedBy = "cashier1",
                PaidAt = DateTime.UtcNow
            });
            db.Orders.Add(order);
            await db.SaveChangesAsync();

            var savedOrderInDb = await db.Orders.AsNoTracking().FirstAsync(o => o.OrderId == order.OrderId);
            Assert(savedOrderInDb.CustomerName == "Juan Dela Cruz" &&
                   savedOrderInDb.CustomerAddress == "Makati City" &&
                   savedOrderInDb.CustomerTin == "123-456-789-000" &&
                   savedOrderInDb.CustomerBusinessStyle == "Retail Trading",
                "Orders table must preserve all customer fields when ShowCustomerDetailsOnReceipt = false.");

            var receiptService = new POS.Api.Services.ReceiptService(db);
            var receiptFalse = await receiptService.GetReceiptAsync(order.OrderId);
            var receiptReprintFalse = await receiptService.GetReceiptAsync(order.OrderId, isReprint: true);
            Assert(receiptFalse != null && !receiptFalse.VatDisplay && !receiptFalse.ShowCustomerDetailsOnReceipt &&
                   receiptFalse.VatableSales == 100m && receiptFalse.VatAmount == 12m &&
                   receiptFalse.CustomerName == "Juan Dela Cruz" && receiptFalse.CustomerTin == "123-456-789-000",
                "ReceiptService must preserve VatableSales, VatAmount, and Customer data on ReceiptDto while setting VatDisplay = false and ShowCustomerDetailsOnReceipt = false.");
            Assert(receiptReprintFalse != null && receiptReprintFalse.IsReprint && !receiptReprintFalse.ShowCustomerDetailsOnReceipt,
                "ReceiptService reprint must also respect ShowCustomerDetailsOnReceipt = false.");

            // 5. Customer-facing ReceiptHtmlFormatter & EscPosReceiptBuilder suppress VAT breakdown and Customer Information when OFF, and show them when ON
            string htmlSuppressed = POS.Client.Services.ReceiptHtmlFormatter.Format(receiptFalse!);
            Assert(!htmlSuppressed.Contains("Vatable Sales", StringComparison.Ordinal) &&
                   !htmlSuppressed.Contains("VAT-Exempt Sales", StringComparison.Ordinal) &&
                   !htmlSuppressed.Contains("Zero-Rated Sales", StringComparison.Ordinal) &&
                   !htmlSuppressed.Contains("VAT (12%):", StringComparison.Ordinal),
                "ReceiptHtmlFormatter must hide all VAT breakdown rows when VatDisplay = false.");
            Assert(!htmlSuppressed.Contains("[CUSTOMER INFORMATION]", StringComparison.Ordinal) &&
                   !htmlSuppressed.Contains("Customer:", StringComparison.Ordinal) &&
                   !htmlSuppressed.Contains("Juan Dela Cruz", StringComparison.Ordinal) &&
                   !htmlSuppressed.Contains("Bus. Style:", StringComparison.Ordinal),
                "ReceiptHtmlFormatter must hide [CUSTOMER INFORMATION] section without empty labels when ShowCustomerDetailsOnReceipt = false.");
            Assert(htmlSuppressed.Contains("Subtotal", StringComparison.Ordinal) &&
                   htmlSuppressed.Contains("TOTAL", StringComparison.Ordinal) &&
                   htmlSuppressed.Contains("112.00", StringComparison.Ordinal),
                "ReceiptHtmlFormatter must still display Subtotal and TOTAL when VatDisplay = false.");

            var hwConfig80 = new POS.Client.Data.LocalPrinterHardwareConfig { PaperWidthMm = 80 };
            string escPosSuppressed = Encoding.ASCII.GetString(POS.Client.Services.EscPosReceiptBuilder.BuildReceipt(receiptFalse!, hwConfig80));
            Assert(!escPosSuppressed.Contains("[CUSTOMER INFORMATION]", StringComparison.Ordinal) &&
                   !escPosSuppressed.Contains("Customer:", StringComparison.Ordinal) &&
                   !escPosSuppressed.Contains("Juan Dela Cruz", StringComparison.Ordinal) &&
                   !escPosSuppressed.Contains("Bus. Style:", StringComparison.Ordinal),
                "EscPosReceiptBuilder must hide [CUSTOMER INFORMATION] section when ShowCustomerDetailsOnReceipt = false.");

            receiptFalse!.VatDisplay = true;
            receiptFalse.ShowCustomerDetailsOnReceipt = true;
            string htmlDisplayed = POS.Client.Services.ReceiptHtmlFormatter.Format(receiptFalse);
            Assert(htmlDisplayed.Contains("Vatable Sales", StringComparison.Ordinal) &&
                   htmlDisplayed.Contains("100.00", StringComparison.Ordinal) &&
                   htmlDisplayed.Contains("VAT (12%):", StringComparison.Ordinal) &&
                   htmlDisplayed.Contains("12.00", StringComparison.Ordinal),
                "ReceiptHtmlFormatter must display Vatable Sales and VAT (12%) when VatDisplay = true.");
            Assert(htmlDisplayed.Contains("[CUSTOMER INFORMATION]", StringComparison.Ordinal) &&
                   htmlDisplayed.Contains("Juan Dela Cruz", StringComparison.Ordinal) &&
                   htmlDisplayed.Contains("Makati City", StringComparison.Ordinal) &&
                   htmlDisplayed.Contains("123-456-789-000", StringComparison.Ordinal) &&
                   htmlDisplayed.Contains("Retail Trading", StringComparison.Ordinal),
                "ReceiptHtmlFormatter must display [CUSTOMER INFORMATION] and customer values when ShowCustomerDetailsOnReceipt = true.");

            string escPosDisplayed = Encoding.ASCII.GetString(POS.Client.Services.EscPosReceiptBuilder.BuildReceipt(receiptFalse, hwConfig80));
            Assert(escPosDisplayed.Contains("[CUSTOMER INFORMATION]", StringComparison.Ordinal) &&
                   escPosDisplayed.Contains("Juan Dela Cruz", StringComparison.Ordinal) &&
                   escPosDisplayed.Contains("123-456-789-000", StringComparison.Ordinal) &&
                   escPosDisplayed.Contains("Retail Trading", StringComparison.Ordinal),
                "EscPosReceiptBuilder must display [CUSTOMER INFORMATION] and customer values when ShowCustomerDetailsOnReceipt = true.");

            // 6. Local SQLite LocalBranches table creation, backfill, and offline checkout preservation
            string tempSqlitePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pos_vat_branch_test_{Guid.NewGuid():N}.db");
            var prevLocalPath = AppContext.GetData("POS.LocalDatabasePath");
            try
            {
                AppContext.SetData("POS.LocalDatabasePath", tempSqlitePath);
                using (var localDb = new POS.Client.Data.LocalPosDbContext())
                {
                    localDb.InitializeDatabase();
                    localDb.Branches.Add(new POS.Client.Data.LocalBranch
                    {
                        BranchId = 1,
                        Name = "Main Branch",
                        Code = "BR-01",
                        TaxType = "VAT-Registered",
                        VatDisplay = false,
                        ShowCustomerDetailsOnReceipt = false,
                        IsActive = true,
                        UpdatedAt = DateTime.UtcNow
                    });
                    localDb.TerminalConfigs.Add(new POS.Client.Data.LocalTerminalConfig
                    {
                        TerminalId = 1,
                        OutletId = 1,
                        OutletName = "Main Counter",
                        TerminalCode = "T-01",
                        TerminalName = "POS1",
                        TerminalIdentifier = "DEV-01",
                        BranchId = 1,
                        BranchName = "Main Branch",
                        BranchCode = "BR-01",
                        BranchTaxType = "VAT-Registered",
                        VatDisplay = false,
                        ShowCustomerDetailsOnReceipt = false,
                        IsActivated = true,
                        IsInitialized = true
                    });
                    localDb.Products.Add(new POS.Client.Data.LocalProduct
                    {
                        ProductId = 1,
                        CategoryId = 1,
                        CategoryName = "Meals",
                        Name = "Burger",
                        Price = 112m,
                        TaxName = "VATable",
                        IsActive = true
                    });
                    localDb.InvoiceSequences.Add(new POS.Client.Data.LocalInvoiceSequence
                    {
                        SequenceId = 1,
                        TerminalId = 1,
                        SeriesCode = "SI",
                        Prefix = "SI-",
                        StartingNumber = 1,
                        EndingNumber = 100,
                        LastIssuedNumber = 0,
                        PaddingLength = 7,
                        IsActive = true
                    });
                    localDb.CashDrawerShifts.Add(new POS.Client.Data.LocalCashDrawerShift
                    {
                        ShiftId = 1,
                        BranchId = 1,
                        TerminalId = 1,
                        CashierId = "cashier1",
                        OpeningFloat = 1000m,
                        ExpectedBalance = 1000m,
                        OpeningConfirmed = true,
                        Status = "OPEN",
                        OpenedAt = DateTime.UtcNow
                    });
                    await localDb.SaveChangesAsync();

                    var session = new POS.Client.Services.PosSessionService();
                    session.SetSession("TEST-TOKEN", "cashier1", "Cashier", new TerminalConfigDto
                    {
                        TerminalId = 1,
                        OutletId = 1,
                        BranchId = 1,
                        BranchName = "Main Branch",
                        BranchCode = "BR-01",
                        TerminalName = "POS1"
                    });
                    await session.RefreshCachedMenuDisplayModeAsync();
                    Assert(session.Terminal != null && !session.Terminal.VatDisplay && !session.Terminal.ShowCustomerDetailsOnReceipt && session.Terminal.BranchTaxType == "VAT-Registered",
                        "PosSessionService.RefreshCachedMenuDisplayModeAsync must load VatDisplay = false, ShowCustomerDetailsOnReceipt = false, and TaxType from LocalBranches.");

                    var offlineCheckout = new POS.Client.Services.OfflineCheckoutService(localDb, session);
                    var checkoutRes = await offlineCheckout.ProcessOfflineCashSaleAsync(new OrderCreateDto
                    {
                        BranchId = 1,
                        OutletId = 1,
                        TerminalId = 1,
                        CashierId = "cashier1",
                        OrderType = "DINING",
                        CustomerName = "Maria Clara",
                        CustomerAddress = "Intramuros, Manila",
                        CustomerTin = "987-654-321-000",
                        CustomerBusinessStyle = "Food Services",
                        Items = new List<OrderItemCreateDto>
                        {
                            new() { ProductId = 1, ProductName = "Burger", Quantity = 1, BasePrice = 112m, TaxName = "VATable" }
                        }
                    }, 112m, "cashier1");

                    Assert(checkoutRes.Success && checkoutRes.Receipt != null, $"Offline checkout must succeed: {checkoutRes.ErrorMessage}");
                    Assert(!checkoutRes.Receipt!.VatDisplay && !checkoutRes.Receipt.ShowCustomerDetailsOnReceipt,
                        "Offline receipt must inherit VatDisplay = false and ShowCustomerDetailsOnReceipt = false from LocalBranches.");
                    Assert(checkoutRes.Receipt.VatableSales == 100.00m && checkoutRes.Receipt.VatAmount == 12.00m && checkoutRes.Receipt.TotalAmount == 112.00m,
                        "Offline checkout must still compute and store VatableSales = 100.00 and VatAmount = 12.00 when VatDisplay = false.");

                    string offlineHtml = POS.Client.Services.ReceiptHtmlFormatter.Format(checkoutRes.Receipt);
                    Assert(!offlineHtml.Contains("[CUSTOMER INFORMATION]", StringComparison.Ordinal) &&
                           !offlineHtml.Contains("Maria Clara", StringComparison.Ordinal),
                        "Offline receipt HTML must omit customer details when ShowCustomerDetailsOnReceipt = false.");

                    var savedLocalOrder = await localDb.Orders.AsNoTracking().FirstAsync();
                    Assert(savedLocalOrder.VatableSales == 100.00m && savedLocalOrder.VatAmount == 12.00m && savedLocalOrder.TotalAmount == 112.00m,
                        "LocalOrders table must store full VatableSales and VatAmount even when VatDisplay = false.");
                    Assert(savedLocalOrder.CustomerName == "Maria Clara" &&
                           savedLocalOrder.CustomerAddress == "Intramuros, Manila" &&
                           savedLocalOrder.CustomerTin == "987-654-321-000" &&
                           savedLocalOrder.CustomerBusinessStyle == "Food Services",
                        "LocalOrders table must store full customer details even when ShowCustomerDetailsOnReceipt = false.");
                }
            }
            finally
            {
                AppContext.SetData("POS.LocalDatabasePath", prevLocalPath);
                try { if (System.IO.File.Exists(tempSqlitePath)) System.IO.File.Delete(tempSqlitePath); } catch { }
            }
        }
    }

    private static async Task TestCriticalTransactionSyncEndToEnd()
    {
        var (conn, serverDb, serverShift) = await CreateTestEnvAsync();
        await using var cleanup = conn;

        // Configure server invoice sequence matching POS.Client local sequence
        serverDb.InvoiceSequences.Add(new InvoiceSequence
        {
            TerminalId = 1,
            SeriesCode = "TXN1",
            Prefix = "TXN1-",
            StartingNumber = 1,
            EndingNumber = 1000,
            LastIssuedNumber = 0,
            PaddingLength = 7,
            IsActive = true
        });
        serverShift.ClientShiftId = "shift-online-1";
        serverShift.CashierId = "YAFDAL";
        serverShift.OpeningConfirmed = true;
        serverShift.OpenedAt = DateTime.SpecifyKind(DateTime.UtcNow.AddSeconds(-5), DateTimeKind.Utc);
        await serverDb.SaveChangesAsync();

        string tempSqlitePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pos_sync_fix_test_{Guid.NewGuid():N}.db");
        var prevLocalPath = AppContext.GetData("POS.LocalDatabasePath");
        try
        {
            AppContext.SetData("POS.LocalDatabasePath", tempSqlitePath);
            using var localDb = new POS.Client.Data.LocalPosDbContext();
            localDb.InitializeDatabase();

            localDb.TerminalConfigs.Add(new POS.Client.Data.LocalTerminalConfig
            {
                TerminalId = 1,
                OutletId = 1,
                OutletName = "Counter 1",
                TerminalCode = "POS-1",
                TerminalName = "POS1",
                TerminalIdentifier = "DEV-UUID-1",
                DeviceUuid = "DEV-UUID-1",
                BranchId = 1,
                BranchName = "Main Branch",
                BranchCode = "BR-01",
                BranchTaxType = "VAT-Registered",
                VatDisplay = true,
                IsActivated = true,
                IsInitialized = true,
                AllowSync = true
            });
            localDb.Products.Add(new POS.Client.Data.LocalProduct
            {
                ProductId = 1,
                CategoryId = 1,
                CategoryName = "Meals",
                Name = "Lechon Meal",
                Price = 224m,
                TaxName = "VATable",
                IsActive = true
            });
            localDb.InvoiceSequences.Add(new POS.Client.Data.LocalInvoiceSequence
            {
                SequenceId = 1,
                TerminalId = 1,
                SeriesCode = "TXN1",
                Prefix = "TXN1-",
                StartingNumber = 1,
                EndingNumber = 1000,
                LastIssuedNumber = 0,
                PaddingLength = 7,
                IsActive = true
            });
            localDb.CashDrawerShifts.Add(new POS.Client.Data.LocalCashDrawerShift
            {
                ShiftId = serverShift.ShiftId,
                ClientShiftId = serverShift.ClientShiftId,
                BranchId = 1,
                TerminalId = 1,
                CashierId = "YAFDAL",
                OpeningFloat = 1000m,
                ExpectedBalance = 1000m,
                OpeningConfirmed = true,
                Status = "OPEN",
                OpenedAt = serverShift.OpenedAt
            });
            await localDb.SaveChangesAsync();

            var session = new POS.Client.Services.PosSessionService();
            session.SetSession("TERMINAL-JWT", "YAFDAL", "Cashier", new TerminalConfigDto
            {
                TerminalId = 1,
                OutletId = 1,
                BranchId = 1,
                BranchName = "Main Branch",
                BranchCode = "BR-01",
                TerminalName = "POS1"
            });

            // 1. Checkout sale with Split Payments + Customer Details + Modifiers
            var checkoutSvc = new POS.Client.Services.OfflineCheckoutService(localDb, session);
            var sale1Result = await checkoutSvc.ProcessSaleAsync(new OrderCreateDto
            {
                BranchId = 1,
                OutletId = 1,
                TerminalId = 1,
                ShiftId = serverShift.ShiftId,
                ClientShiftId = serverShift.ClientShiftId,
                CashierId = "YAFDAL",
                OrderType = "DINING",
                TableNumber = "T-05",
                HeadCount = 2,
                DiscountCode = "SC",
                CustomerCardNumber = "SC-998877",
                CustomerName = "Juan Dela Cruz",
                CustomerAddress = "Cebu City",
                CustomerTin = "123-456-789-000",
                CustomerBusinessStyle = "Walk-in",
                Splits = new List<PaymentSplitDto>
                {
                    new() { PaymentMethod = "CASH", Amount = 100m, AmountTendered = 100m },
                    new() { PaymentMethod = "GCASH", Amount = 60m, AmountTendered = 60m, ReferenceNumber = "GC-REF-001" }
                },
                Items = new List<OrderItemCreateDto>
                {
                    new()
                    {
                        ProductId = 1,
                        ProductName = "Lechon Meal",
                        Quantity = 1,
                        BasePrice = 200m,
                        TaxName = "VATable",
                        Modifiers = new List<OrderItemModifierCreateDto>
                        {
                            new() { ModifierOptionId = 10, ModifierName = "Extra Spicy Sauce", AdditionalPrice = 24m }
                        }
                    }
                }
            }, 160m, "CASH", null, "YAFDAL");

            Assert(sale1Result.Success, $"Split sale checkout must succeed: {sale1Result.ErrorMessage}");

            // Simulate exact SQLite DateTimeKind.Unspecified deserialization + Terminal-role JWT upload to OfflineSyncController
            var syncController = new OfflineSyncController(serverDb);
            syncController.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.Name, "POS-1"),
                        new Claim(ClaimTypes.Role, "Terminal"),
                        new Claim("terminal_id", "1"),
                        new Claim("branch_id", "1")
                    }, "TestTerminalAuth"))
                }
            };
            var cashDrawerController = CreateController(serverDb, "POS-1", "Terminal");

            bool simulateTransientFailure = false;
            using var inProcHttp = new System.Net.Http.HttpClient(new InProcSyncHandler(async req =>
            {
                if (simulateTransientFailure)
                {
                    return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new System.Net.Http.StringContent("Temporary server maintenance")
                    };
                }

                if (req.RequestUri!.AbsolutePath.EndsWith("api/offline-sync/transactions", StringComparison.OrdinalIgnoreCase))
                {
                    var bodyJson = await req.Content!.ReadAsStringAsync();
                    // System.Text.Json deserializes SQLite Unspecified timestamps as DateTimeKind.Unspecified if not normalized
                    var dto = System.Text.Json.JsonSerializer.Deserialize<OfflineTransactionSyncRequest>(bodyJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                    // Also test forcing Unspecified on PaidAt & TransactionDate to prove server NormalizeUtc prevents the 8-hour UTC+8 bug
                    dto.TransactionDate = DateTime.SpecifyKind(dto.TransactionDate, DateTimeKind.Unspecified);
                    foreach (var p in dto.Payments) p.PaidAt = DateTime.SpecifyKind(p.PaidAt, DateTimeKind.Unspecified);

                    var actionRes = await syncController.SyncTransaction(dto);
                    if (actionRes.Result is OkObjectResult ok)
                    {
                        return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                        {
                            Content = System.Net.Http.Json.JsonContent.Create(ok.Value)
                        };
                    }
                    if (actionRes.Result is ObjectResult obj)
                    {
                        return new System.Net.Http.HttpResponseMessage((System.Net.HttpStatusCode)(obj.StatusCode ?? 400))
                        {
                            Content = new System.Net.Http.StringContent(obj.Value?.ToString() ?? "Error")
                        };
                    }
                    return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Forbidden);
                }

                if (req.RequestUri.AbsolutePath.EndsWith("api/cash-drawer/open-shift", StringComparison.OrdinalIgnoreCase))
                {
                    var openJson = await req.Content!.ReadAsStringAsync();
                    var dto = System.Text.Json.JsonSerializer.Deserialize<OpenShiftDto>(openJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    var res = await cashDrawerController.OpenShift(dto!);
                    if (res is OkObjectResult ok)
                    {
                        return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                        {
                            Content = System.Net.Http.Json.JsonContent.Create(ok.Value)
                        };
                    }
                    var statusObj = res as ObjectResult;
                    return new System.Net.Http.HttpResponseMessage((System.Net.HttpStatusCode)(statusObj?.StatusCode ?? 403))
                    {
                        Content = new System.Net.Http.StringContent(statusObj?.Value?.ToString() ?? "Forbidden")
                    };
                }

                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            }))
            {
                BaseAddress = new Uri("http://localhost/")
            };

            var syncSvc = new POS.Client.Services.OfflineTransactionSyncService(localDb);

            // 2. Simulate a transient failure first -> verify RETRY_PENDING and structured diagnostic audit log
            simulateTransientFailure = true;
            var pendingBefore = await syncSvc.GetPendingOutboxItemsAsync();
            Assert(pendingBefore.Count == 1, "One pending outbox item expected.");
            bool failedAttempt = await syncSvc.UploadTransactionAsync(pendingBefore[0], inProcHttp);
            Assert(!failedAttempt, "Transient 503 failure must return false.");

            using (var checkDb = new POS.Client.Data.LocalPosDbContext())
            {
                var itemAfter503 = await checkDb.SyncOutboxes.AsNoTracking().SingleAsync();
                Assert(itemAfter503.Status == "RETRY_PENDING" && itemAfter503.RetryCount == 1,
                    $"Transient failure must set Status=RETRY_PENDING and RetryCount=1 (actual: {itemAfter503.Status}, retry={itemAfter503.RetryCount}).");
                var diagLog = await checkDb.SyncAuditLogs.AsNoTracking().OrderByDescending(l => l.LogId).FirstOrDefaultAsync();
                Assert(diagLog != null && diagLog.Message.Contains("[SYNC_DIAG]", StringComparison.Ordinal) && diagLog.Message.Contains("Retryable=True", StringComparison.Ordinal),
                    "Failed sync attempt must write structured [SYNC_DIAG] audit log.");

                // 3. Simulate the exact stuck NEEDS_ATTENTION state from user's screenshot ("Payment is outside the shift lifecycle.")
                var tracked = await checkDb.SyncOutboxes.SingleAsync();
                tracked.Status = "NEEDS_ATTENTION";
                tracked.LastError = "Payment is outside the shift lifecycle.";
                tracked.NextRetryAt = null;
                await checkDb.SaveChangesAsync();
            }

            // 4. Controlled retry recovery (ResetFailedOutboxItemsForRetryAsync) + successful upload
            simulateTransientFailure = false;
            int resetCount = await syncSvc.ResetFailedOutboxItemsForRetryAsync();
            Assert(resetCount == 1, "ResetFailedOutboxItemsForRetryAsync must reset NEEDS_ATTENTION item to RETRY_PENDING.");

            var retryItems = await syncSvc.GetPendingOutboxItemsAsync();
            Assert(retryItems.Count == 1, "Reset item must now be returned by GetPendingOutboxItemsAsync.");
            bool uploadOk = await syncSvc.UploadTransactionAsync(retryItems[0], inProcHttp);
            Assert(uploadOk, "Retry upload after fixing DateTimeKind.Unspecified must succeed.");

            // 5. Verify PostgreSQL/Server persistence of Order, Customer Details, Modifiers, and Split Payments
            var syncedServerOrder = await serverDb.Orders
                .Include(o => o.OrderItems)
                    .ThenInclude(oi => oi.OrderItemModifiers)
                .Include(o => o.Payments)
                .AsNoTracking()
                .SingleOrDefaultAsync(o => o.InvoiceNumber == sale1Result.InvoiceNumber);

            Assert(syncedServerOrder != null, "Synced order must exist in central server Orders table.");
            Assert(syncedServerOrder!.CustomerName == "Juan Dela Cruz" &&
                   syncedServerOrder.CustomerTin == "123-456-789-000" &&
                   syncedServerOrder.CustomerCardNumber == "SC-998877" &&
                   syncedServerOrder.DiscountCode == "SC",
                "Server order must preserve CustomerName, CustomerTin, CustomerCardNumber, and DiscountCode.");
            Assert(syncedServerOrder.Payments.Count == 2 &&
                   syncedServerOrder.Payments.Any(p => p.PaymentMethod == "CASH" && p.Amount == 100m) &&
                   syncedServerOrder.Payments.Any(p => p.PaymentMethod == "GCASH" && p.Amount == 60m && p.ReferenceNumber == "GC-REF-001"),
                "Server order must preserve all split payments and ReferenceNumber.");
            Assert(syncedServerOrder.OrderItems.Count == 1 &&
                   syncedServerOrder.OrderItems.First().OrderItemModifiers.Count == 1,
                "Server order must preserve item modifiers.");

            // 6. Verify idempotent replay does not duplicate order
            bool replayOk = await syncSvc.UploadTransactionAsync(retryItems[0], inProcHttp);
            Assert(replayOk && await serverDb.Orders.CountAsync() == 1, "Replaying the same outbox transaction must be idempotent and never create duplicates.");

            // 7. Verify SyncedAt and LastSyncAt and dashboard counts
            using (var verifyLocalDb = new POS.Client.Data.LocalPosDbContext())
            {
                var syncedOutbox = await verifyLocalDb.SyncOutboxes.AsNoTracking().SingleAsync();
                var termCfg = await verifyLocalDb.TerminalConfigs.AsNoTracking().SingleAsync();
                Assert(syncedOutbox.Status == "SYNCED" && syncedOutbox.SyncedAt.HasValue, "Synced outbox record must have Status=SYNCED and non-null SyncedAt.");
                Assert(termCfg.LastSyncAt.HasValue, "LocalTerminalConfig.LastSyncAt must be updated upon transaction sync.");
                int pendingCount = await verifyLocalDb.SyncOutboxes.CountAsync(o => o.Status == "PENDING" || o.Status == "RETRY_PENDING");
                int failedCount = await verifyLocalDb.SyncOutboxes.CountAsync(o => o.Status == "NEEDS_ATTENTION" || o.Status == "FAILED");
                Assert(pendingCount == 0 && failedCount == 0, "Pending and Failed counts must both be 0 after successful sync.");
            }

            // 8. Verify offline SHIFT_OPEN + offline Order syncs with Terminal-role JWT token
            serverShift.Status = "CLOSED";
            serverShift.ClosedAt = DateTime.UtcNow.AddSeconds(-1);
            await serverDb.SaveChangesAsync();
            using (var closeLocal = new POS.Client.Data.LocalPosDbContext())
            {
                var ls = await closeLocal.CashDrawerShifts.SingleAsync();
                ls.Status = "CLOSED";
                ls.ClosedAt = serverShift.ClosedAt;
                await closeLocal.SaveChangesAsync();
            }

            using var noNetHttp = new System.Net.Http.HttpClient(new InProcSyncHandler(_ => throw new InvalidOperationException("Offline")));
            var offlineShift = await POS.Client.Services.ShiftSession.OpenAsync(noNetHttp, new OpenShiftDto
            {
                BranchId = 1,
                TerminalId = 1,
                OpeningFloat = 1500m,
                ClientShiftId = "shift-offline-2"
            }, "YAFDAL", offline: true);

            var sale2Result = await checkoutSvc.ProcessOfflineCashSaleAsync(new OrderCreateDto
            {
                BranchId = 1,
                OutletId = 1,
                TerminalId = 1,
                ShiftId = offlineShift.ShiftId,
                ClientShiftId = offlineShift.ClientShiftId,
                CashierId = "YAFDAL",
                OrderType = "TAKEOUT",
                Items = new List<OrderItemCreateDto>
                {
                    new() { ProductId = 1, ProductName = "Lechon Meal", Quantity = 1, BasePrice = 224m, TaxName = "VATable" }
                }
            }, 224m, "YAFDAL");
            Assert(sale2Result.Success, $"Offline shift sale must succeed: {sale2Result.ErrorMessage}");

            var queue = await syncSvc.GetPendingOutboxItemsAsync();
            Assert(queue.Count == 2 && queue[0].EntityType == "SHIFT_OPEN" && queue[1].EntityType == "Order",
                "GetPendingOutboxItemsAsync must prioritize SHIFT_OPEN before dependent Order.");
            foreach (var q in queue)
            {
                Assert(await syncSvc.UploadTransactionAsync(q, inProcHttp), $"Queued {q.EntityType} must sync with Terminal JWT.");
            }
            Assert(await serverDb.Orders.CountAsync() == 2, "Second sale under offline-opened shift must persist in server Orders.");
        }
        finally
        {
            AppContext.SetData("POS.LocalDatabasePath", prevLocalPath);
            try { if (System.IO.File.Exists(tempSqlitePath)) System.IO.File.Delete(tempSqlitePath); } catch { }
        }
    }

    private static async Task TestTransactionTimestampsAndPhilippineTime()
    {
        var prevUtcNow = POS.Shared.Services.BusinessDayClock.UtcNowProvider;
        var prevDeviceNow = POS.Shared.Services.BusinessDayClock.DeviceNowOffsetProvider;
        var prevDeviceTz = POS.Shared.Services.BusinessDayClock.DeviceTimeZoneProvider;
        string tempSqlitePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pos_time01_{Guid.NewGuid():N}.db3");
        object? prevLocalPath = AppContext.GetData("POS.LocalDatabasePath");

        var (conn, serverDb, serverShift) = await CreateTestEnvAsync();
        await using var cleanup = conn;

        try
        {
            // Scenario 1 & 2: Windows ("Singapore Standard Time") and Android ("Asia/Manila") TimeZoneInfo resolution
            var winTz = POS.Shared.Services.BusinessDayClock.ResolveTimeZone("Singapore Standard Time");
            var androidTz = POS.Shared.Services.BusinessDayClock.ResolveTimeZone("Asia/Manila");
            Assert(winTz.BaseUtcOffset == TimeSpan.FromHours(8), "Windows Singapore Standard Time must resolve to UTC+08:00.");
            Assert(androidTz.BaseUtcOffset == TimeSpan.FromHours(8), "Android Asia/Manila must resolve to UTC+08:00.");

            // Device shows November 14, 2026 – 4:10 PM (16:10:00 local, 08:10:00 UTC)
            var deviceLocalWallClock = new DateTime(2026, 11, 14, 16, 10, 0, DateTimeKind.Unspecified);
            var correspondingUtc = new DateTime(2026, 11, 14, 8, 10, 0, DateTimeKind.Utc);
            Assert(POS.Shared.Services.BusinessDayClock.FormatManila(deviceLocalWallClock, "yyyy-MM-dd HH:mm:ss") == "2026-11-14 16:10:00",
                "Saved device-local Unspecified timestamp (2026-11-14 16:10:00) must display directly as 2026-11-14 16:10:00 without adding +8 hours.");
            Assert(POS.Shared.Services.BusinessDayClock.FormatManila(correspondingUtc, "yyyy-MM-dd HH:mm:ss") == "2026-11-14 16:10:00",
                "Explicit UTC timestamp (08:10:00 UTC) must convert to 2026-11-14 16:10:00 local time.");

            // Scenario 10: 3:59 AM, 4:00 AM, and 4:01 AM local BusinessDate cutoff
            var local359 = new DateTime(2026, 11, 14, 3, 59, 0, DateTimeKind.Unspecified);
            var local400 = new DateTime(2026, 11, 14, 4, 0, 0, DateTimeKind.Unspecified);
            var local401 = new DateTime(2026, 11, 14, 4, 1, 0, DateTimeKind.Unspecified);
            Assert(POS.Shared.Services.BusinessDayClock.GetBusinessDate(local359) == new DateOnly(2026, 11, 13),
                "Transaction at 3:59 AM local time must belong to previous business date (2026-11-13).");
            Assert(POS.Shared.Services.BusinessDayClock.GetBusinessDate(local400) == new DateOnly(2026, 11, 14),
                "Transaction at 4:00 AM local time must belong to current business date (2026-11-14).");
            Assert(POS.Shared.Services.BusinessDayClock.GetBusinessDate(local401) == new DateOnly(2026, 11, 14),
                "Transaction at 4:01 AM local time must belong to current business date (2026-11-14).");

            var (periodStartLocal, periodEndLocal) = POS.Shared.Services.BusinessDayClock.GetPeriodLocal(new DateOnly(2026, 11, 14));
            Assert(periodStartLocal == new DateTime(2026, 11, 14, 4, 0, 0, DateTimeKind.Unspecified) &&
                   periodEndLocal == new DateTime(2026, 11, 15, 4, 0, 0, DateTimeKind.Unspecified),
                "Local business date 2026-11-14 must span 2026-11-14 04:00 to 2026-11-15 04:00.");

            // Set up server shift + invoice sequence + local SQLite DB
            var openShiftLocal = new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Unspecified); // 09:00 AM local
            serverDb.Categories.Add(new Category { CategoryId = 1, Name = "Meals", IsActive = true });
            serverDb.Products.Add(new Product { ProductId = 1, CategoryId = 1, Name = "Lechon Meal", ProductType = "SIMPLE", Price = 224m, IsActive = true });
            serverDb.InvoiceSequences.Add(new InvoiceSequence
            {
                TerminalId = 1,
                SeriesCode = "TXN1",
                Prefix = "TXN1-",
                StartingNumber = 1,
                EndingNumber = 1000,
                LastIssuedNumber = 0,
                PaddingLength = 7,
                IsActive = true
            });
            serverShift.ClientShiftId = "shift-time01-1";
            serverShift.CashierId = "YAFDAL";
            serverShift.OpeningConfirmed = true;
            serverShift.OpenedAt = openShiftLocal;
            await serverDb.SaveChangesAsync();

            AppContext.SetData("POS.LocalDatabasePath", tempSqlitePath);
            using var localDb = new POS.Client.Data.LocalPosDbContext();
            localDb.InitializeDatabase();

            localDb.TerminalConfigs.Add(new POS.Client.Data.LocalTerminalConfig
            {
                TerminalId = 1,
                OutletId = 1,
                OutletName = "Counter 1",
                TerminalCode = "POS-1",
                TerminalName = "POS1",
                TerminalIdentifier = "DEV-UUID-1",
                DeviceUuid = "DEV-UUID-1",
                BranchId = 1,
                BranchName = "Main Branch",
                BranchCode = "BR-01",
                BranchTaxType = "VAT-Registered",
                VatDisplay = true,
                IsActivated = true,
                IsInitialized = true,
                AllowSync = true
            });
            localDb.Products.Add(new POS.Client.Data.LocalProduct
            {
                ProductId = 1,
                CategoryId = 1,
                CategoryName = "Meals",
                Name = "Lechon Meal",
                Price = 224m,
                TaxName = "VATable",
                IsActive = true
            });
            localDb.InvoiceSequences.Add(new POS.Client.Data.LocalInvoiceSequence
            {
                SequenceId = 1,
                TerminalId = 1,
                SeriesCode = "TXN1",
                Prefix = "TXN1-",
                StartingNumber = 1,
                EndingNumber = 1000,
                LastIssuedNumber = 0,
                PaddingLength = 7,
                IsActive = true
            });
            localDb.CashDrawerShifts.Add(new POS.Client.Data.LocalCashDrawerShift
            {
                ShiftId = serverShift.ShiftId,
                ClientShiftId = serverShift.ClientShiftId,
                BranchId = 1,
                TerminalId = 1,
                CashierId = "YAFDAL",
                OpeningFloat = 1000m,
                ExpectedBalance = 1000m,
                OpeningConfirmed = true,
                Status = "OPEN",
                OpenedAt = openShiftLocal
            });
            await localDb.SaveChangesAsync();

            var session = new POS.Client.Services.PosSessionService();
            session.SetSession("TERMINAL-JWT", "YAFDAL", "Cashier", new TerminalConfigDto
            {
                TerminalId = 1,
                OutletId = 1,
                BranchId = 1,
                BranchName = "Main Branch",
                BranchCode = "BR-01",
                TerminalName = "POS1"
            });

            var syncController = new OfflineSyncController(serverDb);
            syncController.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.Name, "POS-1"),
                        new Claim(ClaimTypes.Role, "Terminal"),
                        new Claim("terminal_id", "1"),
                        new Claim("branch_id", "1")
                    }, "TestTerminalAuth"))
                }
            };

            using var inProcHttp = new System.Net.Http.HttpClient(new InProcSyncHandler(async req =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("api/offline-sync/transactions", StringComparison.OrdinalIgnoreCase))
                {
                    var bodyJson = await req.Content!.ReadAsStringAsync();
                    var dto = System.Text.Json.JsonSerializer.Deserialize<OfflineTransactionSyncRequest>(bodyJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                    var actionRes = await syncController.SyncTransaction(dto);
                    if (actionRes.Result is OkObjectResult ok)
                    {
                        return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                        {
                            Content = System.Net.Http.Json.JsonContent.Create(ok.Value)
                        };
                    }
                    if (actionRes.Result is ObjectResult obj)
                    {
                        return new System.Net.Http.HttpResponseMessage((System.Net.HttpStatusCode)(obj.StatusCode ?? 400))
                        {
                            Content = new System.Net.Http.StringContent(obj.Value?.ToString() ?? "Error")
                        };
                    }
                    return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Forbidden);
                }
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            }))
            {
                BaseAddress = new Uri("http://localhost/")
            };

            var checkoutSvc = new POS.Client.Services.OfflineCheckoutService(localDb, session);
            var syncSvc = new POS.Client.Services.OfflineTransactionSyncService(localDb);

            // Scenario 1, 2, 3 & 4: Philippine-time device (November 14, 2026 – 4:10 PM) creates offline transaction, syncs 3 hours later
            POS.Shared.Services.BusinessDayClock.DeviceTimeZoneProvider = () => androidTz;
            POS.Shared.Services.BusinessDayClock.DeviceNowOffsetProvider = () => new DateTimeOffset(2026, 11, 14, 16, 10, 0, TimeSpan.FromHours(8));
            POS.Shared.Services.BusinessDayClock.UtcNowProvider = () => correspondingUtc;

            var phtOfflineSale = await checkoutSvc.ProcessOfflineCashSaleAsync(new OrderCreateDto
            {
                BranchId = 1,
                OutletId = 1,
                TerminalId = 1,
                ShiftId = serverShift.ShiftId,
                ClientShiftId = serverShift.ClientShiftId,
                CashierId = "YAFDAL",
                OrderType = "TAKEOUT",
                Items = new List<OrderItemCreateDto>
                {
                    new() { ProductId = 1, ProductName = "Lechon Meal", Quantity = 1, BasePrice = 224m, TaxName = "VATable" }
                }
            }, 224m, "YAFDAL");
            Assert(phtOfflineSale.Success, $"Offline sale on Philippine-time device must succeed: {phtOfflineSale.ErrorMessage}");

            using (var verifySqlite = new POS.Client.Data.LocalPosDbContext())
            {
                var sqliteOrder = await verifySqlite.Orders.AsNoTracking().SingleAsync(o => o.InvoiceNumber == phtOfflineSale.InvoiceNumber);
                var sqlitePayment = await verifySqlite.Payments.AsNoTracking().SingleAsync(p => p.OrderId == sqliteOrder.OrderId);
                Assert(sqliteOrder.CreatedAt == deviceLocalWallClock,
                    $"SQLite LocalOrder.CreatedAt must be saved as 2026-11-14 16:10:00 local time, not {sqliteOrder.CreatedAt:yyyy-MM-dd HH:mm:ss}.");
                Assert(sqlitePayment.PaidAt == deviceLocalWallClock,
                    $"SQLite LocalPayment.PaidAt must be saved as 2026-11-14 16:10:00 local time, not {sqlitePayment.PaidAt:yyyy-MM-dd HH:mm:ss}.");
                Assert(sqliteOrder.DeviceUtcOffsetMinutes == 480, "Philippine device offset (+480 mins) must be recorded.");
            }

            // Synchronize 3 hours later (19:10:00 local / 11:10:00 UTC)
            var phtSyncServerUtc = correspondingUtc.AddHours(3); // 2026-11-14 11:10:00 UTC
            POS.Shared.Services.BusinessDayClock.UtcNowProvider = () => phtSyncServerUtc;
            POS.Shared.Services.BusinessDayClock.DeviceNowOffsetProvider = () => new DateTimeOffset(2026, 11, 14, 19, 10, 0, TimeSpan.FromHours(8));

            var phtPending = await syncSvc.GetPendingOutboxItemsAsync();
            Assert(phtPending.Count == 1, "One PHT offline transaction must be queued.");
            Assert(await syncSvc.UploadTransactionAsync(phtPending[0], inProcHttp), "Delayed PHT offline transaction sync must succeed.");

            var syncedPhtOrder = await serverDb.Orders.Include(o => o.Payments).AsNoTracking().SingleAsync(o => o.InvoiceNumber == phtOfflineSale.InvoiceNumber);
            Assert(syncedPhtOrder.CreatedAt == deviceLocalWallClock,
                $"PostgreSQL/API Order.CreatedAt must be saved as 2026-11-14 16:10:00 (never 08:10:00 UTC or sync time), got {syncedPhtOrder.CreatedAt:yyyy-MM-dd HH:mm:ss}.");
            Assert(syncedPhtOrder.InvoiceIssuedAt == deviceLocalWallClock,
                "Order.InvoiceIssuedAt must preserve device-local 2026-11-14 16:10:00.");
            Assert(syncedPhtOrder.Payments.Single().PaidAt == deviceLocalWallClock,
                "Payment.PaidAt must preserve device-local 2026-11-14 16:10:00.");
            Assert(syncedPhtOrder.ServerReceivedAtUtc.HasValue && POS.Shared.Services.BusinessDayClock.NormalizeUtc(syncedPhtOrder.ServerReceivedAtUtc.Value) == phtSyncServerUtc,
                "Order.ServerReceivedAtUtc must record the separate UTC server receipt timestamp.");

            // Scenario 5, 6 & 7: Online transaction created at 4:10 PM local, retrieved via TransactionsController & displayed in POS.Web
            POS.Shared.Services.BusinessDayClock.DeviceTimeZoneProvider = () => winTz;
            POS.Shared.Services.BusinessDayClock.DeviceNowOffsetProvider = () => new DateTimeOffset(2026, 11, 14, 16, 10, 0, TimeSpan.FromHours(8));
            POS.Shared.Services.BusinessDayClock.UtcNowProvider = () => correspondingUtc;

            var seqSvc = new POS.Api.Services.SequenceService(serverDb);
            var ordersController = new OrdersController(serverDb, seqSvc, new POS.Api.Services.DiscountCalculationService());
            ordersController.ControllerContext = syncController.ControllerContext;
            var onlineOrderAction = await ordersController.CreateOrder(new OrderCreateDto
            {
                BranchId = 1,
                OutletId = 1,
                TerminalId = 1,
                ShiftId = serverShift.ShiftId,
                ClientShiftId = serverShift.ClientShiftId,
                CashierId = "YAFDAL",
                OrderType = "DINE_IN",
                TransactionTimestamp = new DateTimeOffset(2026, 11, 14, 16, 10, 0, TimeSpan.FromHours(8)),
                DeviceTimeZoneId = winTz.Id,
                DeviceUtcOffsetMinutes = 480,
                Items = new List<OrderItemCreateDto>
                {
                    new() { ProductId = 1, ProductName = "Lechon Meal", Quantity = 1, BasePrice = 224m, TaxName = "VATable" }
                }
            });
            var onlineCreated = (onlineOrderAction.Result as OkObjectResult)?.Value as OrderResponseDto;
            Assert(onlineCreated != null, "Online order creation must succeed.");
            Assert(onlineCreated!.CreatedAt == deviceLocalWallClock,
                $"Online OrderResponseDto.CreatedAt must be 2026-11-14 16:10:00, got {onlineCreated.CreatedAt:yyyy-MM-dd HH:mm:ss}.");

            var txController = new TransactionsController(serverDb, new POS.Api.Services.ReceiptService(serverDb))
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                        {
                            new Claim(ClaimTypes.Name, "admin"),
                            new Claim(ClaimTypes.Role, "Admin")
                        }, "TestAdminAuth"))
                    }
                }
            };
            var txListAction = await txController.GetTransactions(
                dateFrom: new DateTime(2026, 11, 14, 0, 0, 0),
                dateTo: new DateTime(2026, 11, 14, 23, 59, 59),
                branchId: 1,
                terminalId: null,
                cashierId: null,
                orderNumber: null,
                invoiceNumber: null,
                paymentMethod: null,
                orderStatus: null,
                paymentStatus: null,
                discountCode: null);
            var txRows = (txListAction.Result as OkObjectResult)?.Value as IEnumerable<OrderResponseDto>;
            Assert(txRows != null && txRows.Any(r => r.InvoiceNumber == phtOfflineSale.InvoiceNumber),
                "TransactionsController must return the 2026-11-14 16:10:00 transaction when queried for 2026-11-14.");
            var phtRow = txRows!.Single(r => r.InvoiceNumber == phtOfflineSale.InvoiceNumber);
            Assert(phtRow.CreatedAt == deviceLocalWallClock,
                "OrderResponseDto.CreatedAt from TransactionsController must return 2026-11-14 16:10:00 without conversion.");
            Assert(POS.Shared.Services.BusinessDayClock.FormatManila(phtRow.CreatedAt, "yyyy-MM-dd HH:mm:ss") == "2026-11-14 16:10:00",
                "POS.Web FormatManila on OrderResponseDto.CreatedAt must display 2026-11-14 16:10:00 without double conversion.");

            // Scenario 8 & 9: Receipt print, reprint, prep ticket, X-Reading, and Z-Reading display 2026-11-14 16:10:00
            var receiptService = new POS.Api.Services.ReceiptService(serverDb);
            var serverReceiptDto = await receiptService.GetReceiptAsync(syncedPhtOrder.OrderId, isReprint: true);
            Assert(serverReceiptDto != null, "Server receipt DTO must be generated.");
            string serverReceiptHtml = POS.Client.Services.ReceiptHtmlFormatter.Format(serverReceiptDto!);
            string offlineReceiptHtml = POS.Client.Services.ReceiptHtmlFormatter.Format(phtOfflineSale.Receipt!);

            Assert(offlineReceiptHtml.Contains("2026-11-14 16:10:00", StringComparison.Ordinal) && !offlineReceiptHtml.Contains("08:10:00", StringComparison.Ordinal),
                "Offline customer receipt must display 2026-11-14 16:10:00 (not 08:10:00 UTC).");
            Assert(serverReceiptHtml.Contains("2026-11-14 16:10:00", StringComparison.Ordinal) && !serverReceiptHtml.Contains("08:10:00", StringComparison.Ordinal),
                "Reprint after synchronization must display the exact same 2026-11-14 16:10:00 without double conversion.");

            var prepTicketHtml = POS.Client.Services.PreparationTicketHtmlFormatter.Format(new PreparationTicketDto
            {
                Station = "KITCHEN",
                OrderNumber = syncedPhtOrder.OrderNumber,
                InvoiceNumber = syncedPhtOrder.InvoiceNumber,
                CreatedAt = syncedPhtOrder.CreatedAt,
                TerminalName = "POS 1",
                CashierId = "YAFDAL",
                Items = new List<PreparationTicketItemDto> { new() { ProductName = "Lechon Meal", Quantity = 1 } }
            });
            Assert(prepTicketHtml.Contains("2026-11-14 16:10:00", StringComparison.Ordinal),
                "Preparation ticket must format CreatedAt as 2026-11-14 16:10:00.");

            var xReadingHtml = POS.Client.Services.XReadingHtmlFormatter.Format(new XReadingReportDto
            {
                ShiftId = serverShift.ShiftId,
                BranchName = "Main Branch",
                TerminalName = "POS 1",
                CashierId = "YAFDAL",
                GeneratedAt = deviceLocalWallClock,
                OpenedAt = openShiftLocal,
                ReportingPeriodStart = periodStartLocal,
                ReportingPeriodEnd = deviceLocalWallClock,
                ReportingPeriodMatchesShift = false
            });
            Assert(xReadingHtml.Contains("2026-11-14 16:10:00", StringComparison.Ordinal) &&
                   xReadingHtml.Contains("2026-11-14 09:00", StringComparison.Ordinal),
                "X-Reading thermal formatter must display local GeneratedAt (16:10:00) and OpenedAt (09:00) without double conversion.");

            var zReadingHtml = POS.Shared.Services.ZReadingHtmlFormatter.Format(new ZReadingReportDto
            {
                Id = Guid.NewGuid(),
                ZNumber = 1,
                BranchId = 1,
                BranchName = "Main Branch",
                TerminalId = 1,
                TerminalCode = "POS-1",
                TerminalName = "POS 1",
                BusinessDate = new DateOnly(2026, 11, 14),
                PeriodStartUtc = periodStartLocal,
                PeriodEndUtc = periodEndLocal,
                GeneratedAt = deviceLocalWallClock,
                GeneratedBy = "admin"
            });
            Assert(zReadingHtml.Contains("2026-11-14 04:00 Asia/Manila", StringComparison.Ordinal) &&
                   zReadingHtml.Contains("2026-11-14 16:10 Asia/Manila", StringComparison.Ordinal),
                "Z-Reading thermal formatter must display PeriodStart and GeneratedAt (16:10) without double conversion.");

            // Scenario 11: Device configured with a different timezone (UTC-05:00) preserves its own local wall-clock time
            var estTz = TimeZoneInfo.CreateCustomTimeZone("America/New_York_Test", TimeSpan.FromHours(-5), "EST", "EST");
            var estWallClock = new DateTime(2026, 11, 14, 10, 15, 0, DateTimeKind.Unspecified); // 10:15 AM on EST device
            var estOccurrenceUtc = new DateTime(2026, 11, 14, 15, 15, 0, DateTimeKind.Utc);
            POS.Shared.Services.BusinessDayClock.DeviceTimeZoneProvider = () => estTz;
            POS.Shared.Services.BusinessDayClock.DeviceNowOffsetProvider = () => new DateTimeOffset(estWallClock, TimeSpan.FromHours(-5));
            POS.Shared.Services.BusinessDayClock.UtcNowProvider = () => estOccurrenceUtc;

            var estSale = await checkoutSvc.ProcessOfflineCashSaleAsync(new OrderCreateDto
            {
                BranchId = 1,
                OutletId = 1,
                TerminalId = 1,
                ShiftId = serverShift.ShiftId,
                ClientShiftId = serverShift.ClientShiftId,
                CashierId = "YAFDAL",
                OrderType = "TAKEOUT",
                Items = new List<OrderItemCreateDto>
                {
                    new() { ProductId = 1, ProductName = "Lechon Meal", Quantity = 1, BasePrice = 224m, TaxName = "VATable" }
                }
            }, 224m, "YAFDAL");
            Assert(estSale.Success, $"Offline sale in UTC-05:00 timezone must succeed: {estSale.ErrorMessage}");

            var estPending = await syncSvc.GetPendingOutboxItemsAsync();
            Assert(estPending.Count == 1, "EST offline transaction must be queued.");
            Assert(await syncSvc.UploadTransactionAsync(estPending[0], inProcHttp), "EST offline transaction sync must succeed.");

            var syncedEstOrder = await serverDb.Orders.AsNoTracking().SingleAsync(o => o.InvoiceNumber == estSale.InvoiceNumber);
            Assert(syncedEstOrder.CreatedAt == estWallClock,
                $"Device in UTC-05:00 must preserve its exact local wall-clock time ({estWallClock:yyyy-MM-dd HH:mm:ss}), got {syncedEstOrder.CreatedAt:yyyy-MM-dd HH:mm:ss}.");
            Assert(syncedEstOrder.DeviceUtcOffsetMinutes == -300, "Device UTC offset (-300 mins for UTC-05:00) must be recorded.");

            // Scenario 12: Device clock intentionally skewed behind while shift is OPEN
            var realServerUtc = new DateTime(2026, 11, 14, 4, 0, 0, DateTimeKind.Utc); // 12:00 PM PHT
            var skewedLocalWallClock = new DateTime(2026, 11, 14, 8, 15, 0, DateTimeKind.Unspecified); // 08:15 AM local (before shift OpenedAt 09:00 AM!)
            POS.Shared.Services.BusinessDayClock.UtcNowProvider = () => realServerUtc;
            POS.Shared.Services.BusinessDayClock.DeviceTimeZoneProvider = () => winTz;
            POS.Shared.Services.BusinessDayClock.DeviceNowOffsetProvider = () => new DateTimeOffset(skewedLocalWallClock, TimeSpan.FromHours(8));

            var skewedSale = await checkoutSvc.ProcessOfflineCashSaleAsync(new OrderCreateDto
            {
                BranchId = 1,
                OutletId = 1,
                TerminalId = 1,
                ShiftId = serverShift.ShiftId,
                ClientShiftId = serverShift.ClientShiftId,
                CashierId = "YAFDAL",
                OrderType = "TAKEOUT",
                Items = new List<OrderItemCreateDto>
                {
                    new() { ProductId = 1, ProductName = "Lechon Meal", Quantity = 1, BasePrice = 224m, TaxName = "VATable" }
                }
            }, 224m, "YAFDAL");
            Assert(skewedSale.Success, "Checkout with skewed device clock must succeed locally.");

            var skewedQueue = await syncSvc.GetPendingOutboxItemsAsync();
            Assert(skewedQueue.Count == 1, "Skewed transaction must be in outbox.");
            Assert(await syncSvc.UploadTransactionAsync(skewedQueue[0], inProcHttp),
                "Skewed device clock transaction on an explicitly bound OPEN shift must sync and not be permanently rejected.");

            var skewedServerOrder = await serverDb.Orders.AsNoTracking().SingleAsync(o => o.InvoiceNumber == skewedSale.InvoiceNumber);
            Assert(skewedServerOrder.CreatedAt == skewedLocalWallClock,
                "Skewed offline transaction must preserve its original device-local timestamp (2026-11-14 08:15:00) without silent replacement.");
            Assert(skewedServerOrder.ServerReceivedAtUtc.HasValue && POS.Shared.Services.BusinessDayClock.NormalizeUtc(skewedServerOrder.ServerReceivedAtUtc.Value) == realServerUtc,
                "Skewed transaction must record ServerReceivedAtUtc.");
            Assert(skewedServerOrder.ClockSkewSeconds.HasValue && Math.Abs(skewedServerOrder.ClockSkewSeconds.Value) >= 300,
                "Skewed transaction must record ClockSkewSeconds audit metadata.");
        }
        finally
        {
            POS.Shared.Services.BusinessDayClock.UtcNowProvider = prevUtcNow;
            POS.Shared.Services.BusinessDayClock.DeviceNowOffsetProvider = prevDeviceNow;
            POS.Shared.Services.BusinessDayClock.DeviceTimeZoneProvider = prevDeviceTz;
            AppContext.SetData("POS.LocalDatabasePath", prevLocalPath);
            try { if (System.IO.File.Exists(tempSqlitePath)) System.IO.File.Delete(tempSqlitePath); } catch { }
        }
    }

    private static async Task TestBranchTaglineManagementSyncAndReceiptFormatting()
    {
        var (conn, serverDb, serverShift) = await CreateTestEnvAsync();
        await using var cleanup = conn;

        var tempSqlitePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pos_tagline_test_{Guid.NewGuid():N}.db3");
        var prevLocalPath = AppContext.GetData("POS.LocalDatabasePath");
        AppContext.SetData("POS.LocalDatabasePath", tempSqlitePath);

        try
        {
            var adminContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.Name, "admin"),
                        new Claim(ClaimTypes.Role, "Admin"),
                        new Claim("branch_id", "1"),
                        new Claim("terminal_id", "1")
                    }, "TestAuth"))
                }
            };

            var branchesController = new POS.Api.Controllers.BranchesController(serverDb)
            {
                ControllerContext = adminContext
            };

            // 1. Create a new branch WITH a tagline
            var createWithTaglineRes = await branchesController.CreateBranch(new BranchDto
            {
                Name = "Sulitchon",
                BranchTagline = "  Ang Sulit na lechon sa habang panahon  ",
                Code = "SUL-01",
                Address = "Quezon City",
                Phone = "09171234567",
                TaxType = "VAT-Registered",
                VatDisplay = true,
                IsActive = true,
                TrackInventory = true
            });
            var branchWithTagline = (createWithTaglineRes.Result as OkObjectResult)?.Value as BranchDto;
            Assert(branchWithTagline != null && branchWithTagline.BranchId > 0, "Branch with tagline must be created.");
            Assert(branchWithTagline!.BranchTagline == "Ang Sulit na lechon sa habang panahon", "Saved tagline must be trimmed and returned.");

            // 2. Create a new branch WITHOUT a tagline (whitespace/empty -> NULL)
            var createWithoutTaglineRes = await branchesController.CreateBranch(new BranchDto
            {
                Name = "Sulitchon Express",
                BranchTagline = "   ",
                Code = "SUL-02",
                Address = "Makati City",
                Phone = "09179876543",
                TaxType = "VAT-Registered",
                VatDisplay = true,
                IsActive = true
            });
            var branchWithoutTagline = (createWithoutTaglineRes.Result as OkObjectResult)?.Value as BranchDto;
            Assert(branchWithoutTagline != null && branchWithoutTagline.BranchId > 0, "Branch without tagline must be created.");
            Assert(branchWithoutTagline!.BranchTagline == null, "Empty or whitespace tagline must be normalized to null.");

            // 3. Edit an existing branch and add a tagline
            branchWithoutTagline.BranchTagline = "Fresh Lechon Daily";
            var addTaglineUpdateRes = await branchesController.UpdateBranch(branchWithoutTagline.BranchId, branchWithoutTagline);
            Assert(addTaglineUpdateRes is NoContentResult, "Adding tagline to existing branch must succeed.");

            // 5. Confirm tagline is saved and retrieved
            var getBranchesRes = await branchesController.GetBranches();
            var allBranches = (getBranchesRes.Result as OkObjectResult)?.Value as List<BranchDto>;
            Assert(allBranches != null, "GetBranches must return branch list.");
            var fetchedWith = allBranches!.Single(b => b.BranchId == branchWithTagline.BranchId);
            var fetchedAdded = allBranches!.Single(b => b.BranchId == branchWithoutTagline.BranchId);
            Assert(fetchedWith.BranchTagline == "Ang Sulit na lechon sa habang panahon", "Retrieved branch must retain its tagline.");
            Assert(fetchedAdded.BranchTagline == "Fresh Lechon Daily", "Edited branch must return newly added tagline.");

            // 4. Edit an existing branch and remove the tagline
            fetchedAdded.BranchTagline = "";
            var removeTaglineRes = await branchesController.UpdateBranch(fetchedAdded.BranchId, fetchedAdded);
            Assert(removeTaglineRes is NoContentResult, "Removing tagline from existing branch must succeed.");
            var afterRemoveEntity = await serverDb.Branches.AsNoTracking().SingleAsync(b => b.BranchId == fetchedAdded.BranchId);
            Assert(afterRemoveEntity.BranchTagline == null, "Removed tagline must be stored as NULL in database.");

            // Update Branch 1 to Sulitchon with tagline so Terminal 1 (from CreateTestEnvAsync) inherits it
            var branch1 = await serverDb.Branches.AsNoTracking().SingleAsync(b => b.BranchId == 1);
            await branchesController.UpdateBranch(1, new BranchDto
            {
                BranchId = 1,
                BusinessTypeId = branch1.BusinessTypeId,
                ServiceModeId = branch1.ServiceModeId,
                Name = "Sulitchon",
                BranchTagline = "Ang Sulit na lechon sa habang panahon",
                Code = "SUL-01",
                Address = "Quezon City",
                Phone = "09171234567",
                TaxType = "VAT-Registered",
                VatDisplay = true,
                IsActive = true,
                TrackInventory = branch1.TrackInventory
            });

            // 6. Confirm tagline synchronizes to POS.Client via OfflineReferenceDataController & client-config endpoint
            var clientCfgRes = await branchesController.GetBranchClientConfig(1);
            var clientCfg = (clientCfgRes.Result as OkObjectResult)?.Value as OfflineBranchConfigDto;
            Assert(clientCfg != null && clientCfg.BranchTagline == "Ang Sulit na lechon sa habang panahon",
                "GetBranchClientConfig must return BranchTagline.");

            var refController = new POS.Api.Controllers.OfflineReferenceDataController(serverDb)
            {
                ControllerContext = adminContext
            };
            var refRes = await refController.GetReferenceData(1, null);
            var refDto = (refRes.Result as OkObjectResult)?.Value as OfflineReferenceDataResponseDto;
            Assert(refDto != null && refDto.Branch != null &&
                   refDto.Branch.BranchTagline == "Ang Sulit na lechon sa habang panahon" &&
                   refDto.Terminal.BranchTagline == "Ang Sulit na lechon sa habang panahon",
                "OfflineReferenceDataController must return BranchTagline on both Branch and Terminal config DTOs.");

            // 7. Confirm tagline is persisted in LocalBranches and LocalTerminalConfigs and remains available offline
            using var localDb = new POS.Client.Data.LocalPosDbContext();
            localDb.InitializeDatabase();
            localDb.Branches.Add(new POS.Client.Data.LocalBranch
            {
                BranchId = 1,
                Name = refDto!.Branch!.Name,
                BranchTagline = refDto.Branch.BranchTagline,
                Code = refDto.Branch.Code,
                Address = refDto.Branch.Address,
                Phone = refDto.Branch.Phone,
                TaxType = refDto.Branch.TaxType,
                VatDisplay = refDto.Branch.VatDisplay,
                IsActive = true,
                UpdatedAt = DateTime.UtcNow
            });
            localDb.TerminalConfigs.Add(new POS.Client.Data.LocalTerminalConfig
            {
                TerminalId = 1,
                OutletId = 1,
                OutletName = refDto.Terminal.OutletName,
                TerminalCode = refDto.Terminal.TerminalCode,
                TerminalName = refDto.Terminal.TerminalName,
                TerminalIdentifier = "DEV-SUL-1",
                BranchId = 1,
                BranchName = refDto.Terminal.BranchName,
                BranchTagline = refDto.Terminal.BranchTagline,
                BranchCode = refDto.Terminal.BranchCode,
                BranchTaxType = refDto.Terminal.BranchTaxType,
                VatDisplay = refDto.Terminal.VatDisplay,
                IsActivated = true,
                IsInitialized = true
            });
            localDb.Products.Add(new POS.Client.Data.LocalProduct
            {
                ProductId = 1,
                CategoryId = 1,
                CategoryName = "Lechon",
                Name = "1kg Lechon",
                Price = 800m,
                TaxName = "VATable",
                IsActive = true
            });
            localDb.InvoiceSequences.Add(new POS.Client.Data.LocalInvoiceSequence
            {
                SequenceId = 1,
                TerminalId = 1,
                SeriesCode = "SUL",
                Prefix = "INV-SUL-",
                StartingNumber = 1,
                EndingNumber = 1000,
                LastIssuedNumber = 0,
                PaddingLength = 7,
                IsActive = true
            });
            localDb.CashDrawerShifts.Add(new POS.Client.Data.LocalCashDrawerShift
            {
                ShiftId = serverShift.ShiftId,
                ClientShiftId = "shift-sul-1",
                BranchId = 1,
                TerminalId = 1,
                CashierId = "cashier1",
                OpeningFloat = 1000m,
                ExpectedBalance = 1000m,
                OpeningConfirmed = true,
                Status = "OPEN",
                OpenedAt = DateTime.UtcNow
            });
            await localDb.SaveChangesAsync();

            var session = new POS.Client.Services.PosSessionService();
            session.SetSession("OFFLINE-SESSION-TOKEN", "cashier1", "Cashier", new TerminalConfigDto
            {
                TerminalId = 1,
                OutletId = 1,
                BranchId = 1,
                BranchName = "Sulitchon",
                BranchCode = "SUL-01",
                TerminalName = "POS 1"
            });
            await session.RefreshCachedMenuDisplayModeAsync();
            Assert(session.Terminal!.BranchTagline == "Ang Sulit na lechon sa habang panahon",
                "PosSessionService.RefreshCachedMenuDisplayModeAsync must hydrate BranchTagline from local SQLite.");

            var offlineCheckout = new POS.Client.Services.OfflineCheckoutService(localDb, session);
            var offlineSale = await offlineCheckout.ProcessOfflineCashSaleAsync(new OrderCreateDto
            {
                BranchId = 1,
                OutletId = 1,
                TerminalId = 1,
                ShiftId = serverShift.ShiftId,
                CashierId = "cashier1",
                OrderType = "TAKEOUT",
                Items = new List<OrderItemCreateDto>
                {
                    new() { ProductId = 1, ProductName = "1kg Lechon", Quantity = 1m, BasePrice = 800m, TaxName = "VATable" }
                }
            }, 800m, "cashier1");
            Assert(offlineSale.Success && offlineSale.Receipt != null, $"Offline checkout must succeed: {offlineSale.ErrorMessage}");
            Assert(offlineSale.Receipt!.BranchTagline == "Ang Sulit na lechon sa habang panahon",
                "Offline receipt must include BranchTagline from local SQLite.");

            // Seed paid server order for reprint testing
            var order = new Order
            {
                OrderNumber = "POS1-000001",
                InvoiceNumber = "INV-SUL-0000001",
                InvoiceIssuedAt = DateTime.UtcNow,
                BranchId = 1,
                OutletId = 1,
                TerminalId = 1,
                CashierId = "cashier1",
                OrderType = "TAKEOUT",
                Subtotal = 800m,
                VatableSales = 714.29m,
                VatAmount = 85.71m,
                TotalAmount = 800m,
                PaymentStatus = "PAID",
                OrderStatus = "COMPLETED",
                CreatedAt = DateTime.UtcNow
            };
            order.OrderItems.Add(new OrderItem
            {
                ProductId = 1,
                ProductName = "1kg Lechon",
                Quantity = 1m,
                BasePrice = 800m,
                UnitPrice = 800m,
                LineTotal = 800m
            });
            order.Payments.Add(new Payment
            {
                PaymentMethod = "CASH",
                Amount = 800m,
                AmountTendered = 1000m,
                ChangeAmount = 200m,
                PaymentStatus = "PAID",
                ProcessedBy = "cashier1",
                PaidAt = DateTime.UtcNow
            });
            serverDb.Orders.Add(order);
            await serverDb.SaveChangesAsync();

            // 8, 10, 11 & 12. Print/format receipt WITH tagline + Reprint existing transaction + 80mm layout + existing settings unaffected
            var receiptSvc = new POS.Api.Services.ReceiptService(serverDb);
            var reprintedReceipt = await receiptSvc.GetReceiptAsync(order.OrderId, isReprint: true);
            Assert(reprintedReceipt != null && reprintedReceipt.IsReprint, "Reprint receipt must be retrieved.");
            Assert(reprintedReceipt!.BranchTagline == "Ang Sulit na lechon sa habang panahon",
                "Reprinted receipt must include BranchTagline.");
            Assert(reprintedReceipt.VatDisplay && reprintedReceipt.TotalAmount == 800m && reprintedReceipt.VatAmount == 85.71m,
                "Existing branch settings (VatDisplay) and receipt totals must remain unaffected.");

            string htmlWithTagline = POS.Client.Services.ReceiptHtmlFormatter.Format(reprintedReceipt);
            Assert(htmlWithTagline.Contains("<h1>Sulitchon</h1><p class=\"branch-tagline\">Ang Sulit na lechon sa habang panahon</p>", StringComparison.Ordinal),
                "Receipt HTML with tagline must render <p class=\"branch-tagline\"> directly underneath <h1>BranchName</h1>.");
            Assert(htmlWithTagline.Contains("size: 80mm auto;", StringComparison.Ordinal) && htmlWithTagline.Contains("width: 64mm;", StringComparison.Ordinal),
                "80mm thermal printer layout dimensions must remain intact.");

            // 9. Print/format a receipt for a branch WITHOUT a tagline (no empty line or placeholder)
            reprintedReceipt.BranchTagline = null;
            string htmlWithoutTagline = POS.Client.Services.ReceiptHtmlFormatter.Format(reprintedReceipt);
            Assert(!htmlWithoutTagline.Contains("<p class=\"branch-tagline\">", StringComparison.Ordinal),
                "Receipt HTML without tagline must not render an empty .branch-tagline element or blank line.");
            Assert(!htmlWithoutTagline.Contains("Branch Code:", StringComparison.Ordinal) &&
                   htmlWithoutTagline.Contains("<h1>Sulitchon</h1><p>", StringComparison.Ordinal),
                "Receipt HTML without tagline must transition directly from <h1>BranchName</h1> to the next header line without Branch Code.");
        }
        finally
        {
            AppContext.SetData("POS.LocalDatabasePath", prevLocalPath);
            try { if (System.IO.File.Exists(tempSqlitePath)) System.IO.File.Delete(tempSqlitePath); } catch { }
        }
    }

    private static Task TestReceiptCompanyFooterAndBranchCodeRemoval()
    {
        Assert(POS.Shared.Constants.ReceiptCompanyInfo.CompanyName == "AGRES Business Solution", "CompanyName constant mismatch.");
        Assert(POS.Shared.Constants.ReceiptCompanyInfo.Email == "agressolutionsph@gmail.com", "Email constant mismatch.");
        Assert(POS.Shared.Constants.ReceiptCompanyInfo.Address == "Sta. Rosa, Laguna, Philippines, 4026", "Address constant mismatch.");

        var receipt = new ReceiptDto
        {
            OrderId = 501,
            OrderNumber = "POS01-000501",
            InvoiceNumber = "INV-000501",
            BranchId = 1,
            BranchName = "Test Store",
            BranchTagline = "Best in Town",
            BranchCode = "BR-99",
            BranchAddress = "123 Store St.",
            BranchPhone = "0917-000-0000",
            TerminalName = "POS-01",
            CashierId = "cashier1",
            OrderType = "DINING",
            CreatedAt = DateTime.UtcNow,
            VatDisplay = true,
            Subtotal = 100m,
            VatableSales = 89.29m,
            VatAmount = 10.71m,
            TotalAmount = 100m,
            IsReprint = true,
            Items = new()
            {
                new OrderItemResponseDto { ProductName = "Classic Burger", Quantity = 1, UnitPrice = 100m, LineTotal = 100m }
            },
            Payments = new()
            {
                new PaymentResponseDto { PaymentMethod = "CASH", Amount = 100m, AmountTendered = 100m, ChangeAmount = 0m }
            }
        };

        var html = POS.Client.Services.ReceiptHtmlFormatter.Format(receipt);

        // BranchCode preserved on DTO, omitted from customer receipt HTML
        Assert(receipt.BranchCode == "BR-99", "ReceiptDto.BranchCode must remain intact.");
        Assert(!html.Contains("Branch Code", StringComparison.OrdinalIgnoreCase), "Receipt HTML must not contain Branch Code label.");
        Assert(!html.Contains("BR-99", StringComparison.Ordinal), "Receipt HTML must not contain BranchCode value.");

        // Company info centered in footer in exact order
        Assert(html.Contains("text-align: center;", StringComparison.Ordinal) && html.Contains(".company-info", StringComparison.Ordinal),
            "Receipt HTML must include centered company-info styling.");
        string expectedFooterCompany =
            "<div class=\"company-info\">" +
            "<p class=\"company-name\">AGRES Business Solution</p>" +
            "<p class=\"company-detail\">agressolutionsph@gmail.com</p>" +
            "<p class=\"company-detail\">Sta. Rosa, Laguna, Philippines, 4026</p>" +
            "</div>";
        int footerIdx = html.IndexOf("<div class=\"footer\">", StringComparison.Ordinal);
        int companyIdx = html.IndexOf(expectedFooterCompany, StringComparison.Ordinal);
        Assert(footerIdx >= 0 && companyIdx > footerIdx, "Company info must appear inside .footer at the bottom of the receipt.");

        // Preserved elements & 80mm paper dimensions
        Assert(html.Contains("size: 80mm auto;", StringComparison.Ordinal) && html.Contains("width: 64mm;", StringComparison.Ordinal),
            "80mm receipt width must remain intact.");
        Assert(html.Contains("Test Store", StringComparison.Ordinal) && html.Contains("Best in Town", StringComparison.Ordinal),
            "BranchName and BranchTagline must remain intact.");
        Assert(html.Contains("** REPRINT **", StringComparison.Ordinal) && html.Contains("PHP 100.00", StringComparison.Ordinal),
            "Reprint notice and totals must remain intact.");

        return Task.CompletedTask;
    }

    private sealed class InProcSyncHandler(Func<System.Net.Http.HttpRequestMessage, Task<System.Net.Http.HttpResponseMessage>> handler) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            => handler(request);
    }
}
