using System.Globalization;
using Tausi.Licensing;

namespace Tausi.LicenseTool;

// NexaGen's licence tool (internal; never shipped to customers).
//   generate-keypair <folder>                     a new key pair: private.key (keep offline) and public.key
//   issue <private.key> --business "Name" --terminals 4 [--edition Standard] [--expires 2027-12-31|never]
//         [--support-until 2027-12-31] [--id ID]  prints a licence key
//   verify <public.key or base64> <key>          checks a key and prints what it says
public static class LicenceTool
{
    public static int Main(string[] args) => Run(args);

    public static int Run(string[] args)
    {
        try
        {
            return args.FirstOrDefault() switch
            {
                "generate-keypair" when args.Length == 2 => GenerateKeyPair(args[1]),
                "issue" when args.Length >= 2 => Issue(args[1], Options(args.Skip(2).ToArray())),
                "verify" when args.Length == 3 => Verify(args[1], args[2]),
                _ => Usage(),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            Usage:
              tausi-licence generate-keypair <folder>
              tausi-licence issue <private.key> --business "Mau's Village" --terminals 4 [--edition Standard]
                                  [--expires 2027-12-31|never] [--support-until 2027-12-31] [--id ID]
              tausi-licence verify <public.key|base64> <key>
            """);
        return 2;
    }

    private static int GenerateKeyPair(string folder)
    {
        Directory.CreateDirectory(folder);
        var privatePath = Path.Combine(folder, "private.key");
        if (File.Exists(privatePath)) throw new IOException($"{privatePath} already exists. Never overwrite a key that has issued licences.");
        var (privateKey, publicKey) = LicenceKeys.NewKeyPair();
        File.WriteAllText(privatePath, Convert.ToBase64String(privateKey) + Environment.NewLine);
        File.WriteAllText(Path.Combine(folder, "public.key"), Convert.ToBase64String(publicKey) + Environment.NewLine);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(privatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Console.WriteLine($"Private key: {privatePath} (keep it offline and backed up; never commit it)");
        Console.WriteLine($"Public key (goes in the server): {Convert.ToBase64String(publicKey)}");
        return 0;
    }

    private static Dictionary<string, string> Options(string[] rest)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < rest.Length; i++)
        {
            if (!rest[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= rest.Length) throw new ArgumentException($"Expected --option value at \"{rest[i]}\".");
            options[rest[i][2..]] = rest[++i];
        }
        return options;
    }

    private static int Issue(string privatePath, Dictionary<string, string> o)
    {
        var privateKey = Convert.FromBase64String(File.ReadAllText(privatePath).Trim());
        if (!o.TryGetValue("business", out var business) || business.Trim().Length == 0) throw new ArgumentException("--business is required.");
        if (!o.TryGetValue("terminals", out var terminalsText) || !int.TryParse(terminalsText, out var terminals) || terminals < 1)
            throw new ArgumentException("--terminals must be 1 or more.");
        DateTimeOffset? Date(string name) =>
            !o.TryGetValue(name, out var text) || text == "never"
                ? null
                : new DateTimeOffset(DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(new TimeOnly(23, 59, 59)), TimeSpan.Zero);
        var payload = new LicencePayload(
            o.GetValueOrDefault("id") ?? $"TL-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
            business.Trim(), o.GetValueOrDefault("edition") ?? "Standard", terminals, DateTimeOffset.UtcNow, Date("expires"), Date("support-until"));
        Console.WriteLine(LicenceKeys.Issue(payload, privateKey));
        return 0;
    }

    private static int Verify(string publicKey, string key)
    {
        var bytes = Convert.FromBase64String(File.Exists(publicKey) ? File.ReadAllText(publicKey).Trim() : publicKey);
        var problem = LicenceKeys.TryRead(key, [bytes], out var payload);
        if (problem != LicenceKeyProblem.None)
        {
            Console.Error.WriteLine($"Not valid: {problem}");
            return 1;
        }
        Console.WriteLine($"Valid: {payload}");
        return 0;
    }
}
