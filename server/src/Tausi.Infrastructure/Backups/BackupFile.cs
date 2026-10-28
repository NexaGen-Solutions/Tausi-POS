using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tausi.Application.Backups;
using Tausi.Application.Common;
using Tausi.Domain.Common;

namespace Tausi.Infrastructure.Backups;

public sealed record BackupManifestFile(string Path, long Size, string Sha256);

/// <summary>manifest.json inside every .tausibak.</summary>
public sealed record BackupManifest(
    int Format,
    string Product,
    string Version,
    string Schema,
    string Provider,
    string Business,
    DateTimeOffset CreatedAt,
    BackupReason Reason,
    IReadOnlyList<BackupManifestFile> Files)
{
    public const int CurrentFormat = 1;
    public const string FileName = "manifest.json";
}

/// <summary>
/// A .tausibak is one zip: manifest.json, database/ (the database backup), data/ (the data folder:
/// keys, PIN pepper, logo and uploads) and config/. The manifest holds a SHA-256 for every other
/// file. With a password the whole zip is encrypted: AES-256-CBC with an HMAC-SHA256 over the result,
/// keys from the password by PBKDF2-SHA256. The HMAC is checked before anything is decrypted.
/// </summary>
public static class BackupFile
{
    public const string Extension = ".tausibak";
    public const string DatabaseFolder = "database/";
    public const string DataFolder = "data/";
    public const string ConfigFolder = "config/";

    private static readonly byte[] Magic = "TAUSIENC1"u8.ToArray();
    private const int Iterations = 600_000;
    private const int SaltSize = 16;
    private const int MacSize = 32;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // The manifest is for people too: "Joe's Bar", not "Joe\u0027s Bar".
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>Writes the zip: each (entry name, source file) plus the manifest listing their checksums.</summary>
    public static async Task Write(string path, BackupManifest header, IEnumerable<(string Entry, string Source)> files, CancellationToken ct)
    {
        var listed = new List<BackupManifestFile>();
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (entry, source) in files)
            {
                ct.ThrowIfCancellationRequested();
                var zipEntry = zip.CreateEntry(entry, entry.StartsWith(DatabaseFolder, StringComparison.Ordinal) ? CompressionLevel.Fastest : CompressionLevel.Optimal);
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                await using var output = zipEntry.Open();
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                int read;
                long size = 0;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    size += read;
                }
                listed.Add(new BackupManifestFile(entry, size, Convert.ToHexStringLower(sha.GetHashAndReset())));
            }
            var manifest = zip.CreateEntry(BackupManifest.FileName);
            await using var m = manifest.Open();
            await JsonSerializer.SerializeAsync(m, header with { Files = listed }, Json, ct);
        }
    }

    public static bool IsEncrypted(string path)
    {
        using var stream = File.OpenRead(path);
        var head = new byte[Magic.Length];
        return stream.Read(head) == head.Length && head.AsSpan().SequenceEqual(Magic);
    }

    /// <summary>Encrypts the zip at <paramref name="source"/> into <paramref name="target"/>.</summary>
    public static async Task Encrypt(string source, string target, string password, CancellationToken ct)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var (encKey, macKey) = Keys(password, salt);
        using var aes = Aes.Create();
        aes.Key = encKey;
        aes.GenerateIV();
        await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            await output.WriteAsync(Magic, ct);
            await output.WriteAsync(salt, ct);
            await output.WriteAsync(aes.IV, ct);
            await using (var input = File.OpenRead(source))
            await using (var crypto = new CryptoStream(output, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true))
                await input.CopyToAsync(crypto, ct);
            var mac = await Mac(output, output.Length, macKey, ct);
            output.Seek(0, SeekOrigin.End);
            await output.WriteAsync(mac, ct);
        }
    }

    /// <summary>Checks the password (the HMAC) and decrypts into <paramref name="target"/>.</summary>
    public static async Task Decrypt(string source, string target, string? password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password))
            throw new DomainException("This backup is encrypted. Enter its password.", BackupCodes.WrongPassword);
        await using var input = File.OpenRead(source);
        if (input.Length < Magic.Length + SaltSize + 16 + MacSize) throw Invalid();
        var header = new byte[Magic.Length + SaltSize + 16];
        await input.ReadExactlyAsync(header, ct);
        var salt = header[Magic.Length..(Magic.Length + SaltSize)];
        var iv = header[(Magic.Length + SaltSize)..];
        var (encKey, macKey) = Keys(password, salt);

        var expected = new byte[MacSize];
        input.Seek(-MacSize, SeekOrigin.End);
        await input.ReadExactlyAsync(expected, ct);
        var actual = await Mac(input, input.Length - MacSize, macKey, ct);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new DomainException("That password doesn't open this backup (or the file is damaged).", BackupCodes.WrongPassword);

        using var aes = Aes.Create();
        aes.Key = encKey;
        aes.IV = iv;
        input.Seek(header.Length, SeekOrigin.Begin);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
        await using var body = new SubStream(input, input.Length - MacSize - header.Length);
        await using var crypto = new CryptoStream(body, aes.CreateDecryptor(), CryptoStreamMode.Read);
        await crypto.CopyToAsync(output, ct);
    }

    /// <summary>Reads the manifest and checks every file against its checksum. Throws when anything is missing or changed.</summary>
    public static async Task<BackupManifest> Check(string zipPath, CancellationToken ct)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var manifest = await ReadManifest(zip, ct);
            foreach (var file in manifest.Files)
            {
                var entry = zip.GetEntry(file.Path) ?? throw Invalid($"{file.Path} is missing.");
                await using var stream = entry.Open();
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
                if (hash != file.Sha256) throw Invalid($"{file.Path} doesn't match its checksum.");
            }
            return manifest;
        }
        catch (InvalidDataException e)
        {
            throw Invalid(e.Message);
        }
    }

    public static async Task<BackupManifest> ReadManifest(ZipArchive zip, CancellationToken ct)
    {
        var entry = zip.GetEntry(BackupManifest.FileName) ?? throw Invalid("It has no manifest.");
        await using var stream = entry.Open();
        var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, Json, ct) ?? throw Invalid("Its manifest is empty.");
        if (manifest.Format > BackupManifest.CurrentFormat)
            throw new DomainException($"This backup was made by a newer Tausi POS ({manifest.Version}). Update this server first.", BackupCodes.Invalid);
        return manifest;
    }

    private static DomainException Invalid(string? why = null) =>
        new(("This isn't a readable Tausi POS backup. " + why).Trim(), BackupCodes.Invalid);

    private static (byte[] Enc, byte[] Mac) Keys(string password, byte[] salt)
    {
        var bytes = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 64);
        return (bytes[..32], bytes[32..]);
    }

    private static async Task<byte[]> Mac(Stream stream, long length, byte[] key, CancellationToken ct)
    {
        stream.Seek(0, SeekOrigin.Begin);
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        var buffer = new byte[81920];
        var left = length;
        while (left > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct);
            if (read == 0) break;
            hmac.AppendData(buffer, 0, read);
            left -= read;
        }
        return hmac.GetHashAndReset();
    }

    /// <summary>The first <c>length</c> bytes from the stream's current position.</summary>
    private sealed class SubStream(Stream inner, long size) : Stream
    {
        private readonly long _size = size;
        private long _left = size;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _size;
        public override long Position { get => _size - _left; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_left <= 0) return 0;
            var read = inner.Read(buffer, offset, (int)Math.Min(count, _left));
            _left -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_left <= 0) return 0;
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _left)], ct);
            _left -= read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
