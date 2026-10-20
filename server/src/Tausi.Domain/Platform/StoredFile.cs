using Tausi.Domain.Common;

namespace Tausi.Domain.Platform;

/// <summary>A file kept in local storage (e.g. the business logo), served at /files/{id}.</summary>
public class StoredFile : Entity
{
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long Size { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
