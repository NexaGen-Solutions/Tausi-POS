namespace Tausi.Domain.Common;

/// <summary>
/// A business rule was broken. The API turns this into a 400 problem response; <see cref="Code"/>,
/// when set, is a stable machine-readable reason (e.g. "pin_taken") for the UI.
/// </summary>
public class DomainException(string message, string? code = null) : Exception(message)
{
    public string? Code { get; } = code;
}
