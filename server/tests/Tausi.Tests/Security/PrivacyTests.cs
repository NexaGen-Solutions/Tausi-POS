using System.Reflection;
using FluentAssertions;

namespace Tausi.Tests.Security;

/// <summary>
/// The admin site and the API never return PIN hashes, password hashes or device tokens: no type the
/// Application or API layers return has a property named like one. The one exception is the pairing
/// claim, which hands a new terminal its own device token once.
/// </summary>
public class PrivacyTests
{
    private static readonly string[] Forbidden = ["PinHash", "PasswordHash", "TokenHash", "SessionHash", "PinLookup", "DeviceToken", "Pepper", "SigningKey"];

    private static readonly HashSet<string> Allowed = ["Tausi.Application.Terminals.PairingClaim.DeviceToken"];

    public static IEnumerable<Type> ReturnedTypes() =>
        new[] { typeof(Application.DependencyInjection).Assembly, typeof(Api.Platform.ServerHost).Assembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsPublic || t.IsNestedPublic)
            .Where(t => !t.IsInterface && !t.IsEnum && !typeof(Exception).IsAssignableFrom(t) && !(t.IsAbstract && t.IsSealed))
            // Records and plain data classes: the shapes that become JSON.
            .Where(t => t.GetMethod("<Clone>$") is not null || t.Name.EndsWith("Dto", StringComparison.Ordinal) || t.Name.EndsWith("Response", StringComparison.Ordinal));

    [Fact]
    public void No_returned_type_carries_a_hash_or_a_device_token()
    {
        var types = ReturnedTypes().ToList();
        types.Should().HaveCountGreaterThan(100, "the scan finds the DTOs");
        var leaks = types
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => (Type: t, Property: p)))
            .Where(x => Forbidden.Any(f => x.Property.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .Select(x => $"{x.Type.FullName?.Replace('+', '.')}.{x.Property.Name}")
            .Where(name => !Allowed.Contains(name))
            .ToList();
        leaks.Should().BeEmpty();
    }
}
