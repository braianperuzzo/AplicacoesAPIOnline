using System.Text.Json;
using System.Text.RegularExpressions;
using AplicacoesOnline.Models.MetaWhatsApp;

namespace AplicacoesOnline.Services.MetaWhatsApp;

public sealed class MetaWhatsAppAuditReportService
{
    private static readonly Regex EventRegex = new("^\\[(?<ts>[^\\]]+)\\] EVENTO=(?<event>.+)$", RegexOptions.Compiled);
    private readonly IWebHostEnvironment _environment;

    public MetaWhatsAppAuditReportService(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    public WhatsAppAuditReportResult Query(string? user, DateTimeOffset? from, DateTimeOffset? to, string? actionType)
    {
        var baseDirectory = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, "..", "Arquivos e Documentos", "WhatsappLogMensagens"));
        if (!Directory.Exists(baseDirectory))
        {
            return new WhatsAppAuditReportResult { Items = [], Total = 0, GeneratedAt = DateTimeOffset.UtcNow };
        }

        var items = new List<WhatsAppAuditReportItem>();
        foreach (var file in Directory.EnumerateFiles(baseDirectory, "*.txt", SearchOption.TopDirectoryOnly))
        {
            ParseFile(file, items);
        }

        var filtered = items.Where(item =>
            (!from.HasValue || item.Timestamp >= from.Value)
            && (!to.HasValue || item.Timestamp <= to.Value)
            && (string.IsNullOrWhiteSpace(user) || string.Equals(item.User, user.Trim(), StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(actionType) || string.Equals(item.ActionType, actionType.Trim(), StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(static item => item.Timestamp)
            .ToList();

        return new WhatsAppAuditReportResult
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Total = filtered.Count,
            Items = filtered
        };
    }

    private static void ParseFile(string filePath, ICollection<WhatsAppAuditReportItem> target)
    {
        var lines = File.ReadAllLines(filePath);
        DateTimeOffset? currentTs = null;
        string? currentEvent = null;
        string? currentTraceId = null;
        string? currentPhone = null;
        string? currentUser = null;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (currentTs.HasValue && IsCriticalAction(currentEvent))
                {
                    target.Add(new WhatsAppAuditReportItem
                    {
                        Timestamp = currentTs.Value,
                        EventName = currentEvent ?? string.Empty,
                        ActionType = ToActionType(currentEvent),
                        User = currentUser ?? "desconhecido",
                        Phone = currentPhone,
                        TraceId = currentTraceId
                    });
                }

                currentTs = null;
                currentEvent = null;
                currentTraceId = null;
                currentPhone = null;
                currentUser = null;
                continue;
            }

            var eventMatch = EventRegex.Match(line);
            if (eventMatch.Success)
            {
                currentTs = DateTimeOffset.TryParse(eventMatch.Groups["ts"].Value, out var parsed)
                    ? parsed
                    : null;
                currentEvent = eventMatch.Groups["event"].Value.Trim();
                continue;
            }

            if (line.StartsWith("actor=", StringComparison.OrdinalIgnoreCase))
            {
                currentUser = line[6..].Trim();
                continue;
            }

            if (line.StartsWith("telefone=", StringComparison.OrdinalIgnoreCase))
            {
                currentPhone = line[9..].Trim();
                continue;
            }

            if (line.StartsWith("traceId=", StringComparison.OrdinalIgnoreCase))
            {
                currentTraceId = line[8..].Trim();
                continue;
            }

            if (line.StartsWith("audit=", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var json = line[6..].Trim();
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("actor", out var actorProp)
                        && actorProp.ValueKind == JsonValueKind.String
                        && string.IsNullOrWhiteSpace(currentUser))
                    {
                        currentUser = actorProp.GetString();
                    }
                }
                catch
                {
                    // ignore malformed metadata
                }
            }
        }
    }

    private static bool IsCriticalAction(string? eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName)) return false;
        var normalized = eventName.Trim().ToUpperInvariant();
        return normalized.Contains("MANUAL", StringComparison.Ordinal)
               || normalized.Contains("EXPORT", StringComparison.Ordinal)
               || normalized.Contains("ENCERR", StringComparison.Ordinal)
               || normalized.Contains("ALTERACAO", StringComparison.Ordinal)
               || normalized.Contains("PREFER", StringComparison.Ordinal)
               || normalized.Contains("PRESET", StringComparison.Ordinal);
    }

    private static string ToActionType(string? eventName)
    {
        var normalized = eventName?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalized.Contains("MANUAL", StringComparison.Ordinal)) return "envio_manual";
        if (normalized.Contains("EXPORT", StringComparison.Ordinal)) return "exportacao";
        if (normalized.Contains("ENCERR", StringComparison.Ordinal)) return "encerramento";
        return "alteracao";
    }
}
