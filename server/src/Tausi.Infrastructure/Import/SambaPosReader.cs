using System.Data.Common;
using Microsoft.Data.SqlClient;
using Tausi.Application.Setup;
using Tausi.Domain.Common;

namespace Tausi.Infrastructure.Import;

/// <summary>
/// Reads a SambaPOS 5 database for the import. Read only: the connection asks for
/// ApplicationIntent=ReadOnly and every command goes through <see cref="Select"/>, which runs SELECTs
/// and nothing else. The schema is read first; a table or column that isn't there is reported in
/// <see cref="SambaPosData.Problems"/> and that part skipped, since SambaPOS versions differ.
/// PINs and passwords are never selected.
/// </summary>
public sealed class SambaPosReader : ISambaPosSource
{
    /// <summary>Every command sent, for the tests (and nothing but SELECTs ever is).</summary>
    public Action<string>? OnCommand { get; set; }

    public static string ConnectionString(SambaPosConnection c)
    {
        if (string.IsNullOrWhiteSpace(c.Server)) throw new DomainException("Enter the SambaPOS server, for example POSSERVER2\\SQLEXPRESS.", "sambapos_server");
        var b = new SqlConnectionStringBuilder
        {
            DataSource = c.Server.Trim(),
            InitialCatalog = string.IsNullOrWhiteSpace(c.Database) ? "SambaPOS5" : c.Database.Trim(),
            ApplicationIntent = ApplicationIntent.ReadOnly,
            ApplicationName = "Tausi POS import (read only)",
            ConnectTimeout = 10,
            Encrypt = SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = true,
        };
        if (c.WindowsLogin) b.IntegratedSecurity = true;
        else
        {
            b.UserID = c.User ?? "";
            b.Password = c.Password ?? "";
        }
        return b.ConnectionString;
    }

    public async Task<string> Test(SambaPosConnection connection, CancellationToken ct)
    {
        await using var sql = await Open(connection, ct);
        var tables = await Tables(sql, ct);
        if (!tables.ContainsKey("MenuItems"))
            throw new DomainException($"Connected to {sql.Database}, but it has no MenuItems table. Is it the SambaPOS database?", "sambapos_schema");
        var count = (await Select(sql, "SELECT COUNT(*) FROM [MenuItems]", r => r.GetInt32(0), ct)).Single();
        return $"Connected to {sql.Database} on {sql.DataSource}: {count} menu items.";
    }

    public async Task<SambaPosData> Read(SambaPosConnection connection, CancellationToken ct)
    {
        await using var sql = await Open(connection, ct);
        var tables = await Tables(sql, ct);
        var problems = new List<string>();

        bool Has(string table, params string[] columns)
        {
            if (!tables.TryGetValue(table, out var have))
            {
                problems.Add($"{table}: table not found, skipped.");
                return false;
            }
            var missing = columns.Where(c => !have.Contains(c)).ToList();
            if (missing.Count == 0) return true;
            problems.Add($"{table}: no {string.Join(", ", missing)} column, skipped.");
            return false;
        }
        string? Column(string table, params string[] choices) =>
            tables.TryGetValue(table, out var have) ? choices.FirstOrDefault(have.Contains) : null;

        var items = Has("MenuItems", "Id", "Name")
            ? await Select(sql, $"SELECT [Id], [Name], {(Column("MenuItems", "GroupCode") is { } g ? $"[{g}]" : "NULL")} FROM [MenuItems]",
                r => new SambaMenuItem(r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)), ct)
            : [];
        var portions = Has("MenuItemPortions", "Id", "MenuItemId", "Name")
            ? await Select(sql, "SELECT [Id], [MenuItemId], [Name] FROM [MenuItemPortions]",
                r => new SambaPortion(r.GetInt32(0), r.GetInt32(1), r.IsDBNull(2) ? "" : r.GetString(2)), ct)
            : [];
        var prices = Has("MenuItemPrices", "MenuItemPortionId", "Price")
            ? await Select(sql, $"SELECT [MenuItemPortionId], {(Column("MenuItemPrices", "PriceTag") is { } t ? $"[{t}]" : "NULL")}, [Price] FROM [MenuItemPrices]",
                r => new SambaPrice(r.GetInt32(0), r.IsDBNull(1) ? null : r.GetString(1), Convert.ToDecimal(r.GetValue(2))), ct)
            : [];

