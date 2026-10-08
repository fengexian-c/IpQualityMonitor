using System.Security.Cryptography;
using IpQualityMonitor.Application;

namespace IpQualityMonitor.Web;

public sealed record AdminHash(int SchemaVersion, int Iterations, string Salt, string Hash);
public sealed class AdminCredentials
{
    private readonly AdminHash _record;
    public AdminCredentials(ServerStorage storage, IConfiguration configuration)
    {
        var path = Path.Combine(storage.DirectoryPath, "admin-auth.json");
        if (File.Exists(path)) _record = ServerStorage.Read<AdminHash>(path);
        else
        {
            var file = configuration["IPQUALITY_ADMIN_PASSWORD_FILE"];
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                throw new InvalidOperationException("首次启动必须通过 IPQUALITY_ADMIN_PASSWORD_FILE 提供管理员密码文件。");
            var info = new FileInfo(file);
            if (info.Length > 4096) throw new InvalidDataException("管理员密码文件过大。");
            var password = File.ReadAllText(file).TrimEnd('\r', '\n');
            if (password.Length is < 16 or > 256) throw new InvalidDataException("管理员密码须为 16–256 个字符。");
            var salt = RandomNumberGenerator.GetBytes(32);
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210000, HashAlgorithmName.SHA512, 32);
            _record = new(1, 210000, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
            ServerStorage.WriteAtomic(path, _record);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        if (_record.SchemaVersion != 1 || _record.Iterations is < 100000 or > 1000000 ||
            Convert.FromBase64String(_record.Salt).Length != 32 || Convert.FromBase64String(_record.Hash).Length != 32)
            throw new InvalidDataException("管理员凭据格式无效。");
    }
    public string SecurityStamp => _record.Salt;
    public bool Verify(string password)
    {
        if (password.Length is < 1 or > 256) return false;
        var computed = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(_record.Salt),
            _record.Iterations, HashAlgorithmName.SHA512, 32);
        return CryptographicOperations.FixedTimeEquals(computed, Convert.FromBase64String(_record.Hash));
    }
}
