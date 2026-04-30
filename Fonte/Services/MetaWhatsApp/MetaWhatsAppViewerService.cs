using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using AplicacoesOnline.Models.MetaWhatsApp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AplicacoesOnline.Services.MetaWhatsApp;

public sealed class MetaWhatsAppViewerService
{
    private const string DefaultRelativeDirectory = "Arquivos e Documentos/WhatsappLogMensagens";
    private static readonly TimeSpan DefaultSlaThreshold = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StreamIndexRefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProjectionRefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly Encoding Utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly HashSet<string> InboundEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "WEBHOOK_META_RECEBIDO",
        "CLIENTE_RESPONDEU",
        "RESPOSTA_CLASSIFICADA",
        "MIDIA_RECEBIDA"
    };

    private static readonly HashSet<string> OutboundEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "ENVIO_ACEITO_META",
        "DOCUMENTOS_ENVIADOS",
        "RESUMO_ENVIADO",
        "AVISO_SEM_TITULOS_ENVIADO",
        "ATENDIMENTO_MANUAL_ENVIADO",
        "RESPOSTA_FORA_PADRAO_ENVIADA"
    };
    private static readonly HashSet<string> TechnicalOnlyEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "EXECUTION_CONTEXT_REGISTRADO",
        "META_STATUS_AUDITADO_SEM_DISPARO",
        "ENVIO_ACEITO_META",
        "DOCUMENTOS_ENVIADOS",
        "MIDIA_RECEBIDA",
        "CORRELACAO_NAO_RESOLVIDA"
    };
    private static readonly string[] TechnicalOnlyHints =
    {
        "atualização de envio recebida no endpoint de status",
        "atualizacao de envio recebida no endpoint de status",
        "contexto de execução registrado para consulta por execution_id",
        "contexto de execucao registrado para consulta por execution_id",
        "template não enviado: etapa atual não está mapeada para disparo",
        "template nao enviado: etapa atual nao esta mapeada para disparo",
        "mensagem com mídia/documento detectada no webhook da meta",
        "mensagem com midia/documento detectada no webhook da meta",
        "webhook recebido, porém sem correlação para interação ativa",
        "webhook recebido, porem sem correlacao para interacao ativa"
    };

    private readonly string? _configuredDirectory;
    private readonly string _contentRootPath;
    private readonly bool _isProduction;
    private readonly object _projectionSync = new();
    private readonly object _streamIndexSync = new();
    private readonly Dictionary<string, FileProjectionCacheEntry> _fileProjectionByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _phoneToFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ConversationProjection> _projectionByPhone = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StreamIndexedFile> _streamIndexByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<MetaWhatsAppViewerService> _logger;
    private DateTimeOffset _streamIndexLastRefreshUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _projectionLastRefreshUtc = DateTimeOffset.MinValue;
    private string? _streamIndexDirectory;
    private string? _projectionDirectory;
    public MetaWhatsAppViewerService(
        IOptions<MetaWhatsAppWebhookOptions> options,
        IWebHostEnvironment environment,
        ILogger<MetaWhatsAppViewerService> logger)
    {
        _configuredDirectory = options.Value.PersistentLogDirectory;
        _contentRootPath = environment.ContentRootPath;
        _isProduction = environment.IsProduction();
        _logger = logger;
    }

    public WhatsAppViewerConversationPage GetConversations(string? search, int limit, string? cursor, WhatsAppViewerConversationFilters? filters = null)
    {
        filters ??= new WhatsAppViewerConversationFilters();
        EnsureProjectionCache();

        var safeLimit = Math.Clamp(limit, 1, 500);
        var pageCandidates = new List<WhatsAppViewerConversationSummary>(safeLimit + 1);
        var cursorKey = ParseConversationListCursor(cursor);
        lock (_projectionSync)
        {
            foreach (var projection in _projectionByPhone.Values)
            {
                var summary = projection.Summary;
                if (!MatchesSearch(summary, search) || !MatchesFilters(summary, filters))
                {
                    continue;
                }

                if (!IsAfterConversationCursor(summary, cursorKey))
                {
                    continue;
                }

                pageCandidates.Add(summary);
                pageCandidates.Sort(CompareConversationSummaries);
                if (pageCandidates.Count > safeLimit + 1)
                {
                    pageCandidates.RemoveAt(pageCandidates.Count - 1);
                }
            }
        }

        var hasMore = pageCandidates.Count > safeLimit;
        if (hasMore)
        {
            pageCandidates.RemoveAt(pageCandidates.Count - 1);
        }

        var nextCursor = hasMore && pageCandidates.Count > 0
            ? BuildConversationListCursor(pageCandidates[^1])
            : null;

        return new WhatsAppViewerConversationPage
        {
            Conversations = pageCandidates,
            NextCursor = nextCursor
        };
    }

    public WhatsAppViewerConversationTimeline GetConversationTimeline(string phone)
    {
        var normalizedPhone = NormalizeDigits(phone);
        EnsureProjectionCache();
        lock (_projectionSync)
        {
            if (!string.IsNullOrWhiteSpace(normalizedPhone)
                && _projectionByPhone.TryGetValue(normalizedPhone, out var projection))
            {
                projection.Touch();
                return new WhatsAppViewerConversationTimeline
                {
                    Phone = normalizedPhone,
                    DisplayName = projection.Summary.DisplayName,
                    EventCount = projection.Timeline.Length,
                    Events = projection.Timeline.ToList()
                };
            }

            return new WhatsAppViewerConversationTimeline
            {
                Phone = normalizedPhone ?? phone,
                DisplayName = null,
                EventCount = 0,
                Events = new List<WhatsAppViewerEventItem>()
            };
        }
    }

    public WhatsAppViewerConversationTimelinePage GetConversationTimelinePage(
        string phone,
        DateTimeOffset? fromEventTime,
        int pageSize,
        string? cursor)
    {
        var normalizedPhone = NormalizeDigits(phone);
        EnsureProjectionCache();

        var safePageSize = Math.Clamp(pageSize, 1, 500);
        lock (_projectionSync)
        {
            if (string.IsNullOrWhiteSpace(normalizedPhone)
                || !_projectionByPhone.TryGetValue(normalizedPhone, out var projection))
            {
                return new WhatsAppViewerConversationTimelinePage
                {
                    Phone = normalizedPhone ?? phone,
                    DisplayName = null,
                    EventCount = 0,
                    Events = new List<WhatsAppViewerEventItem>(),
                    NextCursor = null
                };
            }

            projection.Touch();
            var filtered = projection.Timeline
                .Where(item => !fromEventTime.HasValue || item.Timestamp >= fromEventTime.Value)
                .ToArray();

            var cursorKey = ParseConversationTimelineCursor(cursor);
            var eligible = filtered
                .Where(item => IsBeforeTimelineCursor(item, cursorKey))
                .ToArray();
            var endIndex = eligible.Length;
            var startIndex = Math.Max(0, endIndex - safePageSize);
            var page = eligible[startIndex..endIndex];
            var nextCursor = startIndex > 0 && page.Length > 0
                ? BuildConversationTimelineCursor(page[0])
                : null;

            return new WhatsAppViewerConversationTimelinePage
            {
                Phone = normalizedPhone,
                DisplayName = projection.Summary.DisplayName,
                EventCount = filtered.Length,
                Events = page.ToList(),
                NextCursor = nextCursor
            };
        }
    }

    public WhatsAppViewerConversationSearchPage SearchConversationTimeline(
        string phone,
        string? text,
        string? status,
        string? interactionId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int pageSize,
        string? cursor)
    {
        var normalizedPhone = NormalizeDigits(phone);
        EnsureProjectionCache();
        var safePageSize = Math.Clamp(pageSize, 1, 500);
        var safeText = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        var safeStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim();
        var safeInteractionId = string.IsNullOrWhiteSpace(interactionId) ? null : interactionId.Trim();

        lock (_projectionSync)
        {
            if (string.IsNullOrWhiteSpace(normalizedPhone)
                || !_projectionByPhone.TryGetValue(normalizedPhone, out var projection))
            {
                return new WhatsAppViewerConversationSearchPage
                {
                    Phone = normalizedPhone ?? phone,
                    DisplayName = null,
                    TotalMatches = 0
                };
            }

            projection.Touch();
            var matches = projection.Timeline.Where(evt =>
                    (!from.HasValue || evt.Timestamp >= from.Value)
                    && (!to.HasValue || evt.Timestamp <= to.Value)
                    && (safeStatus is null || MatchesSearchStatus(evt, safeStatus))
                    && (safeInteractionId is null || MatchesInteractionId(evt, safeInteractionId))
                    && (safeText is null || MatchesSearchText(evt, safeText)))
                .ToArray();

            var cursorKey = ParseConversationTimelineCursor(cursor);
            var eligible = matches
                .Where(item => IsBeforeTimelineCursor(item, cursorKey))
                .ToArray();
            if (eligible.Length == 0)
            {
                return new WhatsAppViewerConversationSearchPage
                {
                    Phone = normalizedPhone,
                    DisplayName = projection.Summary.DisplayName,
                    TotalMatches = matches.Length,
                    Events = new List<WhatsAppViewerEventItem>(),
                    NextCursor = null
                };
            }

            var endIndex = eligible.Length;
            var startIndex = Math.Max(0, endIndex - safePageSize);
            var page = eligible[startIndex..endIndex].ToList();
            var nextCursor = startIndex > 0 && page.Count > 0
                ? BuildConversationTimelineCursor(page[0])
                : null;

            return new WhatsAppViewerConversationSearchPage
            {
                Phone = normalizedPhone,
                DisplayName = projection.Summary.DisplayName,
                TotalMatches = matches.Length,
                Events = page,
                NextCursor = nextCursor
            };
        }
    }

    public WhatsAppViewerMetricsResult GetMetrics(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? phone,
        string? status,
        int windowMinutes,
        string? operatorProfile = null)
    {
        EnsureProjectionCache();
        var normalizedPhone = NormalizeDigits(phone);
        var safeWindowMinutes = Math.Clamp(windowMinutes, 1, 24 * 60);
        var safeStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim();
        var safeOperatorProfile = string.IsNullOrWhiteSpace(operatorProfile) ? null : operatorProfile.Trim();

        List<WhatsAppViewerEventItem> allEvents;
        lock (_projectionSync)
        {
            if (!string.IsNullOrWhiteSpace(normalizedPhone))
            {
                allEvents = _projectionByPhone.TryGetValue(normalizedPhone, out var conversation)
                    ? conversation.Timeline.ToList()
                    : new List<WhatsAppViewerEventItem>();
            }
            else
            {
                allEvents = _projectionByPhone.Values
                    .SelectMany(static projection => projection.Timeline)
                    .ToList();
            }
        }

        var filtered = allEvents
            .Where(evt => (!from.HasValue || evt.Timestamp >= from.Value)
                          && (!to.HasValue || evt.Timestamp <= to.Value)
                          && (safeStatus is null || MatchesStatus(evt, safeStatus))
                          && (safeOperatorProfile is null || MatchesOperatorProfile(evt, safeOperatorProfile)))
            .OrderBy(evt => evt.Timestamp)
            .ToArray();

        var totals = ComputeMetricsCounters(filtered);
        var buckets = BuildMetricBuckets(filtered, safeWindowMinutes);
        var averageFirstResponseTimeSeconds = ComputeAverageFirstResponseTimeSeconds(filtered);
        var interactionResolutionRate = ComputeInteractionResolutionRate(filtered);
        var failureRateByTemplateChannel = BuildFailureRateByTemplateAndChannel(filtered);
        var backlogByAge = BuildBacklogByAge(filtered, DateTimeOffset.UtcNow);

        return new WhatsAppViewerMetricsResult
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            From = from,
            To = to,
            Phone = normalizedPhone,
            Status = safeStatus,
            OperatorProfile = safeOperatorProfile,
            WindowMinutes = safeWindowMinutes,
            TotalSent = totals.TotalSent,
            TotalResponses = totals.TotalResponses,
            TotalInternalActions = totals.TotalInternalActions,
            SendFailureRate = totals.SendFailureRate,
            AverageResponseTimeSeconds = totals.AverageResponseTimeSeconds,
            AverageFirstResponseTimeSeconds = averageFirstResponseTimeSeconds,
            InteractionResolutionRate = interactionResolutionRate,
            FailureRateByTemplateChannel = failureRateByTemplateChannel,
            BacklogByAge = backlogByAge,
            Buckets = buckets
        };
    }

    public WhatsAppViewerConversationAggregates GetConversationAggregates(string phone, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var normalizedPhone = NormalizeDigits(phone);
        EnsureProjectionCache();

        lock (_projectionSync)
        {
            if (string.IsNullOrWhiteSpace(normalizedPhone)
                || !_projectionByPhone.TryGetValue(normalizedPhone, out var projection))
            {
                return new WhatsAppViewerConversationAggregates
                {
                    Phone = normalizedPhone ?? phone
                };
            }

            projection.Touch();
            var scoped = projection.Timeline
                .Where(evt => (!from.HasValue || evt.Timestamp >= from.Value)
                              && (!to.HasValue || evt.Timestamp <= to.Value))
                .ToArray();

            return new WhatsAppViewerConversationAggregates
            {
                Phone = normalizedPhone,
                DisplayName = projection.Summary.DisplayName,
                EventCount = scoped.Length,
                InboundCount = scoped.Count(static evt => string.Equals(evt.Direction, "inbound", StringComparison.OrdinalIgnoreCase)),
                OutboundCount = scoped.Count(static evt => string.Equals(evt.Direction, "outbound", StringComparison.OrdinalIgnoreCase)),
                InternalCount = scoped.Count(static evt => string.Equals(evt.Direction, "internal", StringComparison.OrdinalIgnoreCase)),
                LastEventAt = scoped.LastOrDefault()?.Timestamp
            };
        }
    }

    public WhatsAppViewerStreamBatch GetConversationDeltas(string? phone, string? consumerId, string? cursorToken, int pageSize)
    {
        var baseDirectory = ResolveBaseDirectory();
        var safePageSize = Math.Clamp(pageSize, 1, 500);
        if (!Directory.Exists(baseDirectory))
        {
            return new WhatsAppViewerStreamBatch(Array.Empty<WhatsAppViewerEventItem>(), BuildCursorToken(new StreamCursorEnvelope
            {
                Version = 1,
                ConsumerId = consumerId,
                Phone = NormalizeDigits(phone)
            }), false, false, 0);
        }

        var normalizedPhone = NormalizeDigits(phone);
        var resetDetected = false;
        var cursorEnvelope = ParseCursorToken(cursorToken);
        if (cursorEnvelope is null)
        {
            cursorEnvelope = new StreamCursorEnvelope();
            if (!string.IsNullOrWhiteSpace(cursorToken))
            {
                resetDetected = true;
            }
        }

        if (!string.Equals(cursorEnvelope.ConsumerId, consumerId, StringComparison.Ordinal)
            || !string.Equals(cursorEnvelope.Phone, normalizedPhone, StringComparison.OrdinalIgnoreCase))
        {
            cursorEnvelope = new StreamCursorEnvelope();
            resetDetected = true;
        }

        cursorEnvelope.Version = 1;
        cursorEnvelope.ConsumerId = consumerId;
        cursorEnvelope.Phone = normalizedPhone;

        var buffered = DequeueBufferedEvents(cursorEnvelope, safePageSize);
        AssignStreamEventIds(cursorEnvelope, buffered);
        if (buffered.Count > 0)
        {
            return new WhatsAppViewerStreamBatch(buffered, BuildCursorToken(cursorEnvelope), cursorEnvelope.BufferedEvents.Count > 0, resetDetected, cursorEnvelope.LastIssuedEventId);
        }

        var indexedFiles = GetStreamIndexedFiles(baseDirectory, cursorEnvelope);
        var deltas = new List<WhatsAppViewerEventItem>();
        foreach (var file in indexedFiles)
        {
            deltas.AddRange(ParseFileDeltas(file.Path, file, normalizedPhone, cursorEnvelope));
        }

        var ordered = deltas.OrderBy(static item => item.Timestamp).ToArray();
        AssignStreamEventIds(cursorEnvelope, ordered);
        if (ordered.Length <= safePageSize)
        {
            return new WhatsAppViewerStreamBatch(ordered, BuildCursorToken(cursorEnvelope), false, resetDetected, cursorEnvelope.LastIssuedEventId);
        }

        var page = ordered.Take(safePageSize).ToArray();
        cursorEnvelope.BufferedEvents = ordered.Skip(safePageSize).ToList();
        return new WhatsAppViewerStreamBatch(page, BuildCursorToken(cursorEnvelope), true, resetDetected, cursorEnvelope.LastIssuedEventId);
    }

    private static void AssignStreamEventIds(StreamCursorEnvelope cursorEnvelope, IEnumerable<WhatsAppViewerEventItem> events)
    {
        foreach (var evt in events)
        {
            cursorEnvelope.LastIssuedEventId += 1;
            evt.EventId = cursorEnvelope.LastIssuedEventId;
        }
    }

    public WhatsAppViewerDiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        EnsureProjectionCache();
        lock (_projectionSync)
        {
            return new WhatsAppViewerDiagnosticsSnapshot
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                ProjectionLastRefreshUtc = _projectionLastRefreshUtc,
                StreamIndexLastRefreshUtc = _streamIndexLastRefreshUtc,
                TrackedFiles = _fileProjectionByPath.Count,
                TrackedPhones = _projectionByPhone.Count,
                StreamIndexedFiles = _streamIndexByPath.Count
            };
        }
    }

    private List<WhatsAppViewerEventItem> ParseFile(string file)
    {
        var items = new List<WhatsAppViewerEventItem>();
        var lines = ReadAllLinesWithFallback(file);
        var state = new ParseState();
        ParseLines(lines, file, items, state);
        state.Flush(file, items);
        return items;
    }

    private void EnsureProjectionCache()
    {
        var baseDirectory = ResolveBaseDirectory();
        lock (_projectionSync)
        {
            var now = DateTimeOffset.UtcNow;
            if (string.Equals(_projectionDirectory, baseDirectory, StringComparison.OrdinalIgnoreCase)
                && now - _projectionLastRefreshUtc < ProjectionRefreshInterval)
            {
                return;
            }
            _projectionDirectory = baseDirectory;

            var startedAt = DateTimeOffset.UtcNow;
            if (!Directory.Exists(baseDirectory))
            {
                InvalidateAllCaches();
                _projectionLastRefreshUtc = now;
                return;
            }

            var discoveredFiles = Directory
                .EnumerateFiles(baseDirectory, "*.txt", SearchOption.TopDirectoryOnly)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var removedFiles = _fileProjectionByPath.Keys
                .Where(path => !discoveredFiles.Contains(path))
                .ToArray();
            foreach (var removedFile in removedFiles)
            {
                RemoveFileProjection(removedFile);
            }

            foreach (var file in discoveredFiles)
            {
                var info = new FileInfo(file);
                if (!info.Exists)
                {
                    RemoveFileProjection(file);
                    continue;
                }

                var lastWriteTimeUtc = info.LastWriteTimeUtc;
                var length = info.Length;
                if (_fileProjectionByPath.TryGetValue(file, out var cached)
                    && cached.LastWriteTimeUtc == lastWriteTimeUtc
                    && cached.Cursor == length)
                {
                    cached.Touch();
                    continue;
                }

                var events = ParseFile(file);
                UpsertFileProjection(file, events, lastWriteTimeUtc, length);
            }

            EnforceMemoryLimits();
            _projectionLastRefreshUtc = now;
            var elapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            if (elapsedMs >= 50)
            {
                _logger.LogInformation(
                    "Viewer projection refresh in {ElapsedMs} ms | files={Files} phones={Phones}",
                    Math.Round(elapsedMs, 1),
                    _fileProjectionByPath.Count,
                    _projectionByPhone.Count);
            }
        }
    }

    private void UpsertFileProjection(string file, IReadOnlyList<WhatsAppViewerEventItem> events, DateTime lastWriteTimeUtc, long cursor)
    {
        if (_fileProjectionByPath.TryGetValue(file, out var existing))
        {
            foreach (var previousPhone in existing.EventsByPhone.Keys)
            {
                if (_phoneToFiles.TryGetValue(previousPhone, out var files))
                {
                    files.Remove(file);
                    if (files.Count == 0)
                    {
                        _phoneToFiles.Remove(previousPhone);
                    }
                }
            }
        }

        var eventsByPhone = events
            .GroupBy(static item => item.Phone, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderBy(static item => item.Timestamp).ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var projection = new FileProjectionCacheEntry
        {
            Path = file,
            LastWriteTimeUtc = lastWriteTimeUtc,
            Cursor = cursor,
            EventsByPhone = eventsByPhone
        };
        projection.Touch();
        _fileProjectionByPath[file] = projection;
        var affectedPhones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var phone in eventsByPhone.Keys)
        {
            affectedPhones.Add(phone);
            if (!_phoneToFiles.TryGetValue(phone, out var files))
            {
                files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _phoneToFiles[phone] = files;
            }

            files.Add(file);
        }

        if (existing is not null)
        {
            foreach (var phone in existing.EventsByPhone.Keys)
            {
                affectedPhones.Add(phone);
            }
        }

        foreach (var phone in affectedPhones)
        {
            RebuildPhoneProjection(phone);
        }
    }

    private void RemoveFileProjection(string file)
    {
        if (!_fileProjectionByPath.Remove(file, out var removed))
        {
            return;
        }

        var affectedPhones = removed.EventsByPhone.Keys.ToArray();
        foreach (var phone in affectedPhones)
        {
            if (_phoneToFiles.TryGetValue(phone, out var files))
            {
                files.Remove(file);
                if (files.Count == 0)
                {
                    _phoneToFiles.Remove(phone);
                }
            }

            RebuildPhoneProjection(phone);
        }
    }

    private void RebuildPhoneProjection(string phone)
    {
        if (!_phoneToFiles.TryGetValue(phone, out var files) || files.Count == 0)
        {
            _projectionByPhone.Remove(phone);
            return;
        }

        var events = new List<WhatsAppViewerEventItem>();
        foreach (var file in files)
        {
            if (_fileProjectionByPath.TryGetValue(file, out var fileProjection)
                && fileProjection.EventsByPhone.TryGetValue(phone, out var fromFile))
            {
                fileProjection.Touch();
                events.AddRange(fromFile);
            }
        }

        if (events.Count == 0)
        {
            _projectionByPhone.Remove(phone);
            return;
        }

        var ordered = events.OrderBy(static item => item.Timestamp).ToArray();
        var summary = BuildSummary(ordered);
        var conversationProjection = new ConversationProjection(summary, ordered);
        conversationProjection.Touch();
        _projectionByPhone[phone] = conversationProjection;
    }

    private static WhatsAppViewerConversationSummary BuildSummary(IReadOnlyList<WhatsAppViewerEventItem> events)
    {
        var latest = events.OrderByDescending(static item => item.Timestamp).First();
        var latestRelevant = events
            .Where(IsConversationRelevantEvent)
            .OrderByDescending(static item => item.Timestamp)
            .FirstOrDefault();
        var displayEvent = latestRelevant ?? latest;
        var conversationStatus = ResolveConversationStatus(events);
        var isUnread = ResolveIsUnread(events);
        var hasActiveInteraction = ResolveHasActiveInteraction(events, conversationStatus);
        var isSlaBreached = ResolveSlaBreached(events, isUnread);
        var waitingMinutes = ResolveWaitingMinutes(events, isUnread);
        var hasRecentFailure = ResolveHasRecentFailure(events);
        var criticityWeight = ResolveCriticityWeight(events);
        var operatorName = ResolveLatestEventField(events, "operator", "operator_name", "perfil", "profile", "actor", "responsavel", "responsavel_nome");
        var templateOrCampaign = ResolveLatestEventField(events, "template", "template_name", "campaign", "campaign_name", "campanha", "campaign_id", "campaign_code");
        var errorCode = ResolveLatestEventField(events, "error_code", "status_error_code", "erro_codigo", "codigo_erro", "error", "erro");
        var priorityScore = ResolvePriorityScore(
            isSlaBreached,
            waitingMinutes,
            hasRecentFailure,
            isUnread,
            criticityWeight,
            hasActiveInteraction);
        return new WhatsAppViewerConversationSummary
        {
            Phone = displayEvent.Phone,
            DisplayName = ResolveDisplayName(events),
            LastEventAt = displayEvent.Timestamp,
            LastEventName = displayEvent.EventName,
            LastMessagePreview = displayEvent.MainText,
            LastInteractionId = displayEvent.InteractionId,
            EventCount = events.Count,
            ConversationStatus = conversationStatus,
            HasActiveInteraction = hasActiveInteraction,
            IsUnread = isUnread,
            IsSlaBreached = isSlaBreached,
            WaitingMinutes = waitingMinutes,
            HasRecentFailure = hasRecentFailure,
            PriorityScore = priorityScore,
            PriorityTier = ResolvePriorityTier(priorityScore),
            EventType = displayEvent.EventName,
            OperatorName = operatorName,
            TemplateOrCampaign = templateOrCampaign,
            ErrorCode = errorCode
        };
    }

    private void EnforceMemoryLimits()
    {
        var maxFileEntries = _isProduction ? 250 : 1000;
        if (_fileProjectionByPath.Count <= maxFileEntries)
        {
            return;
        }

        var evictionQueue = _fileProjectionByPath.Values
            .OrderBy(static item => item.LastAccessUtc)
            .Take(_fileProjectionByPath.Count - maxFileEntries)
            .Select(static item => item.Path)
            .ToArray();

        foreach (var path in evictionQueue)
        {
            RemoveFileProjection(path);
        }
    }

    private void InvalidateAllCaches()
    {
        _fileProjectionByPath.Clear();
        _phoneToFiles.Clear();
        _projectionByPhone.Clear();
    }

    private IReadOnlyList<WhatsAppViewerEventItem> ParseFileDeltas(string file, StreamIndexedFile indexedFile, string? normalizedPhone, StreamCursorEnvelope cursorEnvelope)
    {
        if (!cursorEnvelope.Files.TryGetValue(file, out var state))
        {
            state = new StreamFileCursorState
            {
                Position = indexedFile.Length,
                LastWriteTimeUtc = indexedFile.LastWriteTimeUtc
            };
            cursorEnvelope.Files[file] = state;
            return Array.Empty<WhatsAppViewerEventItem>();
        }

        if (indexedFile.LastWriteTimeUtc == state.LastWriteTimeUtc
            && indexedFile.Length == state.Position)
        {
            return Array.Empty<WhatsAppViewerEventItem>();
        }

        if (indexedFile.Length < state.Position)
        {
            state.Position = 0;
            state.PendingBlock = string.Empty;
        }

        if (indexedFile.Length == state.Position)
        {
            state.LastWriteTimeUtc = indexedFile.LastWriteTimeUtc;
            return Array.Empty<WhatsAppViewerEventItem>();
        }

        string appended;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            appended = ReadSegmentTextWithFallback(stream, state.Position);
            state.Position = stream.Length;
            state.LastWriteTimeUtc = indexedFile.LastWriteTimeUtc;
        }
        catch (IOException)
        {
            cursorEnvelope.Files.Remove(file);
            return Array.Empty<WhatsAppViewerEventItem>();
        }

        if (string.IsNullOrWhiteSpace(appended) && string.IsNullOrWhiteSpace(state.PendingBlock))
        {
            return Array.Empty<WhatsAppViewerEventItem>();
        }

        var merged = string.IsNullOrWhiteSpace(state.PendingBlock) ? appended : state.PendingBlock + appended;
        var lines = merged.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var hasTrailingBreak = merged.EndsWith('\n');
        if (!hasTrailingBreak)
        {
            state.PendingBlock = lines[^1];
            lines = lines[..^1];
        }
        else
        {
            state.PendingBlock = string.Empty;
        }

        if (lines.Length == 0)
        {
            return Array.Empty<WhatsAppViewerEventItem>();
        }

        var parsed = new List<WhatsAppViewerEventItem>();
        var parseState = new ParseState();
        ParseLines(lines, file, parsed, parseState);
        if (hasTrailingBreak)
        {
            parseState.Flush(file, parsed);
        }

        if (!string.IsNullOrWhiteSpace(normalizedPhone))
        {
            return parsed
                .Where(item => string.Equals(item.Phone, normalizedPhone, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        return parsed;
    }

    private List<WhatsAppViewerEventItem> DequeueBufferedEvents(StreamCursorEnvelope cursorEnvelope, int pageSize)
    {
        if (cursorEnvelope.BufferedEvents.Count == 0)
        {
            return new List<WhatsAppViewerEventItem>();
        }

        if (cursorEnvelope.BufferedEvents.Count <= pageSize)
        {
            var all = cursorEnvelope.BufferedEvents.ToList();
            cursorEnvelope.BufferedEvents.Clear();
            return all;
        }

        var page = cursorEnvelope.BufferedEvents.Take(pageSize).ToList();
        cursorEnvelope.BufferedEvents = cursorEnvelope.BufferedEvents.Skip(pageSize).ToList();
        return page;
    }

    private IReadOnlyList<StreamIndexedFile> GetStreamIndexedFiles(string baseDirectory, StreamCursorEnvelope cursorEnvelope)
    {
        lock (_streamIndexSync)
        {
            RefreshStreamIndex(baseDirectory, cursorEnvelope);
            return _streamIndexByPath
                .Select(static pair => pair.Value with { Path = pair.Key })
                .ToArray();
        }
    }

    private void RefreshStreamIndex(string baseDirectory, StreamCursorEnvelope cursorEnvelope)
    {
        var now = DateTimeOffset.UtcNow;
        if (string.Equals(_streamIndexDirectory, baseDirectory, StringComparison.OrdinalIgnoreCase)
            && now - _streamIndexLastRefreshUtc < StreamIndexRefreshInterval)
        {
            return;
        }

        _streamIndexDirectory = baseDirectory;
        _streamIndexLastRefreshUtc = now;
        var discoveredFiles = Directory.EnumerateFiles(baseDirectory, "*.txt", SearchOption.TopDirectoryOnly).ToArray();
        var discovered = new HashSet<string>(discoveredFiles, StringComparer.OrdinalIgnoreCase);

        foreach (var removed in _streamIndexByPath.Keys.Where(path => !discovered.Contains(path)).ToArray())
        {
            _streamIndexByPath.Remove(removed);
            cursorEnvelope.Files.Remove(removed);
        }

        foreach (var file in discoveredFiles)
        {
            var info = new FileInfo(file);
            if (!info.Exists)
            {
                _streamIndexByPath.Remove(file);
                cursorEnvelope.Files.Remove(file);
                continue;
            }

            var lastWriteTimeUtc = info.LastWriteTimeUtc;
            if (_streamIndexByPath.TryGetValue(file, out var cached)
                && cached.LastWriteTimeUtc == lastWriteTimeUtc
                && cached.Length == info.Length)
            {
                continue;
            }

            _streamIndexByPath[file] = new StreamIndexedFile
            {
                LastWriteTimeUtc = lastWriteTimeUtc,
                Length = info.Length
            };
        }
    }

    private static StreamCursorEnvelope? ParseCursorToken(string? cursorToken)
    {
        if (string.IsNullOrWhiteSpace(cursorToken))
        {
            return null;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(cursorToken));
            var parsed = JsonSerializer.Deserialize<StreamCursorEnvelope>(json);
            if (parsed is null)
            {
                return null;
            }

            parsed.Files ??= new Dictionary<string, StreamFileCursorState>(StringComparer.OrdinalIgnoreCase);
            parsed.BufferedEvents ??= new List<WhatsAppViewerEventItem>();
            return parsed;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildCursorToken(StreamCursorEnvelope cursorEnvelope)
    {
        cursorEnvelope.Files ??= new Dictionary<string, StreamFileCursorState>(StringComparer.OrdinalIgnoreCase);
        var json = JsonSerializer.Serialize(cursorEnvelope);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static string[] ReadAllLinesWithFallback(string file)
    {
        try
        {
            return File.ReadAllLines(file, Utf8Strict);
        }
        catch (DecoderFallbackException)
        {
            return File.ReadAllLines(file, Encoding.Latin1);
        }
    }

    private static string ReadSegmentTextWithFallback(FileStream stream, long position)
    {
        stream.Seek(position, SeekOrigin.Begin);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        try
        {
            return Utf8Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static void ParseLines(IEnumerable<string> lines, string file, ICollection<WhatsAppViewerEventItem> items, ParseState state)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(line))
            {
                state.Flush(file, items);
                state.ResetCurrentEvent();
                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal) && line.Contains("] EVENTO=", StringComparison.Ordinal))
            {
                state.Flush(file, items);
                state.StartNewHeader(line);
                continue;
            }

            state.ReadField(line);
        }
    }

    private static string ClassifyDirection(string eventName, IReadOnlyDictionary<string, string?> fields)
    {
        var explicitDirection = FirstNonEmpty(TryGet(fields, "direction"), TryGet(fields, "direcao"), TryGet(fields, "message_direction"));
        if (string.Equals(explicitDirection, "inbound", StringComparison.OrdinalIgnoreCase)
            || string.Equals(explicitDirection, "entrada", StringComparison.OrdinalIgnoreCase))
        {
            return "inbound";
        }

        if (string.Equals(explicitDirection, "outbound", StringComparison.OrdinalIgnoreCase)
            || string.Equals(explicitDirection, "saida", StringComparison.OrdinalIgnoreCase)
            || string.Equals(explicitDirection, "saída", StringComparison.OrdinalIgnoreCase))
        {
            return "outbound";
        }

        if (InboundEvents.Contains(eventName))
        {
            return "inbound";
        }

        if (OutboundEvents.Contains(eventName) || eventName.StartsWith("ENVIO_", StringComparison.OrdinalIgnoreCase))
        {
            return "outbound";
        }

        if (eventName.StartsWith("META_STATUS_", StringComparison.OrdinalIgnoreCase))
        {
            return "meta";
        }

        return "system";
    }

    private static bool IsSendEvent(WhatsAppViewerEventItem evt)
    {
        if (IsTechnicalOnlyEvent(evt))
        {
            return false;
        }

        if (string.Equals(evt.Direction, "outbound", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(evt.Category, "sent", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return evt.EventName.StartsWith("ENVIO_", StringComparison.OrdinalIgnoreCase)
               || evt.EventName.EndsWith("_ENVIADO", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsResponseEvent(WhatsAppViewerEventItem evt)
        => !IsTechnicalOnlyEvent(evt)
           && (string.Equals(evt.Direction, "inbound", StringComparison.OrdinalIgnoreCase)
               || string.Equals(evt.Category, "customer", StringComparison.OrdinalIgnoreCase));

    private static bool IsConversationRelevantEvent(WhatsAppViewerEventItem evt)
    {
        if (IsTechnicalOnlyEvent(evt))
        {
            return false;
        }

        if (IsSendEvent(evt) || IsResponseEvent(evt))
        {
            return true;
        }

        return evt.EventName.StartsWith("INTERACAO_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTechnicalOnlyEvent(WhatsAppViewerEventItem evt)
    {
        var eventName = evt.EventName ?? string.Empty;
        if (TechnicalOnlyEvents.Contains(eventName))
        {
            return true;
        }

        var normalizedText = (evt.MainText ?? string.Empty).Trim().ToLowerInvariant();
        return TechnicalOnlyHints.Any(normalizedText.Contains);
    }

    private static bool IsInternalActionEvent(WhatsAppViewerEventItem evt)
    {
        if (evt.EventName.StartsWith("INTERACAO_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(evt.Category, "system", StringComparison.OrdinalIgnoreCase)
               && !IsSendEvent(evt)
               && !IsResponseEvent(evt);
    }

    private static bool IsFailedSendEvent(WhatsAppViewerEventItem evt)
    {
        if (!IsSendEvent(evt))
        {
            return false;
        }

        var status = FirstNonEmpty(TryGet(evt.Fields, "status"), TryGet(evt.Fields, "event_type")) ?? string.Empty;
        if (string.Equals(evt.Category, "error", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return evt.EventName.Contains("FALH", StringComparison.OrdinalIgnoreCase)
               || evt.EventName.Contains("ERRO", StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, "ERROR", StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, "REJECTED", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesStatus(WhatsAppViewerEventItem evt, string status)
    {
        var itemStatus = FirstNonEmpty(TryGet(evt.Fields, "status"), TryGet(evt.Fields, "event_type"));
        if (!string.IsNullOrWhiteSpace(itemStatus)
            && string.Equals(itemStatus.Trim(), status, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(evt.EventName, status, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesSearchStatus(WhatsAppViewerEventItem evt, string status)
    {
        var normalized = status.Trim();
        var itemStatus = FirstNonEmpty(TryGet(evt.Fields, "status"), TryGet(evt.Fields, "event_type"), evt.EventName) ?? string.Empty;
        return itemStatus.Contains(normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesInteractionId(WhatsAppViewerEventItem evt, string interactionId)
    {
        var normalized = interactionId.Trim();
        var raw = FirstNonEmpty(evt.InteractionId, TryGet(evt.Fields, "interaction_id")) ?? string.Empty;
        return raw.Contains(normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesSearchText(WhatsAppViewerEventItem evt, string text)
    {
        var normalized = text.Trim();
        var chunks = new[]
        {
            evt.MainText,
            evt.EventName,
            TryGet(evt.Fields, "text"),
            TryGet(evt.Fields, "texto"),
            TryGet(evt.Fields, "resposta_raw"),
            TryGet(evt.Fields, "detalhes"),
            TryGet(evt.Fields, "status")
        };
        return chunks.Any(chunk => !string.IsNullOrWhiteSpace(chunk)
            && chunk.Contains(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesOperatorProfile(WhatsAppViewerEventItem evt, string operatorProfile)
    {
        var normalized = operatorProfile.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        var dimensions = new[]
        {
            TryGet(evt.Fields, "operator"),
            TryGet(evt.Fields, "operator_name"),
            TryGet(evt.Fields, "perfil"),
            TryGet(evt.Fields, "profile"),
            TryGet(evt.Fields, "actor"),
            TryGet(evt.Fields, "responsavel"),
            TryGet(evt.Fields, "responsavel_nome")
        };

        return dimensions.Any(value => !string.IsNullOrWhiteSpace(value)
                                       && value.Contains(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static MetricsCounters ComputeMetricsCounters(IReadOnlyList<WhatsAppViewerEventItem> orderedEvents)
    {
        var counters = new MetricsCounters();
        var pendingSendsByPhone = new Dictionary<string, Queue<DateTimeOffset>>(StringComparer.OrdinalIgnoreCase);

        foreach (var evt in orderedEvents)
        {
            if (IsSendEvent(evt))
            {
                counters.TotalSent++;
                if (IsFailedSendEvent(evt))
                {
                    counters.FailedSent++;
                }

                if (!pendingSendsByPhone.TryGetValue(evt.Phone, out var queue))
                {
                    queue = new Queue<DateTimeOffset>();
                    pendingSendsByPhone[evt.Phone] = queue;
                }

                queue.Enqueue(evt.Timestamp);
            }

            if (IsResponseEvent(evt))
            {
                counters.TotalResponses++;
                if (pendingSendsByPhone.TryGetValue(evt.Phone, out var queue) && queue.Count > 0)
                {
                    var sendTimestamp = queue.Dequeue();
                    var latency = evt.Timestamp - sendTimestamp;
                    if (latency >= TimeSpan.Zero)
                    {
                        counters.ResponseSampleCount++;
                        counters.ResponseTotalSeconds += latency.TotalSeconds;
                    }
                }
            }

            if (IsInternalActionEvent(evt))
            {
                counters.TotalInternalActions++;
            }
        }

        return counters;
    }

    private static List<WhatsAppViewerMetricsBucket> BuildMetricBuckets(
        IReadOnlyList<WhatsAppViewerEventItem> orderedEvents,
        int windowMinutes)
    {
        if (orderedEvents.Count == 0)
        {
            return new List<WhatsAppViewerMetricsBucket>();
        }

        var window = TimeSpan.FromMinutes(windowMinutes);
        var start = AlignToWindowStart(orderedEvents[0].Timestamp, windowMinutes);
        var endLimit = orderedEvents[^1].Timestamp;
        var buckets = new List<WhatsAppViewerMetricsBucket>();

        for (var cursor = start; cursor <= endLimit; cursor = cursor.Add(window))
        {
            var windowEnd = cursor.Add(window);
            var eventsInBucket = orderedEvents
                .Where(evt => evt.Timestamp >= cursor && evt.Timestamp < windowEnd)
                .ToArray();

            if (eventsInBucket.Length == 0)
            {
                continue;
            }

            var counters = ComputeMetricsCounters(eventsInBucket);
            buckets.Add(new WhatsAppViewerMetricsBucket
            {
                WindowStart = cursor,
                WindowEnd = windowEnd,
                TotalSent = counters.TotalSent,
                TotalResponses = counters.TotalResponses,
                TotalInternalActions = counters.TotalInternalActions,
                SendFailureRate = counters.SendFailureRate,
                AverageResponseTimeSeconds = counters.AverageResponseTimeSeconds
            });
        }

        return buckets;
    }

    private static double ComputeAverageFirstResponseTimeSeconds(IReadOnlyList<WhatsAppViewerEventItem> orderedEvents)
    {
        var firstSendByInteraction = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var sampledInteractions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalSeconds = 0d;
        var sampleCount = 0;

        foreach (var evt in orderedEvents)
        {
            var interactionId = FirstNonEmpty(evt.InteractionId, TryGet(evt.Fields, "interaction_id"));
            if (string.IsNullOrWhiteSpace(interactionId))
            {
                continue;
            }

            if (IsSendEvent(evt))
            {
                if (!firstSendByInteraction.ContainsKey(interactionId))
                {
                    firstSendByInteraction[interactionId] = evt.Timestamp;
                }
                continue;
            }

            if (!IsResponseEvent(evt)
                || sampledInteractions.Contains(interactionId)
                || !firstSendByInteraction.TryGetValue(interactionId, out var sendAt))
            {
                continue;
            }

            var latency = evt.Timestamp - sendAt;
            if (latency < TimeSpan.Zero)
            {
                continue;
            }

            sampleCount++;
            totalSeconds += latency.TotalSeconds;
            sampledInteractions.Add(interactionId);
        }

        return sampleCount == 0 ? 0d : totalSeconds / sampleCount;
    }

    private static double ComputeInteractionResolutionRate(IReadOnlyList<WhatsAppViewerEventItem> orderedEvents)
    {
        var interactions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var evt in orderedEvents)
        {
            var interactionId = FirstNonEmpty(evt.InteractionId, TryGet(evt.Fields, "interaction_id"));
            if (string.IsNullOrWhiteSpace(interactionId))
            {
                continue;
            }

            interactions.Add(interactionId);
            if (IsResolvedInteractionEvent(evt))
            {
                resolved.Add(interactionId);
            }
        }

        return interactions.Count == 0 ? 0d : (double)resolved.Count / interactions.Count;
    }

    private static List<WhatsAppViewerFailureRateByDimension> BuildFailureRateByTemplateAndChannel(IReadOnlyList<WhatsAppViewerEventItem> orderedEvents)
    {
        var grouped = orderedEvents
            .Where(IsSendEvent)
            .GroupBy(evt => (
                Template: FirstNonEmpty(TryGet(evt.Fields, "template"), TryGet(evt.Fields, "template_name"), "desconhecido")!,
                Channel: FirstNonEmpty(TryGet(evt.Fields, "channel"), TryGet(evt.Fields, "canal"), "desconhecido")!))
            .Select(group =>
            {
                var totalSent = group.Count();
                var failedSent = group.Count(IsFailedSendEvent);
                return new WhatsAppViewerFailureRateByDimension
                {
                    Template = group.Key.Template,
                    Channel = group.Key.Channel,
                    TotalSent = totalSent,
                    FailedSent = failedSent,
                    FailureRate = totalSent == 0 ? 0d : (double)failedSent / totalSent
                };
            })
            .OrderByDescending(item => item.FailureRate)
            .ThenByDescending(item => item.FailedSent)
            .ThenBy(item => item.Template, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();

        return grouped;
    }

    private static List<WhatsAppViewerBacklogAgeBucket> BuildBacklogByAge(IReadOnlyList<WhatsAppViewerEventItem> orderedEvents, DateTimeOffset now)
    {
        var interactionState = new Dictionary<string, InteractionBacklogState>(StringComparer.OrdinalIgnoreCase);
        foreach (var evt in orderedEvents)
        {
            var interactionId = FirstNonEmpty(evt.InteractionId, TryGet(evt.Fields, "interaction_id"));
            if (string.IsNullOrWhiteSpace(interactionId))
            {
                continue;
            }

            if (!interactionState.TryGetValue(interactionId, out var state))
            {
                state = new InteractionBacklogState();
                interactionState[interactionId] = state;
            }

            state.LastEventAt = evt.Timestamp;
            if (IsSendEvent(evt))
            {
                state.HasSend = true;
            }

            if (IsResponseEvent(evt))
            {
                state.HasResponse = true;
            }

            if (IsResolvedInteractionEvent(evt))
            {
                state.IsResolved = true;
            }
        }

        var buckets = new[]
        {
            new WhatsAppViewerBacklogAgeBucket { Label = "0-15m" },
            new WhatsAppViewerBacklogAgeBucket { Label = "15-60m" },
            new WhatsAppViewerBacklogAgeBucket { Label = "1-4h" },
            new WhatsAppViewerBacklogAgeBucket { Label = "4-24h" },
            new WhatsAppViewerBacklogAgeBucket { Label = ">24h" }
        };

        foreach (var state in interactionState.Values.Where(static item => item.HasSend && !item.HasResponse && !item.IsResolved))
        {
            var age = now - state.LastEventAt;
            if (age < TimeSpan.FromMinutes(15)) buckets[0].Count++;
            else if (age < TimeSpan.FromHours(1)) buckets[1].Count++;
            else if (age < TimeSpan.FromHours(4)) buckets[2].Count++;
            else if (age < TimeSpan.FromHours(24)) buckets[3].Count++;
            else buckets[4].Count++;
        }

        return buckets.ToList();
    }

    private static bool IsResolvedInteractionEvent(WhatsAppViewerEventItem evt)
    {
        if (evt.EventName.Contains("ENCERRAD", StringComparison.OrdinalIgnoreCase)
            || evt.EventName.Contains("RESOLV", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var status = FirstNonEmpty(TryGet(evt.Fields, "status"), TryGet(evt.Fields, "status_final"), TryGet(evt.Fields, "final_status")) ?? string.Empty;
        return status.Contains("ENCERR", StringComparison.OrdinalIgnoreCase)
               || status.Contains("RESOLV", StringComparison.OrdinalIgnoreCase)
               || status.Contains("CONCLUID", StringComparison.OrdinalIgnoreCase)
               || status.Contains("CANCELAD", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTimeOffset AlignToWindowStart(DateTimeOffset timestamp, int windowMinutes)
    {
        var window = TimeSpan.FromMinutes(windowMinutes);
        var ticks = timestamp.UtcTicks - (timestamp.UtcTicks % window.Ticks);
        return new DateTimeOffset(ticks, TimeSpan.Zero).ToOffset(timestamp.Offset);
    }

    private static string ClassifyCategory(string eventName, IReadOnlyDictionary<string, string?> fields, string direction)
    {
        var explicitCategory = FirstNonEmpty(TryGet(fields, "category"), TryGet(fields, "categoria"), TryGet(fields, "message_category"));
        if (string.Equals(explicitCategory, "customer", StringComparison.OrdinalIgnoreCase))
        {
            return "customer";
        }

        if (string.Equals(explicitCategory, "sent", StringComparison.OrdinalIgnoreCase))
        {
            return "sent";
        }

        if (string.Equals(explicitCategory, "meta", StringComparison.OrdinalIgnoreCase))
        {
            return "meta";
        }

        if (string.Equals(explicitCategory, "system", StringComparison.OrdinalIgnoreCase))
        {
            return "system";
        }

        if (eventName.StartsWith("META_STATUS_", StringComparison.OrdinalIgnoreCase)
            || string.Equals(eventName, "META_STATUS_AUDITADO_SEM_DISPARO", StringComparison.OrdinalIgnoreCase))
        {
            return "meta";
        }

        var status = TryGet(fields, "status");
        var errorField = FirstNonEmpty(TryGet(fields, "erro"), TryGet(fields, "error"));
        if (!string.IsNullOrWhiteSpace(errorField)
            || eventName.Contains("ERRO", StringComparison.OrdinalIgnoreCase)
            || eventName.Contains("FALH", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            return "error";
        }

        return direction switch
        {
            "outbound" => "sent",
            "inbound" => "customer",
            "meta" => "meta",
            _ => "system"
        };
    }

    private static string BuildMainText(string eventName, IReadOnlyDictionary<string, string?> fields)
    {
        var direct = FirstNonEmpty(
            TryGet(fields, "text"),
            TryGet(fields, "texto"),
            TryGet(fields, "resposta_raw"),
            TryGet(fields, "detalhes"),
            TryGet(fields, "erro"));

        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct!;
        }

        var payload = TryGet(fields, "payload_resumido");
        if (!string.IsNullOrWhiteSpace(payload))
        {
            return SimplifyJsonPayload(payload!);
        }

        var status = TryGet(fields, "status");
        if (!string.IsNullOrWhiteSpace(status))
        {
            return $"{eventName}: {status}";
        }

        return eventName;
    }

    private static string SimplifyJsonPayload(string payload)
    {
        try
        {
            using var json = JsonDocument.Parse(payload);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                var first = root[0];
                var status = first.TryGetProperty("status", out var statusNode) ? statusNode.GetString() : null;
                var id = first.TryGetProperty("id", out var idNode) ? idNode.GetString() : null;
                if (!string.IsNullOrWhiteSpace(status) || !string.IsNullOrWhiteSpace(id))
                {
                    return $"status={status ?? "-"}, id={id ?? "-"}";
                }
            }
        }
        catch
        {
            // fallback para preview de texto
        }

        return payload.Length > 240 ? payload[..240] + "..." : payload;
    }

    private static string? ResolveDisplayName(IEnumerable<WhatsAppViewerEventItem> events)
    {
        foreach (var evt in events.OrderByDescending(static item => item.Timestamp))
        {
            var name = FirstNonEmpty(
                TryGet(evt.Fields, "customer_name"),
                TryGet(evt.Fields, "cliente"),
                TryGet(evt.Fields, "NM_PESSOA"),
                TryGet(evt.Fields, "nm_pessoa"));

            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return null;
    }

    private string ResolveBaseDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_configuredDirectory))
        {
            var configured = _configuredDirectory.Trim().Trim('"');
            if (Path.IsPathRooted(configured))
            {
                return configured;
            }

            return Path.GetFullPath(Path.Combine(_contentRootPath, configured));
        }

        return Path.GetFullPath(Path.Combine(_contentRootPath, "..", DefaultRelativeDirectory));
    }

    private static bool MatchesSearch(WhatsAppViewerConversationSummary summary, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        var needle = search.Trim();
        return summary.Phone.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || (summary.DisplayName?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static bool MatchesFilters(WhatsAppViewerConversationSummary summary, WhatsAppViewerConversationFilters filters)
    {
        if (!string.IsNullOrWhiteSpace(filters.Status))
        {
            if (!string.Equals(summary.ConversationStatus, filters.Status.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(filters.EventType))
        {
            var expected = filters.EventType.Trim();
            if (string.IsNullOrWhiteSpace(summary.EventType)
                || !summary.EventType.Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(filters.OperatorName))
        {
            var expected = filters.OperatorName.Trim();
            if (string.IsNullOrWhiteSpace(summary.OperatorName)
                || !summary.OperatorName.Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(filters.TemplateOrCampaign))
        {
            var expected = filters.TemplateOrCampaign.Trim();
            if (string.IsNullOrWhiteSpace(summary.TemplateOrCampaign)
                || !summary.TemplateOrCampaign.Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(filters.ErrorCode))
        {
            var expected = filters.ErrorCode.Trim();
            if (string.IsNullOrWhiteSpace(summary.ErrorCode)
                || !summary.ErrorCode.Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (filters.From.HasValue && (!summary.LastEventAt.HasValue || summary.LastEventAt.Value < filters.From.Value))
        {
            return false;
        }

        if (filters.To.HasValue && (!summary.LastEventAt.HasValue || summary.LastEventAt.Value > filters.To.Value))
        {
            return false;
        }

        if (filters.OnlyUnread && !summary.IsUnread)
        {
            return false;
        }

        if (filters.OnlySlaBreached && !summary.IsSlaBreached)
        {
            return false;
        }

        if (filters.HasActiveInteraction && !summary.HasActiveInteraction)
        {
            return false;
        }

        return true;
    }

    private static string? ResolveLatestEventField(IEnumerable<WhatsAppViewerEventItem> events, params string[] keys)
    {
        foreach (var evt in events.OrderByDescending(static item => item.Timestamp))
        {
            foreach (var key in keys)
            {
                var value = TryGet(evt.Fields, key);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }

        return null;
    }

    private static string ResolveConversationStatus(IEnumerable<WhatsAppViewerEventItem> events)
    {
        var latestByTimestamp = events
            .Where(IsConversationRelevantEvent)
            .OrderByDescending(static item => item.Timestamp)
            .ToArray();

        if (latestByTimestamp.Length == 0)
        {
            latestByTimestamp = events.OrderByDescending(static item => item.Timestamp).ToArray();
        }

        foreach (var evt in latestByTimestamp)
        {
            var status = FirstNonEmpty(TryGet(evt.Fields, "status"), TryGet(evt.Fields, "event_type"));
            if (!string.IsNullOrWhiteSpace(status) && status != "-")
            {
                return status!;
            }
        }

        return latestByTimestamp.FirstOrDefault()?.EventName ?? "DESCONHECIDO";
    }

    private static bool ResolveHasActiveInteraction(IEnumerable<WhatsAppViewerEventItem> events, string? status)
    {
        var normalizedStatus = (status ?? string.Empty).Trim().ToUpperInvariant();
        if (normalizedStatus is "RECUSADO" or "CANCELADA" or "ERRO_ENVIO" or "FAILED" or "READ")
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(normalizedStatus))
        {
            return true;
        }

        var latestName = events
            .Where(IsConversationRelevantEvent)
            .OrderByDescending(static item => item.Timestamp)
            .Select(static item => item.EventName)
            .FirstOrDefault();

        return !string.IsNullOrWhiteSpace(latestName)
            && !latestName.Contains("ENCERRADA", StringComparison.OrdinalIgnoreCase)
            && !latestName.Contains("FALHOU", StringComparison.OrdinalIgnoreCase)
            && !latestName.Contains("RECUSA", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ResolveIsUnread(IEnumerable<WhatsAppViewerEventItem> events)
    {
        var latestInbound = events
            .Where(IsResponseEvent)
            .OrderByDescending(static item => item.Timestamp)
            .FirstOrDefault();

        if (latestInbound is null)
        {
            return false;
        }

        var latestOutbound = events
            .Where(IsSendEvent)
            .OrderByDescending(static item => item.Timestamp)
            .FirstOrDefault();

        return latestOutbound is null || latestInbound.Timestamp > latestOutbound.Timestamp;
    }

    private static bool ResolveSlaBreached(IEnumerable<WhatsAppViewerEventItem> events, bool isUnread)
    {
        if (!isUnread)
        {
            return false;
        }

        var latestInbound = events
            .Where(IsResponseEvent)
            .OrderByDescending(static item => item.Timestamp)
            .FirstOrDefault();

        if (latestInbound is null)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        return now - latestInbound.Timestamp >= DefaultSlaThreshold;
    }

    private static int ResolveWaitingMinutes(IEnumerable<WhatsAppViewerEventItem> events, bool isUnread)
    {
        if (!isUnread)
        {
            return 0;
        }

        var latestInbound = events
            .Where(IsResponseEvent)
            .OrderByDescending(static item => item.Timestamp)
            .FirstOrDefault();

        if (latestInbound is null)
        {
            return 0;
        }

        var elapsed = DateTimeOffset.UtcNow - latestInbound.Timestamp;
        return Math.Max(0, (int)Math.Floor(elapsed.TotalMinutes));
    }

    private static bool ResolveHasRecentFailure(IEnumerable<WhatsAppViewerEventItem> events)
    {
        var latestFailureAt = events
            .Where(IsFailureEvent)
            .OrderByDescending(static item => item.Timestamp)
            .Select(static item => item.Timestamp)
            .FirstOrDefault();
        if (latestFailureAt == default)
        {
            return false;
        }

        return DateTimeOffset.UtcNow - latestFailureAt <= TimeSpan.FromHours(6);
    }

    private static int ResolveCriticityWeight(IEnumerable<WhatsAppViewerEventItem> events)
    {
        var tagsText = string.Join(" ", events
            .SelectMany(static item => item.Fields.Values)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .TakeLast(60));
        if (string.IsNullOrWhiteSpace(tagsText))
        {
            return 0;
        }

        if (ContainsAny(tagsText, "p1", "crítica", "critica", "crítico", "critico", "urgente", "vip", "alta_criticidade"))
        {
            return 30;
        }

        if (ContainsAny(tagsText, "p2", "alta", "importante", "atenção", "atencao"))
        {
            return 18;
        }

        if (ContainsAny(tagsText, "p3", "normal", "rotina", "baixa"))
        {
            return 8;
        }

        return 0;
    }

    private static int ResolvePriorityScore(
        bool isSlaBreached,
        int waitingMinutes,
        bool hasRecentFailure,
        bool isUnread,
        int criticityWeight,
        bool hasActiveInteraction)
    {
        var score = 0;
        if (isSlaBreached) score += 30;
        if (waitingMinutes > 0) score += Math.Min(25, waitingMinutes / 3);
        if (hasRecentFailure) score += 25;
        if (isUnread) score += 15;
        if (hasActiveInteraction) score += 5;
        score += Math.Clamp(criticityWeight, 0, 30);
        return Math.Clamp(score, 0, 100);
    }

    private static string ResolvePriorityTier(int score)
    {
        if (score >= 75) return "P1";
        if (score >= 45) return "P2";
        return "P3";
    }

    private static bool IsFailureEvent(WhatsAppViewerEventItem item)
    {
        var status = FirstNonEmpty(TryGet(item.Fields, "status"), TryGet(item.Fields, "event_type"), item.EventName);
        return IsFailureStatus(status);
    }

    private static bool IsFailureStatus(string? status)
    {
        var normalized = (status ?? string.Empty).Trim().ToUpperInvariant();
        return normalized.Contains("FALHA", StringComparison.Ordinal)
               || normalized.Contains("ERRO", StringComparison.Ordinal)
               || normalized.Contains("FAILED", StringComparison.Ordinal)
               || normalized.Contains("RECUSADO", StringComparison.Ordinal);
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (value.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? NormalizeDigits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = Regex.Replace(value, "\\D", string.Empty);
        return string.IsNullOrWhiteSpace(digits) ? null : digits;
    }

    private static string? TryGet(IReadOnlyDictionary<string, string?> fields, string key)
        => fields.TryGetValue(key, out var value) ? value : null;

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static int CompareConversationSummaries(WhatsAppViewerConversationSummary? left, WhatsAppViewerConversationSummary? right)
    {
        var leftAt = left?.LastEventAt ?? DateTimeOffset.MinValue;
        var rightAt = right?.LastEventAt ?? DateTimeOffset.MinValue;
        var byDate = rightAt.CompareTo(leftAt);
        if (byDate != 0)
        {
            return byDate;
        }

        return string.Compare(left?.Phone, right?.Phone, StringComparison.Ordinal);
    }

    private static bool IsAfterConversationCursor(WhatsAppViewerConversationSummary summary, ConversationListCursor cursor)
    {
        if (!cursor.HasValue)
        {
            return true;
        }

        var summaryAt = summary.LastEventAt ?? DateTimeOffset.MinValue;
        if (summaryAt < cursor.LastEventAt)
        {
            return true;
        }

        if (summaryAt > cursor.LastEventAt)
        {
            return false;
        }

        return string.Compare(summary.Phone, cursor.Phone, StringComparison.Ordinal) > 0;
    }

    private static string BuildConversationListCursor(WhatsAppViewerConversationSummary summary)
    {
        var ticks = (summary.LastEventAt ?? DateTimeOffset.MinValue).UtcDateTime.Ticks;
        var raw = $"{ticks}|{summary.Phone}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    private static ConversationListCursor ParseConversationListCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return ConversationListCursor.Empty;
        }

        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var separator = raw.IndexOf('|');
            if (separator <= 0)
            {
                return ConversationListCursor.Empty;
            }

            var ticksText = raw[..separator];
            var phone = raw[(separator + 1)..];
            if (!long.TryParse(ticksText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
            {
                return ConversationListCursor.Empty;
            }

            return new ConversationListCursor(new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc)), phone);
        }
        catch
        {
            return ConversationListCursor.Empty;
        }
    }

    private static TimelineCursor ParseConversationTimelineCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return TimelineCursor.Empty;
        }

        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var separator = raw.IndexOf('|');
            if (separator <= 0)
            {
                return TimelineCursor.Empty;
            }

            var ticksText = raw[..separator];
            var eventId = raw[(separator + 1)..];
            if (!long.TryParse(ticksText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
            {
                return TimelineCursor.Empty;
            }

            return new TimelineCursor(new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc)), eventId);
        }
        catch
        {
            return TimelineCursor.Empty;
        }
    }

    private static string BuildConversationTimelineCursor(WhatsAppViewerEventItem item)
    {
        var ticks = item.Timestamp.UtcDateTime.Ticks;
        var eventId = ResolveStableEventId(item);
        var raw = $"{ticks}|{eventId}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    private static bool IsBeforeTimelineCursor(WhatsAppViewerEventItem item, TimelineCursor cursor)
    {
        if (!cursor.HasValue)
        {
            return true;
        }

        if (item.Timestamp < cursor.Timestamp)
        {
            return true;
        }

        if (item.Timestamp > cursor.Timestamp)
        {
            return false;
        }

        var eventId = ResolveStableEventId(item);
        return string.Compare(eventId, cursor.EventId, StringComparison.Ordinal) < 0;
    }

    private static string ResolveStableEventId(WhatsAppViewerEventItem item)
    {
        var preferred = FirstNonEmpty(item.MetaMessageId, item.InteractionId);
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred!;
        }

        var raw = string.Join('|',
            item.Timestamp.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture),
            item.EventName,
            item.Direction,
            item.Phone,
            item.MainText,
            item.Author,
            item.Status,
            item.Fields.TryGetValue("source_file", out var sourceFile) ? sourceFile : null,
            item.Fields.TryGetValue("source_offset", out var sourceOffset) ? sourceOffset : null);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private sealed class StreamFileCursorState
    {
        public long Position { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
        public string PendingBlock { get; set; } = string.Empty;
    }

    private sealed class StreamCursorEnvelope
    {
        public int Version { get; set; } = 1;
        public string? ConsumerId { get; set; }
        public string? Phone { get; set; }
        public long LastIssuedEventId { get; set; }
        public Dictionary<string, StreamFileCursorState> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<WhatsAppViewerEventItem> BufferedEvents { get; set; } = new();
    }

    public sealed record WhatsAppViewerStreamBatch(IReadOnlyList<WhatsAppViewerEventItem> Events, string Cursor, bool HasMore, bool ResetDetected, long LastEventId);

    private sealed record StreamIndexedFile
    {
        public string Path { get; init; } = string.Empty;
        public DateTime LastWriteTimeUtc { get; init; }
        public long Length { get; init; }
    }

    private sealed class MetricsCounters
    {
        public int TotalSent { get; set; }
        public int FailedSent { get; set; }
        public int TotalResponses { get; set; }
        public int TotalInternalActions { get; set; }
        public int ResponseSampleCount { get; set; }
        public double ResponseTotalSeconds { get; set; }
        public double SendFailureRate => TotalSent == 0 ? 0d : (double)FailedSent / TotalSent;
        public double AverageResponseTimeSeconds => ResponseSampleCount == 0 ? 0d : ResponseTotalSeconds / ResponseSampleCount;
    }

    private sealed class InteractionBacklogState
    {
        public DateTimeOffset LastEventAt { get; set; }
        public bool HasSend { get; set; }
        public bool HasResponse { get; set; }
        public bool IsResolved { get; set; }
    }

    private readonly record struct ConversationListCursor(DateTimeOffset LastEventAt, string Phone)
    {
        public static ConversationListCursor Empty => new(DateTimeOffset.MinValue, string.Empty);
        public bool HasValue => LastEventAt != DateTimeOffset.MinValue || !string.IsNullOrWhiteSpace(Phone);
    }

    private readonly record struct TimelineCursor(DateTimeOffset Timestamp, string EventId)
    {
        public static TimelineCursor Empty => new(DateTimeOffset.MinValue, string.Empty);
        public bool HasValue => Timestamp != DateTimeOffset.MinValue || !string.IsNullOrWhiteSpace(EventId);
    }

    private sealed class FileProjectionCacheEntry
    {
        public required string Path { get; init; }
        public DateTime LastWriteTimeUtc { get; init; }
        public long Cursor { get; init; }
        public required Dictionary<string, WhatsAppViewerEventItem[]> EventsByPhone { get; init; }
        public DateTimeOffset LastAccessUtc { get; private set; } = DateTimeOffset.UtcNow;

        public void Touch() => LastAccessUtc = DateTimeOffset.UtcNow;
    }

    private sealed class ConversationProjection
    {
        public ConversationProjection(WhatsAppViewerConversationSummary summary, WhatsAppViewerEventItem[] timeline)
        {
            Summary = summary;
            Timeline = timeline;
        }

        public WhatsAppViewerConversationSummary Summary { get; }
        public WhatsAppViewerEventItem[] Timeline { get; }
        public DateTimeOffset LastAccessUtc { get; private set; } = DateTimeOffset.UtcNow;

        public void Touch() => LastAccessUtc = DateTimeOffset.UtcNow;
    }

    private sealed class ParseState
    {
        public DateTimeOffset? Timestamp { get; private set; }
        public string EventName { get; private set; } = string.Empty;
        public Dictionary<string, string?>? Fields { get; private set; }

        public void ResetCurrentEvent()
        {
            Timestamp = null;
            EventName = string.Empty;
            Fields = null;
        }

        public void StartNewHeader(string line)
        {
            Fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            var closing = line.IndexOf(']');
            var datePart = closing > 1 ? line[1..closing] : string.Empty;
            if (DateTimeOffset.TryParseExact(datePart, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsedTimestamp))
            {
                Timestamp = parsedTimestamp;
            }

            var evtIndex = line.IndexOf("EVENTO=", StringComparison.Ordinal);
            EventName = evtIndex >= 0 ? line[(evtIndex + "EVENTO=".Length)..].Trim() : "DESCONHECIDO";
        }

        public void ReadField(string line)
        {
            if (Fields is null)
            {
                return;
            }

            var equalsIndex = line.IndexOf('=');
            if (equalsIndex <= 0)
            {
                return;
            }

            var key = line[..equalsIndex].Trim();
            var value = line[(equalsIndex + 1)..].Trim();
            Fields[key] = value == "-" ? null : value;
        }

        public void Flush(string file, ICollection<WhatsAppViewerEventItem> items)
        {
            if (Timestamp is null || string.IsNullOrWhiteSpace(EventName) || Fields is null)
            {
                return;
            }

            var canonicalPhone = FirstNonEmpty(
                NormalizeDigits(TryGet(Fields, "canonical_phone")),
                NormalizeDigits(TryGet(Fields, "telefone")),
                NormalizeDigits(TryGet(Fields, "customer_phone")),
                NormalizeDigits(TryGet(Fields, "recipient_e164")),
                NormalizeDigits(Path.GetFileNameWithoutExtension(file))) ?? "unknown";

            var interactionId = FirstNonEmpty(TryGet(Fields, "interaction_id"), TryGet(Fields, "canonical_correlation_key"));
            var metaMessageId = FirstNonEmpty(TryGet(Fields, "meta_message_id"), TryGet(Fields, "wamid"));
            var direction = ClassifyDirection(EventName, Fields);
            var category = ClassifyCategory(EventName, Fields, direction);

            items.Add(new WhatsAppViewerEventItem
            {
                Timestamp = Timestamp.Value,
                EventName = EventName,
                Direction = direction,
                Category = category,
                Phone = canonicalPhone,
                InteractionId = interactionId,
                MetaMessageId = metaMessageId,
                MainText = BuildMainText(EventName, Fields),
                Fields = new Dictionary<string, string?>(Fields, StringComparer.OrdinalIgnoreCase)
            });
        }
    }
}
