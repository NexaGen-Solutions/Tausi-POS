using Microsoft.Extensions.DependencyInjection;
using Nexagen.Application.Auditing;
using Nexagen.Application.Auth;
using Nexagen.Application.Day;
using Nexagen.Application.Dev;
using Nexagen.Application.Printers;
using Nexagen.Application.Printing;
using Nexagen.Application.Security;
using Nexagen.Application.Settings;
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

        // Use cases: one class per action.
        services.AddScoped<PinLogin>();
        services.AddScoped<Logout>();
        services.AddScoped<ChangePin>();
        services.AddScoped<GetMaxPinLength>();
        services.AddScoped<OpenDay>();
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
