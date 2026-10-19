namespace Nexagen.Domain.Common;

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
}

/// <summary>
/// An entity people can edit. <see cref="RowVersion"/> is an optimistic concurrency token,
/// bumped on every save so two editors cannot silently overwrite each other.
/// </summary>
public abstract class VersionedEntity : Entity
{
    public long RowVersion { get; set; }
}
