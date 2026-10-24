using System.Net.Http.Json;
using System.Text.Json;

namespace Tausi.IntegrationTests.Support;

/// <summary>Changing settings and permissions the way the admin screens do: load the version, then save.</summary>
public static class AdminApi
{
    /// <summary>PUT /api/admin/settings with these keys, at the current version.</summary>
    public static async Task<HttpResponseMessage> SaveSettings(this HttpClient admin, Dictionary<string, object?> values)
    {
        var current = await (await admin.GetAsync("/api/admin/settings")).Json();
        return await admin.PutAsJsonAsync("/api/admin/settings", new { values, version = current.GetProperty("version").GetInt64() });
    }

    public static async Task SetSetting(this HttpClient admin, string key, object? value) =>
        (await admin.SaveSettings(new() { [key] = value })).EnsureSuccessStatusCode();

    /// <summary>The current value of a setting from GET /api/admin/settings.</summary>
    public static async Task<JsonElement> Setting(this HttpClient admin, string key)
    {
        var current = await (await admin.GetAsync("/api/admin/settings")).Json();
        return current.GetProperty("sections").EnumerateArray().SelectMany(s => s.GetProperty("entries").EnumerateArray())
            .Single(e => e.GetProperty("key").GetString() == key).GetProperty("value");
    }

    /// <summary>PUT /api/admin/permissions with one changed cell, at the current version.</summary>
    public static async Task<HttpResponseMessage> SavePermission(this HttpClient admin, Guid roleId, string key, string value)
    {
        var grid = await (await admin.GetAsync("/api/admin/permissions")).Json();
        return await admin.PutAsJsonAsync("/api/admin/permissions", new
        {
            version = grid.GetProperty("version").GetString(),
            changes = new[] { new { roleId, permissionKey = key, value } },
        });
    }

    public static async Task SetPermission(this HttpClient admin, Guid roleId, string key, string value) =>
        (await admin.SavePermission(roleId, key, value)).EnsureSuccessStatusCode();
}
