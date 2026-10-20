using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Files;
using Tausi.Application.Security;
using Tausi.Domain.Common;
using Tausi.Domain.People;
using Tausi.Domain.Platform;
using Tausi.Domain.Settings;

namespace Tausi.Application.Settings;

public sealed record LogoUpload(string FileName, string ContentType, long Length, Stream Content);

public sealed class UploadBusinessLogo(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
    IFileStorage files, IAuditLog audit, IPosEvents events, TimeProvider clock)
{
    public const long MaxBytes = 2 * 1024 * 1024;
    public static readonly string[] AllowedTypes = ["image/png", "image/jpeg", "image/webp"];

    public async Task<BusinessSettingsDto> Execute(LogoUpload upload, CancellationToken ct)
    {
        var grant = await permissions.Require(session.RequireUser(), PermissionKeys.SettingsManage, approval.ApprovalToken, ct);
        if (!AllowedTypes.Contains(upload.ContentType))
            throw new DomainException("The logo must be a PNG, JPEG or WebP image.");
        if (upload.Length is <= 0 or > MaxBytes)
            throw new DomainException("The logo must be smaller than 2 MB.");

        var settings = await db.BusinessSettings.FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("The business has not been set up yet.");

        var file = new StoredFile
        {
            FileName = Path.GetFileName(upload.FileName),
            ContentType = upload.ContentType,
            Size = upload.Length,
            CreatedAt = clock.GetUtcNow(),
        };
        await files.Save(file.Id, upload.Content, ct);
        db.StoredFiles.Add(file);

        var oldLogo = settings.LogoFileId;
        settings.LogoFileId = file.Id;
        audit.Write(new AuditRecord(
            AuditActions.LogoChanged, nameof(BusinessSettings), settings.Id.ToString(),
            OldValue: new { logoFileId = oldLogo }, NewValue: new { logoFileId = file.Id },
            ApprovedByUserId: grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
        await events.Publish(PosEventNames.SettingsChanged, new { }, ct);
        return BusinessSettingsDto.From(settings);
    }
}
