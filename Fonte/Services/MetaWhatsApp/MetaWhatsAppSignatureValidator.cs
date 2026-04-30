using System.Security.Cryptography;
using System.Text;

namespace AplicacoesOnline.Services.MetaWhatsApp;

public static class MetaWhatsAppSignatureValidator
{
    public static bool IsValid(string appSecret, string rawBody, string? signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(appSecret)
            || string.IsNullOrWhiteSpace(rawBody)
            || string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        const string expectedPrefix = "sha256=";
        if (!signatureHeader.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var providedHashHex = signatureHeader[expectedPrefix.Length..].Trim();
        if (providedHashHex.Length != 64)
        {
            return false;
        }

        var bodyBytes = Encoding.UTF8.GetBytes(rawBody);
        var keyBytes = Encoding.UTF8.GetBytes(appSecret);

        using var hmac = new HMACSHA256(keyBytes);
        var computedHash = hmac.ComputeHash(bodyBytes);
        var computedHashHex = Convert.ToHexString(computedHash).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computedHashHex),
            Encoding.UTF8.GetBytes(providedHashHex.ToLowerInvariant()));
    }
}
