using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Tausi.Application.Updates;

namespace Tausi.Infrastructure.Updates;

/// <summary>NexaGen's update feed over HTTPS (plain HTTP only to this PC, for testing).</summary>
public sealed class HttpUpdateSource(IOptions<TausiOptions> options) : IUpdateSource
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<UpdateFeed> Fetch(CancellationToken ct)
    {
        var url = Checked(options.Value.Updates.FeedUrl);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        return await Http.GetFromJsonAsync<UpdateFeed>(url, Json, timeout.Token)
               ?? throw new InvalidOperationException("The update feed is empty.");
    }

    public async Task Download(string url, string path, CancellationToken ct)
    {
        using var response = await Http.GetAsync(Checked(url), HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var file = new FileStream(path + ".part", FileMode.Create);
        await response.Content.CopyToAsync(file, ct);
        file.Close();
        File.Move(path + ".part", path, overwrite: true);
    }

    private static Uri Checked(string url)
    {
        var uri = new Uri(url);
        if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
            throw new InvalidOperationException("Updates are only fetched over HTTPS.");
        return uri;
    }
}

/// <summary>
/// The terminal update the server offers: &lt;program&gt;/updates/terminal/terminal.json (version, notes,
/// pubDate, file, signature) and the installer it names, put there by the server installer.
/// </summary>
public sealed class TerminalUpdateFiles(IOptions<TausiOptions> options, IHostEnvironment env) : ITerminalUpdateFiles
{
    private sealed record Manifest(string Version, string? Notes, DateTimeOffset? PubDate, string File, string Signature);

    private string Folder => Path.GetFullPath(options.Value.Updates.TerminalFolder, env.ContentRootPath);

    public TerminalUpdate? Current()
    {
        var path = Path.Combine(Folder, "terminal.json");
        if (!File.Exists(path)) return null;
        try
        {
            var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return m is null || PathOf(m.File) is null ? null : new TerminalUpdate(m.Version, m.Notes, m.PubDate, m.File, m.Signature);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string? PathOf(string file)
    {
        if (string.IsNullOrWhiteSpace(file) || file != Path.GetFileName(file)) return null;
        var path = Path.Combine(Folder, file);
        return File.Exists(path) ? path : null;
    }
}
