using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AplicacoesOnline.Models.MetaWhatsApp;
using Microsoft.Extensions.Options;

namespace AplicacoesOnline.Services.MetaWhatsApp;

public sealed class MetaWhatsAppViewerExportJobService
{
    private const int ExportPageSize = 500;
    private readonly ConcurrentDictionary<string, ExportJobState> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly MetaWhatsAppViewerService _viewerService;
    private readonly IWebHostEnvironment _environment;
    private readonly MetaWhatsAppWebhookOptions _options;

    public MetaWhatsAppViewerExportJobService(
        MetaWhatsAppViewerService viewerService,
        IWebHostEnvironment environment,
        IOptions<MetaWhatsAppWebhookOptions> options)
    {
        _viewerService = viewerService;
        _environment = environment;
        _options = options.Value;
    }

    public ExportJobCreateResult CreateJob(string phone, string format, DateTimeOffset? from, DateTimeOffset? to, bool canViewSensitiveData)
    {
        CleanupExpiredJobs();

        var normalizedFormat = (format ?? "json").Trim().ToLowerInvariant();
        if (normalizedFormat is not ("json" or "csv"))
        {
            return ExportJobCreateResult.Failed("invalid_format", "Use format=json ou format=csv.");
        }

        if (to.HasValue && from.HasValue && to.Value < from.Value)
        {
            return ExportJobCreateResult.Failed("invalid_period", "Parâmetro 'to' não pode ser menor que 'from'.");
        }

        var safeMaxHours = Math.Clamp(_options.MaxExportPeriodHours, 1, 24 * 31);
        var periodStart = from ?? to?.AddHours(-safeMaxHours) ?? DateTimeOffset.UtcNow.AddHours(-safeMaxHours);
        var periodEnd = to ?? DateTimeOffset.UtcNow;
        if (periodEnd - periodStart > TimeSpan.FromHours(safeMaxHours))
        {
            return ExportJobCreateResult.Failed(
                "period_too_large",
                $"A janela máxima permitida para exportação é de {safeMaxHours} horas.");
        }

        var jobId = Guid.NewGuid().ToString("N");
        var state = new ExportJobState
        {
            JobId = jobId,
            Phone = phone,
            Format = normalizedFormat,
            From = from,
            To = to,
            CanViewSensitiveData = canViewSensitiveData,
            Status = "queued",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
        };

        _jobs[jobId] = state;
        _ = Task.Run(() => RunJobAsync(state));

        return ExportJobCreateResult.Succeeded(ToSnapshot(state));
    }

    public ExportJobSnapshot? GetJob(string jobId)
    {
        CleanupExpiredJobs();
        if (!_jobs.TryGetValue(jobId, out var state))
        {
            return null;
        }

        return ToSnapshot(state);
    }

