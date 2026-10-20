using Tausi.Domain.Common;

namespace Tausi.Domain.People;

/// <summary>One wrong PIN typed on a terminal, kept for the lockout rule.</summary>
public class PinFailure : Entity
{
    public Guid TerminalId { get; set; }
    public DateTimeOffset At { get; set; }
}
