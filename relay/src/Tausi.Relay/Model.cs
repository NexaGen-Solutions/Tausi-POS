using Microsoft.EntityFrameworkCore;

namespace Tausi.Relay;

/// <summary>A business (one bar's server) that uses the relay.</summary>
public sealed class Business
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// <summary>SHA-256 of the API key, hex. The key itself is shown once and never stored.</summary>
    public string ApiKeyHash { get; set; } = "";

    /// <summary>The key's first characters, to tell keys apart on the admin page.</summary>
    public string ApiKeyPrefix { get; set; } = "";

    /// <summary>The unguessable part of this business's callback addresses.</summary>
    public string PathToken { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    /// <summary>When its server last collected payments.</summary>
    public DateTime? LastSeenAt { get; set; }

    public bool Active { get; set; } = true;
}

/// <summary>Where a payment came from.</summary>
public static class PaymentKinds
{
    /// <summary>The result of an STK prompt (paid or not).</summary>
    public const string Stk = "stk";

    /// <summary>A till or paybill payment (also one made by scanning a QR on a bill).</summary>
    public const string C2B = "c2b";
}

/// <summary>
/// One message from Safaricom for one business, held until the business's server acknowledges it,
/// then kept 30 days. <see cref="Key"/> is the M-Pesa transaction id (an STK prompt that wasn't paid
/// has none, so it is "stk:" and its checkout request id): a repeated callback is stored once.
/// </summary>
public sealed class RelayPayment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BusinessId { get; set; }
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? TransactionId { get; set; }
    public string? CheckoutRequestId { get; set; }
    public string? MerchantRequestId { get; set; }

    /// <summary>STK: Daraja's result code (0 = paid) and its words.</summary>
    public int? ResultCode { get; set; }
    public string? ResultDesc { get; set; }

    public decimal Amount { get; set; }
    public string? PayerName { get; set; }

    /// <summary>Masked ("0712 ••• 678"); the full number is never stored.</summary>
    public string? Phone { get; set; }

    public string? ShortCode { get; set; }

    /// <summary>C2B: the account number or reference ("BillRefNumber").</summary>
    public string? BillRef { get; set; }

    public string? TransactionType { get; set; }

    /// <summary>When Safaricom says it was paid (or answered), if it said.</summary>
    public DateTime? PaidAt { get; set; }

    public DateTime ReceivedAt { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
}

public sealed class RelayDb(DbContextOptions<RelayDb> options) : DbContext(options)
{
    public DbSet<Business> Businesses => Set<Business>();
    public DbSet<RelayPayment> Payments => Set<RelayPayment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Business>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.ApiKeyHash).HasMaxLength(64);
            e.Property(x => x.ApiKeyPrefix).HasMaxLength(16);
            e.Property(x => x.PathToken).HasMaxLength(64);
            e.HasIndex(x => x.ApiKeyHash).IsUnique();
            e.HasIndex(x => x.PathToken).IsUnique();
        });
        b.Entity<RelayPayment>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(80);
            e.Property(x => x.Kind).HasMaxLength(8);
            e.Property(x => x.TransactionId).HasMaxLength(20);
            e.Property(x => x.CheckoutRequestId).HasMaxLength(80);
            e.Property(x => x.MerchantRequestId).HasMaxLength(80);
            e.Property(x => x.ResultDesc).HasMaxLength(300);
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.PayerName).HasMaxLength(120);
            e.Property(x => x.Phone).HasMaxLength(40);
            e.Property(x => x.ShortCode).HasMaxLength(20);
            e.Property(x => x.BillRef).HasMaxLength(40);
            e.Property(x => x.TransactionType).HasMaxLength(40);
            e.HasIndex(x => new { x.BusinessId, x.Key }).IsUnique();
            e.HasIndex(x => new { x.BusinessId, x.AcknowledgedAt });
            e.HasOne<Business>().WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
