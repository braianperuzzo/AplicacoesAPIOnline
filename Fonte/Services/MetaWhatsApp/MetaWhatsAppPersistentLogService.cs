using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using AplicacoesOnline.Models.MetaWhatsApp;
using Microsoft.Extensions.Options;

namespace AplicacoesOnline.Services.MetaWhatsApp;

public interface IMetaWhatsAppPersistentLogService
{
    Task<bool> AppendEventAsync(string? rawPhone, string eventName, DateTimeOffset timestamp, IReadOnlyDictionary<string, string?> fields, string? deduplicationKey = null);
    Task<PersistentLogProbeResult> WriteProbeAsync(string? reason = null);
}

public sealed record PersistentLogProbeResult(
    bool Success,
    string? ConfiguredDirectory,
    string ResolvedDirectory,
    string ProbeFilePath,
    string? ErrorMessage);

public sealed class MetaWhatsAppPersistentLogService : IMetaWhatsAppPersistentLogService
{
    private const string DefaultRelativeDirectory = "Arquivos e Documentos/WhatsappLogMensagens";

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.Ordinal);
    private readonly ILogger<MetaWhatsAppPersistentLogService> _logger;
    private readonly string? _configuredDirectory;
    private readonly string _contentRootPath;
    private string? _baseDirectory;
    private readonly object _baseDirectoryLock = new();

    public MetaWhatsAppPersistentLogService(
        IOptions<MetaWhatsAppWebhookOptions> options,
        IWebHostEnvironment environment,
        ILogger<MetaWhatsAppPersistentLogService> logger)
    {
        _logger = logger;
        _logger.LogInformation("MetaWhatsAppPersistentLogService construtor iniciado.");
        try
        {
            _configuredDirectory = options.Value.PersistentLogDirectory;
            _contentRootPath = environment.ContentRootPath;
            _logger.LogInformation(
                "Inicializando MetaWhatsAppPersistentLogService. ContentRoot={ContentRoot}. ConfiguredDirectory={ConfiguredDirectory}",
                _contentRootPath,
                _configuredDirectory);
            _logger.LogInformation("MetaWhatsAppPersistentLogService construtor finalizado.");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Falha no construtor de MetaWhatsAppPersistentLogService. ExceptionType={ExceptionType}. InnerExceptionType={InnerExceptionType}. Message={Message}",
                ex.GetType().FullName,
                ex.InnerException?.GetType().FullName,
                ex.Message);
            throw;
        }
    }

    public async Task<bool> AppendEventAsync(string? rawPhone, string eventName, DateTimeOffset timestamp, IReadOnlyDictionary<string, string?> fields, string? deduplicationKey = null)
    {
        var baseDirectory = GetOrInitializeBaseDirectory();
        Directory.CreateDirectory(baseDirectory);

        var correlationFileKey = ResolveCorrelationFileKey(rawPhone, fields);
        if (string.IsNullOrWhiteSpace(correlationFileKey))
        {
            correlationFileKey = "unknown";
            _logger.LogWarning(
                "Log persistente WhatsApp sem identificador válido. Gravando em arquivo de fallback. Evento: {EventName}. InteractionId={InteractionId}",
                eventName,
                TryGetField(fields, "interaction_id"));
        }

        var filePath = Path.Combine(baseDirectory, $"{correlationFileKey}.txt");
        var lockForFile = _fileLocks.GetOrAdd(correlationFileKey, _ => new SemaphoreSlim(1, 1));
        var normalizedDeduplicationKey = SanitizeDeduplicationKey(deduplicationKey);

        var builder = new StringBuilder();
        builder.Append('[')
            .Append(timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
            .Append("] EVENTO=")
            .Append(eventName)
            .AppendLine();

        if (!string.IsNullOrWhiteSpace(normalizedDeduplicationKey))
        {
            builder.Append("event_key=").Append(normalizedDeduplicationKey).AppendLine();
        }

        var normalizedAudit = BuildAuditMetadata(fields);
        if (normalizedAudit is not null)
        {
            builder.Append("audit=").Append(JsonSerializer.Serialize(normalizedAudit)).AppendLine();
        }

        foreach (var field in fields)
        {
            var key = field.Key?.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var value = string.IsNullOrWhiteSpace(field.Value)
                ? "-"
                : field.Value!.Trim().ReplaceLineEndings(" ");

            builder.Append(key).Append('=').Append(value).AppendLine();
        }

        builder.AppendLine();

        await lockForFile.WaitAsync();
        try
        {
            if (!string.IsNullOrWhiteSpace(normalizedDeduplicationKey) && File.Exists(filePath))
            {
                var existing = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
                if (existing.Contains($"event_key={normalizedDeduplicationKey}", StringComparison.Ordinal))
                {
                    _logger.LogInformation(
                        "Log persistente WhatsApp deduplicado. Evento: {EventName}. Arquivo: {FilePath}. EventKey: {EventKey}",
                        eventName,
                        filePath,
                        normalizedDeduplicationKey);
                    return false;
                }
            }

            await File.AppendAllTextAsync(filePath, builder.ToString(), Encoding.UTF8);

            _logger.LogInformation(
                "Log persistente WhatsApp gravado. Evento: {EventName}. Arquivo: {FilePath}",
                eventName,
                filePath);
            return true;
        }
        finally
        {
            lockForFile.Release();
        }
    }

    public async Task<PersistentLogProbeResult> WriteProbeAsync(string? reason = null)
    {
        var baseDirectory = GetOrInitializeBaseDirectory();
        var now = DateTimeOffset.UtcNow;
        var safeFileName = $"probe-{now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt";
        var probePath = Path.Combine(baseDirectory, safeFileName);

        try
        {
            Directory.CreateDirectory(baseDirectory);
            var payload = $"[{now:O}] PROBE=WHATSAPP_PERSISTENT_LOG reason={reason ?? "-"}{Environment.NewLine}";
            await File.WriteAllTextAsync(probePath, payload, Encoding.UTF8);
            _logger.LogInformation("Probe de log persistente gravado em {ProbePath}", probePath);
            return new PersistentLogProbeResult(true, _configuredDirectory, baseDirectory, probePath, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao gravar probe de log persistente em {BaseDirectory}", baseDirectory);
            return new PersistentLogProbeResult(false, _configuredDirectory, baseDirectory, probePath, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private string GetOrInitializeBaseDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_baseDirectory))
        {
            return _baseDirectory;
        }

        lock (_baseDirectoryLock)
        {
            if (!string.IsNullOrWhiteSpace(_baseDirectory))
            {
                return _baseDirectory;
            }

            _baseDirectory = ResolveWritableBaseDirectory(_configuredDirectory, _contentRootPath, _logger);
            _logger.LogInformation(
                "MetaWhatsAppPersistentLogService inicializado com diretório base {BaseDirectory}",
                _baseDirectory);
            return _baseDirectory;
        }
    }

    public static string BuildCompactPayload(params object?[] items)
    {
        var filtered = items.Where(item => item is not null).ToArray();
        return filtered.Length == 0
            ? "-"
            : JsonSerializer.Serialize(filtered);
    }

    private static string ResolveBaseDirectory(string? configuredDirectory, string contentRootPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            configuredDirectory = configuredDirectory.Trim().Trim('"');
            if (Path.IsPathRooted(configuredDirectory))
            {
                return Path.GetFullPath(configuredDirectory);
            }

            return Path.GetFullPath(Path.Combine(contentRootPath, configuredDirectory));
        }

        return Path.GetFullPath(Path.Combine(contentRootPath, "..", DefaultRelativeDirectory));
    }

    private static string ResolveWritableBaseDirectory(string? configuredDirectory, string contentRootPath, ILogger logger)
    {
        var resolvedBaseDirectory = ResolveBaseDirectory(configuredDirectory, contentRootPath);
        if (TryEnsureDirectory(resolvedBaseDirectory, out var baseDirectoryError))
        {
            logger.LogInformation(
                "WhatsApp persistent log directory resolved. ContentRoot={ContentRoot} ConfiguredDirectory={ConfiguredDirectory} ResolvedDirectory={ResolvedDirectory}",
                contentRootPath,
                configuredDirectory,
                resolvedBaseDirectory);
            return resolvedBaseDirectory;
        }

        var fallbackDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AplicacoesOnline", "WhatsappLogMensagens"));
        if (TryEnsureDirectory(fallbackDirectory, out var fallbackDirectoryError))
        {
            logger.LogError(
                "Falha ao inicializar diretório de log persistente configurado. ConfiguredDirectory={ConfiguredDirectory}. ResolvedDirectory={ResolvedDirectory}. Error={Error}. FallbackDirectory={FallbackDirectory}",
                configuredDirectory,
                resolvedBaseDirectory,
                baseDirectoryError,
                fallbackDirectory);
            return fallbackDirectory;
        }

        logger.LogError(
            "Falha ao inicializar diretório de log persistente e fallback. ConfiguredDirectory={ConfiguredDirectory}. ResolvedDirectory={ResolvedDirectory}. Error={Error}. FallbackDirectory={FallbackDirectory}. FallbackError={FallbackError}",
            configuredDirectory,
            resolvedBaseDirectory,
            baseDirectoryError,
            fallbackDirectory,
            fallbackDirectoryError);

        return resolvedBaseDirectory;
    }

    private static bool TryEnsureDirectory(string directory, out string? error)
    {
        try
        {
            Directory.CreateDirectory(directory);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static string? CleanPhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        var digits = Regex.Replace(phone, "\\D", string.Empty);
        return string.IsNullOrWhiteSpace(digits) ? null : digits;
    }

    private static string? CleanIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        var normalized = identifier.Trim();
        normalized = Regex.Replace(normalized, "[^a-zA-Z0-9:_-]", "_");
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    public static string BuildDeduplicationKey(string eventType, string? interactionId, string? metaMessageId, DateTimeOffset timestamp, string? status = null)
    {
        return $"{eventType.Trim().ToUpperInvariant()}|{interactionId?.Trim() ?? "-"}|{metaMessageId?.Trim() ?? "-"}|{status?.Trim().ToUpperInvariant() ?? "-"}|{timestamp.ToUnixTimeSeconds()}";
    }

    private static string? SanitizeDeduplicationKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return key.Trim().ReplaceLineEndings(" ");
    }

    private static string? TryGetField(IReadOnlyDictionary<string, string?> fields, string key)
        => fields.TryGetValue(key, out var value) ? value : null;

    private static string? ResolveCorrelationFileKey(string? rawPhone, IReadOnlyDictionary<string, string?> fields)
    {
        var canonicalPhone = CleanPhone(TryGetField(fields, "canonical_phone"));
        var aliasPreferredPhone = ExtractPreferredPhoneFromAliases(TryGetField(fields, "phone_aliases"));

        return FirstNonEmpty(
            canonicalPhone,
            aliasPreferredPhone,
            CleanPhone(TryGetField(fields, "recipient_e164")),
            CleanPhone(TryGetField(fields, "recipient_phone_e164")),
            CleanPhone(TryGetField(fields, "telefone")),
            CleanPhone(TryGetField(fields, "customer_phone")),
            CleanPhone(rawPhone),
            CleanIdentifier(TryGetField(fields, "interaction_id")),
            CleanIdentifier(TryGetField(fields, "meta_message_id")),
            CleanIdentifier(TryGetField(fields, "wamid")),
            CleanIdentifier(TryGetField(fields, "canonical_correlation_key")),
            CleanIdentifier(TryGetField(fields, "customer_parent_user_id")),
            CleanIdentifier(TryGetField(fields, "customer_user_id")));
    }

    private static Dictionary<string, string>? BuildAuditMetadata(IReadOnlyDictionary<string, string?> fields)
    {
        var actor = FirstNonEmpty(TryGetField(fields, "actor"), TryGetField(fields, "ator"))?.Trim();
        var motivo = FirstNonEmpty(TryGetField(fields, "motivo"), TryGetField(fields, "reason"), TryGetField(fields, "reason_code"))?.Trim();
        var origem = FirstNonEmpty(TryGetField(fields, "origem"), TryGetField(fields, "source"), TryGetField(fields, "interaction_context"))?.Trim();
        var traceId = FirstNonEmpty(TryGetField(fields, "traceId"), TryGetField(fields, "trace_id"))?.Trim();

        if (string.IsNullOrWhiteSpace(actor)
            && string.IsNullOrWhiteSpace(motivo)
            && string.IsNullOrWhiteSpace(origem)
            && string.IsNullOrWhiteSpace(traceId))
        {
            return null;
        }

        return new Dictionary<string, string>
        {
            ["actor"] = string.IsNullOrWhiteSpace(actor) ? "-" : actor,
            ["motivo"] = string.IsNullOrWhiteSpace(motivo) ? "-" : motivo,
            ["origem"] = string.IsNullOrWhiteSpace(origem) ? "-" : origem,
            ["traceId"] = string.IsNullOrWhiteSpace(traceId) ? "-" : traceId
        };
    }

    private static string? ExtractPreferredPhoneFromAliases(string? aliasesRaw)
    {
        if (string.IsNullOrWhiteSpace(aliasesRaw))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(aliasesRaw);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var cleaned = CleanPhone(item.GetString());
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    return cleaned;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string? FirstNonEmpty(params string?[] candidates)
        => candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
