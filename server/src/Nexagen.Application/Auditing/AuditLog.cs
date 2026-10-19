using Nexagen.Application.Abstractions;
using Nexagen.Application.Common;
using Nexagen.Application.Security;
using Nexagen.Domain.Audit;

namespace Nexagen.Application.Auditing;

public sealed class AuditLog(INexagenDb db, ICurrentSession session, TimeProvider clock) : IAuditLog
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
