using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Agreement.Srp;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;

namespace HardwareScope.Core;

internal sealed class PairingManager(Func<(string Token, string Fingerprint)> credentials)
{
    private static readonly byte[] Identity = Encoding.UTF8.GetBytes("HardwareScope");
    private readonly object gate = new();
    private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> starts = new();
    private string? pin;
    private DateTimeOffset? expiresAt;
    private DateTimeOffset? lockedUntil;
    private int failedAttempts;

    private sealed record Session(Srp6Server Server, DateTimeOffset ExpiresAt);

    public PairingInfo CreatePin()
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            Cleanup(now);
            sessions.Clear();
            pin = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture);
            expiresAt = now.AddMinutes(5);
            return new(pin, expiresAt, lockedUntil);
        }
    }

    public PairingInfo GetInfo()
    {
        lock (gate)
        {
            Cleanup(DateTimeOffset.UtcNow);
            return new(pin, expiresAt, lockedUntil);
        }
    }

    public void Cancel(Action? changeCredentials = null)
    {
        lock (gate)
        {
            changeCredentials?.Invoke();
            pin = null;
            expiresAt = null;
            sessions.Clear();
        }
    }

    public (int Status, object Body) Dispatch(string method, string path, JsonElement? body)
    {
        if (method != "POST") return Error(405, "配对接口仅接受 POST 请求");
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            Cleanup(now);
            if (lockedUntil != null) return Error(429, "PIN 尝试次数过多，请在一分钟后重试");
            if (path == "/pair/start") return Start(body, now);
            if (path == "/pair/finish") return Finish(body, now);
            return Error(404, "未找到配对接口");
        }
    }

    private (int Status, object Body) Start(JsonElement? body, DateTimeOffset now)
    {
        if (starts.Count >= 5) return Error(429, "配对请求过于频繁，请稍后重试");
        starts.Enqueue(now);
        if (body?.ValueKind != JsonValueKind.Object) return Error(400, "配对请求必须为 JSON 对象");
        if (pin == null || expiresAt == null) return Error(409, "请先在电脑端生成新的六位 PIN");
        if (sessions.Count >= 4) return Error(429, "配对会话已满，请稍后重试");

        var salt = RandomNumberGenerator.GetBytes(16);
        var generator = new Srp6VerifierGenerator();
        generator.Init(Srp6StandardGroups.rfc5054_3072, new Sha256Digest());
        var password = Encoding.ASCII.GetBytes(pin);
        BigInteger verifier;
        try { verifier = generator.GenerateVerifier(salt, Identity, password); }
        finally { CryptographicOperations.ZeroMemory(password); }
        var server = new Srp6Server();
        server.Init(Srp6StandardGroups.rfc5054_3072, verifier, new Sha256Digest(), new SecureRandom());
        var b = server.GenerateServerCredentials();
        var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var deadline = now.AddSeconds(30);
        if (expiresAt.Value < deadline) deadline = expiresAt.Value;
        sessions.Add(sessionId, new(server, deadline));
        return (200, new { sessionId, salt = Hex(salt), b = b.ToString(16), expiresAt = deadline });
    }

    private (int Status, object Body) Finish(JsonElement? body, DateTimeOffset now)
    {
        // Remove a recognized session before checking any proof fields: every challenge is single use.
        var sessionId = StringProperty(body, "sessionId");
        Session? session = null;
        if (sessionId is { Length: 32 } && IsHex(sessionId)) sessions.Remove(sessionId, out session);
        if (session == null || pin == null || session.ExpiresAt <= now)
            return Failed(now, 401, "配对会话已过期或无效，请重新配对");
        var aText = StringProperty(body, "a");
        var m1Text = StringProperty(body, "m1");
        if (!TryInteger(aText, 768, out var a) || a.SignValue == 0 || a.CompareTo(Srp6StandardGroups.rfc5054_3072.N) >= 0 ||
            !TryInteger(m1Text, 64, out var m1))
            return Failed(now, 400, "配对证明格式无效");

        session.Server.CalculateSecret(a);
        if (!session.Server.VerifyClientEvidenceMessage(m1)) return Failed(now, 401, "PIN 不正确，请重新输入");
        var m2 = session.Server.CalculateServerEvidenceMessage();
        var key = BigIntegers.AsUnsignedByteArray(32, session.Server.CalculateSessionKey());
        var nonce = RandomNumberGenerator.GetBytes(12);
        var current = credentials();
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new
        {
            token = current.Token, fingerprint = current.Fingerprint, product = "HardwareScope"
        }, SettingsStore.Json);
        var ciphertext = new byte[plaintext.Length + 16];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plaintext, ciphertext.AsSpan(0, plaintext.Length), ciphertext.AsSpan(plaintext.Length),
                Encoding.UTF8.GetBytes("HardwareScope.Pair.v1|" + sessionId));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
        pin = null;
        expiresAt = null;
        sessions.Clear();
        return (200, new { m2 = m2.ToString(16), nonce = Hex(nonce), ciphertext = Convert.ToBase64String(ciphertext) });
    }

    private (int Status, object Body) Failed(DateTimeOffset now, int status, string message)
    {
        if (++failedAttempts >= 5)
        {
            lockedUntil = now.AddSeconds(60);
            sessions.Clear();
            return Error(429, "PIN 尝试次数过多，请在一分钟后重试");
        }
        return Error(status, message);
    }

    private void Cleanup(DateTimeOffset now)
    {
        if (lockedUntil <= now) { lockedUntil = null; failedAttempts = 0; }
        if (expiresAt <= now) { pin = null; expiresAt = null; sessions.Clear(); }
        foreach (var id in sessions.Where(s => s.Value.ExpiresAt <= now).Select(s => s.Key).ToArray()) sessions.Remove(id);
        while (starts.TryPeek(out var started) && started <= now.AddMinutes(-1)) starts.Dequeue();
    }

    private static (int Status, object Body) Error(int status, string message) => (status, new CommandResult(false, message));
    private static string? StringProperty(JsonElement? body, string name) => body?.ValueKind == JsonValueKind.Object &&
        body.Value.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();
    private static bool IsHex(string text) => text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
    private static bool TryInteger(string? text, int maxCharacters, out BigInteger value)
    {
        value = BigInteger.Zero;
        if (text == null || text.Length is 0 || text.Length > maxCharacters || !IsHex(text)) return false;
        value = new BigInteger(text, 16);
        return true;
    }
}
