using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Tausi.Application.Money;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

/// <summary>The cash book over HTTP (PostgreSQL): money actions, the queue, attachments and the back-fill.</summary>
[Collection(AppCollection.Name)]
public class MoneyApiTests(TausiApp app)
{
    private Guid Category(string name) => app.Db(db => db.ExpenseCategories.Single(c => c.Name == name).Id);

    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Json();
    }

    [Fact]
    public async Task An_expense_with_a_receipt_a_supplier_payment_and_banking_move_the_tiles()
    {
        var admin = await app.AdminClient();
        var before = await Ok(await admin.GetAsync("/api/admin/cashbook"));
        var safe = before.GetProperty("tiles").GetProperty("cashOnHand").GetDecimal();

        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3]);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "receipt.png");
        var uploaded = await Ok(await admin.PostAsync("/api/admin/money/attachments", form));
        var fileId = uploaded.GetProperty("fileId").GetGuid();
        (await app.CreateClient().GetAsync($"/files/{fileId}")).StatusCode.Should().Be(HttpStatusCode.NotFound, "receipts are private");

        var expense = await Ok(await admin.PostAsJsonAsync("/api/admin/money/expenses", new
        {
            amount = 1500, categoryId = Category("Repairs"), paidFrom = "Safe", payee = "Fundi", attachmentFileId = fileId,
        }));
        var after = await Ok(await admin.GetAsync("/api/admin/cashbook"));
        after.GetProperty("tiles").GetProperty("cashOnHand").GetDecimal().Should().Be(safe - 1500);
        var expenseId = expense.GetProperty("id").GetGuid();
        (await admin.GetAsync($"/api/admin/money/{expenseId}/attachment")).StatusCode.Should().Be(HttpStatusCode.OK);

        var kbl = app.Db(db => db.Suppliers.Single(s => s.Name == "KBL").Id);
        var owed = app.Db(db => SupplierBalances.Owed(db, CancellationToken.None).GetAwaiter().GetResult()).GetValueOrDefault(kbl);
        var tooMuch = await admin.PostAsJsonAsync("/api/admin/money/supplier-payments", new { supplierId = kbl, amount = owed + 1, paidFrom = "Bank" });
        tooMuch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await tooMuch.Json()).GetProperty("detail").GetString().Should().StartWith("KBL is owed");

        var banked = await Ok(await admin.PostAsJsonAsync("/api/admin/money/moves", new { from = "Safe", to = "Bank", amount = 15000, fee = 50 }));
        banked.GetProperty("title").GetString().Should().Be("Banked cash");
        var reversed = await Ok(await admin.PostAsJsonAsync($"/api/admin/money/{expenseId}/reverse", new { reason = "Entered twice" }));
        reversed.GetProperty("reversalStatus").GetString().Should().Be("Posted");

        var accounts = await Ok(await admin.GetAsync("/api/admin/accounts"));
        accounts.GetProperty("trial").GetProperty("difference").GetDecimal().Should().Be(0);
        var csv = await admin.GetAsync($"/api/admin/cashbook/export.csv?date={after.GetProperty("date").GetString()}");
        csv.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        (await admin.GetAsync($"/api/admin/cashbook/summary.pdf?date={after.GetProperty("date").GetString()}")).Content.Headers.ContentType!.MediaType
            .Should().Be("application/pdf");
    }

    [Fact]
    public async Task The_back_fill_dates_existing_entries_on_postgresql()
    {
        var id = Guid.NewGuid();
        app.Db(db =>
        {
            db.JournalEntries.Add(new Domain.Ledger.JournalEntry { Id = id, Date = DateTimeOffset.UtcNow.AddDays(-3), Memo = "Before task 12", SourceType = "Test", CreatedAt = DateTimeOffset.UtcNow });
            db.SaveChanges();
            return 0;
        });

        app.Db(db => new LedgerBackfill(db).Run(CancellationToken.None).GetAwaiter().GetResult());

        app.Db(db => db.JournalEntries.Single(e => e.Id == id).BusinessDate).Should().NotBeNull();
        app.Db(db => db.JournalEntries.Count(e => e.BusinessDate == null)).Should().Be(0);
    }
    private async Task<HttpClient> SignedInAs(string user, string role, string password)
    {
        var admin = await app.AdminClient();
        var roleId = app.Db(db => db.Roles.Single(r => r.Name == role).Id);
        var staff = app.Db(db => db.Users.Single(u => u.Name == user));
        (await admin.PutAsJsonAsync($"/api/admin/staff/{staff.Id}", new { name = user, roleId, adminPassword = password, rowVersion = staff.RowVersion }))
            .EnsureSuccessStatusCode();
        return await app.AdminClient(user.ToLowerInvariant(), password);
    }

    [Fact]
    public async Task A_supervisors_supplier_payment_waits_until_a_manager_approves_and_only_cash_book_opens_a_receipt()
    {
        var admin = await app.AdminClient();
        var supervisor = await SignedInAs("MAU", "Supervisor", "mau-password-1");
        var before = await Ok(await admin.GetAsync("/api/admin/cashbook"));
        var bank = before.GetProperty("tiles").GetProperty("bank").GetDecimal();

        // Expenses are cash.expense (Allowed for supervisors); paying a supplier is cash.book (Needs approval).
        var kbl = app.Db(db => db.Suppliers.Single(s => s.Name == "KBL").Id);
        await Ok(await admin.PostAsJsonAsync("/api/admin/deliveries", new
        {
            supplierId = kbl, invoiceNumber = "MONEY-" + Guid.NewGuid().ToString("N")[..6], payment = "OnCredit",
            locationId = app.Db(db => db.StockLocations.Single(l => l.Name == "Store").Id),
            lines = new[] { new { stockItemId = app.Db(db => db.StockItems.Single(i => i.Name == "Tusker Lager").Id), unit = "crate", quantity = 1, unitCost = 2500 } },
        }));
        var pending = await Ok(await supervisor.PostAsJsonAsync("/api/admin/money/supplier-payments", new { supplierId = kbl, amount = 800, paidFrom = "Bank" }));
        pending.GetProperty("status").GetString().Should().Be("Pending");
        var id = pending.GetProperty("id").GetGuid();
        (await Ok(await admin.GetAsync("/api/admin/cashbook"))).GetProperty("tiles").GetProperty("bank").GetDecimal().Should().Be(bank);
        var queue = await Ok(await admin.GetAsync("/api/admin/awaiting"));
        queue.GetRawText().Should().Contain(id.ToString());

        (await supervisor.PostAsync($"/api/admin/money/{id}/approve", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "nobody approves their own");
        (await Ok(await admin.PostAsync($"/api/admin/money/{id}/approve", null))).GetProperty("status").GetString().Should().Be("Posted");
        (await Ok(await admin.GetAsync("/api/admin/cashbook"))).GetProperty("tiles").GetProperty("bank").GetDecimal().Should().Be(bank - 800);

        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent("%PDF-1.4 receipt"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "receipt.pdf");
        var fileId = (await Ok(await supervisor.PostAsync("/api/admin/money/attachments", form))).GetProperty("fileId").GetGuid();
        var expense = await Ok(await supervisor.PostAsJsonAsync("/api/admin/money/expenses", new
        {
            amount = 300, categoryId = Category("Transport"), paidFrom = "Safe", attachmentFileId = fileId,
        }));
        expense.GetProperty("status").GetString().Should().Be("Posted");
        var expenseId = expense.GetProperty("id").GetGuid();
        (await admin.GetAsync($"/api/admin/money/{expenseId}/attachment")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Without cash.book the receipt stays closed; reports.all still reads the cash book.
        var supervisorRole = app.Db(db => db.Roles.Single(r => r.Name == "Supervisor").Id);
        await admin.SetPermission(supervisorRole, "cash.book", "NotAllowed");
        try
        {
            (await supervisor.GetAsync($"/api/admin/money/{expenseId}/attachment")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await supervisor.GetAsync("/api/admin/cashbook")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await admin.SetPermission(supervisorRole, "cash.book", "NeedsApproval");
        }
    }
}
