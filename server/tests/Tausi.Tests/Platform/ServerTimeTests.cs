using FluentAssertions;

namespace Tausi.Tests.Platform;

/// <summary>
/// Task 16, Part C.7: a terminal with a wrong clock changes nothing, because no request from a terminal
/// or the admin site carries a time: the server stamps every ticket, line, payment and print with its own
/// clock (TimeProvider). Calendar dates the admin picks (report ranges, an expense's day) are DateOnly.
/// </summary>
public class ServerTimeTests
{
    [Fact]
    public void No_request_carries_a_time_of_day_from_the_client()
    {
        var requests = typeof(Tausi.Application.DependencyInjection).Assembly.GetTypes()
            .Where(t => t.Name.EndsWith("Request", StringComparison.Ordinal) && !t.IsAbstract)
            .ToList();
        requests.Should().HaveCountGreaterThan(40, "it looks at every request the API takes");

        var times = requests
            .SelectMany(t => t.GetProperties().Select(p => (Type: t, Property: p)))
            .Where(x => (Nullable.GetUnderlyingType(x.Property.PropertyType) ?? x.Property.PropertyType) is var type
                        && (type == typeof(DateTime) || type == typeof(DateTimeOffset)))
            .Select(x => $"{x.Type.Name}.{x.Property.Name}")
            .ToList();

        times.Should().BeEmpty("the server's clock is the only clock");
    }
}
