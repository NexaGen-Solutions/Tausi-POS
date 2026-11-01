using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Tausi.Domain.Printing;

namespace Tausi.Infrastructure.Printing;

/// <summary>
/// Wakes the <see cref="PrintDispatcher"/> as soon as print jobs are committed (after the save, or after
/// its transaction commits), so a slip starts printing at once instead of at the dispatcher's next look.
/// </summary>
public sealed class PrintWakeUp : ISaveChangesInterceptor, IDbTransactionInterceptor
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    /// <summary>Contexts whose saved print jobs wait for their transaction to commit.</summary>
    private readonly ConditionalWeakTable<DbContext, object> _waiting = new();

    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Note(eventData.Context);
        return result;
    }

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Note(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Saved(eventData.Context);
        return result;
    }

    public ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Saved(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Committed(eventData.Context);

    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Committed(eventData.Context);
        return Task.CompletedTask;
    }

    private void Note(DbContext? context)
    {
        if (context is not null && context.ChangeTracker.Entries<PrintJob>().Any(e => e.State == EntityState.Added))
            _waiting.AddOrUpdate(context, true);
    }

    private void Saved(DbContext? context)
    {
        // Outside a transaction the jobs are visible now; inside one, when it commits.
        if (context is not null && context.Database.CurrentTransaction is null && _waiting.Remove(context)) Wake();
    }

    private void Committed(DbContext? context)
    {
        if (context is not null && _waiting.Remove(context)) Wake();
    }

    /// <summary>Wakes the dispatcher (one wake-up waiting is enough).</summary>
    public void Wake()
    {
        if (_signal.CurrentCount > 0) return;
        try { _signal.Release(); }
        catch (SemaphoreFullException) { }
    }

    /// <summary>Waits until woken, or <paramref name="timeout"/> at most.</summary>
    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}
