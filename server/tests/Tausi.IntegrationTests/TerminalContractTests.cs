using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

/// <summary>
/// The previous terminal app against this server: every call in Contracts/terminal-*.json still answers
/// with the status and the fields that terminal reads. A terminal older than TerminalVersions.Minimum
/// updates before login instead, so contracts below it can be removed.
/// </summary>
[Collection(StaffCollection.Name)]
public class TerminalContractTests(StaffApp app)
{
    public static IEnumerable<object[]> Contracts() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Contracts"), "terminal-*.json").Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(Contracts))]
    public async Task An_older_terminal_app_still_gets_what_it_reads(string file)
    {
        var contract = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Contracts", file))).RootElement;
        Application.Updates.SemVer.Compare(contract.GetProperty("terminalVersion").GetString(), Application.Updates.TerminalVersions.Minimum)
            .Should().BeGreaterThanOrEqualTo(0, $"{file} is older than the minimum terminal version: remove it");

        // Its wrong admin password counts towards the sign-in limits: start and end with none.
        var throttle = app.Services.GetRequiredService<Application.Auth.LoginThrottle>();
        throttle.Reset();
        using var reset = new Reset(throttle);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(Application.Updates.TerminalVersions.Header, contract.GetProperty("terminalVersion").GetString());
        JsonElement? pairing = null;
        foreach (var call in contract.GetProperty("calls").EnumerateArray())
        {
            var name = call.GetProperty("name").GetString();
            var request = new HttpRequestMessage(new HttpMethod(call.GetProperty("method").GetString()!), call.GetProperty("path").GetString());
            if (call.TryGetProperty("body", out var body))
                request.Content = JsonContent.Create(body.ValueKind == JsonValueKind.String && body.GetString() == "claim"
                    ? new { pairingId = pairing!.Value.GetProperty("pairingId").GetGuid(), secret = pairing.Value.GetProperty("secret").GetString() }
                    : (object)body);
            var response = await client.SendAsync(request);

            call.GetProperty("status").EnumerateArray().Select(s => s.GetInt32()).Should().Contain((int)response.StatusCode, name);
            foreach (var header in contract.GetProperty("headers").EnumerateArray())
                response.Headers.Contains(header.GetString()!).Should().BeTrue($"{name} answers with {header}");
            var fields = call.GetProperty("fields").EnumerateArray().Select(f => f.GetString()!).ToList();
            if (fields.Count == 0) continue;
            var json = await response.Json();
            foreach (var field in fields) json.TryGetProperty(field, out _).Should().BeTrue($"{name} has '{field}'");
            if (call.GetProperty("path").GetString() == "/api/terminals/pairing") pairing = json;
        }
    }

    private sealed class Reset(Application.Auth.LoginThrottle throttle) : IDisposable
    {
        public void Dispose() => throttle.Reset();
    }
}
