using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Files;
using Tausi.Application.Printing;
using Tausi.Domain.Common;
using Tausi.Domain.Menu;
using Tausi.Domain.Platform;

namespace Tausi.Application.Menu;

public sealed record ItemImageUpload(string FileName, string ContentType, long Length, Stream Content);

/// <summary>
/// An item's picture for the order screen: a PNG or JPG up to 2 MB, kept as a PNG at most 512 px
/// (the same check and copy as the logo). Needs menu.edit; audited; terminals refetch the menu.
/// </summary>
public sealed class ItemImages(ITausiDb db, MenuAccess access, IFileStorage files, ILogoImages images, IAuditLog audit, TimeProvider clock)
{
    public const long MaxBytes = 2 * 1024 * 1024;
    public static readonly string[] AllowedTypes = ["image/png", "image/jpeg"];

    public async Task<SavedRef> Upload(Guid itemId, ItemImageUpload upload, CancellationToken ct)
    {
        var grant = await access.RequireEdit(ct);
        if (!AllowedTypes.Contains(upload.ContentType))
            throw new DomainException("The picture must be a PNG or JPG image.", MenuCodes.ImageType);
        if (upload.Length is <= 0 or > MaxBytes)
            throw new DomainException("The picture must be smaller than 2 MB.", MenuCodes.ImageSize);
        using var buffer = new MemoryStream();
        await upload.Content.CopyToAsync(buffer, ct);
        if (buffer.Length > MaxBytes) throw new DomainException("The picture must be smaller than 2 MB.", MenuCodes.ImageSize);
        var image = images.Read(buffer.ToArray());
        if (image is null || image.ContentType != upload.ContentType)
            throw new DomainException("That file isn't a PNG or JPG picture.", MenuCodes.ImageType);

        var item = await Item(itemId, ct);
        var file = new StoredFile
        {
            FileName = Path.ChangeExtension(Path.GetFileName(upload.FileName), null) + "-tile.png",
            ContentType = "image/png", Size = image.WebPng.Length, CreatedAt = clock.GetUtcNow(),
        };
        await files.Save(file.Id, new MemoryStream(image.WebPng), ct);
        db.StoredFiles.Add(file);
        return await Saved(item, file.Id, grant.ApprovedByUserId, ct);
    }

    public async Task<SavedRef> Remove(Guid itemId, CancellationToken ct)
    {
        var grant = await access.RequireEdit(ct);
        var item = await Item(itemId, ct);
        return item.ImageFileId is null ? new SavedRef(item.Id, item.RowVersion) : await Saved(item, null, grant.ApprovedByUserId, ct);
    }

    private async Task<MenuItem> Item(Guid id, CancellationToken ct) =>
        await db.MenuItems.FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException("Item not found.");

    private async Task<SavedRef> Saved(MenuItem item, Guid? image, Guid? approvedBy, CancellationToken ct)
    {
        audit.Write(new AuditRecord(AuditActions.MenuItemChanged, nameof(MenuItem), item.Id.ToString(),
            new { item.Name, imageFileId = item.ImageFileId }, new { item.Name, imageFileId = image }, approvedBy));
        item.ImageFileId = image;
        await MenuAccess.Save(db, ct);
        await access.Changed(ct);
        return new SavedRef(item.Id, item.RowVersion);
    }
}
