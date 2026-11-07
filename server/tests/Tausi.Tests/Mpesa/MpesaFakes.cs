using Tausi.Application.Mpesa;

namespace Tausi.Tests.Mpesa;

/// <summary>Reversible "encryption" for tests; a value from "another PC" can't be read.</summary>
public sealed class FakeSecrets : ISecretProtector
{
    public string Protect(string plain) => "enc:" + new string(plain.Reverse().ToArray());

    public string? Unprotect(string protectedText) =>
        protectedText.StartsWith("enc:", StringComparison.Ordinal) ? new string(protectedText[4..].Reverse().ToArray()) : null;
}

/// <summary>
/// Daraja for tests: records every call; prompts get checkout ids "ws_CO_1", "ws_CO_2"…; the query
/// answers from <see cref="QueryAnswers"/> (none: still waiting); <see cref="Down"/> makes every call
/// fail; <see cref="QrDelay"/> makes the QR slow.
/// </summary>
public sealed class FakeDaraja : IDaraja
{
    public List<string> Calls { get; } = [];
    public List<StkPushRequest> Pushes { get; } = [];
    public Dictionary<string, (int Code, string Desc)> QueryAnswers { get; } = [];
    public List<(string ShortCode, string Confirmation, string Validation)> Registrations { get; } = [];
    public bool Down { get; set; }
    public bool WrongCredentials { get; set; }
    public TimeSpan QrDelay { get; set; }
    public byte[] QrPng { get; set; } = TestQr.Png();

    private void Call(string name)
    {
        Calls.Add(name);
        if (Down) throw new DarajaException("Can't reach Safaricom. Check the internet connection.");
    }

    public Task CheckCredentials(MpesaCredentials credentials, bool live, CancellationToken ct)
    {
        Call("oauth");
        if (WrongCredentials) throw new DarajaException("Safaricom didn't accept the consumer key and secret.");
        return Task.CompletedTask;
    }

    public Task<StkPushResult> StkPush(MpesaCredentials credentials, bool live, StkPushRequest request, CancellationToken ct)
    {
        Call("stkpush");
        Pushes.Add(request);
        return Task.FromResult(new StkPushResult($"ws_CO_{Pushes.Count}", $"m_{Pushes.Count}", "Success. Request accepted for processing"));
    }

    public Task<StkQueryResult> StkQuery(MpesaCredentials credentials, bool live, string shortCode, string checkoutRequestId, CancellationToken ct)
    {
        Call("stkquery");
        return Task.FromResult(QueryAnswers.TryGetValue(checkoutRequestId, out var a) ? new StkQueryResult(a.Code, a.Desc) : new StkQueryResult(null, null));
    }

    public async Task<byte[]> DynamicQr(MpesaCredentials credentials, bool live, DynamicQrRequest request, CancellationToken ct)
    {
        Call("qr");
        if (QrDelay > TimeSpan.Zero) await Task.Delay(QrDelay, ct);
        return QrPng;
    }

    public Task RegisterC2B(MpesaCredentials credentials, bool live, string shortCode, string confirmationUrl, string validationUrl, CancellationToken ct)
    {
        Call("register");
        Registrations.Add((shortCode, confirmationUrl, validationUrl));
        return Task.CompletedTask;
    }
}

/// <summary>A relay in memory: <see cref="Waiting"/> are delivered until acknowledged.</summary>
public sealed class FakeRelay : IRelayClient
{
    public List<RelayPaymentDto> Waiting { get; } = [];
    public List<Guid> Acknowledged { get; } = [];
    public bool Down { get; set; }
    public int Collections { get; private set; }

    public Task<IReadOnlyList<RelayPaymentDto>> Collect(string relayUrl, string apiKey, int waitSeconds, CancellationToken ct)
    {
        if (Down) throw new HttpRequestException("Connection refused");
        Collections++;
        return Task.FromResult<IReadOnlyList<RelayPaymentDto>>(Waiting.Where(p => !Acknowledged.Contains(p.Id)).ToList());
    }

    public Task Acknowledge(string relayUrl, string apiKey, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        Acknowledged.AddRange(ids);
        return Task.CompletedTask;
    }

    public Task<RelayAccount> Me(string relayUrl, string apiKey, CancellationToken ct) =>
        Down ? throw new HttpRequestException("Connection refused")
            : Task.FromResult(new RelayAccount("Demo Bar", new RelayCallbacks(
                "https://pay.example.test/pay/tok123/stk", "https://pay.example.test/pay/tok123/c2b/validation",
                "https://pay.example.test/pay/tok123/c2b/confirmation")));

    public static RelayPaymentDto C2B(string code, decimal amount, string? billRef = null, DateTime? at = null) =>
        new(Guid.NewGuid(), "c2b", code, null, null, null, null, amount, "JOHN K", "0712 ••• 678", "123456", billRef, "Buy Goods",
            at ?? new DateTime(2026, 10, 20, 18, 5, 0, DateTimeKind.Utc), DateTime.UtcNow);

    public static RelayPaymentDto Stk(string checkout, int code, string? receipt = null, decimal amount = 0) =>
        new(Guid.NewGuid(), "stk", receipt, checkout, null, code, code == 0 ? "Processed" : "Cancelled", amount, null, "0712 ••• 678", null, null, null,
            receipt is null ? null : new DateTime(2026, 10, 20, 18, 6, 0, DateTimeKind.Utc), DateTime.UtcNow);
}

/// <summary>A small black-and-white PNG standing in for Daraja's QR.</summary>
public static class TestQr
{
    public static byte[] Png()
    {
        using var bitmap = new SkiaSharp.SKBitmap(29, 29);
        for (var y = 0; y < 29; y++)
        for (var x = 0; x < 29; x++)
            bitmap.SetPixel(x, y, (x * 7 + y * 3) % 5 < 2 ? SkiaSharp.SKColors.Black : SkiaSharp.SKColors.White);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        return image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray();
    }
}
