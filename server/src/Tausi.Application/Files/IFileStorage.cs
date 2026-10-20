namespace Tausi.Application.Files;

/// <summary>Local file storage on the server (logos and other uploads).</summary>
public interface IFileStorage
{
    Task Save(Guid fileId, Stream content, CancellationToken ct = default);

    /// <summary>Returns null when the file is missing.</summary>
    Stream? Open(Guid fileId);
}
