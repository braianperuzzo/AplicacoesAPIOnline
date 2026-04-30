using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using AplicacoesOnline.Models.ShortLinks;
using AplicacoesOnline.Options;
using Microsoft.Extensions.Options;

namespace AplicacoesOnline.Services.ShortLinks;

public class ShortLinksService : IShortLinksService
{
    private const string TokenAlphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const int MaxTokenGenerationAttempts = 20;

    private readonly IOptions<ShortLinksOptions> _options;
    private readonly ILogger<ShortLinksService> _logger;
    private readonly string _storageFilePath;
    private readonly ConcurrentDictionary<string, ShortLinkTokenPayload> _entries;
    private readonly SemaphoreSlim _storageLock = new(1, 1);

    public ShortLinksService(
        IOptions<ShortLinksOptions> options,
        ILogger<ShortLinksService> logger)
    {
        _options = options;
        _logger = logger;
        _storageFilePath = ResolveStorageFilePath();
        _entries = new ConcurrentDictionary<string, ShortLinkTokenPayload>(StringComparer.Ordinal);

        EnsureStorageDirectoryExists();
        LoadExistingEntries();
    }

    public (ShortLinkCreateResponse? Response, Dictionary<string, string[]>? Errors) Create(ShortLinkCreateRequest request)
    {
        var validationErrors = ValidateCreateRequest(request);
        if (validationErrors.Count > 0)
        {
            _logger.LogWarning("Short link creation rejected due to validation errors. Errors={Errors}", validationErrors);
            return (null, validationErrors);
        }

        var now = DateTime.UtcNow;
        var expirationHours = request.ExpiresInHours ?? _options.Value.DefaultExpirationHours;
        var expiresAtUtc = now.AddHours(expirationHours);

        var payload = new ShortLinkTokenPayload
        {
            DestinationUrl = request.DestinationUrl.Trim(),
            ExpiresAtUtc = expiresAtUtc,
            Category = request.Category?.Trim(),
            Description = request.Description?.Trim(),
            Metadata = request.Metadata
        };

        var token = GenerateUniqueToken();
        _entries[token] = payload;
        PersistEntries();

        var shortUrl = BuildShortUrl(token);
        _logger.LogInformation(
            "Short link created. Category={Category} ExpiresAtUtc={ExpiresAtUtc} DestinationHost={DestinationHost} TokenLength={TokenLength}",
            payload.Category,
            payload.ExpiresAtUtc,
            GetHost(payload.DestinationUrl),
            token.Length);

        var response = new ShortLinkCreateResponse
        {
            Ok = true,
            Token = token,
            ShortUrl = shortUrl,
            DestinationUrlPreview = payload.DestinationUrl,
            ExpiresAtUtc = expiresAtUtc
        };

        return (response, null);
    }

    public ShortLinkResolveResult Resolve(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return ShortLinkResolveResult.Invalid("token_missing");
        }

        var normalizedToken = token.Trim();
        if (!_entries.TryGetValue(normalizedToken, out var payload))
        {
            return ShortLinkResolveResult.Invalid("token_invalid");
        }

        if (!IsDestinationUrlValid(payload.DestinationUrl, out _))
        {
            _logger.LogWarning("Short link resolve failed: destination URL from token is invalid.");
            return ShortLinkResolveResult.Invalid("destination_url_invalid");
        }

        if (payload.ExpiresAtUtc <= DateTime.UtcNow)
        {
            _logger.LogInformation("Short link resolve expired. ExpiresAtUtc={ExpiresAtUtc}", payload.ExpiresAtUtc);
            return ShortLinkResolveResult.ExpiredResult(payload.ExpiresAtUtc);
        }

