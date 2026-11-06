using Microsoft.Extensions.DependencyInjection;
using Tausi.Application.Mpesa;

namespace Tausi.IntegrationTests.Support;

/// <summary>For tests that build their own services: M-Pesa present but not set up (bills print without a QR).</summary>
public static class NoMpesa
{
    public static IServiceCollection AddNoMpesa(this IServiceCollection services)
    {
        services.AddLogging();
        services.AddSingleton<ISecretProtector, NoSecrets>();
        services.AddSingleton<IDaraja>(_ => throw new InvalidOperationException("M-Pesa isn't set up in this test."));
        services.AddSingleton<IMonoImages, Tausi.Infrastructure.Mpesa.SkiaMonoImages>();
        services.AddSingleton<BillQrCache>();
        services.AddSingleton<RelayStatus>();
        services.AddSingleton<IMpesaCatchUp, NoCatchUp>();
        return services;
    }

    private sealed class NoSecrets : ISecretProtector
    {
        public string Protect(string plain) => plain;

        public string? Unprotect(string protectedText) => null;
    }

    private sealed class NoCatchUp : IMpesaCatchUp
    {
        public Task<int> Run(CancellationToken ct) => Task.FromResult(0);
    }
}
