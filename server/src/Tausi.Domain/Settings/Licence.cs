using Tausi.Domain.Common;

namespace Tausi.Domain.Settings;

public class Licence : VersionedEntity
{
    public string Key { get; set; } = "";
    public string BusinessName { get; set; } = "";
    public int TerminalLimit { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
}
