using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Files;
using Tausi.Application.Printing;
using Tausi.Application.Security;
using Tausi.Domain.Common;
using Tausi.Domain.People;
using Tausi.Domain.Platform;
using Tausi.Domain.Settings;

namespace Tausi.Application.Settings;

public sealed record LogoUpload(string FileName, string ContentType, long Length, Stream Content);

/// <summary>
/// The business logo: a PNG or JPG up to 2 MB. The original is kept for printing, with a copy sized
/// for screens; the printer's 1-bit raster is made again at once. Remove takes the logo off
/// screens, bills and receipts. Both need <c>settings.manage</c> and announce <c>settings.changed</c>.
/// </summary>
public sealed class LogoCommands(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
    IFileStorage files, ILogoImages images, IAuditLog audit, IPosEvents events, TimeProvider clock)
{
    public const long MaxBytes = 2 * 1024 * 1024;
    public static readonly string[] AllowedTypes = ["image/png", "image/jpeg"];

    public async Task<SettingsDto> Upload(LogoUpload upload, CancellationToken ct)
    {
        var grant = await permissions.Require(session.RequireUser(), PermissionKeys.SettingsManage, approval.ApprovalToken, ct);
        if (!AllowedTypes.Contains(upload.ContentType)) throw new DomainException("The logo must be a PNG or JPG image.", "logo_type");
        if (upload.Length is <= 0 or > MaxBytes) throw new DomainException("The logo must be smaller than 2 MB.", "logo_size");

        using var buffer = new MemoryStream();
        await upload.Content.CopyToAsync(buffer, ct);
        if (buffer.Length > MaxBytes) throw new DomainException("The logo must be smaller than 2 MB.", "logo_size");
        var image = images.Read(buffer.ToArray());
        if (image is null || image.ContentType != upload.ContentType)
            throw new DomainException("That file isn't a PNG or JPG picture.", "logo_type");

        var row = await Row(ct);
        var now = clock.GetUtcNow();
        var name = Path.GetFileName(upload.FileName);
        var original = new StoredFile { FileName = name, ContentType = image.ContentType, Size = buffer.Length, CreatedAt = now };
        var web = new StoredFile
        {
            FileName = Path.ChangeExtension(name, null) + "-web.png", ContentType = "image/png", Size = image.WebPng.Length, CreatedAt = now,
        };
        buffer.Position = 0;
        await files.Save(original.Id, buffer, ct);
        await files.Save(web.Id, new MemoryStream(image.WebPng), ct);
        db.StoredFiles.Add(original);
        db.StoredFiles.Add(web);

        var old = row.LogoFileId;
        row.LogoFileId = original.Id;
        row.LogoWebFileId = web.Id;
        return await Saved(row, old, original.Id, grant.ApprovedByUserId, ct);
    }

    public async Task<SettingsDto> Remove(CancellationToken ct)
    {
        var grant = await permissions.Require(session.RequireUser(), PermissionKeys.SettingsManage, approval.ApprovalToken, ct);
        var row = await Row(ct);
        var old = row.LogoFileId;
        if (old is null) return SettingsView.Of(row.RowVersion, SettingsRegistry.Read(row), null);
        row.LogoFileId = null;
        row.LogoWebFileId = null;
        return await Saved(row, old, null, grant.ApprovedByUserId, ct);
    }

    private async Task<BusinessSettings> Row(CancellationToken ct) =>
        await db.BusinessSettings.OrderBy(s => s.Id).FirstOrDefaultAsync(ct)
        ?? throw new NotFoundException("The business has not been set up yet.");

    private async Task<SettingsDto> Saved(BusinessSettings row, Guid? old, Guid? logo, Guid? approvedBy, CancellationToken ct)
    {
        audit.Write(new AuditRecord(AuditActions.LogoChanged, AuditEntities.Setting, SettingsRegistry.BusinessLogo,
            new { logoFileId = old }, new { logoFileId = logo }, approvedBy));
        await db.SaveChangesAsync(ct);
        await images.Refresh(ct);
        await events.Publish(PosEventNames.SettingsChanged, new { keys = new[] { SettingsRegistry.BusinessLogo } }, ct);
        return SettingsView.Of(row.RowVersion, SettingsRegistry.Read(row), row.LogoWebFileId ?? row.LogoFileId);
    }
}

/// <summary>The logo as the printer prints it (1-bit), for the settings screen.</summary>
public sealed class LogoPrintPreview(
    ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval, ILogoProvider logos, ILogoImages images)
{
    public async Task<byte[]?> Execute(int paperWidthMm, CancellationToken ct)
    {
        await permissions.Require(session.RequireUser(), PermissionKeys.SettingsManage, approval.ApprovalToken, ct);
        var bitmap = await logos.GetLogo(ReceiptBuilder.LogoWidthFor(paperWidthMm), ct);
        return bitmap is null ? null : images.ToPng(bitmap);
    }
}
