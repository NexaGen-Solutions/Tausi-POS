using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Tausi.Domain.Platform;

namespace Tausi.Infrastructure.Persistence;

/// <summary>
/// Marks a request's Idempotency-Key as having run, in the same save (so the same transaction) as the
/// request's own changes: if they commit so does the mark, and if they roll back so does it. After a
/// failure the mark tells a request that did its work from one that didn't.
/// </summary>
public sealed class IdempotencyMark : SaveChangesInterceptor
{
    /// <summary>The key's record while its request runs; set by the idempotency middleware.</summary>
    public Guid? RecordId { get; set; }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Mark(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Mark(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Mark(DbContext? context)
    {
        if (context is null || RecordId is not { } id) return;
        var changes = context.ChangeTracker.Entries().Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
        if (!changes) return;
        var entry = context.ChangeTracker.Entries<IdempotencyRecord>().FirstOrDefault(e => e.Entity.Id == id)
            ?? context.Attach(new IdempotencyRecord { Id = id });
        entry.Entity.Ran = true;
        entry.Property(r => r.Ran).IsModified = true;
    }
}
