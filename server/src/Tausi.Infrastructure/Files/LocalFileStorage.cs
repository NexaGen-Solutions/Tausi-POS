using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Tausi.Application.Files;

namespace Tausi.Infrastructure.Files;

/// <summary>Files in &lt;data&gt;/files/&lt;id&gt;. Metadata (name, type) is in the StoredFiles table.</summary>
public sealed class LocalFileStorage(IOptions<TausiOptions> options, IHostEnvironment env) : IFileStorage
{
    private readonly string _root = Path.Combine(
        Path.GetFullPath(options.Value.DataDirectory, env.ContentRootPath), "files");

    public async Task Save(Guid fileId, Stream content, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_root);
        await using var file = File.Create(PathFor(fileId));
        await content.CopyToAsync(file, ct);
    }

    public Stream? Open(Guid fileId)
    {
        var path = PathFor(fileId);
        return File.Exists(path) ? File.OpenRead(path) : null;
    }

    private string PathFor(Guid fileId) => Path.Combine(_root, fileId.ToString("N"));
}
