using System.Text;
using Tausi.Application.Money;
using Tausi.Application.Stock;

namespace Tausi.Api.Endpoints;

public static class MoneyEndpoints
{
    public static void MapMoneyEndpoints(this WebApplication app)
    {
        var book = app.MapGroup("/api/admin/cashbook").WithTags("Money").RequireAuthorization();
        book.MapGet("/", async (DateOnly? date, bool? all, CashBook cashBook, TimeProvider clock, Tausi.Application.Abstractions.ITausiDb db, CancellationToken ct) =>
            await cashBook.Day(date ?? await BusinessDates.Today(db, clock, ct), all ?? false, ct));
        book.MapGet("/entries/{id:guid}", (Guid id, CashBook cashBook, CancellationToken ct) => cashBook.Entry(id, ct));
        book.MapGet("/export.csv", async (DateOnly date, bool? all, CashBook cashBook, CancellationToken ct) =>
            Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(await cashBook.Csv(date, all ?? false, ct))).ToArray(),
                "text/csv; charset=utf-8", $"cashbook-{date:yyyy-MM-dd}.csv"));
        book.MapGet("/summary.pdf", async (DateOnly date, CashBookPrint print, CancellationToken ct) =>
            Results.File(await print.Pdf(date, ct), "application/pdf", $"summary-{date:yyyy-MM-dd}.pdf"));
        book.MapPost("/summary/print", async (DateOnly date, PrintSummaryRequest r, CashBookPrint print, CancellationToken ct) =>
        {
            await print.PrintReceipt(date, r, ct);
            return Results.NoContent();
        });

        var money = app.MapGroup("/api/admin/money").WithTags("Money").RequireAuthorization();
        money.MapPost("/expenses", (ExpenseRequest r, MoneyActions m, CancellationToken ct) => m.AddExpense(r, ct));
        money.MapPost("/supplier-payments", (SupplierPaymentRequest r, MoneyActions m, CancellationToken ct) => m.PaySupplier(r, ct));
        money.MapPost("/moves", (MoveMoneyRequest r, MoneyActions m, CancellationToken ct) => m.MoveMoney(r, ct));
        money.MapPost("/owner", (OwnerMoneyRequest r, MoneyActions m, CancellationToken ct) => m.OwnerMoney(r, ct));
        money.MapPost("/other-income", (OtherIncomeRequest r, MoneyActions m, CancellationToken ct) => m.OtherIncome(r, ct));
        money.MapGet("/opening-balances", async (MoneyActions m, CancellationToken ct) => new { locked = await m.OpeningLocked(ct) });
        money.MapPost("/opening-balances", (OpeningBalancesRequest r, MoneyActions m, CancellationToken ct) => m.OpeningBalances(r, ct));
        money.MapPost("/{id:guid}/approve", (Guid id, MoneyActions m, CancellationToken ct) => m.Approve(id, ct));
        money.MapPost("/{id:guid}/reject", (Guid id, RejectMoneyRequest r, MoneyActions m, CancellationToken ct) => m.Reject(id, r, ct));
        money.MapPost("/{id:guid}/reverse", (Guid id, ReverseMoneyRequest r, MoneyActions m, CancellationToken ct) => m.Reverse(id, r, ct));
        money.MapPost("/attachments", async (IFormFile file, MoneyAttachments attachments, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            return await attachments.Upload(new FileUpload(file.FileName, file.ContentType, file.Length, stream), ct);
        }).DisableAntiforgery();
        money.MapGet("/{id:guid}/attachment", async (Guid id, MoneyAttachments attachments, CancellationToken ct) =>
            await attachments.Open(id, ct) is { } file ? Results.File(file.Content, file.ContentType, file.FileName) : Results.NotFound());
        money.MapGet("/categories", (ExpenseCategories c, CancellationToken ct) => c.List(ct));
        money.MapPost("/categories", (SaveCategoryRequest r, ExpenseCategories c, CancellationToken ct) => c.Save(null, r, ct));
        money.MapPut("/categories/{id:guid}", (Guid id, SaveCategoryRequest r, ExpenseCategories c, CancellationToken ct) => c.Save(id, r, ct));
        money.MapDelete("/categories/{id:guid}", (Guid id, ExpenseCategories c, CancellationToken ct) => c.Delete(id, ct));

        var accounts = app.MapGroup("/api/admin/accounts").WithTags("Money").RequireAuthorization();
        accounts.MapGet("/", async (DateOnly? date, Accounts a, TimeProvider clock, Tausi.Application.Abstractions.ITausiDb db, CancellationToken ct) =>
            await a.Balances(date ?? await BusinessDates.Today(db, clock, ct), ct));
        accounts.MapGet("/{code}/ledger", (string code, DateOnly from, DateOnly to, Accounts a, CancellationToken ct) => a.Ledger(code, from, to, ct));

        // The shared admin approvals queue (stock and money).
        app.MapGet("/api/admin/awaiting", (AwaitingApproval awaiting, CancellationToken ct) => awaiting.Execute(ct))
            .WithTags("Money").RequireAuthorization();
    }
}
