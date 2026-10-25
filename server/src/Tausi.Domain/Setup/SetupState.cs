using Tausi.Domain.Common;

namespace Tausi.Domain.Setup;

/// <summary>
/// The first-run setup: one row. The server is in setup mode until <see cref="CompletedAt"/> is set.
/// The setup token (printed in the console and written to setup-token.txt) unlocks the wizard;
/// only its hash is kept. Five wrong tries lock the wizard for five minutes.
/// </summary>
public class SetupState : Entity
{
    public const int MaxTries = 5;
    public static readonly TimeSpan LockTime = TimeSpan.FromMinutes(5);

    public DateTimeOffset? CompletedAt { get; set; }
    public string? TokenHash { get; set; }
    public DateTimeOffset? TokenCreatedAt { get; set; }
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }

    /// <summary>The browser that unlocked the wizard: the hash of its setup session.</summary>
    public string? SessionHash { get; set; }

    /// <summary>"Bar", "Restaurant" or "BarAndRestaurant" (step 1), for the type defaults.</summary>
    public string? BusinessType { get; set; }

    public bool IsComplete => CompletedAt is not null;
}

public enum SetupStepStatus
{
    Done,
    Skipped,
}

/// <summary>A finished (or skipped) wizard step, so the wizard resumes at the next one.</summary>
public class SetupProgress : Entity
{
    public string Step { get; set; } = "";
    public SetupStepStatus Status { get; set; }
    public DateTimeOffset At { get; set; }
    /// <summary>What it did, for the Finish summary ("212 items in 9 categories").</summary>
    public string? Summary { get; set; }
}

public static class SetupSteps
{
    public const string Business = "business";
    public const string Admin = "admin";
    public const string Devices = "devices";
    public const string Menu = "menu";
    public const string Stock = "stock";
    public const string Licence = "licence";

    public static readonly IReadOnlyList<string> All = [Business, Admin, Devices, Menu, Stock, Licence];

    /// <summary>Steps 3, 4 and 5 can be left for later ("I'll do this later").</summary>
    public static readonly IReadOnlySet<string> Skippable = new HashSet<string> { Devices, Menu, Stock };
}
