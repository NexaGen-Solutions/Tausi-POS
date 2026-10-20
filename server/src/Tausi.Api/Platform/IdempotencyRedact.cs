namespace Tausi.Api.Platform;

/// <summary>
/// Endpoint metadata: top-level JSON fields to blank in the copy the idempotency store keeps for
/// replays. Used for answers carrying a one-time secret (a temporary PIN), which must never be
/// stored. A replay then answers with the field set to null.
/// </summary>
public sealed record IdempotencyRedact(params string[] Fields);

public static class IdempotencyRedactExtensions
{
    public static TBuilder RedactOnReplay<TBuilder>(this TBuilder builder, params string[] fields)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new IdempotencyRedact(fields));
}
