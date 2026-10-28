using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Tausi.Domain.Common;
using Tausi.Domain.Devices;
using Tausi.Domain.People;
using Tausi.Domain.Platform;
using Tausi.Domain.Printing;

namespace Tausi.Infrastructure.Persistence;

/// <summary>
/// Training mode. A request from a training terminal sets <see cref="TrainingMode"/>; every
/// <see cref="ITrainingData"/> table then shows that request only training rows, and every other request
/// (reports, cash-ups, the admin site, background work) only real ones. Saving in training mode keeps
/// the rows it may write (training data and a few housekeeping tables) and drops everything else, so
/// the ledger, stock and settings can't change from a training terminal, whatever the code does.
/// </summary>
public partial class TausiDbContext
{
    /// <summary>True for the requests of a training terminal (set by the device-token middleware).</summary>
    public bool TrainingMode { get; set; }

    /// <summary>What a training terminal may change besides training data: sessions, sign-in limits, printing, its own status.</summary>
    private static readonly HashSet<Type> AllowedInTraining =
    [
        typeof(UserSession), typeof(PinFailure), typeof(ApprovalGrant), typeof(IdempotencyRecord),
        typeof(PrintJob), typeof(Printer), typeof(Terminal), typeof(User),
    ];

    private void ConfigureTraining(ModelBuilder m)
    {
        var filter = typeof(TausiDbContext).GetMethod(nameof(TrainingFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        foreach (var type in m.Model.GetEntityTypes().Select(t => t.ClrType).Where(t => typeof(ITrainingData).IsAssignableFrom(t)).ToList())
            filter.MakeGenericMethod(type).Invoke(this, [m]);
    }

    private void TrainingFilter<T>(ModelBuilder m) where T : class, ITrainingData =>
        m.Entity<T>().HasQueryFilter(x => x.IsTraining == TrainingMode);

    /// <summary>Marks new rows as training or real, and in training mode drops what training may not change.</summary>
    private void KeepTrainingApart()
    {
        foreach (var entry in ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList())
        {
            if (entry.Entity is ITrainingData data)
            {
                if (entry.State == EntityState.Added) data.IsTraining = TrainingMode;
                else if (data.IsTraining != TrainingMode) Drop(entry);
            }
            else if (TrainingMode && !AllowedInTraining.Contains(entry.Metadata.ClrType))
            {
                Drop(entry);
            }
        }
    }

    private static void Drop(EntityEntry entry)
    {
        if (entry.State == EntityState.Added)
        {
            entry.State = EntityState.Detached;
            return;
        }
        entry.CurrentValues.SetValues(entry.OriginalValues);
        entry.State = EntityState.Unchanged;
    }
}