    public ExportDownloadResult? TryResolveDownload(string jobId, string token)
    {
        CleanupExpiredJobs();
        if (!_jobs.TryGetValue(jobId, out var state))
        {
            return null;
        }

        if (!string.Equals(state.Status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(state.FilePath)
            || string.IsNullOrWhiteSpace(state.DownloadToken)
            || string.IsNullOrWhiteSpace(state.ContentType)
            || string.IsNullOrWhiteSpace(state.FileName)
            || !File.Exists(state.FilePath))
        {
            return ExportDownloadResult.Failed("not_ready", "Arquivo ainda não está pronto para download.");
        }

        if (!string.Equals(state.DownloadToken, token, StringComparison.Ordinal))
        {
            return ExportDownloadResult.Failed("invalid_token", "Token de download inválido.");
        }

        if (!state.DownloadTokenExpiresAt.HasValue || state.DownloadTokenExpiresAt.Value < DateTimeOffset.UtcNow)
        {
            return ExportDownloadResult.Failed("token_expired", "Link expirado. Gere um novo arquivo para download.");
        }

        return ExportDownloadResult.Succeeded(state.FilePath, state.FileName, state.ContentType);
    }

    private async Task RunJobAsync(ExportJobState state)
    {
        try
        {
            state.Status = "running";
            var safeMaxEvents = Math.Clamp(_options.MaxExportEvents, 1, 500_000);
            var exportDirectory = Path.Combine(_environment.ContentRootPath, "Arquivos e Documentos", "WhatsappViewerExports");
            Directory.CreateDirectory(exportDirectory);

            var extension = state.Format == "csv" ? "csv" : "json";
            var filePath = Path.Combine(exportDirectory, $"{state.Phone}-{state.JobId}.{extension}");
            await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));

            var cursor = (string?)null;
            var exportedCount = 0;
            var writtenAny = false;
            var eventCount = 0;
            var displayName = string.Empty;

            if (state.Format == "csv")
            {
                await writer.WriteLineAsync("timestamp,eventName,direction,category,status,interaction_id,meta_message_id,texto_principal");
            }
            else
            {
                await writer.WriteAsync("{");
                await writer.WriteAsync("\"phone\":");
                await writer.WriteAsync(JsonSerializer.Serialize(state.Phone));
                await writer.WriteAsync(",\"exported_at\":");
                await writer.WriteAsync(JsonSerializer.Serialize(DateTimeOffset.UtcNow));
                await writer.WriteAsync(",\"events\":[");
            }

            do
            {
                var page = _viewerService.GetConversationTimelinePage(state.Phone, state.From, ExportPageSize, cursor);
                eventCount = page.EventCount;
                displayName = page.DisplayName ?? string.Empty;

                foreach (var evt in page.Events)
                {
                    if (state.To.HasValue && evt.Timestamp > state.To.Value)
                    {
                        continue;
                    }

                    exportedCount++;
                    if (exportedCount > safeMaxEvents)
                    {
                        throw new ExportLimitException(
                            "max_export_events_exceeded",
                            $"A exportação ultrapassou o limite configurado de {safeMaxEvents} eventos.");
                    }

                    var record = new
                    {
                        timestamp = evt.Timestamp,
                        eventName = evt.EventName,
                        direction = evt.Direction,
                        category = evt.Category,
                        status = evt.Fields.TryGetValue("status", out var status) ? status : null,
                        interaction_id = evt.InteractionId,
                        meta_message_id = evt.MetaMessageId,
                        texto_principal = state.CanViewSensitiveData ? evt.MainText : MaskText(evt.MainText)
                    };

                    if (state.Format == "csv")
                    {
                        await writer.WriteLineAsync(string.Join(",",
                            CsvEscape(record.timestamp.ToString("o")),
                            CsvEscape(record.eventName),
                            CsvEscape(record.direction),
                            CsvEscape(record.category),
                            CsvEscape(record.status),
                            CsvEscape(record.interaction_id),
                            CsvEscape(record.meta_message_id),
                            CsvEscape(record.texto_principal)));
                    }
                    else
                    {
                        if (writtenAny)
                        {
                            await writer.WriteAsync(',');
                        }

                        await writer.WriteAsync(JsonSerializer.Serialize(record));
                        writtenAny = true;
                    }
                }

                cursor = page.NextCursor;
            } while (!string.IsNullOrWhiteSpace(cursor));

            if (state.Format == "json")
            {
                await writer.WriteAsync("]");
                await writer.WriteAsync(",\"event_count\":");
                await writer.WriteAsync(eventCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await writer.WriteAsync(",\"display_name\":");
                await writer.WriteAsync(JsonSerializer.Serialize(displayName));
                await writer.WriteAsync("}");
            }

            await writer.FlushAsync();

            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');

            var tokenTtlMinutes = Math.Clamp(_options.ExportDownloadTokenTtlMinutes, 1, 30);
            state.FilePath = filePath;
            state.FileName = $"{state.Phone}-auditoria.{extension}";
            state.ContentType = state.Format == "csv" ? "text/csv; charset=utf-8" : "application/json; charset=utf-8";
            state.ExportCount = exportedCount;
            state.DownloadToken = token;
            state.DownloadTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(tokenTtlMinutes);
            state.Status = "completed";
            state.ExpiresAt = state.DownloadTokenExpiresAt.Value.AddMinutes(1);
        }
        catch (ExportLimitException ex)
        {
            state.Status = "failed";
            state.ErrorCode = ex.ErrorCode;
            state.ErrorDetail = ex.Message;
        }
        catch (Exception ex)
        {
            state.Status = "failed";
            state.ErrorCode = "export_failed";
            state.ErrorDetail = ex.Message;
        }
    }