        _logger.LogInformation("Short link resolved. DestinationHost={DestinationHost} ExpiresAtUtc={ExpiresAtUtc}", GetHost(payload.DestinationUrl), payload.ExpiresAtUtc);
        return ShortLinkResolveResult.Resolved(payload.DestinationUrl, payload.ExpiresAtUtc);
    }

    private string BuildShortUrl(string token)
    {
        var baseUri = new Uri(_options.Value.PublicBaseUrl.TrimEnd('/'), UriKind.Absolute);
        var routePrefix = _options.Value.RoutePrefix.Trim('/');
        return new Uri(baseUri, $"{routePrefix}/{token}").ToString();
    }

    private Dictionary<string, string[]> ValidateCreateRequest(ShortLinkCreateRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (!IsDestinationUrlValid(request.DestinationUrl, out var destinationError))
        {
            errors["destination_url"] = [destinationError ?? "destination_url_invalid"];
        }

        var expirationHours = request.ExpiresInHours ?? _options.Value.DefaultExpirationHours;
        if (expirationHours <= 0 || expirationHours > 24 * 365)
        {
            errors["expires_in_hours"] = ["expires_in_hours_must_be_between_1_and_8760"];
        }

        return errors;
    }

    private bool IsDestinationUrlValid(string? destinationUrl, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(destinationUrl))
        {
            error = "destination_url_required";
            return false;
        }

        var normalized = destinationUrl.Trim();
        if (normalized.Length > _options.Value.MaxUrlLength)
        {
            error = "destination_url_too_long";
            return false;
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            error = "destination_url_must_be_absolute";
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "destination_url_host_required";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "destination_url_user_info_not_allowed";
            return false;
        }

        var allowedSchemes = _options.Value.AllowedSchemes;
        if (!allowedSchemes.Any(scheme => string.Equals(scheme, uri.Scheme, StringComparison.OrdinalIgnoreCase)))
        {
            error = "destination_url_scheme_not_allowed";
            return false;
        }

        if (!IsHostAllowlisted(uri.Host))
        {
            error = "destination_url_host_not_allowlisted";
            return false;
        }

        return true;
    }

    private bool IsHostAllowlisted(string host)
    {
        var allowlist = _options.Value.TrustedHostsAllowlist;
        if (allowlist is null || allowlist.Length == 0)
        {
            return true;
        }

        return allowlist.Any(allowedHost =>
            !string.IsNullOrWhiteSpace(allowedHost)
            && string.Equals(host, allowedHost.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private string GenerateUniqueToken()
    {
        var tokenLength = Math.Clamp(_options.Value.TokenLength, 4, 32);

        for (var attempt = 1; attempt <= MaxTokenGenerationAttempts; attempt++)
        {
            var token = GenerateToken(tokenLength);
            if (!_entries.ContainsKey(token))
            {
                return token;
            }
        }

        throw new InvalidOperationException("Não foi possível gerar token único para short link.");
    }

    private static string GenerateToken(int length)
    {
        Span<byte> randomBytes = stackalloc byte[length];
        RandomNumberGenerator.Fill(randomBytes);

        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = TokenAlphabet[randomBytes[i] % TokenAlphabet.Length];
        }

        return new string(chars);
    }

    private string ResolveStorageFilePath()
    {
        var configuredPath = _options.Value.StorageFilePath;
        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        return Path.Combine(AppContext.BaseDirectory, configuredPath);
    }

    private void EnsureStorageDirectoryExists()
    {
        var directory = Path.GetDirectoryName(_storageFilePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
    }

    private void LoadExistingEntries()
    {
        if (!File.Exists(_storageFilePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_storageFilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            var parsed = JsonSerializer.Deserialize<Dictionary<string, ShortLinkTokenPayload>>(json);
            if (parsed is null)
            {
                return;
            }

            foreach (var (token, payload) in parsed)
            {
                if (string.IsNullOrWhiteSpace(token) || payload is null)
                {
                    continue;
                }

                _entries[token] = payload;
            }

            _logger.LogInformation("Short links storage loaded. Entries={EntriesCount}", _entries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao carregar armazenamento de short links. FilePath={StorageFilePath}", _storageFilePath);
        }
    }

    private void PersistEntries()
    {
        _storageLock.Wait();
        try
        {
            var activeEntries = _entries
                .Where(pair => pair.Value.ExpiresAtUtc > DateTime.UtcNow)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

            foreach (var expiredKey in _entries.Keys.Except(activeEntries.Keys).ToList())
            {
                _entries.TryRemove(expiredKey, out _);
            }

            var json = JsonSerializer.Serialize(activeEntries);
            File.WriteAllText(_storageFilePath, json);
        }
        finally
        {
            _storageLock.Release();
        }
    }

    private static string? GetHost(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host
            : null;
    }
}
