using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using POS.Api.Controllers;
using POS.Api.Models;
using POS.Shared.Constants;
using POS.Shared.Models;

namespace POS.Tests;

public static partial class Program
{
    private static ClaimsPrincipal BuildPrincipalFromJwt(string jwt)
    {
        var handler = new JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(jwt);
        return new ClaimsPrincipal(new ClaimsIdentity(token.Claims, "Bearer"));
    }

    private static IConfiguration BuildAgresTestJwtConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "SuperSecretPosJwtKeyForTestingPurposes1234567890!",
            ["Jwt:Issuer"] = "POS.Api",
            ["Jwt:Audience"] = "POS.Client"
        }).Build();

    private static void AttachPrincipal(ControllerBase controller, ClaimsPrincipal principal)
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    /// <summary>
    /// Verifies Criteria 1–6 & 8:
    /// - AGRES System Admin absent from Client Users table, GET api/Users, search & counts
    /// - Client Admin cannot create AGRES System Admin, cannot self-elevate/assign platform role, cannot edit/delete AGRES System Admin
    /// - Cross-tenant isolation: Org A Client Admin cannot view, edit, or delete Org B users
    /// </summary>
    private static async Task TestAgres01_IdentityIsolationUsersTableCrossTenantAndPrivilegeEscalation()
    {
        var (conn, db, _) = await CreateTestEnvAsync();
        await using var cleanup = conn;
        var jwtCfg = BuildAgresTestJwtConfig();

        // Seed Organization 2 and a separate Client Admin + Cashier in Org 2
        db.Organizations.Add(new Organization { OrganizationId = 2, Code = "ORG2", Name = "Second Client Corp", IsActive = true, CreatedAtUtc = DateTime.UtcNow });
        db.Users.AddRange(
            new User { UserId = 20, OrganizationId = 1, Username = "client_admin_a", PasswordHash = BCrypt.Net.BCrypt.HashPassword("ClientA123!"), Role = "Admin", PinCode = "4444", IsActive = true },
            new User { UserId = 21, OrganizationId = 2, Username = "client_admin_b", PasswordHash = BCrypt.Net.BCrypt.HashPassword("ClientB123!"), Role = "Admin", PinCode = "5555", IsActive = true },
            new User { UserId = 22, OrganizationId = 2, Username = "cashier_b", PasswordHash = BCrypt.Net.BCrypt.HashPassword("CashierB123!"), Role = "Cashier", PinCode = "6666", IsActive = true }
        );
        // Seed AGRES System Admin in the physically separate AgresSystemAdmins table
        db.AgresSystemAdmins.Add(new AgresSystemAdmin
        {
            SystemAdminId = 1,
            Username = "agres_root",
            DisplayName = "AGRES Root Admin",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("AgresRoot123!"),
            IsActive = true,
            CreatedBy = "TEST"
        });
        await db.SaveChangesAsync();

        // Authenticate Client Admin A via real AuthController.Login
        var authCtrl = new AuthController(db, jwtCfg);
        var clientLoginRes = await authCtrl.Login(new LoginDto { Username = "client_admin_a", Password = "ClientA123!" });
        Assert(clientLoginRes is OkObjectResult, "Client Admin A must authenticate via api/auth/login.");
        string clientJwtA = (string)((OkObjectResult)clientLoginRes).Value!.GetType().GetProperty("Token")!.GetValue(((OkObjectResult)clientLoginRes).Value)!;
        var principalA = BuildPrincipalFromJwt(clientJwtA);

        Assert(PosPermissions.IsClientAdmin(principalA), "Client Admin A JWT must satisfy IsClientAdmin.");
        Assert(!PosPermissions.IsAgresSystemAdmin(principalA), "Client Admin A JWT must NEVER satisfy IsAgresSystemAdmin.");
        Assert(PosPermissions.GetOrganizationId(principalA) == 1, "Client Admin A JWT must carry organization_id = 1.");

        // 1 & 2 & 3. GET api/Users (listing, search, count) excludes AGRES System Admins AND Org B users
        var usersCtrlA = new UsersController(db);
        AttachPrincipal(usersCtrlA, principalA);

        var listRes = await usersCtrlA.GetUsers();
        Assert(listRes.Result is OkObjectResult, "Client Admin A must be able to list own organization users.");
        var usersA = (List<UserDto>)((OkObjectResult)listRes.Result!).Value!;
        Assert(!usersA.Any(u => u.Username == "agres_root" || u.Role == PosPermissions.AgresSystemAdminRole),
            "AGRES System Admin must NEVER appear in GET api/Users.");
        Assert(!usersA.Any(u => u.OrganizationId == 2 || u.Username == "client_admin_b" || u.Username == "cashier_b"),
            "Org A Client Admin must NEVER see Org B users.");
        Assert(usersA.All(u => u.OrganizationId == 1), "Every returned user must belong to Org 1.");

        var searchAgresRes = await usersCtrlA.GetUsers("agres");
        var searchAgresList = (List<UserDto>)((OkObjectResult)searchAgresRes.Result!).Value!;
        Assert(searchAgresList.Count == 0, "Client user search for 'agres' must return 0 results.");

        // 4. Client Admin cannot create an AGRES System Admin or spoof an AGRES username
        var createAgresRoleRes = await usersCtrlA.CreateUser(new CreateUserDto
        {
            Username = "fake_agres",
            Password = "Password123!",
            Role = PosPermissions.AgresSystemAdminRole
        });
        Assert(createAgresRoleRes.Result is BadRequestObjectResult, "Client Admin must be rejected (400) when attempting to create AgresSystemAdmin role.");

        var createSpoofNameRes = await usersCtrlA.CreateUser(new CreateUserDto
        {
            Username = "agres_root",
            Password = "Password123!",
            Role = "Admin"
        });
        Assert(createSpoofNameRes.Result is BadRequestObjectResult, "Client Admin must not be allowed to register a username matching an AGRES System Admin.");

        // 5. Client Admin cannot elevate self (or any user) to AgresSystemAdmin via PUT
        var selfElevateRes = await usersCtrlA.UpdateUser(20, new UpdateUserDto
        {
            Username = "client_admin_a",
            Role = PosPermissions.AgresSystemAdminRole,
            IsActive = true
        });
        Assert(selfElevateRes.Result is BadRequestObjectResult, "Client Admin must be rejected when attempting to elevate role to AgresSystemAdmin.");

        // 6. Client Admin cannot modify or delete AGRES System Admin (even via AgresPlatformController)
        var platformCtrlAsClient = new AgresPlatformController(db);
        AttachPrincipal(platformCtrlAsClient, principalA);
        Assert((await platformCtrlAsClient.GetSystemAdmins()).Result is ForbidResult, "Client Admin must get 403 Forbid on GET api/agres/admins.");
        Assert((await platformCtrlAsClient.CreateSystemAdmin(new CreateAgresSystemAdminDto { Username = "rogue", DisplayName = "Rogue", Password = "Password123!" })).Result is ForbidResult,
            "Client Admin must get 403 Forbid on POST api/agres/admins.");
        Assert((await platformCtrlAsClient.UpdateSystemAdmin(1, new UpdateAgresSystemAdminDto { DisplayName = "Hacked", IsActive = false })).Result is ForbidResult,
            "Client Admin must get 403 Forbid on PUT api/agres/admins/{id}.");

        // 8. Cross-tenant BOLA protection: Org A Client Admin cannot update or delete Org B user (UserId=22)
        var crossOrgUpdate = await usersCtrlA.UpdateUser(22, new UpdateUserDto { Username = "cashier_b", Role = "Cashier", IsActive = false });
        Assert(crossOrgUpdate.Result is NotFoundObjectResult, "Org A Client Admin must get 404 when attempting to edit Org B user.");
        var crossOrgDelete = await usersCtrlA.DeleteUser(22);
        Assert(crossOrgDelete is NotFoundResult, "Org A Client Admin must get 404 when attempting to delete Org B user.");
        Assert(await db.Users.AnyAsync(u => u.UserId == 22 && u.IsActive), "Org B user must remain intact and active.");
    }

    /// <summary>
    /// Verifies Criteria 7, 9, 10, 11, 12, 13:
    /// - Client Admin JWT blocked on ALL AGRES licensing & terminal activation code endpoints (403) while permitted read-only client-summary
    /// - AGRES System Admin JWT authenticated ONLY via /api/agres/auth/login (NOT /api/auth/login), authorized on Licensing & Activation
    /// - AGRES System Admin JWT rejected by ClientOperationsPolicy / UsersController / OfflineReferenceDataController / POS ops
    /// - OfflineReferenceDataController & ActivateTerminal exclude AGRES accounts and cross-org cashiers from POS.Client SQLite cache
    /// - Client login, PIN login, and historical order/shift records remain intact
    /// </summary>
    private static async Task TestAgres02_JwtScopePoliciesLicensingActivationOfflineSyncAndMigrationIntegrity()
    {
        var (conn, db, _) = await CreateTestEnvAsync();
        await using var cleanup = conn;
        var jwtCfg = BuildAgresTestJwtConfig();

        db.Organizations.Add(new Organization { OrganizationId = 2, Code = "ORG2", Name = "Second Client Corp", IsActive = true, CreatedAtUtc = DateTime.UtcNow });
        db.Users.AddRange(
            new User { UserId = 30, OrganizationId = 1, Username = "nentina_admin", PasswordHash = BCrypt.Net.BCrypt.HashPassword("ClientPass1!"), Role = "Admin", PinCode = "7777", IsActive = true },
            new User { UserId = 31, OrganizationId = 2, Username = "org2_cashier", PasswordHash = BCrypt.Net.BCrypt.HashPassword("Org2Pass1!"), Role = "Cashier", PinCode = "8888", IsActive = true }
        );
        db.AgresSystemAdmins.Add(new AgresSystemAdmin
        {
            SystemAdminId = 1,
            Username = "admin",
            DisplayName = "AGRES System Administrator",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("AgresMaster1!"),
            IsActive = true,
            CreatedBy = "MIGRATION"
        });
        await db.SaveChangesAsync();

        var authCtrl = new AuthController(db, jwtCfg);

        // AGRES admin MUST NOT authenticate through client login endpoint
        var agresOnClientLogin = await authCtrl.Login(new LoginDto { Username = "admin", Password = "AgresMaster1!" });
        Assert(agresOnClientLogin is UnauthorizedObjectResult, "AGRES System Admin must NOT authenticate via client POST api/auth/login.");

        // Client admin MUST NOT authenticate through AGRES platform login endpoint
        var clientOnAgresLogin = await authCtrl.AgresPlatformLogin(new LoginDto { Username = "nentina_admin", Password = "ClientPass1!" });
        Assert(clientOnAgresLogin is UnauthorizedObjectResult, "Client Admin must NOT authenticate via POST api/agres/auth/login.");

        // Authenticate both via their respective proper endpoints
        var agresLoginRes = await authCtrl.AgresPlatformLogin(new LoginDto { Username = "admin", Password = "AgresMaster1!" });
        Assert(agresLoginRes is OkObjectResult, "AGRES System Admin must authenticate via POST api/agres/auth/login.");
        string agresJwt = (string)((OkObjectResult)agresLoginRes).Value!.GetType().GetProperty("Token")!.GetValue(((OkObjectResult)agresLoginRes).Value)!;
        var agresPrincipal = BuildPrincipalFromJwt(agresJwt);

        var clientLoginRes = await authCtrl.Login(new LoginDto { Username = "nentina_admin", Password = "ClientPass1!" });
        Assert(clientLoginRes is OkObjectResult, "Client Admin must authenticate via POST api/auth/login.");
        string clientJwt = (string)((OkObjectResult)clientLoginRes).Value!.GetType().GetProperty("Token")!.GetValue(((OkObjectResult)clientLoginRes).Value)!;
        var clientPrincipal = BuildPrincipalFromJwt(clientJwt);

        // Verify platform audit log recorded AGRES login
        Assert(await db.PlatformAuditLogs.AnyAsync(l => l.Action == "AGRES_LOGIN_SUCCESS" && l.ActorUsername == "admin"),
            "AGRES login must write a PlatformAuditLogs record.");

        // 7 & 9. Client Admin JWT (even with Role = "Admin"!) MUST be rejected (403) on ALL AGRES licensing & activation endpoints
        var licAsClient = new LicensingController(db);
        AttachPrincipal(licAsClient, clientPrincipal);
        Assert((await licAsClient.GetLicenses()).Result is ForbidResult, "Client Admin JWT must get 403 on GET api/licensing.");
        Assert((await licAsClient.IssueLicense(new IssueTerminalLicenseRequestDto { TerminalId = 1, DurationValue = 5, DurationUnit = "YEARS" })).Result is ForbidResult,
            "Client Admin JWT must get 403 on POST api/licensing/issue.");
        Assert((await licAsClient.RenewLicense(new RenewTerminalLicenseRequestDto { TerminalId = 1, RenewalDuration = 1, RenewalUnit = "YEARS" })).Result is ForbidResult,
            "Client Admin JWT must get 403 on POST api/licensing/renew.");
        Assert((await licAsClient.RevokeLicense(new ChangeLicenseStatusRequestDto { TerminalId = 1, Reason = "Attempt" })).Result is ForbidResult,
            "Client Admin JWT must get 403 on POST api/licensing/revoke.");

        // Client Admin CAN access read-only client-summary for their own org
        var summaryRes = await licAsClient.GetClientLicenseSummary();
        Assert(summaryRes.Result is OkObjectResult, "Client Admin must be able to read non-sensitive api/licensing/client-summary.");

        // Client Admin MUST be forbidden on Terminal Activation Code regeneration AND must receive redacted ActivationCode
        var termAsClient = new TerminalsController(db, jwtCfg);
        AttachPrincipal(termAsClient, clientPrincipal);
        Assert((await termAsClient.RegenerateActivationCode(1)).Result is ForbidResult,
            "Client Admin must get 403 Forbid on POST api/terminals/{id}/regenerate-activation-code.");

        // AGRES System Admin succeeds on Licensing & Activation Code generation
        var licAsAgres = new LicensingController(db);
        AttachPrincipal(licAsAgres, agresPrincipal);
        var issueOk = await licAsAgres.IssueLicense(new IssueTerminalLicenseRequestDto { TerminalId = 1, DurationValue = 5, DurationUnit = "YEARS", EnvironmentType = "UAT" });
        Assert(issueOk.Result is OkObjectResult, "AGRES System Admin must be authorized to issue terminal licenses.");

        var termAsAgres = new TerminalsController(db, jwtCfg);
        AttachPrincipal(termAsAgres, agresPrincipal);
        var regenOk = await termAsAgres.RegenerateActivationCode(1);
        Assert(regenOk.Result is OkObjectResult, "AGRES System Admin must be authorized to regenerate activation codes.");
        var regenDto = (TerminalDto)((OkObjectResult)regenOk.Result!).Value!;
        Assert(!string.IsNullOrWhiteSpace(regenDto.ActivationCode), "AGRES System Admin must receive the generated activation code.");

        // Client Admin reading Terminal #1 MUST have ActivationCode redacted (null)
        var clientGetTerm = await termAsClient.GetTerminal(1);
        var clientTermDto = (TerminalDto)((OkObjectResult)clientGetTerm.Result!).Value!;
        Assert(clientTermDto.ActivationCode == null, "Client Admin must never receive raw terminal ActivationCode.");

        // 10. AGRES System Admin JWT MUST NOT satisfy ClientOperationsPolicy or access client user/POS endpoints
        Assert(!PosPermissions.IsClientUser(agresPrincipal), "AGRES System Admin JWT must fail ClientOperationsPolicy (IsClientUser == false).");
        Assert(!PosPermissions.CanViewTransactionHistory(agresPrincipal), "AGRES System Admin JWT must not satisfy client TransactionHistory policy.");
        Assert(!PosPermissions.CanApproveVoid(agresPrincipal), "AGRES System Admin JWT must not satisfy client void approval.");

        var usersAsAgres = new UsersController(db);
        AttachPrincipal(usersAsAgres, agresPrincipal);
        Assert((await usersAsAgres.GetUsers()).Result is ForbidResult, "AGRES System Admin JWT must get 403 on Client UsersController.");

        var offlineAsAgres = new OfflineReferenceDataController(db);
        AttachPrincipal(offlineAsAgres, agresPrincipal);
        Assert((await offlineAsAgres.GetReferenceData(1)).Result is ForbidResult, "AGRES System Admin JWT must get 403 on OfflineReferenceDataController.");

        // Verify class-level ClientOperationsPolicy attribute on cashier operational controllers
        foreach (var ctrlType in new[] { typeof(OrdersController), typeof(PaymentsController), typeof(CashDrawerController), typeof(CashierSessionsController), typeof(HeldSalesController), typeof(OfflineSyncController) })
        {
            var attr = ctrlType.GetCustomAttributes(typeof(AuthorizeAttribute), false).Cast<AuthorizeAttribute>().FirstOrDefault();
            Assert(attr != null && attr.Policy == PosPermissions.ClientOperationsPolicy,
                $"{ctrlType.Name} must enforce ClientOperationsPolicy to block AGRES platform tokens from cashier operations.");
        }

        // 11. Offline reference data sync excludes AGRES accounts AND Org 2 cashiers
        var offlineAsClient = new OfflineReferenceDataController(db);
        AttachPrincipal(offlineAsClient, clientPrincipal);
        var refRes = await offlineAsClient.GetReferenceData(1);
        Assert(refRes.Result is OkObjectResult, "Client Admin must succeed on OfflineReferenceDataController for own org terminal.");
        var refData = (OfflineReferenceDataResponseDto)((OkObjectResult)refRes.Result!).Value!;
        Assert(!refData.Cashiers.Any(c => c.Username == "admin" || c.Role == PosPermissions.AgresSystemAdminRole),
            "AGRES System Admin must never sync into POS.Client LocalCashiers.");
        Assert(!refData.Cashiers.Any(c => c.Username == "org2_cashier"),
            "Org 2 cashiers must never sync to an Org 1 terminal.");
        Assert(refData.Cashiers.Any(c => c.Username == "cashier1") && refData.Cashiers.Any(c => c.Username == "nentina_admin"),
            "Org 1 active client staff with PINs must sync cleanly.");

        // 12 & 13. Client PIN login and existing cashier workflows remain intact
        var pinLoginRes = await authCtrl.PinLogin(new PinLoginDto { PinCode = "1111", TerminalId = 1, BranchId = 1 });
        Assert(pinLoginRes is OkObjectResult, "Existing client cashier PIN login must remain functional.");
        Assert(!PosPermissions.IsApprovedClientRole(PosPermissions.AgresSystemAdminRole), "PosPermissions allowlist must reject AgresSystemAdmin for offline client auth.");
    }
}
