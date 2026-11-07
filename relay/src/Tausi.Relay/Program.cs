namespace Tausi.Relay;

public static class Program
{
    /// <summary>
    /// <c>TausiRelay</c> runs the relay; <c>TausiRelay hash-password</c> reads a password from standard
    /// input and prints the hash for <c>Relay:AdminPasswordHash</c>.
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() == "hash-password")
        {
            var password = Console.In.ReadLine() ?? "";
            if (password.Length < 12)
            {
                await Console.Error.WriteLineAsync("Use a password of at least 12 characters.");
                return 1;
            }
            Console.WriteLine(AdminPasswords.Hash(password));
            return 0;
        }

        var app = RelayHost.Build(args);
        await RelayHost.Prepare(app);
        await app.RunAsync();
        return 0;
    }
}
