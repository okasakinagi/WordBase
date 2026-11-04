using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using WordBase.Core.Abstractions;

namespace WordBase.Data;

/// <summary>
/// API Key 使用 DPAPI（当前 Windows 用户作用域）加密后落盘。
/// 副作用是密钥绑定当前用户：换机/换用户无法解密，这是刻意的——泄露的备份里拿不到明文。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WordBase.SecretStore.v1");
    private readonly string _path;

    public DpapiSecretStore(string path) => _path = path;

    public bool LastReadFailed { get; private set; }

    public string? Read(string name)
    {
        LastReadFailed = false;
        if (!File.Exists(_path))
        {
            return null;
        }

        Dictionary<string, string> payload;
        try
        {
            payload = ReadPayload();
        }
        catch (Exception)
        {
            LastReadFailed = true;
            return null;
        }

        if (!payload.TryGetValue(name, out var base64))
        {
            return null;
        }

        try
        {
            var cipher = Convert.FromBase64String(base64);
            var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            // 换机器 / 换用户后解密必然失败，属于预期行为。
            LastReadFailed = true;
            return null;
        }
    }

    public void Write(string name, string? value)
    {
        var payload = File.Exists(_path) ? ReadPayload() : new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.IsNullOrEmpty(value))
        {
            payload.Remove(name);
        }
        else
        {
            var plain = Encoding.UTF8.GetBytes(value);
            var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            payload[name] = Convert.ToBase64String(cipher);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(payload, SecretJsonOptions));
        File.Move(temp, _path, overwrite: true);
    }

    private static readonly System.Text.Json.JsonSerializerOptions SecretJsonOptions = new() { WriteIndented = true };

    private Dictionary<string, string> ReadPayload()
    {
        var json = File.ReadAllText(_path);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        return parsed ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
