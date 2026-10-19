namespace Nexagen.Domain.Common;

/// <summary>A business rule was broken. The API turns this into a 400 problem response.</summary>
public class DomainException(string message) : Exception(message);
