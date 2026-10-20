using Microsoft.Extensions.DependencyInjection;
using Nexagen.Application.Auditing;
using Nexagen.Application.Auth;
using Nexagen.Application.Day;
using Nexagen.Application.Menu;
using Nexagen.Application.Roles;
using Nexagen.Application.Dev;
using Nexagen.Application.Printers;
using Nexagen.Application.Printing;
using Nexagen.Application.Security;
using Nexagen.Application.Settings;
using Nexagen.Application.Staff;
using Nexagen.Application.Terminals;

namespace Nexagen.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<IAuditLog, AuditLog>();
        services.AddScoped<PrintQueue>();
        services.AddScoped<PinAuthenticator>();
        services.AddScoped<SessionKeeper>();
        services.AddScoped<PinRules>();
        services.AddScoped<StaffAccess>();
        services.AddScoped<MenuAccess>();

        // Use cases: one class per action.
        services.AddScoped<PinLogin>();
        services.AddScoped<Logout>();
        services.AddScoped<ChangePin>();
        services.AddScoped<GetMaxPinLength>();
        services.AddScoped<OpenDay>();
        services.AddScoped<ListStaff>();
        services.AddScoped<GetStaffMember>();
        services.AddScoped<CreateStaff>();
        services.AddScoped<UpdateStaff>();
        services.AddScoped<ResetStaffPin>();
        services.AddScoped<DeactivateStaff>();
        services.AddScoped<ReactivateStaff>();
        services.AddScoped<GetAdminMenu>();
        services.AddScoped<GetPriceLog>();
        services.AddScoped<GetPosMenu>();
        services.AddScoped<SaveCategory>();
        services.AddScoped<ReorderCategories>();
        services.AddScoped<DeactivateCategory>();
        services.AddScoped<SaveItem>();
        services.AddScoped<SetItemPrices>();
        services.AddScoped<SaveGroup>();
        services.AddScoped<SetOptionPrices>();
        services.AddScoped<SavePriceList>();
        services.AddScoped<MakeDefaultPriceList>();
        services.AddScoped<ExportMenu>();
        services.AddScoped<MenuImport>();
        services.AddScoped<SetTerminalPriceList>();
        services.AddScoped<ListTerminals>();
        services.AddScoped<SetRolePermission>();
        services.AddScoped<AdminLogin>();
        services.AddScoped<IssueApproval>();
        services.AddScoped<StartPairing>();
        services.AddScoped<CompletePairing>();
        services.AddScoped<ClaimPairing>();
        services.AddScoped<GetDeviceInfo>();
        services.AddScoped<GetBrand>();
        services.AddScoped<GetBusinessSettings>();
        services.AddScoped<UpdateBusinessSettings>();
        services.AddScoped<UploadBusinessLogo>();
        services.AddScoped<TestPrint>();
        services.AddScoped<DevVoidProbe>();
        return services;
    }
}