        var tagGroups = Has("OrderTagGroups", "Id", "Name")
            ? await Select(sql,
                $"SELECT [Id], [Name], {Int(Column("OrderTagGroups", "MinSelectedItems"), "0")}, {Int(Column("OrderTagGroups", "MaxSelectedItems"), "1")} FROM [OrderTagGroups]",
                r => new SambaTagGroup(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3)), ct)
            : [];
        var tags = Has("OrderTags", "Id", "OrderTagGroupId", "Name")
            ? await Select(sql, $"SELECT [Id], [OrderTagGroupId], [Name], {(Column("OrderTags", "Price") is { } p ? $"[{p}]" : "0")} FROM [OrderTags]",
                r => new SambaTag(r.GetInt32(0), r.GetInt32(1), r.GetString(2), Convert.ToDecimal(r.GetValue(3))), ct)
            : [];
        var tagMaps = Has("OrderTagMaps", "OrderTagGroupId")
            ? await Select(sql,
                $"SELECT [OrderTagGroupId], {(Column("OrderTagMaps", "MenuItemId") is { } mi ? $"[{mi}]" : "NULL")}, {(Column("OrderTagMaps", "MenuItemGroupCode") is { } gc ? $"[{gc}]" : "NULL")} FROM [OrderTagMaps]",
                r => new SambaTagMap(r.GetInt32(0), r.IsDBNull(1) || r.GetInt32(1) == 0 ? null : r.GetInt32(1),
                    r.IsDBNull(2) || r.GetString(2).Trim().Length == 0 ? null : r.GetString(2).Trim()), ct)
            : [];

        // Tables are the entities of the entity type whose entities are called "Table".
        var tablesRead = Has("Entities", "Id", "Name", "EntityTypeId") && Has("EntityTypes", "Id", "Name")
            ? await Select(sql,
                $"SELECT e.[Id], e.[Name] FROM [Entities] e JOIN [EntityTypes] t ON t.[Id] = e.[EntityTypeId] " +
                $"WHERE t.[Name] LIKE '%Table%'{(Column("EntityTypes", "EntityName") is { } en ? $" OR t.[{en}] LIKE '%Table%'" : "")}",
                r => new SambaTable(r.GetInt32(0), r.GetString(1)), ct)
            : [];

        // Users: names and roles only (the PinCode column is never read).
        var roleColumn = Column("Users", "UserRoleId", "UserRole_Id");
        List<SambaUser> users = [];
        if (Has("Users", "Id", "Name"))
        {
            var withRoles = roleColumn is not null && tables.ContainsKey("UserRoles");
            users = await Select(sql,
                withRoles
                    ? $"SELECT u.[Id], u.[Name], r.[Name] FROM [Users] u LEFT JOIN [UserRoles] r ON r.[Id] = u.[{roleColumn}]"
                    : "SELECT [Id], [Name], NULL FROM [Users]",
                r => new SambaUser(r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)), ct);
            if (!withRoles) problems.Add("Users: roles couldn't be read; choose each person's role.");
        }

        var screen = tables.ContainsKey("ScreenMenuItems") && tables.ContainsKey("ScreenMenuCategories")
                     && Column("ScreenMenuItems", "ScreenMenuCategoryId") is not null && Column("ScreenMenuItems", "MenuItemId") is not null
            ? await Select(sql,
                "SELECT i.[MenuItemId], c.[Name] FROM [ScreenMenuItems] i JOIN [ScreenMenuCategories] c ON c.[Id] = i.[ScreenMenuCategoryId]",
                r => new SambaScreenItem(r.GetInt32(0), r.GetString(1)), ct)
            : [];
        return new SambaPosData(items, portions, prices, tagGroups, tags, tagMaps, tablesRead, users, screen, problems);
    }

    private static string Int(string? column, string fallback) => column is null ? fallback : $"ISNULL([{column}], {fallback})";

    private async Task<SqlConnection> Open(SambaPosConnection connection, CancellationToken ct)
    {
        var sql = new SqlConnection(ConnectionString(connection));
        try
        {
            await sql.OpenAsync(ct);
            return sql;
        }
        catch (SqlException ex)
        {
            await sql.DisposeAsync();
            throw new DomainException($"Couldn't connect to SambaPOS: {ex.Message}", "sambapos_connect");
        }
    }

    /// <summary>Every table's columns, from the schema views.</summary>
    private async Task<Dictionary<string, HashSet<string>>> Tables(SqlConnection sql, CancellationToken ct)
    {
        var rows = await Select(sql, "SELECT TABLE_NAME, COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo'",
            r => (Table: r.GetString(0), Column: r.GetString(1)), ct);
        return rows.GroupBy(r => r.Table, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Column).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The only way this class talks to SambaPOS: a SELECT, read row by row.</summary>
    private async Task<List<T>> Select<T>(SqlConnection sql, string text, Func<DbDataReader, T> map, CancellationToken ct)
    {
        if (!text.TrimStart().StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase) || text.Contains(';'))
            throw new InvalidOperationException("The SambaPOS import only runs single SELECT statements.");
        OnCommand?.Invoke(text);
        await using var command = new SqlCommand(text, sql) { CommandTimeout = 60 };
        await using var reader = await command.ExecuteReaderAsync(ct);
        var list = new List<T>();
        while (await reader.ReadAsync(ct)) list.Add(map(reader));
        return list;
    }
}
