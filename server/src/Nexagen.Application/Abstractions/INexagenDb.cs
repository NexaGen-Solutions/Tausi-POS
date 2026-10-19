using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Nexagen.Domain.Audit;
using Nexagen.Domain.Devices;
using Nexagen.Domain.Ledger;
using Nexagen.Domain.People;
using Nexagen.Domain.Platform;
using Nexagen.Domain.Printing;
using Nexagen.Domain.Service;
using Nexagen.Domain.Settings;

namespace Nexagen.Application.Abstractions;

/// <summary>The database as the use cases see it. Implemented by the EF Core context in Infrastructure.</summary>
public interface INexagenDb
{
    DbSet<BusinessSettings> BusinessSettings { get; }
    DbSet<Licence> Licences { get; }
    DbSet<Role> Roles { get; }
    DbSet<RolePermission> RolePermissions { get; }
    DbSet<User> Users { get; }
    DbSet<ApprovalGrant> ApprovalGrants { get; }
    DbSet<PinFailure> PinFailures { get; }
    DbSet<UserSession> UserSessions { get; }
    DbSet<WorkPeriod> WorkPeriods { get; }
    DbSet<Terminal> Terminals { get; }
    DbSet<TerminalPairing> TerminalPairings { get; }
    DbSet<Printer> Printers { get; }
    DbSet<AuditEntry> AuditEntries { get; }
    DbSet<IdempotencyRecord> IdempotencyRecords { get; }
    DbSet<StoredFile> StoredFiles { get; }
    DbSet<PrintJob> PrintJobs { get; }
    DbSet<LedgerAccount> LedgerAccounts { get; }
    DbSet<JournalEntry> JournalEntries { get; }
    DbSet<JournalLine> JournalLines { get; }

    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
