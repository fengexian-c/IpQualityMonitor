using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IpQualityMonitor.Application;

namespace IpQualityMonitor.Web;

public sealed record AdminHash(int SchemaVersion, int Iterations, string Salt, string Hash, string? Username = null);
public sealed class AdminCredentials
{
    private const int MaximumFileBytes = 4096;
    private readonly AdminHash _record;
    private readonly byte[] _salt;
    private readonly byte[] _hash;
    public string Username { get; }
    public AdminCredentials(ServerStorage storage, IConfiguration configuration)
    {
        // ServerStorage already owns the exclusive dataset lease. Only a missing auth file
        // means bootstrap; malformed/unreadable existing credentials must never be replaced.
        var path = Path.Combine(storage.DirectoryPath, "admin-auth.json");
        byte[]? saved;
        try { saved = ReadBounded(path); }
        catch (FileNotFoundException) { saved = null; }
        if (saved is not null)
        {
            try
            {
                // Match the legacy text reader's BOM handling without rewriting the file.
                using var reader = new StreamReader(new MemoryStream(saved), Encoding.UTF8, true);
                _record = JsonSerializer.Deserialize<AdminHash>(reader.ReadToEnd()) ?? throw InvalidCredentials();
            }
            catch (JsonException) { throw InvalidCredentials(); }
        }
        else
        {
            var username = configuration["IPQUALITY_ADMIN_USERNAME"] ?? "admin";
            if (!ValidUsername(username))
                throw new InvalidDataException("IPQUALITY_ADMIN_USERNAME 须为 1–64 个 ASCII 字母、数字、点、下划线或连字符。");
            var password = configuration["IPQUALITY_ADMIN_PASSWORD"];
            var file = configuration["IPQUALITY_ADMIN_PASSWORD_FILE"];
            // Presence, not truthiness: an explicitly blank setting still counts as a source.
            if ((password is null) == (file is null))
                throw new InvalidOperationException("首次启动须且只能设置 IPQUALITY_ADMIN_PASSWORD 或 IPQUALITY_ADMIN_PASSWORD_FILE 其中之一。");
            if (file is not null)
            {
                try
                {
                    var bytes = ReadBounded(file);
                    try
                    {
                        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
                        password = reader.ReadToEnd().TrimEnd('\r', '\n');
                    }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                { throw new InvalidDataException("无法读取 IPQUALITY_ADMIN_PASSWORD_FILE，或密码文件超过 4096 字节。"); }
            }
            if (password is null || password.Length is < 16 or > 256)
                throw new InvalidDataException("管理员密码须为 16–256 个字符。");
            var salt = RandomNumberGenerator.GetBytes(32);
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210000, HashAlgorithmName.SHA512, 32);
            _record = new(2, 210000, Convert.ToBase64String(salt), Convert.ToBase64String(hash), username);
            WriteInitial(path, _record);
        }
        if (_record.SchemaVersion is not (1 or 2) || _record.Iterations is < 100000 or > 1000000 ||
            _record.Salt is null || _record.Hash is null ||
            (_record.SchemaVersion == 2 && !ValidUsername(_record.Username)))
            throw InvalidCredentials();
        Username = _record.SchemaVersion == 1 ? "admin" : _record.Username!;
        try
        {
            _salt = Convert.FromBase64String(_record.Salt);
            _hash = Convert.FromBase64String(_record.Hash);
        }
        catch (FormatException) { throw InvalidCredentials(); }
        if (_salt.Length != 32 || _hash.Length != 32) throw InvalidCredentials();
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    // Keep legacy cookie stamps unchanged; new stamps also bind the stored identity.
    public string SecurityStamp => _record.SchemaVersion == 1 ? _record.Salt :
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("v2\n" + Username + "\n" + _record.Salt)));
    public bool Verify(string? username, string? password)
    {
        if (username is null || username.Length is < 1 or > 64 || password is null || password.Length is < 1 or > 256)
            return false;
        var computed = Rfc2898DeriveBytes.Pbkdf2(password, _salt,
            _record.Iterations, HashAlgorithmName.SHA512, 32);
        try
        {
            var passwordMatches = CryptographicOperations.FixedTimeEquals(computed, _hash);
            var usernameMatches = string.Equals(username, Username, StringComparison.Ordinal);
            return passwordMatches & usernameMatches;
        }
        finally { CryptographicOperations.ZeroMemory(computed); }
    }
    private static bool ValidUsername(string? username) => username is { Length: >= 1 and <= 64 } &&
        username.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');
    private static InvalidDataException InvalidCredentials() => new("管理员凭据格式无效或不支持；请保留原文件并离线恢复。");
    private static byte[] ReadBounded(string path)
    {
        // Bound the read itself, not just FileInfo: a file can grow between stat and read.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumFileBytes) throw InvalidCredentials();
        var buffer = new byte[MaximumFileBytes + 1];
        int count = 0, read;
        while (count < buffer.Length && (read = stream.Read(buffer, count, buffer.Length - count)) != 0) count += read;
        if (count > MaximumFileBytes) throw InvalidCredentials();
        var result = buffer.AsSpan(0, count).ToArray();
        CryptographicOperations.ZeroMemory(buffer);
        return result;
    }
    private static void WriteInitial(string path, AdminHash value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            { JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true); }
            File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
