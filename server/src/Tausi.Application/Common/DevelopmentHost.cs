namespace Tausi.Application.Common;

/// <summary>
/// Whether this server runs in Development. Things that exist only to develop and test Tausi POS (the
/// File printer, the tests' licence key, demo seeding) are refused on an installed server.
/// </summary>
public sealed record DevelopmentHost(bool IsDevelopment);
