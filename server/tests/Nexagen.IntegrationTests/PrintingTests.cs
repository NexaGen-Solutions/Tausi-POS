using System.Net;
using FluentAssertions;
using Nexagen.IntegrationTests.Support;

namespace Nexagen.IntegrationTests;

[Collection(AppCollection.Name)]
public class PrintingTests(NexagenApp app)
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
        text.Should().Contain("Ticket Printer 2").And.Contain("Test OK");
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
