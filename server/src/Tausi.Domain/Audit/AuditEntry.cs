using Tausi.Domain.Common;

namespace Tausi.Domain.Audit;

public class AuditEntry : Entity, ITrainingData
{
    /// <inheritdoc />
    public bool IsTraining { get; set; }

    public DateTimeOffset At { get; set; }
    public Guid? UserId { get; set; }
    public Guid? TerminalId { get; set; }
    public string Action { get; set; } = "";
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? OldValueJson { get; set; }
    public string? NewValueJson { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    /// <summary>Who did it when no user did: "setup" for the first-run wizard.</summary>
    public string? Actor { get; set; }
}
