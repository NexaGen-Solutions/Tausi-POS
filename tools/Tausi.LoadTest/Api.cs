using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Tausi.LoadTest;

/// <summary>One HTTP caller: a terminal (its device token, and the PIN session of whoever is signed in), or the admin site.</summary>
public sealed class Api
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public Api(Uri server, string? deviceToken = null)
    {
        _http = new HttpClient(new HttpClientHandler { UseCookies = true }) { BaseAddress = server, Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.Add("X-Tausi-Version", "1.0.0");
        if (deviceToken is not null) _http.DefaultRequestHeaders.Add("X-Device-Token", deviceToken);
        DeviceToken = deviceToken;
    }

    public string? DeviceToken { get; }

    /// <summary>Who is signed in on this terminal now (one person at a time, as on a real counter).</summary>
    public string? SignedIn { get; private set; }

    public string? SessionToken { get; private set; }

    public async Task SignIn(string name, string pin)
    {
        if (SignedIn == name) return;
        var answer = await Send(HttpMethod.Post, "/api/auth/pin", new { pin });
        SessionToken = answer.GetProperty("token").GetString();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionToken);
        SignedIn = name;
    }

    public Task<JsonElement> Get(string path) => Send(HttpMethod.Get, path, null);
    public Task<JsonElement> Post(string path, object? body = null) => Send(HttpMethod.Post, path, body);
    public Task<JsonElement> Delete(string path) => Send(HttpMethod.Delete, path, null);

    /// <summary>Sends a request (writes carry an Idempotency-Key, as the web app's do); throws on an error answer.</summary>
    public async Task<JsonElement> Send(HttpMethod method, string path, object? body, Stats? stats = null, string? metric = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (method != HttpMethod.Get) request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        var clock = Stopwatch.StartNew();
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        clock.Stop();
        if (metric is not null) stats?.Record(metric, clock.Elapsed.TotalMilliseconds);
        if (!response.IsSuccessStatusCode)
            throw new LoadTestException($"{method} {path} answered {(int)response.StatusCode}: {text[..Math.Min(text.Length, 300)]}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }
}

public sealed class LoadTestException(string message) : Exception(message);
