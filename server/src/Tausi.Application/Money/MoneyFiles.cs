using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Files;
using Tausi.Application.Security;
using Tausi.Domain.Cash;
using Tausi.Domain.Common;
using Tausi.Domain.People;
using Tausi.Domain.Platform;

namespace Tausi.Application.Money;

public sealed record ExpenseCategoryDto(Guid Id, string Name, bool Active, bool InUse, long RowVersion);

public sealed record SaveCategoryRequest(string Name, bool Active, long? RowVersion);

public sealed record AttachmentDto(Guid FileId, string FileName);

public sealed record FileUpload(string FileName, string ContentType, long Length, Stream Content);

/// <summary>Who may see the money pages: cash.book or reports.all (not Not allowed).</summary>
public sealed class MoneyAccess(IPermissionService permissions)
{
    public async Task RequireView(SessionUser user, CancellationToken ct)
    {
        if (await permissions.Check(user, PermissionKeys.CashBook, ct) != PermissionValue.NotAllowed) return;
        if (await permissions.Check(user, PermissionKeys.ReportsAll, ct) != PermissionValue.NotAllowed) return;
        throw new PermissionDeniedException(PermissionKeys.CashBook);
    }

    public async Task RequireCashBook(SessionUser user, CancellationToken ct)
    {
        if (await permissions.Check(user, PermissionKeys.CashBook, ct) == PermissionValue.NotAllowed)
            throw new PermissionDeniedException(PermissionKeys.CashBook);
    }
}

/// <summary>
/// Expense categories (cash.book): add, rename, switch off. One that is in use (an expense, a pay-out
/// or a journal line) can be switched off but not deleted.
/// </summary>
public sealed class ExpenseCategories(ITausiDb db, ICurrentSession session, IPermissionService permissions, IAuditLog audit)
{
    public async Task<IReadOnlyList<ExpenseCategoryDto>> List(CancellationToken ct)
    {
        session.RequireUser();
        var used = await InUse(ct);
        return (await db.ExpenseCategories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct))
            .Select(c => new ExpenseCategoryDto(c.Id, c.Name, c.Active, used.Contains(c.Id), c.RowVersion)).ToList();
    }

    public async Task<IReadOnlyList<ExpenseCategoryDto>> Save(Guid? id, SaveCategoryRequest r, CancellationToken ct)
    {
        await Require(ct);
        var name = (r.Name ?? "").Trim();
        if (name.Length is < 2 or > ExpenseCategory.MaxNameLength) throw new DomainException("Give the category a name.", "name_invalid");
        if (await db.ExpenseCategories.AnyAsync(c => c.Id != id && c.Name.ToLower() == name.ToLower(), ct))
            throw new DomainException($"There is already a category called {name}.", "name_taken");
        ExpenseCategory category;
        if (id is { } existing)
        {
            category = await db.ExpenseCategories.FirstOrDefaultAsync(c => c.Id == existing, ct) ?? throw new NotFoundException("Category not found.");
            if (r.RowVersion is { } v && v != category.RowVersion) throw new ConflictException("Someone else changed this category. Reload and try again.");
        }
        else
        {
            category = new ExpenseCategory { SortOrder = (await db.ExpenseCategories.MaxAsync(c => (int?)c.SortOrder, ct) ?? 0) + 10 };
            db.ExpenseCategories.Add(category);
        }
        category.Name = name;
        category.Active = r.Active;
        audit.Write(new AuditRecord(AuditActions.ExpenseCategoryChanged, nameof(ExpenseCategory), category.Id.ToString(), null, new { name, r.Active }));
        await db.SaveChangesAsync(ct);
        return await List(ct);
    }

    public async Task<IReadOnlyList<ExpenseCategoryDto>> Delete(Guid id, CancellationToken ct)
    {
        await Require(ct);
        var category = await db.ExpenseCategories.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Category not found.");
        if ((await InUse(ct)).Contains(id))
            throw new ConflictException($"{category.Name} is in use. Switch it off instead.", "category_in_use");
        db.ExpenseCategories.Remove(category);
        audit.Write(new AuditRecord(AuditActions.ExpenseCategoryChanged, nameof(ExpenseCategory), id.ToString(), new { category.Name }, null));
        await db.SaveChangesAsync(ct);
        return await List(ct);
    }

    private async Task Require(CancellationToken ct)
    {
        var user = session.RequireUser();
        if (await permissions.Check(user, PermissionKeys.CashBook, ct) != PermissionValue.Allowed)
            throw new PermissionDeniedException(PermissionKeys.CashBook);
    }

    private async Task<HashSet<Guid>> InUse(CancellationToken ct)
    {
        var ids = await db.MoneyDocuments.Where(d => d.CategoryId != null).Select(d => d.CategoryId!.Value)
            .Union(db.DrawerMovements.Where(m => m.CategoryId != null).Select(m => m.CategoryId!.Value))
            .Union(db.JournalLines.Where(l => l.ExpenseCategoryId != null).Select(l => l.ExpenseCategoryId!.Value))
            .ToListAsync(ct);
        return ids.ToHashSet();
    }
}

/// <summary>Expense receipts: a JPG, PNG or PDF up to 5 MB, kept private (only people with cash.book can open them).</summary>
public sealed class MoneyAttachments(ITausiDb db, ICurrentSession session, MoneyAccess access, IPermissionService permissions, IFileStorage files, TimeProvider clock)
{
    public const long MaxBytes = 5 * 1024 * 1024;

    public async Task<AttachmentDto> Upload(FileUpload upload, CancellationToken ct)
    {
        var user = session.RequireUser();
        if (await permissions.Check(user, PermissionKeys.CashExpense, ct) == PermissionValue.NotAllowed)
            throw new PermissionDeniedException(PermissionKeys.CashExpense);
        if (upload.Length is <= 0 or > MaxBytes) throw new DomainException("The receipt must be smaller than 5 MB.", "file_size");
        using var buffer = new MemoryStream();
        await upload.Content.CopyToAsync(buffer, ct);
        if (buffer.Length > MaxBytes) throw new DomainException("The receipt must be smaller than 5 MB.", "file_size");
        var type = Sniff(buffer.GetBuffer().AsSpan(0, (int)Math.Min(buffer.Length, 8)))
            ?? throw new DomainException("The receipt must be a JPG, PNG or PDF.", "file_type");
        var file = new StoredFile
        {
            FileName = Path.GetFileName(upload.FileName), ContentType = type, Size = buffer.Length, CreatedAt = clock.GetUtcNow(), IsPrivate = true,
        };
        buffer.Position = 0;
        await files.Save(file.Id, buffer, ct);
        db.StoredFiles.Add(file);
        await db.SaveChangesAsync(ct);
        return new AttachmentDto(file.Id, file.FileName);
    }

    public async Task<(Stream Content, string ContentType, string FileName)?> Open(Guid documentId, CancellationToken ct)
    {
        await access.RequireCashBook(session.RequireUser(), ct);
        var fileId = await db.MoneyDocuments.Where(d => d.Id == documentId).Select(d => d.AttachmentFileId).FirstOrDefaultAsync(ct);
        if (fileId is not { } id) return null;
        var meta = await db.StoredFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct);
        var stream = meta is null ? null : files.Open(id);
        return stream is null ? null : (stream, meta!.ContentType, meta.FileName);
    }

    /// <summary>What the file really is, from its first bytes.</summary>
    public static string? Sniff(ReadOnlySpan<byte> head) =>
        head.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }) ? "image/jpeg"
        : head.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }) ? "image/png"
        : head.StartsWith("%PDF"u8) ? "application/pdf"
        : null;
}
