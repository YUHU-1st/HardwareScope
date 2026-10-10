using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HardwareScope.Core;
using Org.BouncyCastle.Crypto.Agreement.Srp;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;

public static class PairingChecks
{
    public static async Task RunAsync(HttpClient untrustedClient, MonitorHost host, Action<string, bool, string> check)
    {
        using (var method = await untrustedClient.GetAsync("/pair/start"))
            check("Pairing requires POST", method.StatusCode == HttpStatusCode.MethodNotAllowed, "");
        using (var unavailable = await untrustedClient.PostAsJsonAsync("/pair/start", new { }))
            check("Pairing requires a locally generated PIN", unavailable.StatusCode == HttpStatusCode.Conflict, "");

        var firstPin = host.CreatePairingPin();
        check("Pairing PIN has exactly six decimal digits", firstPin.Pin is { Length: 6 } && firstPin.Pin.All(char.IsAsciiDigit), "");
        check("Pairing PIN expires within five minutes", firstPin.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(4) && firstPin.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(5), "");
        var wrongAttempt = await StartAsync(untrustedClient, WrongPin(firstPin.Pin!));
        using (var wrong = await untrustedClient.PostAsJsonAsync("/pair/finish", wrongAttempt.Request))
            check("Wrong PIN cannot finish SRP pairing", wrong.StatusCode == HttpStatusCode.Unauthorized, "");

        host.CreatePairingPin();
        var stale = await StartAsync(untrustedClient, host.GetPairingInfo().Pin!);
        host.CreatePairingPin();
        using (var refreshed = await untrustedClient.PostAsJsonAsync("/pair/finish", stale.Request))
            check("Refreshing a PIN invalidates existing challenges", refreshed.StatusCode == HttpStatusCode.Unauthorized, "");

        var attempt = await StartAsync(untrustedClient, host.GetPairingInfo().Pin!);
        using var response = await untrustedClient.PostAsJsonAsync("/pair/finish", attempt.Request);
        check("Six digit PIN completes SRP pairing", response.IsSuccessStatusCode, "");
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>();
        check("Pairing response does not expose credentials as plaintext", !envelope.TryGetProperty("token", out _) && !envelope.TryGetProperty("fingerprint", out _), "");
        check("Client rejects an invalid server proof", !attempt.Client.VerifyServerEvidenceMessage(BigInteger.Zero), "");
        check("Client verifies the real server proof", attempt.Client.VerifyServerEvidenceMessage(new BigInteger(envelope.GetProperty("m2").GetString()!, 16)), "");
        var key = BigIntegers.AsUnsignedByteArray(32, attempt.Client.CalculateSessionKey());
        var nonce = Convert.FromHexString(envelope.GetProperty("nonce").GetString()!);
        var ciphertext = Convert.FromBase64String(envelope.GetProperty("ciphertext").GetString()!);
        var plaintext = new byte[ciphertext.Length - 16];
        var aad = Encoding.UTF8.GetBytes("HardwareScope.Pair.v1|" + attempt.SessionId);
        try
        {
            using var aes = new AesGcm(key, 16);
            var tampered = ciphertext.ToArray();
            tampered[0] ^= 1;
            check("Pairing payload rejects ciphertext tampering", RefusesDecryption(aes, nonce, tampered, aad), "");
            check("Pairing payload is bound to its challenge", RefusesDecryption(aes, nonce, ciphertext, Encoding.UTF8.GetBytes("HardwareScope.Pair.v1|wrong-session")), "");
            aes.Decrypt(nonce, ciphertext.AsSpan(0, plaintext.Length), ciphertext.AsSpan(plaintext.Length), plaintext, aad);
            using var identity = JsonDocument.Parse(plaintext);
            var root = identity.RootElement;
            check("Encrypted identity matches the HTTPS certificate", root.GetProperty("fingerprint").GetString() == host.GetConnectionInfo().CertificateSha256 && root.GetProperty("product").GetString() == "HardwareScope", "");
            var token = root.GetProperty("token").GetString()!;
            check("PIN pairing grants a strong remote credential", token.Length == 64 && token == host.GetConnectionInfo().Token, "");
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/snapshot");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var snapshot = await untrustedClient.SendAsync(request);
            check("Paired credential authorizes the real HTTPS API", snapshot.IsSuccessStatusCode, "");
            using var statusRequest = new HttpRequestMessage(HttpMethod.Get, "/api/connection");
            statusRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var status = await untrustedClient.SendAsync(statusRequest);
            using var statusJson = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
            check("Remote status does not reveal PIN or token", !statusJson.RootElement.TryGetProperty("pin", out _) && statusJson.RootElement.GetProperty("token").GetString() == "", "");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
        check("Successful pairing consumes the PIN", host.GetPairingInfo().Pin == null, "");
        using (var replay = await untrustedClient.PostAsJsonAsync("/pair/finish", attempt.Request))
            check("Successful pairing proof cannot be replayed", replay.StatusCode == HttpStatusCode.Unauthorized, "");
        using (var consumed = await untrustedClient.PostAsJsonAsync("/pair/start", new { }))
            check("Consumed PIN cannot start another pairing", consumed.StatusCode == HttpStatusCode.Conflict, "");

        await WithFreshHostAsync(async (client, freshHost) =>
        {
            freshHost.CreatePairingPin();
            using (var malformed = await client.PostAsync("/pair/finish", new StringContent("{invalid", Encoding.UTF8, "application/json")))
                check("Malformed pairing JSON is rejected", malformed.StatusCode == HttpStatusCode.BadRequest, "");
            var invalidInputs = new (string Name, string A, object? Proof)[]
            {
                ("Zero SRP public value is rejected", "0", "1"),
                ("Modulus SRP public value is rejected", Srp6StandardGroups.rfc5054_3072.N.ToString(16), "1"),
                ("Oversized SRP public value is rejected", new string('f', 769), "1"),
                ("Non-string SRP proof is rejected", "1", 1)
            };
            foreach (var input in invalidInputs)
            {
                using var start = await client.PostAsJsonAsync("/pair/start", new { });
                var challenge = await start.Content.ReadFromJsonAsync<JsonElement>();
                using var finish = await client.PostAsJsonAsync("/pair/finish", new { sessionId = challenge.GetProperty("sessionId").GetString(), a = input.A, m1 = input.Proof });
                check(input.Name, finish.StatusCode == HttpStatusCode.BadRequest, "");
            }
            using var fifth = await client.PostAsJsonAsync("/pair/start", new { });
            check("Five pairing starts are allowed per minute", fifth.IsSuccessStatusCode, "");
            using var sixth = await client.PostAsJsonAsync("/pair/start", new { });
            check("Sixth pairing start is throttled globally", sixth.StatusCode == HttpStatusCode.TooManyRequests, "");
        });

        await WithFreshHostAsync(async (client, freshHost) =>
        {
            var info = freshHost.CreatePairingPin();
            for (var index = 0; index < 5; index++)
            {
                var bad = await StartAsync(client, WrongPin(info.Pin!));
                using var finish = await client.PostAsJsonAsync("/pair/finish", bad.Request);
                check($"Wrong PIN attempt {index + 1} is denied", finish.StatusCode == (index == 4 ? HttpStatusCode.TooManyRequests : HttpStatusCode.Unauthorized), "");
            }
            var locked = freshHost.GetPairingInfo().LockedUntil;
            check("Five bad proofs trigger a one-minute cooldown", locked > DateTimeOffset.UtcNow.AddSeconds(55) && locked <= DateTimeOffset.UtcNow.AddSeconds(60), "");
            var renewed = freshHost.CreatePairingPin();
            check("New PIN preserves the pairing cooldown", renewed.LockedUntil == locked, "");
            using var denied = await client.PostAsJsonAsync("/pair/start", new { });
            check("PIN refresh cannot bypass cooldown", denied.StatusCode == HttpStatusCode.TooManyRequests, "");
        });
    }

    private static async Task<Attempt> StartAsync(HttpClient client, string pin)
    {
        using var start = await client.PostAsJsonAsync("/pair/start", new { });
        start.EnsureSuccessStatusCode();
        var challenge = await start.Content.ReadFromJsonAsync<JsonElement>();
        var srp = new Srp6Client();
        srp.Init(Srp6StandardGroups.rfc5054_3072, new Sha256Digest(), new SecureRandom());
        var a = srp.GenerateClientCredentials(Convert.FromHexString(challenge.GetProperty("salt").GetString()!), Encoding.UTF8.GetBytes("HardwareScope"), Encoding.ASCII.GetBytes(pin));
        srp.CalculateSecret(new BigInteger(challenge.GetProperty("b").GetString()!, 16));
        var sessionId = challenge.GetProperty("sessionId").GetString()!;
        return new(srp, sessionId, new { sessionId, a = a.ToString(16), m1 = srp.CalculateClientEvidenceMessage().ToString(16) });
    }

    private static string WrongPin(string pin) => ((int.Parse(pin) + 1) % 1_000_000).ToString("D6");

    private static bool RefusesDecryption(AesGcm aes, byte[] nonce, byte[] ciphertext, byte[] aad)
    {
        try
        {
            aes.Decrypt(nonce, ciphertext.AsSpan(0, ciphertext.Length - 16), ciphertext.AsSpan(ciphertext.Length - 16), new byte[ciphertext.Length - 16], aad);
            return false;
        }
        catch (AuthenticationTagMismatchException) { return true; }
    }

    private static async Task WithFreshHostAsync(Func<HttpClient, MonitorHost, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"HardwareScope-pairing-check-{Guid.NewGuid():N}");
        try
        {
            using var picker = new TcpListener(IPAddress.Loopback, 0);
            picker.Start();
            var port = ((IPEndPoint)picker.LocalEndpoint).Port;
            picker.Stop();
            await using var host = new MonitorHost(directory);
            await host.SaveSettingsAsync(new AppSettings { RemoteEnabled = true, Port = port, SamplingIntervalMs = 10000 });
            await host.StartAsync();
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate != null &&
                    Convert.ToHexString(SHA256.HashData(certificate.RawData)).Equals(host.GetConnectionInfo().CertificateSha256, StringComparison.OrdinalIgnoreCase)
            };
            using var client = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(15) };
            await action(client, host);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed record Attempt(Srp6Client Client, string SessionId, object Request);
}
