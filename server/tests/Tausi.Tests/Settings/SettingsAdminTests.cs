using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Tests.Tickets;

namespace Tausi.Tests.Settings;

public class SettingsAdminTests : IAsyncLifetime
{
    private TicketsFixture F = null!;
    private static readonly CancellationToken None = CancellationToken.None;

    public async Task InitializeAsync()
    {
        F = await TicketsFixture.Create();
        await F.LoginOn("Administrator", "Counter A");
    }

    public async Task DisposeAsync() => await F.DisposeAsync();

    private static Dictionary<string, JsonElement> Values(object values) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(values))!;

    private async Task<SettingsDto> Save(object values, long? version = null) =>
        await F.Get<UpdateSettings>().Execute(
            new UpdateSettingsRequest(Values(values), version ?? (await F.Get<GetSettings>().Execute(None)).Version), None);

    [Fact]
    public async Task Only_the_changed_keys_are_saved_each_audited_with_old_and_new_and_announced()
    {
        F.Events.Published.Clear();
        var dto = await Save(new Dictionary<string, object>
        {
            ["pos.idleLogoutMinutes"] = 15, ["payments.mpesaCodeRequired"] = false, ["business.vatRate"] = 16, ["pos.approvalChime"] = true,
        });

        var audits = F.Read().AuditEntries.Where(a => a.Action == AuditActions.SettingsChanged).OrderBy(a => a.EntityId).ToList();
        audits.Select(a => a.EntityId).Should().Equal("payments.mpesaCodeRequired", "pos.idleLogoutMinutes");
        audits[1].OldValueJson.Should().Contain("10");
        audits[1].NewValueJson.Should().Contain("15");
        var (name, payload) = F.Events.Published.Single();
        name.Should().Be(PosEventNames.SettingsChanged);
        JsonSerializer.SerializeToElement(payload).GetProperty("keys").EnumerateArray().Select(k => k.GetString())
            .Should().BeEquivalentTo("pos.idleLogoutMinutes", "payments.mpesaCodeRequired");

        var all = dto.Sections.SelectMany(s => s.Entries).ToDictionary(e => e.Key);
        all["pos.idleLogoutMinutes"].Value!.GetValue<int>().Should().Be(15);
        all["pos.idleLogoutMinutes"].Default!.GetValue<int>().Should().Be(10);
        (await PosSettings.Load(F.Db, None)).IdleLogoutMinutes.Should().Be(15);

        var history = await F.Get<SettingsHistory>().Execute(None);
        history.Should().Contain(h => h.Key == "pos.idleLogoutMinutes" && h.Old == "10" && h.New == "15" && h.Who == "Administrator");
    }

    [Fact]
    public async Task Nothing_changed_saves_nothing_and_announces_nothing()
    {
        F.Events.Published.Clear();
        await Save(new Dictionary<string, object> { ["pos.idleLogoutMinutes"] = 10 });
        F.Events.Published.Should().BeEmpty();
        F.Read().AuditEntries.Count(a => a.Action == AuditActions.SettingsChanged).Should().Be(0);
    }

    [Fact]
    public async Task Unknown_keys_and_bad_values_come_back_per_field_and_nothing_is_saved()
    {
        var act = () => Save(new Dictionary<string, object> { ["pos.idleLogoutMinutes"] = 0, ["pos.nothing"] = true, ["business.name"] = "New" });

        var errors = (await act.Should().ThrowAsync<FieldErrorsException>()).Which.Errors;
        errors.Keys.Should().BeEquivalentTo("pos.idleLogoutMinutes", "pos.nothing");
        F.Read().BusinessSettings.Single().Name.Should().Be("Test");
    }

    [Fact]
    public async Task A_save_from_an_older_version_is_refused()
    {
        var loaded = (await F.Get<GetSettings>().Execute(None)).Version;
        await Save(new Dictionary<string, object> { ["business.name"] = "First" }, loaded);

        var act = () => Save(new Dictionary<string, object> { ["business.name"] = "Second" }, loaded);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("Someone else saved settings. Reload to see their changes.");
        F.Read().BusinessSettings.Single().Name.Should().Be("First");
    }

    [Fact]
    public async Task Settings_need_settings_manage()
    {
        await F.LoginOn("MAU", "Counter A");
        await FluentActions.Awaiting(() => F.Get<GetSettings>().Execute(None)).Should().ThrowAsync<PermissionDeniedException>();
        await FluentActions.Awaiting(() => F.Get<UpdateSettings>().Execute(new UpdateSettingsRequest([], 0), None))
            .Should().ThrowAsync<PermissionDeniedException>();
    }

    [Fact]
    public async Task The_preview_checks_unsaved_values_like_a_save()
    {
        var act = () => F.Get<ReceiptPreview>().Execute(new ReceiptPreviewRequest(42, Values(new Dictionary<string, object> { ["business.vatRate"] = 120 })), None);
        await act.Should().ThrowAsync<FieldErrorsException>();
        await FluentActions.Awaiting(() => F.Get<ReceiptPreview>().Execute(new ReceiptPreviewRequest(40), None))
            .Should().ThrowAsync<Domain.Common.DomainException>();

        var preview = await F.Get<ReceiptPreview>().Execute(
            new ReceiptPreviewRequest(32, Values(new Dictionary<string, object> { ["business.name"] = "Unsaved Name", ["money.rounding"] = "NearestTen" })), None);
        preview.Bill.Should().Contain("Unsaved Name");
        preview.Bill.Split('\n').Should().OnlyContain(l => l.Length <= 32);
        (await F.Read().BusinessSettings.SingleAsync()).Name.Should().Be("Test");
    }
}
