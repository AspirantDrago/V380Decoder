using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace V380Decoder.src;

internal sealed class DigestAuthentication
{
    internal const string Realm = "V380Decoder";
    private readonly byte[] secret = RandomNumberGenerator.GetBytes(32);

    public string Challenge()
    {
        string time = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        string salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        string value = time + ":" + salt;
        string signature = Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(value)));
        string nonce = Convert.ToBase64String(Encoding.UTF8.GetBytes(value + ":" + signature));
        return $"Digest realm=\"{Realm}\", nonce=\"{nonce}\", algorithm=MD5, qop=\"auth\", charset=UTF-8";
    }

    public bool Verify(string header, string method, string target, string username, string password)
    {
        if (!header.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase)) return false;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(header[7..], "([a-zA-Z0-9_-]+)\\s*=\\s*(?:\"((?:\\\\.|[^\"\\\\])*)\"|([^,\\s]+))"))
        {
            string value = match.Groups[2].Success ? Regex.Replace(match.Groups[2].Value, @"\\(.)", "$1") : match.Groups[3].Value;
            if (!fields.TryAdd(match.Groups[1].Value, value)) return false;
        }
        string Get(string name) => fields.GetValueOrDefault(name, "");
        if (Get("username") != username || Get("realm") != Realm || Get("uri") != target || !ValidNonce(Get("nonce"))) return false;
        string algorithm = Get("algorithm");
        if (algorithm != "" && !algorithm.Equals("MD5", StringComparison.OrdinalIgnoreCase)) return false;
        string ha1 = Hash($"{username}:{Realm}:{password}");
        string ha2 = Hash($"{method}:{target}");
        string expected;
        if (Get("qop") == "") expected = Hash($"{ha1}:{Get("nonce")}:{ha2}");
        else
        {
            if (Get("qop") != "auth" || Get("cnonce") == "" || Get("nc").Length != 8 ||
                !uint.TryParse(Get("nc"), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint count) || count == 0) return false;
            expected = Hash($"{ha1}:{Get("nonce")}:{Get("nc")}:{Get("cnonce")}:auth:{ha2}");
        }
        return Equal(expected, Get("response").ToLowerInvariant());
    }

    private bool ValidNonce(string nonce)
    {
        try
        {
            var fields = Encoding.UTF8.GetString(Convert.FromBase64String(nonce)).Split(':');
            if (fields.Length != 3 || !long.TryParse(fields[0], out long timestamp)) return false;
            long age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp;
            if (age < 0 || age > 300) return false;
            string signature = Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(fields[0] + ":" + fields[1])));
            return Equal(signature, fields[2]);
        }
        catch (FormatException) { return false; }
    }

    internal static bool VerifyBasic(string header, string username, string password)
    {
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        try { return Equal(Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim())), username + ":" + password); }
        catch (FormatException) { return false; }
    }

    internal static string Hash(string text) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static bool Equal(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