    private void CleanupExpiredJobs()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _jobs)
        {
            if (kvp.Value.ExpiresAt > now)
            {
                continue;
            }

            if (_jobs.TryRemove(kvp.Key, out var removed)
                && !string.IsNullOrWhiteSpace(removed.FilePath)
                && File.Exists(removed.FilePath))
            {
                try
                {
                    File.Delete(removed.FilePath);
                }
                catch
                {
                    // no-op
                }
            }
        }
    }

    private static ExportJobSnapshot ToSnapshot(ExportJobState state)
    {
        return new ExportJobSnapshot(
            state.JobId,
            state.Phone,
            state.Format,
            state.Status,
            state.CreatedAt,
            state.ExportCount,
            state.ErrorCode,
            state.ErrorDetail,
            state.DownloadToken,
            state.DownloadTokenExpiresAt,
            state.From,
            state.To);
    }

    private static string CsvEscape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var requiresQuotes = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        var escaped = value.Replace("\"", "\"\"");
        return requiresQuotes ? $"\"{escaped}\"" : escaped;
    }

    private sealed class ExportJobState
    {
        public required string JobId { get; init; }
        public required string Phone { get; init; }
        public required string Format { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset ExpiresAt { get; set; }
        public DateTimeOffset? From { get; init; }
        public DateTimeOffset? To { get; init; }
        public bool CanViewSensitiveData { get; init; }
        public string Status { get; set; } = "queued";
        public int ExportCount { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorDetail { get; set; }
        public string? DownloadToken { get; set; }
        public DateTimeOffset? DownloadTokenExpiresAt { get; set; }
        public string? FilePath { get; set; }
        public string? ContentType { get; set; }
        public string? FileName { get; set; }
    }

    private static string? MaskText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var trimmed = value.Trim();
        if (trimmed.Length <= 4)
        {
            return new string('*', trimmed.Length);
        }

        return $"{trimmed[..2]}***{trimmed[^2..]}";
    }

    private sealed class ExportLimitException : Exception
    {
        public ExportLimitException(string errorCode, string message) : base(message)
        {
            ErrorCode = errorCode;
        }

        public string ErrorCode { get; }
    }
}

public sealed record ExportJobSnapshot(
    string JobId,
    string Phone,
    string Format,
    string Status,
    DateTimeOffset CreatedAt,
    int ExportCount,
    string? ErrorCode,
    string? ErrorDetail,
    string? DownloadToken,
    DateTimeOffset? DownloadTokenExpiresAt,
    DateTimeOffset? From,
    DateTimeOffset? To);

public sealed record ExportDownloadResult(bool Success, string? ErrorCode, string? Detail, string? FilePath, string? FileName, string? ContentType)
{
    public static ExportDownloadResult Failed(string errorCode, string detail) => new(false, errorCode, detail, null, null, null);

    public static ExportDownloadResult Succeeded(string filePath, string fileName, string contentType) => new(true, null, null, filePath, fileName, contentType);
}

public sealed record ExportJobCreateResult(bool Success, string? ErrorCode, string? Detail, ExportJobSnapshot? Job)
{
    public static ExportJobCreateResult Failed(string errorCode, string detail) => new(false, errorCode, detail, null);

    public static ExportJobCreateResult Succeeded(ExportJobSnapshot job) => new(true, null, null, job);
}
