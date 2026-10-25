using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Tausi.Infrastructure.Import;
using Tausi.Infrastructure.Persistence;
using Tausi.Infrastructure.Seeding;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

/// <summary>
/// The setup migration on both databases (a database with users is set up), and the SambaPOS reader
/// on SQL Server: read only, SELECTs only, missing tables reported, and the import end to end.
/// </summary>
public class SetupDatabaseTests(SqlServerFixture sql, PostgresFixture pg) : IClassFixture<SqlServerFixture>, IClassFixture<PostgresFixture>
{
    private const string Skipped = "No Docker and no TAUSI_TEST_SQLSERVER: skipping the SQL Server check.";

    /// <summary>Seeds users, rolls the setup migration back, then forward: the database must come out set up.</summary>
    private static async Task UsersMeanSetUp(TausiDbContext db)
    {
        await db.Database.MigrateAsync();
        await new Seeder(db, new PlainPinHasher(), NullLogger<Seeder>.Instance).SeedDemo("pw", CancellationToken.None, filePrinters: true);
        await db.SetupStates.ExecuteDeleteAsync();
        var migrations = db.Database.GetMigrations().ToList();
        var setup = migrations.FindLastIndex(m => m.EndsWith("_SetupWizard", StringComparison.Ordinal));
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(migrations[setup - 1]);
        await migrator.MigrateAsync(migrations[^1]);
        db.ChangeTracker.Clear();
        (await db.SetupStates.SingleAsync()).CompletedAt.Should().NotBeNull("a database that already has users is set up");
    }

    [Fact]
    public async Task A_database_with_users_is_marked_set_up_by_the_migration_on_postgresql()
    {
        await using var db = new PostgresTausiDbContext(PostgresFixture.Options(pg.ConnectionFor("tausi_setup_mig")));
        await UsersMeanSetUp(db);
    }

    [SkippableFact]
    public async Task A_database_with_users_is_marked_set_up_by_the_migration_on_sql_server()
    {
        Skip.IfNot(sql.Available, Skipped);
        await using var db = new SqlServerTausiDbContext(sql.Options("tausi_setup_mig"));
        await UsersMeanSetUp(db);
    }

    [SkippableFact]
    public async Task The_sambapos_reader_only_selects_never_reads_pins_and_reports_what_it_cant_read()
    {
        Skip.IfNot(sql.Available, Skipped);
        var connection = await SambaPosDatabase.Create(sql, "SambaPOS5_read");
        var commands = new List<string>();
        var reader = new SambaPosReader { OnCommand = commands.Add };

        (await reader.Test(connection, CancellationToken.None)).Should().Contain("3 menu items");
        var data = await reader.Read(connection, CancellationToken.None);
        data.Items.Select(i => i.Name).Should().BeEquivalentTo(["Tusker", "Guinness Samba", "Pool Game"]);
        data.Portions.Should().HaveCount(4);
        data.Prices.Should().Contain(p => p.PortionId == 11 && p.PriceTag == "Happy Hour" && p.Price == 200);
        data.TagMaps.Should().ContainSingle().Which.Should().Be(new Application.Setup.SambaTagMap(5, null, "Beers"));
        data.Tables.Select(t => t.Name).Should().Equal("Patio 1", "Patio 2", "Patio 3");
        data.Users.Select(u => (u.Name, u.Role)).Should().BeEquivalentTo([("Kioko", "Admin"), ("Akinyi", "Waiter"), ("Otieno", "Cashier")]);
        data.Problems.Should().BeEmpty();
        commands.Should().NotBeEmpty().And.OnlyContain(c => c.TrimStart().StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase));
        commands.Should().NotContain(c => c.Contains("PinCode", StringComparison.OrdinalIgnoreCase));
        Infrastructure.Import.SambaPosReader.ConnectionString(connection).Should().Contain("Application Intent=ReadOnly");

        var partial = await SambaPosDatabase.Create(sql, "SambaPOS5_old", withoutTagMaps: true);
        var older = await reader.Read(partial, CancellationToken.None);
        older.Problems.Should().ContainSingle().Which.Should().Be("OrderTagMaps: table not found, skipped.");
        older.Items.Should().HaveCount(3, "everything else is still read");

        var wrong = connection with { Password = "not-the-password" };
        (await FluentActions.Awaiting(() => reader.Test(wrong, CancellationToken.None)).Should().ThrowAsync<Domain.Common.DomainException>())
            .Which.Message.Should().StartWith("Couldn't connect to SambaPOS");
    }
}

/// <summary>The SambaPOS import through the admin API (PostgreSQL), from the SQL Server fixture.</summary>
public class SambaPosImportApiTests(ImportApp app, SqlServerFixture sql) : IClassFixture<ImportApp>, IClassFixture<SqlServerFixture>
{
    [SkippableFact]
    public async Task The_import_previews_brings_everything_across_and_never_duplicates()
    {
        Skip.IfNot(sql.Available, "No Docker and no TAUSI_TEST_SQLSERVER: skipping the SQL Server check.");
        var connection = await SambaPosDatabase.Create(sql, "SambaPOS5_api");
        var admin = await app.AdminClient();
        var body = new { connection, options = new { menu = true, prices = true, modifiers = true, tables = true, users = true } };

        (await (await admin.PostAsJsonAsync("/api/admin/menu/import/sambapos/test", connection)).Json()).GetProperty("message").GetString()
            .Should().Contain("3 menu items");
        var preview = await (await admin.PostAsJsonAsync("/api/admin/menu/import/sambapos/preview", body)).Json();
        preview.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString())
            .Should().BeEquivalentTo(["Tusker", "Guinness Samba (Small)", "Guinness Samba (Large)", "Pool Game"]);
        preview.GetProperty("priceLists").EnumerateArray().Select(p => p.GetString()).Should().Equal("Happy Hour", "Standard");
        preview.GetProperty("modifierGroups").EnumerateArray().Select(g => g.GetString()).Should().Equal("Temperature (Cold / Warm)");
        preview.GetProperty("tables").GetArrayLength().Should().Be(3);
        preview.GetProperty("users").EnumerateArray().Select(u => u.TryGetProperty("pin", out var none)).Should().OnlyContain(hasPin => !hasPin);

        var applied = await admin.PostAsJsonAsync("/api/admin/menu/import/sambapos/apply", body);
        applied.StatusCode.Should().Be(HttpStatusCode.OK, await applied.Content.ReadAsStringAsync());
        var result = await applied.Json();
        result.GetProperty("people").GetArrayLength().Should().Be(3);
        app.Db(db => db.Users.Where(u => u.ExternalRef != null).All(u => u.MustChangePin)).Should().BeTrue();
        var counts = app.Db(db => (db.MenuItems.Count(), db.Tables.Count(), db.Users.Count(), db.PriceLists.Count(), db.ModifierGroups.Count()));

        var again = await (await admin.PostAsJsonAsync("/api/admin/menu/import/sambapos/preview", body)).Json();
        (again.GetProperty("newItems").GetInt32(), again.GetProperty("newTables").GetInt32(), again.GetProperty("newUsers").GetInt32()).Should().Be((0, 0, 0));
        (await admin.PostAsJsonAsync("/api/admin/menu/import/sambapos/apply", body)).StatusCode.Should().Be(HttpStatusCode.OK);
        app.Db(db => (db.MenuItems.Count(), db.Tables.Count(), db.Users.Count(), db.PriceLists.Count(), db.ModifierGroups.Count())).Should().Be(counts);
    }
}
