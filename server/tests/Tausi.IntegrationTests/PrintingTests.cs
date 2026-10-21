using System.Net;
using FluentAssertions;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

[Collection(AppCollection.Name)]
public class PrintingTests(TausiApp app)
{
    [Fact]
    public async Task Test_print_writes_a_readable_slip_through_the_file_transport()
    {
        var admin = await app.AdminClient();
        var printerId = app.Db(db => db.Printers.Single(p => p.Name == "Ticket Printer 2").Id);

        var response = await admin.PostAsync($"/api/printers/{printerId}/test", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string? slip = null;
        for (var i = 0; i < 50 && slip is null; i++)
        {
            await Task.Delay(200);
            if (Directory.Exists(app.PrintOutput))
                slip = Directory.GetFiles(app.PrintOutput, "*Ticket-Printer-2-Test*").FirstOrDefault();
        }
        slip.Should().NotBeNull("the dispatcher sends queued jobs within a second or so");
        var text = await File.ReadAllTextAsync(slip!);
        text.Should().Contain("Ticket Printer 2").And.Contain("Test OK")
            .And.NotContain("Powered by Tausi POS", "the footer is off by default");

        // The business is in Africa/Nairobi (seeded), whatever zone the server (CI: UTC) runs in.
        var nairobi = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Africa/Nairobi"));
        var printed = text.Split('\n').Single(l => l.StartsWith("Time")).Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
        var at = TimeOnly.ParseExact(printed, "HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        Math.Abs((TimeOnly.FromDateTime(nairobi.DateTime) - at).TotalMinutes % (24 * 60)).Should().BeLessThan(2,
            "the test print shows the business's time");
    }

    [Fact]
    public async Task Test_print_needs_devices_manage()
    {
        var terminal = await app.PairTerminal();
        await terminal.LoginWithPin(app.Pins["CAROL"]);
        var printerId = app.Db(db => db.Printers.First().Id);

        (await terminal.Http.PostAsync($"/api/printers/{printerId}/test", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Health_reports_version_database_and_printers()
    {
        var health = await (await app.CreateClient().GetAsync("/api/health")).Json();

        health.GetProperty("database").GetString().Should().Be("ok");
        health.GetProperty("version").GetString().Should().NotBeNullOrEmpty();
        health.GetProperty("printers").GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
    }
}
