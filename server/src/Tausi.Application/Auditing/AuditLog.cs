using Tausi.Application.Abstractions;
using Tausi.Application.Common;
using Tausi.Application.Security;
using Tausi.Domain.Audit;

namespace Tausi.Application.Auditing;

public sealed class AuditLog(ITausiDb db, ICurrentSession session, TimeProvider clock) : IAuditLog
{
    public void Write(AuditRecord record)
    {
        db.AuditEntries.Add(new AuditEntry
        {
            At = clock.GetUtcNow(),
            UserId = record.UserId ?? session.User?.UserId,
            TerminalId = record.TerminalId ?? session.User?.TerminalId ?? session.Terminal?.TerminalId,
            Action = record.Action,
            EntityType = record.EntityType,
            EntityId = record.EntityId,
            OldValueJson = record.OldValue is null ? null : Json.Serialize(record.OldValue),
            NewValueJson = record.NewValue is null ? null : Json.Serialize(record.NewValue),
            ApprovedByUserId = record.ApprovedByUserId,
        });
    }
}
