using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace HardwareScope.Core;

internal sealed class SettingsStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly string root;
    public SettingsStore(string? directory = null)
    {
        root = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HardwareScope");
        Directory.CreateDirectory(root);
    }

    public AppSettings Load()
    {
        var path = Path.Combine(root, "settings.json");
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new();
    }

    public void Save(AppSettings settings)
    {
        var path = Path.Combine(root, "settings.json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json), Encoding.UTF8);
        if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
        else File.Move(temp, path);
    }

    public string GetToken()
    {
        var path = Path.Combine(root, "remote-token.dat");
        if (File.Exists(path)) return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser));
        return RotateToken();
    }

    public string RotateToken()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var path = Path.Combine(root, "remote-token.dat");
        if (File.Exists(path)) File.Copy(path, path + ".bak", true);
        File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));
        return token;
    }

    public X509Certificate2 GetCertificate()
    {
        var path = Path.Combine(root, "server-certificate.dat");
        if (File.Exists(path))
            return X509CertificateLoader.LoadPkcs12(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser), null,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=HardwareScope", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var eku = new System.Security.Cryptography.OidCollection { new("1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost"); san.AddDnsName(Environment.MachineName);
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        var pfx = certificate.Export(X509ContentType.Pkcs12);
        File.WriteAllBytes(path, ProtectedData.Protect(pfx, null, DataProtectionScope.CurrentUser));
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    public static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("HardwareScope", $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue("HardwareScope", false);
    }
}
