using System.Collections.Concurrent;
using AplicacoesOnline.Models.MetaWhatsApp;

namespace AplicacoesOnline.Services.MetaWhatsApp;

public sealed class MetaWhatsAppViewerPresetService
{
    private readonly ConcurrentDictionary<string, WhatsAppViewerPresetRecord> _presets = new(StringComparer.OrdinalIgnoreCase);

    public (bool IsValid, string? ErrorCode, string? ErrorMessage) Validate(WhatsAppViewerPresetUpsertRequest request)
    {
        if (request.Version != WhatsAppViewerPresetContract.CurrentVersion)
        {
            return (false, "unsupported_version", $"Versão {request.Version} não suportada. Versão atual: {WhatsAppViewerPresetContract.CurrentVersion}.");
        }

        var op = request.Operator?.Trim();
        if (string.IsNullOrWhiteSpace(op))
        {
            return (false, "invalid_operator", "Campo operator é obrigatório.");
        }

        var activeTab = Normalize(request.ActiveTab, "all");
        if (!WhatsAppViewerPresetContract.AllowedTabs.Contains(activeTab, StringComparer.OrdinalIgnoreCase))
        {
            return (false, "invalid_active_tab", "active_tab inválida.");
        }

        var sortField = Normalize(request.Sorting?.Field, "last_event_at");
        if (!WhatsAppViewerPresetContract.AllowedSortFields.Contains(sortField, StringComparer.OrdinalIgnoreCase))
        {
            return (false, "invalid_sort_field", "sorting.field inválido.");
        }

        var sortDirection = Normalize(request.Sorting?.Direction, "desc");
        if (!WhatsAppViewerPresetContract.AllowedSortDirections.Contains(sortDirection, StringComparer.OrdinalIgnoreCase))
        {
            return (false, "invalid_sort_direction", "sorting.direction inválido.");
        }

        var quickSortMode = Normalize(request.QuickSortMode, "default");
        if (!WhatsAppViewerPresetContract.AllowedQuickSortModes.Contains(quickSortMode, StringComparer.OrdinalIgnoreCase))
        {
            return (false, "invalid_quick_sort_mode", "quick_sort_mode inválido.");
        }

        var viewerMode = Normalize(request.ViewerMode, "compact");
        if (!WhatsAppViewerPresetContract.AllowedViewerModes.Contains(viewerMode, StringComparer.OrdinalIgnoreCase))
        {
            return (false, "invalid_viewer_mode", "viewer_mode inválido.");
        }

        if (!TryParseDate(request.Filters?.From, out var from))
        {
            return (false, "invalid_from", "filters.from inválido.");
        }

        if (!TryParseDate(request.Filters?.To, out var to))
        {
            return (false, "invalid_to", "filters.to inválido.");
        }

        if (from.HasValue && to.HasValue && from.Value > to.Value)
        {
            return (false, "invalid_date_range", "filters.from não pode ser maior que filters.to.");
        }

        return (true, null, null);
    }

    public WhatsAppViewerPresetRecord Upsert(string scope, string sessionToken, WhatsAppViewerPresetUpsertRequest request)
    {
        var op = request.Operator!.Trim();
        var user = string.IsNullOrWhiteSpace(request.User) ? null : request.User.Trim();
        var key = BuildKey(scope, op);
        var now = DateTimeOffset.UtcNow;

        var record = new WhatsAppViewerPresetRecord
        {
            Version = request.Version,
            Operator = op,
            User = user,
            Session = scope.StartsWith("session:", StringComparison.OrdinalIgnoreCase) ? sessionToken : null,
            ActiveTab = Normalize(request.ActiveTab, "all"),
            Filters = new WhatsAppViewerPresetFilters
            {
                Status = request.Filters?.Status?.Trim(),
                EventType = request.Filters?.EventType?.Trim(),
                Operator = request.Filters?.Operator?.Trim(),
                TemplateCampaign = request.Filters?.TemplateCampaign?.Trim(),
                ErrorCode = request.Filters?.ErrorCode?.Trim(),
                From = NormalizeDateText(request.Filters?.From),
                To = NormalizeDateText(request.Filters?.To),
                OnlyUnread = request.Filters?.OnlyUnread ?? false,
                OnlySlaBreached = request.Filters?.OnlySlaBreached ?? false,
                HasActiveInteraction = request.Filters?.HasActiveInteraction ?? false
            },
            Sorting = new WhatsAppViewerPresetSorting
            {
                Field = Normalize(request.Sorting?.Field, "last_event_at"),
                Direction = Normalize(request.Sorting?.Direction, "desc")
            },
            QuickSortMode = Normalize(request.QuickSortMode, "default"),
            ViewerMode = Normalize(request.ViewerMode, "compact"),
            UpdatedAt = now
        };

        _presets[key] = record;
        return record;
    }

    public WhatsAppViewerPresetRecord? Get(string scope, string @operator)
    {
        var key = BuildKey(scope, @operator.Trim());
        return _presets.TryGetValue(key, out var record) ? record : null;
    }

    public static string ResolveScope(string? user, string? sessionToken)
    {
        if (!string.IsNullOrWhiteSpace(user))
        {
            return $"user:{user.Trim().ToLowerInvariant()}";
        }

        if (!string.IsNullOrWhiteSpace(sessionToken))
        {
            return $"session:{sessionToken}";
        }

        return string.Empty;
    }

    private static string BuildKey(string scope, string @operator)
        => $"{scope}|operator:{@operator.Trim().ToLowerInvariant()}";

    private static string Normalize(string? value, string fallback)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static bool TryParseDate(string? value, out DateTimeOffset? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value)) return true;

        if (!DateTimeOffset.TryParse(value.Trim(), out var dt)) return false;
        parsed = dt;
        return true;
    }

    private static string? NormalizeDateText(string? value)
    {
        if (!TryParseDate(value, out var parsed) || !parsed.HasValue)
        {
            return null;
        }

        return parsed.Value.ToString("o");
    }
}
