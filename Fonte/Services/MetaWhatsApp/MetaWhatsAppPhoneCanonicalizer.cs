namespace AplicacoesOnline.Services.MetaWhatsApp;

public static class MetaWhatsAppPhoneCanonicalizer
{
    public static string? ToRawIdentity(string? rawPhone)
    {
        if (string.IsNullOrWhiteSpace(rawPhone))
        {
            return null;
        }

        var digits = new string(rawPhone.Where(char.IsDigit).ToArray());
        return string.IsNullOrWhiteSpace(digits) ? null : digits;
    }

    public static string? ToCanonicalE164Br(string? rawPhone)
    {
        var digits = ToRawIdentity(rawPhone);
        if (string.IsNullOrWhiteSpace(digits))
        {
            return null;
        }

        if (digits.StartsWith("0", StringComparison.Ordinal))
        {
            digits = digits.TrimStart('0');
        }

        if (digits.Length < 8)
        {
            return null;
        }

        if (digits.Length is >= 8 and <= 15 && !digits.StartsWith("55", StringComparison.Ordinal))
        {
            // Mantém números internacionais não-BR intactos.
            // Prefixa 55 somente quando houver estrutura nacional BR compatível (DDD + número local).
            if (digits.Length is 10 or 11)
            {
                digits = $"55{digits}";
            }
        }

        if (TryParseBrNational(digits, out var national)
            && national.Length == 10
            && IsMobilePrefix(national[2]))
        {
            digits = $"55{national[..2]}9{national[2..]}";
        }

        if (digits.Length is < 10 or > 15)
        {
            return null;
        }

        return digits;
    }


    public static string? ToBrLocalDigits(string? rawPhone)
    {
        var canonical = ToCanonicalE164Br(rawPhone);
        if (string.IsNullOrWhiteSpace(canonical))
        {
            return null;
        }

        if (canonical.StartsWith("55", StringComparison.Ordinal) && canonical.Length > 11)
        {
            return canonical[2..];
        }

        return canonical;
    }

    public static IEnumerable<string> BuildLookupAliases(string? rawPhone)
    {
        var rawIdentity = ToRawIdentity(rawPhone);
        if (string.IsNullOrWhiteSpace(rawIdentity))
        {
            yield break;
        }

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        if (emitted.Add(rawIdentity))
        {
            yield return rawIdentity;
        }

        var canonical = ToCanonicalE164Br(rawIdentity);
        if (!string.IsNullOrWhiteSpace(canonical) && emitted.Add(canonical))
        {
            yield return canonical;
        }

        foreach (var alias in BuildBrMobileNinthDigitAliases(rawIdentity))
        {
            if (emitted.Add(alias))
            {
                yield return alias;
            }
        }
    }

    private static IEnumerable<string> BuildBrMobileNinthDigitAliases(string phone)
    {
        if (!TryParseBrNational(phone, out var national))
        {
            yield break;
        }

        if (national.Length == 11 && national[2] == '9' && IsMobilePrefix(national[3]))
        {
            var withoutNinthDigit = $"{national[..2]}{national[3..]}";
            yield return withoutNinthDigit;
            yield return $"55{withoutNinthDigit}";
            yield break;
        }

        if (national.Length == 10 && IsMobilePrefix(national[2]))
        {
            var withNinthDigit = $"{national[..2]}9{national[2..]}";
            yield return withNinthDigit;
            yield return $"55{withNinthDigit}";
        }
    }

    private static bool TryParseBrNational(string digits, out string national)
    {
        national = string.Empty;
        if (string.IsNullOrWhiteSpace(digits))
        {
            return false;
        }

        var normalized = digits.StartsWith("55", StringComparison.Ordinal) ? digits[2..] : digits;
        if (normalized.Length is < 10 or > 11)
        {
            return false;
        }

        if (!normalized.All(char.IsDigit))
        {
            return false;
        }

        national = normalized;
        return true;
    }

    private static bool IsMobilePrefix(char prefix)
        => prefix is >= '6' and <= '9';
}
