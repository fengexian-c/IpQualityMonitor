using System.Security.Cryptography;
using System.Text.Json;
using IpQualityMonitor.Application;
using IpQualityMonitor.Web;
using Microsoft.Extensions.Configuration;

internal static class AuthChecks
{
    // Disposable test credentials only.
    private const string Password = "test-only-password-123456";
    private const string UserKey = "IPQUALITY_ADMIN_USERNAME", PassKey = "IPQUALITY_ADMIN_PASSWORD", FileKey = "IPQUALITY_ADMIN_PASSWORD_FILE";
    private static IConfiguration Config(params (string Key, string? Value)[] values) => new ConfigurationBuilder()
        .AddInMemoryCollection(values.ToDictionary(x => x.Key, x => x.Value)).Build();
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var root = Path.Combine(Path.GetTempPath(), "iqm-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sequence = 0;
            ServerStorage Storage() => new(Path.Combine(root, (++sequence).ToString()), "auth test");
            void Bad(IConfiguration config, string label)
            {
                using var storage = Storage();
                try { _ = new AdminCredentials(storage, config); throw new Exception("Unexpected bootstrap success: " + label); }
                catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
                { check(!ex.ToString().Contains(Password), label + " error excludes credential values"); }
                check(!File.Exists(Path.Combine(storage.DirectoryPath, "admin-auth.json")), label + " cannot write credentials");
                check(new AdminCredentials(storage, Config((PassKey, Password))).Verify("admin", Password),
                    label + " allows corrected bootstrap in the same directory");
            }
            Bad(Config(), "missing sources");
            Bad(Config((PassKey, "")), "empty env password");
            Bad(Config((FileKey, "")), "empty file path");
            Bad(Config((PassKey, Password), (FileKey, "")), "both sources including blank file");
            Bad(Config((PassKey, ""), (FileKey, "absent")), "both sources including blank env");
            Bad(Config((PassKey, ""), (FileKey, "")), "both sources blank");
            Bad(Config((FileKey, Path.Combine(root, "absent"))), "missing password file");
            Bad(Config((FileKey, root)), "unreadable password file directory");
            foreach (var length in new[] { 15, 257 }) Bad(Config((PassKey, new string('p', length))), "password length " + length);
            foreach (var username in new[] { "", " ", "admin ", "é", "管理员", "a/b", "a\nb", new string('a', 65) })
                Bad(Config((UserKey, username), (PassKey, Password)), "invalid username " + sequence);
            var file = Path.Combine(root, "test-secret");
            File.WriteAllText(file, new string('p', 4097));
            Bad(Config((FileKey, file)), "oversized password file");
            foreach (var (username, password) in new[] { ("a", new string('p', 16)), (new string('A', 64), new string('p', 256)),
                         ("Admin.Name_-9", "  exact-env-test-password\r\n") })
            {
                using var storage = Storage();
                var auth = new AdminCredentials(storage, Config((UserKey, username), (PassKey, password)));
                check(auth.Username == username && auth.Verify(username, password), "env bounds and exact username/password accepted");
                check(!auth.Verify(username.ToLowerInvariant() + "x", password) && !auth.Verify(username, "wrong-password"), "wrong username and password denied");
                check(!auth.Verify(null, password) && !auth.Verify(username, null) && !auth.Verify("", password) &&
                    !auth.Verify(new string('a', 65), password) && !auth.Verify(username, new string('p', 257)), "bounded nullable verification");
                var path = Path.Combine(storage.DirectoryPath, "admin-auth.json");
                var bytes = File.ReadAllBytes(path);
                var record = JsonSerializer.Deserialize<AdminHash>(bytes)!;
                check(record.SchemaVersion == 2 && record.Username == username && !File.ReadAllText(path).Contains(password), "schema 2 persists identity and hash only");
                if (!OperatingSystem.IsWindows()) check(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "auth JSON mode is 0600");
                var restarted = new AdminCredentials(storage, Config((UserKey, "invalid username"), (PassKey, ""), (FileKey, "missing-path")));
                check(restarted.Verify(username, password) && restarted.SecurityStamp == auth.SecurityStamp && File.ReadAllBytes(path).SequenceEqual(bytes),
                    "persisted auth ignores all conflicting invalid bootstrap and preserves bytes/stamp");
                check(new AdminCredentials(storage, Config()).SecurityStamp == auth.SecurityStamp, "no bootstrap required after initialization");
                ServerStorage.WriteAtomic(path, record with { Username = "different-name" });
                var changed = new AdminCredentials(storage, Config());
                check(changed.SecurityStamp != auth.SecurityStamp && changed.Verify("different-name", password), "schema 2 stamp binds persisted username");
            }
            File.WriteAllText(file, "  exact-file-test-password  \r\n\r\n", new System.Text.UTF8Encoding(true));
            using (var storage = Storage())
            {
                var auth = new AdminCredentials(storage, Config((FileKey, file)));
                check(auth.Username == "admin" && auth.Verify("admin", "  exact-file-test-password  "), "file bootstrap defaults admin and strips only trailing CR/LF");
            }
            var salt = RandomNumberGenerator.GetBytes(32);
            var hash = Rfc2898DeriveBytes.Pbkdf2(Password, salt, 210000, HashAlgorithmName.SHA512, 32);
            var legacy = new AdminHash(1, 210000, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
            using (var storage = Storage())
            {
                var path = Path.Combine(storage.DirectoryPath, "admin-auth.json");
                var original = JsonSerializer.SerializeToUtf8Bytes(new { legacy.SchemaVersion, legacy.Iterations, legacy.Salt, legacy.Hash });
                File.WriteAllBytes(path, original);
                var auth = new AdminCredentials(storage, Config((UserKey, "new-name"), (PassKey, "bad"), (FileKey, "absent")));
                check(auth.Username == "admin" && auth.Verify("admin", Password) && !auth.Verify("new-name", Password), "schema 1 uses admin and original password");
                check(auth.SecurityStamp == legacy.Salt && File.ReadAllBytes(path).SequenceEqual(original), "schema 1 old session stamp and exact file bytes retained");
                foreach (var encoding in new System.Text.Encoding[] { new System.Text.UTF8Encoding(true), System.Text.Encoding.Unicode })
                {
                    File.WriteAllText(path, System.Text.Encoding.UTF8.GetString(original), encoding);
                    var marked = File.ReadAllBytes(path);
                    var compatible = new AdminCredentials(storage, Config());
                    check(compatible.Verify("admin", Password) && compatible.SecurityStamp == legacy.Salt && File.ReadAllBytes(path).SequenceEqual(marked),
                        "legacy BOM-marked auth remains readable and byte-identical");
                }
            }
            var valid = legacy with { SchemaVersion = 2, Username = "admin" };
            var badRecords = new[] { "", "null", "{", new string(' ', 4097),
                JsonSerializer.Serialize(valid with { SchemaVersion = 3 }), JsonSerializer.Serialize(valid with { Username = null }),
                JsonSerializer.Serialize(valid with { Username = "" }), JsonSerializer.Serialize(valid with { Username = "bad name" }),
                JsonSerializer.Serialize(valid with { Salt = null! }), JsonSerializer.Serialize(valid with { Hash = null! }),
                JsonSerializer.Serialize(valid with { Salt = "not-base64" }), JsonSerializer.Serialize(valid with { Hash = "not-base64" }),
                JsonSerializer.Serialize(valid with { Salt = Convert.ToBase64String(new byte[31]) }),
                JsonSerializer.Serialize(valid with { Hash = Convert.ToBase64String(new byte[33]) }),
                JsonSerializer.Serialize(valid with { Iterations = 99999 }), JsonSerializer.Serialize(valid with { Iterations = 1000001 }) };
            foreach (var bad in badRecords)
            {
                using var storage = Storage();
                var path = Path.Combine(storage.DirectoryPath, "admin-auth.json");
                File.WriteAllText(path, bad);
                reject(() => _ = new AdminCredentials(storage, Config((PassKey, Password))), "corrupt/unsupported stored auth fails closed " + sequence);
                check(File.ReadAllText(path) == bad, "failed auth remains intact " + sequence);
            }
            using (var storage = Storage())
            {
                var auth = new AdminCredentials(storage, Config((UserKey, "original"), (PassKey, Password)));
                var path = Path.Combine(storage.DirectoryPath, "admin-auth.json");
                var original = File.ReadAllBytes(path);
                reject(() => { using var rival = new ServerStorage(storage.DirectoryPath, "rival");
                    _ = new AdminCredentials(rival, Config((UserKey, "rival"), (PassKey, "other-test-password-1234"))); }, "exclusive lease precedes competing auth initialization");
                check(File.ReadAllBytes(path).SequenceEqual(original), "competing instance cannot replace credentials");
                File.Move(path, path + ".backup");
                var reset = new AdminCredentials(storage, Config((UserKey, "replacement"), (PassKey, Password)));
                check(reset.SecurityStamp != auth.SecurityStamp && reset.Verify("replacement", Password), "offline rebootstrap rotates salt and identity stamp");
                check(Directory.GetFiles(storage.DirectoryPath, "*.tmp").Length == 0, "auth initialization leaves no temporary files");
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
