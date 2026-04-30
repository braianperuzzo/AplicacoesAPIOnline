using AplicacoesOnline.Models.MetaWhatsApp;
using AplicacoesOnline.Services.MetaWhatsApp;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Text.Json;

namespace AplicacoesOnline.Controllers;

[ApiController]
public sealed class MetaWhatsAppViewerController : ControllerBase
{
    private static readonly IReadOnlyDictionary<string, string> CloseReasonCatalog = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SOLICITACAO_CLIENTE"] = "Solicitação do cliente",
        ["RESOLVIDO_OPERACIONAL"] = "Resolvido operacionalmente",
        ["DUPLICIDADE_INTERACAO"] = "Duplicidade de interação",
        ["ENCAMINHADO_OUTRO_CANAL"] = "Encaminhado para outro canal"
    };

    private readonly IWebHostEnvironment _hostEnvironment;
    private readonly MetaWhatsAppViewerService _viewerService;
    private readonly MetaWhatsAppManualSendService _manualSendService;
    private readonly MetaWhatsAppInteractionRouter _router;
    private readonly IMetaWhatsAppPersistentLogService _persistentLogService;
    private readonly MetaWhatsAppViewerSessionService _viewerSessionService;
    private readonly MetaWhatsAppViewerIdentityService _identityService;
    private readonly MetaWhatsAppViewerPresetService _presetService;
    private readonly MetaWhatsAppViewerExportJobService _exportJobService;
    private readonly MetaWhatsAppAuditReportService _auditReportService;
    private readonly ILogger<MetaWhatsAppViewerController> _logger;

    public MetaWhatsAppViewerController(
        IWebHostEnvironment hostEnvironment,
        MetaWhatsAppViewerService viewerService,
        MetaWhatsAppManualSendService manualSendService,
        MetaWhatsAppInteractionRouter router,
        IMetaWhatsAppPersistentLogService persistentLogService,
        MetaWhatsAppViewerSessionService viewerSessionService,
        MetaWhatsAppViewerIdentityService identityService,
        MetaWhatsAppViewerPresetService presetService,
        MetaWhatsAppViewerExportJobService exportJobService,
        MetaWhatsAppAuditReportService auditReportService,
        ILogger<MetaWhatsAppViewerController> logger)
    {
        _hostEnvironment = hostEnvironment;
        _viewerService = viewerService;
        _manualSendService = manualSendService;
        _router = router;
        _persistentLogService = persistentLogService;
        _viewerSessionService = viewerSessionService;
        _identityService = identityService;
        _presetService = presetService;
        _exportJobService = exportJobService;
        _auditReportService = auditReportService;
        _logger = logger;
    }

    [HttpGet("/whatsapp/viewer")]
    public IActionResult ViewerPage()
    {
        var webRootPath = _hostEnvironment.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRootPath))
        {
            webRootPath = Path.Combine(_hostEnvironment.ContentRootPath, "wwwroot");
        }

        var indexPath = Path.Combine(webRootPath, "whatsapp-viewer", "index.html");
        if (!System.IO.File.Exists(indexPath))
        {
            return NotFound(new
            {
                error = "viewer_not_found",
                detail = "Arquivo da interface do visualizador não encontrado no servidor."
            });
        }

        return PhysicalFile(indexPath, "text/html; charset=utf-8");
    }

    [HttpGet("/api/meta/whatsapp/viewer/config")]
    public IActionResult ViewerConfig()
    {
        var logoDataUri = string.Empty;
        try
        {
            var logoPath = Path.Combine(Directory.GetCurrentDirectory(), "Imagens", "Logotipo.png");
            if (System.IO.File.Exists(logoPath))
            {
                var logoBytes = System.IO.File.ReadAllBytes(logoPath);
                logoDataUri = $"data:image/png;base64,{Convert.ToBase64String(logoBytes)}";
            }
        }
        catch
        {
            logoDataUri = string.Empty;
        }

        Request.Cookies.TryGetValue(MetaWhatsAppViewerSessionService.SessionCookieName, out var sessionToken);
        var role = ResolveViewerRole(sessionToken);
        var canViewSensitive = CanViewSensitiveData(sessionToken);
        var username = ResolveUsername(sessionToken);

        return Ok(new
        {
            logoDataUri,
            role,
            username,
            can_view_sensitive_data = canViewSensitive,
            roles = new
            {
                read_only = MetaWhatsAppViewerSessionService.RoleReadOnly,
                @operator = MetaWhatsAppViewerSessionService.RoleOperator
            },
            close_reasons = CloseReasonCatalog.Select(static item => new { code = item.Key, label = item.Value }).ToArray()
        });
    }

    [HttpPost("/api/meta/whatsapp/viewer/session")]
    public IActionResult CreateSession([FromBody] WhatsAppViewerSessionCreateRequest? request = null)
    {
        var configuration = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var configuredApiKey = configuration["Security:ApiKey"];
        var configuredReadOnlyApiKey = configuration["Security:ReadOnlyApiKey"];
        var hasApiKeyHeader = Request.Headers.TryGetValue("X-API-Key", out var providedApiKey);
        var hasAuthorizationHeader = Request.Headers.TryGetValue("Authorization", out var authorizationHeader);
        var bearerToken = hasAuthorizationHeader
            ? authorizationHeader.ToString().Replace("Bearer ", string.Empty, StringComparison.OrdinalIgnoreCase).Trim()
            : string.Empty;

        var suppliedCredential = hasApiKeyHeader ? providedApiKey.ToString() : bearerToken;
        var role = ResolveRoleByCredential(suppliedCredential, configuredApiKey, configuredReadOnlyApiKey);
        var username = default(string?);
        var canViewSensitiveData = !string.Equals(role, MetaWhatsAppViewerSessionService.RoleReadOnly, StringComparison.OrdinalIgnoreCase);

        if (role is null && _identityService.TryAuthenticate(request?.Username, request?.Password, out var identityUser))
        {
            role = identityUser.Role;
            username = identityUser.Username;
            canViewSensitiveData = identityUser.CanViewSensitiveData;
        }

        if (role is null)
        {
            return Unauthorized(new
            {
                error = "Unauthorized",
                errorCode = "authentication_failed",
                detail = "Provide a valid X-API-Key header or Authorization: Bearer <token>."
            });
        }

        var session = _viewerSessionService.CreateSession(role, username, canViewSensitiveData, TimeSpan.FromMinutes(20));
        Response.Cookies.Append(
            MetaWhatsAppViewerSessionService.SessionCookieName,
            session.Token,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Expires = session.ExpiresAt,
                IsEssential = true
            });
        Response.Cookies.Append(
            MetaWhatsAppViewerSessionService.CsrfCookieName,
            session.CsrfToken,
            new CookieOptions
            {
                HttpOnly = false,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Expires = session.ExpiresAt,
                IsEssential = true
            });

        return Ok(new
        {
            ok = true,
            expiresInMinutes = 20,
            csrf_token = session.CsrfToken,
            role = session.Role,
            username = session.Username,
            can_view_sensitive_data = session.CanViewSensitiveData
        });
    }

    [HttpDelete("/api/meta/whatsapp/viewer/session")]
    public IActionResult DeleteSession()
    {
        if (Request.Cookies.TryGetValue(MetaWhatsAppViewerSessionService.SessionCookieName, out var token))
        {
            _viewerSessionService.Revoke(token);
        }

        Response.Cookies.Delete(MetaWhatsAppViewerSessionService.SessionCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            IsEssential = true
        });
        Response.Cookies.Delete(MetaWhatsAppViewerSessionService.CsrfCookieName, new CookieOptions
        {
            HttpOnly = false,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            IsEssential = true
        });

        return Ok(new { ok = true });
    }

    [HttpGet("/api/meta/whatsapp/viewer/conversations")]
    public IActionResult GetConversations(
        [FromQuery] string? search = null,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        [FromQuery] string? status = null,
        [FromQuery(Name = "event_type")] string? eventType = null,
        [FromQuery(Name = "operator")] string? operatorName = null,
        [FromQuery(Name = "template_campaign")] string? templateOrCampaign = null,
        [FromQuery(Name = "error_code")] string? errorCode = null,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery(Name = "only_unread")] bool onlyUnread = false,
        [FromQuery(Name = "only_sla_breached")] bool onlySlaBreached = false,
        [FromQuery(Name = "has_active_interaction")] bool hasActiveInteraction = false)
    {
        var watch = Stopwatch.StartNew();
        var page = _viewerService.GetConversations(search, limit, cursor, new WhatsAppViewerConversationFilters
        {
            Status = status,
            EventType = eventType,
            OperatorName = operatorName,
            TemplateOrCampaign = templateOrCampaign,
            ErrorCode = errorCode,
            From = from,
            To = to,
            OnlyUnread = onlyUnread,
            OnlySlaBreached = onlySlaBreached,
            HasActiveInteraction = hasActiveInteraction
        });
        watch.Stop();
        ApplySensitiveMask(page.Conversations);
        _logger.LogInformation("viewer.get_conversations ms={ElapsedMs} count={Count} search={Search}", watch.ElapsedMilliseconds, page.Conversations.Count, search);
        return Ok(new
        {
            ok = true,
            count = page.Conversations.Count,
            conversations = page.Conversations,
            next_cursor = page.NextCursor
        });
    }

    [HttpGet("/api/meta/whatsapp/viewer/conversations/{phone}")]
    public IActionResult GetConversation(
        [FromRoute] string phone,
        [FromQuery(Name = "from_event_time")] DateTimeOffset? fromEventTime = null,
        [FromQuery(Name = "page_size")] int pageSize = 80,
        [FromQuery] string? cursor = null)
    {
        var watch = Stopwatch.StartNew();
        var timeline = _viewerService.GetConversationTimelinePage(phone, fromEventTime, pageSize, cursor);
        watch.Stop();
        ApplySensitiveMask(timeline.Events);
        _logger.LogInformation("viewer.get_conversation ms={ElapsedMs} phone={Phone} events={Events}", watch.ElapsedMilliseconds, phone, timeline.Events.Count);
        return Ok(new
        {
            phone = timeline.Phone,
            display_name = timeline.DisplayName,
            event_count = timeline.EventCount,
            events = timeline.Events,
            next_cursor = timeline.NextCursor
        });
    }

    [HttpGet("/api/meta/whatsapp/viewer/conversations/{phone}/aggregates")]
    public IActionResult GetConversationAggregates(
        [FromRoute] string phone,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null)
    {
        var watch = Stopwatch.StartNew();
        var aggregates = _viewerService.GetConversationAggregates(phone, from, to);
        watch.Stop();
        _logger.LogInformation("viewer.get_conversation_aggregates ms={ElapsedMs} phone={Phone} events={Events}", watch.ElapsedMilliseconds, phone, aggregates.EventCount);
        return Ok(new
        {
            phone = aggregates.Phone,
            display_name = aggregates.DisplayName,
            event_count = aggregates.EventCount,
            inbound_count = aggregates.InboundCount,
            outbound_count = aggregates.OutboundCount,
            internal_count = aggregates.InternalCount,
            last_event_at = aggregates.LastEventAt
        });
    }

    [HttpGet("/api/meta/whatsapp/viewer/conversations/{phone}/search")]
    public IActionResult SearchConversation(
        [FromRoute] string phone,
        [FromQuery] string? text = null,
        [FromQuery] string? status = null,
        [FromQuery(Name = "interaction_id")] string? interactionId = null,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery(Name = "page_size")] int pageSize = 200,
        [FromQuery] string? cursor = null)
    {
        var watch = Stopwatch.StartNew();
        var page = _viewerService.SearchConversationTimeline(
            phone,
            text,
            status,
            interactionId,
            from,
            to,
            pageSize,
            cursor);
        watch.Stop();
        ApplySensitiveMask(page.Events);
        _logger.LogInformation("viewer.search ms={ElapsedMs} phone={Phone} matches={Matches}", watch.ElapsedMilliseconds, phone, page.TotalMatches);

        return Ok(new
        {
            phone = page.Phone,
            display_name = page.DisplayName,
            total_matches = page.TotalMatches,
            events = page.Events,
            next_cursor = page.NextCursor
        });
    }

    [HttpGet("/api/meta/whatsapp/viewer/conversations/{phone}/export")]
    public IActionResult ExportConversation(
        [FromRoute] string phone,
        [FromQuery] string format = "json",
        [FromQuery(Name = "from_event_time")] DateTimeOffset? fromEventTime = null,
        [FromQuery] DateTimeOffset? to = null)
    {
        var created = _exportJobService.CreateJob(phone, format, fromEventTime, to, CanViewSensitiveData());
        _ = RegisterCriticalAuditAsync("EXPORTACAO_MANUAL", phone, created.Success ? "ok" : created.ErrorCode);
        if (!created.Success)
        {
            return BadRequest(new
            {
                error = created.ErrorCode,
                detail = created.Detail
            });
        }

        var job = created.Job!;
        var startedAt = DateTime.UtcNow;
        while (DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(15))
        {
            var snapshot = _exportJobService.GetJob(job.JobId);
            if (snapshot is null)
            {
                break;
            }

            if (string.Equals(snapshot.Status, "completed", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(snapshot.DownloadToken))
            {
                var download = _exportJobService.TryResolveDownload(snapshot.JobId, snapshot.DownloadToken!);
                if (download is { Success: true })
                {
                    return PhysicalFile(download.FilePath!, download.ContentType!, download.FileName!);
                }
            }

            if (string.Equals(snapshot.Status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new
                {
                    error = snapshot.ErrorCode ?? "export_failed",
                    detail = snapshot.ErrorDetail ?? "Falha ao gerar exportação."
                });
            }

            Thread.Sleep(250);
        }

        return Accepted(new
        {
            ok = false,
            error = "export_timeout",
            detail = "A exportação demorou mais que o esperado. Use o endpoint de jobs para exportação assíncrona."
        });
    }

    [HttpPost("/api/meta/whatsapp/viewer/conversations/{phone}/export-jobs")]
    public IActionResult CreateExportJob(
        [FromRoute] string phone,
        [FromQuery] string format = "json",
        [FromQuery(Name = "from_event_time")] DateTimeOffset? fromEventTime = null,
        [FromQuery] DateTimeOffset? to = null)
    {
        var created = _exportJobService.CreateJob(phone, format, fromEventTime, to, CanViewSensitiveData());
        _ = RegisterCriticalAuditAsync("EXPORTACAO_ASSINCRONA", phone, created.Success ? "ok" : created.ErrorCode);
        if (!created.Success)
        {
            return BadRequest(new
            {
                ok = false,
                error = created.ErrorCode,
                detail = created.Detail
            });
        }

        var job = created.Job!;
        return Accepted(new
        {
            ok = true,
            job_id = job.JobId,
            status = job.Status,
            format = job.Format,
            created_at = job.CreatedAt
        });
    }

    [HttpGet("/api/meta/whatsapp/viewer/conversations/{phone}/export-jobs/{jobId}")]
    public IActionResult GetExportJobStatus([FromRoute] string phone, [FromRoute] string jobId)
    {
        var job = _exportJobService.GetJob(jobId);
        if (job is null || !string.Equals(job.Phone, phone, StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new
            {
                ok = false,
                error = "job_not_found",
                detail = "Job de exportação não encontrado ou expirado."
            });
        }

        var downloadUrl = string.Equals(job.Status, "completed", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(job.DownloadToken)
            ? $"/api/meta/whatsapp/viewer/conversations/{Uri.EscapeDataString(phone)}/export-jobs/{Uri.EscapeDataString(jobId)}/download?token={Uri.EscapeDataString(job.DownloadToken!)}"
            : null;

        return Ok(new
        {
            ok = true,
            job_id = job.JobId,
            phone = job.Phone,
            format = job.Format,
            status = job.Status,
            exported_events = job.ExportCount,
            error = job.ErrorCode,
            detail = job.ErrorDetail,
            download_url = downloadUrl,
            download_expires_at = job.DownloadTokenExpiresAt
        });
    }

    [HttpGet("/api/meta/whatsapp/viewer/conversations/{phone}/export-jobs/{jobId}/download")]
    public IActionResult DownloadExportJob([FromRoute] string phone, [FromRoute] string jobId, [FromQuery] string? token = null)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return BadRequest(new
            {
                ok = false,
                error = "missing_token",
                detail = "Informe o token de download."
            });
        }

        var job = _exportJobService.GetJob(jobId);
        if (job is null || !string.Equals(job.Phone, phone, StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new
            {
                ok = false,
                error = "job_not_found",
                detail = "Job de exportação não encontrado ou expirado."
            });
        }

        var download = _exportJobService.TryResolveDownload(jobId, token);
        if (download is null)
        {
            return NotFound(new
            {
                ok = false,
                error = "job_not_found",
                detail = "Job de exportação não encontrado ou expirado."
            });
        }

        if (!download.Success)
        {
            return BadRequest(new
            {
                ok = false,
                error = download.ErrorCode,
                detail = download.Detail
            });
        }

        return PhysicalFile(download.FilePath!, download.ContentType!, download.FileName!);
    }

    [HttpGet("/api/meta/whatsapp/viewer/metrics")]
    public IActionResult GetMetrics(
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] string? phone = null,
        [FromQuery] string? status = null,
        [FromQuery(Name = "window_minutes")] int windowMinutes = 60)
    {
        var watch = Stopwatch.StartNew();
        var operatorProfile = ResolveOperatorProfileFilter();
        var metrics = _viewerService.GetMetrics(from, to, phone, status, windowMinutes, operatorProfile);
        watch.Stop();
        _logger.LogInformation("viewer.metrics ms={ElapsedMs} phone={Phone} window={Window}", watch.ElapsedMilliseconds, phone, windowMinutes);
        return Ok(new
        {
            ok = true,
            metrics
        });
    }

    [HttpGet("/api/meta/whatsapp/viewer/audit/report")]
    public IActionResult GetAuditReport(
        [FromQuery] string? user = null,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery(Name = "action_type")] string? actionType = null)
    {
        var report = _auditReportService.Query(user, from, to, actionType);
        return Ok(new { ok = true, report });
    }

    [HttpGet("/api/meta/whatsapp/viewer/metrics/aggregated")]
    public IActionResult GetAggregatedMetrics(
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] string? phone = null,
        [FromQuery] string? status = null,
        [FromQuery(Name = "window_minutes")] int windowMinutes = 60,
        [FromQuery] string? operatorProfile = null)
    {
        var effectiveOperatorProfile = !string.IsNullOrWhiteSpace(operatorProfile)
            ? operatorProfile.Trim()
            : ResolveOperatorProfileFilter();
        var metrics = _viewerService.GetMetrics(from, to, phone, status, windowMinutes, effectiveOperatorProfile);
        return Ok(new
        {
            ok = true,
            metrics
        });
    }

    [HttpGet("/api/meta/whatsapp/viewer/stream")]
    public IActionResult StreamConversation(
        [FromQuery] string? phone,
        [FromQuery] string? cursor = null,
        [FromQuery(Name = "page_size")] int pageSize = 200)
    {
        var watch = Stopwatch.StartNew();
        Request.Cookies.TryGetValue(MetaWhatsAppViewerSessionService.SessionCookieName, out var sessionToken);
        var consumerId = string.IsNullOrWhiteSpace(sessionToken)
            ? HttpContext.Connection.Id
            : sessionToken;

        var batch = _viewerService.GetConversationDeltas(phone, consumerId, cursor, pageSize);
        watch.Stop();
        _logger.LogDebug("viewer.stream ms={ElapsedMs} phone={Phone} events={Events} hasMore={HasMore}", watch.ElapsedMilliseconds, phone, batch.Events.Count, batch.HasMore);
        return Ok(new
        {
            ok = true,
            cursor = batch.Cursor,
            has_more = batch.HasMore,
            reset_detected = batch.ResetDetected,
            last_event_id = batch.LastEventId,
            events = batch.Events
        });
    }

    [HttpGet("/api/meta/whatsapp/viewer/stream/sse")]
    public async Task StreamConversationSse(
        [FromQuery] string? phone,
        [FromQuery] string? cursor = null,
        [FromQuery(Name = "page_size")] int pageSize = 200,
        [FromQuery(Name = "last_event_id")] long? lastEventId = null)
    {
        Response.StatusCode = StatusCodes.Status200OK;
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        Request.Cookies.TryGetValue(MetaWhatsAppViewerSessionService.SessionCookieName, out var sessionToken);
        var consumerId = string.IsNullOrWhiteSpace(sessionToken)
            ? HttpContext.Connection.Id
            : sessionToken;

        var previousEventId = Math.Max(0, lastEventId ?? 0);
        var currentCursor = cursor;
        var cancellationToken = HttpContext.RequestAborted;

        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = _viewerService.GetConversationDeltas(phone, consumerId, currentCursor, pageSize);
            currentCursor = batch.Cursor;
            var firstEventId = batch.Events.FirstOrDefault()?.EventId ?? 0;
            var gapDetected = batch.ResetDetected
                || (previousEventId > 0 && firstEventId > 0 && firstEventId > previousEventId + 1);

            if (batch.Events.Count > 0 || gapDetected)
            {
                var payload = JsonSerializer.Serialize(new
                {
                    cursor = batch.Cursor,
                    has_more = batch.HasMore,
                    reset_detected = batch.ResetDetected,
                    gap_detected = gapDetected,
                    last_event_id = batch.LastEventId,
                    events = batch.Events
                });

                await Response.WriteAsync($"id: {Math.Max(batch.LastEventId, previousEventId)}\n", cancellationToken);
                await Response.WriteAsync("event: delta\n", cancellationToken);
                await Response.WriteAsync($"data: {payload}\n\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
                previousEventId = Math.Max(previousEventId, batch.LastEventId);
            }
            else
            {
                await Response.WriteAsync(": keepalive\n\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
            }

            if (batch.HasMore)
            {
                continue;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    [HttpGet("/api/meta/whatsapp/viewer/health")]
    public IActionResult ViewerHealth()
    {
        var watch = Stopwatch.StartNew();
        var diagnostics = _viewerService.GetDiagnosticsSnapshot();
        watch.Stop();
        _logger.LogInformation("viewer.health ms={ElapsedMs} files={Files} phones={Phones}", watch.ElapsedMilliseconds, diagnostics.TrackedFiles, diagnostics.TrackedPhones);
        return Ok(new { ok = true, diagnostics });
    }

    [HttpPost("/api/meta/whatsapp/viewer/presets")]
    public IActionResult SavePreset([FromBody] WhatsAppViewerPresetUpsertRequest request)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var validation = _presetService.Validate(request);
        if (!validation.IsValid)
        {
            return UnprocessableEntity(new
            {
                ok = false,
                error = validation.ErrorCode,
                detail = validation.ErrorMessage,
                supported_version = WhatsAppViewerPresetContract.CurrentVersion
            });
        }

        Request.Cookies.TryGetValue(MetaWhatsAppViewerSessionService.SessionCookieName, out var sessionToken);
        var scope = MetaWhatsAppViewerPresetService.ResolveScope(request.User, sessionToken);
        if (string.IsNullOrWhiteSpace(scope))
        {
            return Unauthorized(new
            {
                ok = false,
                error = "missing_scope",
                detail = "Informe user ou utilize sessão autenticada para salvar preset."
            });
        }

        var record = _presetService.Upsert(scope, sessionToken ?? string.Empty, request);
        _ = RegisterCriticalAuditAsync("ALTERACAO_PRESET_VISUALIZADOR", null, "ok");
        return Ok(new { ok = true, preset = record });
    }

    [HttpGet("/api/meta/whatsapp/viewer/presets")]
    public IActionResult GetPreset([FromQuery(Name = "operator")] string? @operator, [FromQuery] string? user = null)
    {
        var normalizedOperator = @operator?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedOperator))
        {
            return BadRequest(new
            {
                ok = false,
                error = "invalid_operator",
                detail = "Informe o parâmetro operator."
            });
        }

        Request.Cookies.TryGetValue(MetaWhatsAppViewerSessionService.SessionCookieName, out var sessionToken);
        var scope = MetaWhatsAppViewerPresetService.ResolveScope(user, sessionToken);
        if (string.IsNullOrWhiteSpace(scope))
        {
            return Unauthorized(new
            {
                ok = false,
                error = "missing_scope",
                detail = "Informe user ou utilize sessão autenticada para carregar preset."
            });
        }

        var record = _presetService.Get(scope, normalizedOperator);
        if (record is null)
        {
            return NotFound(new
            {
                ok = false,
                error = "preset_not_found",
                detail = "Nenhum preset encontrado para o operador informado."
            });
        }

        return Ok(new { ok = true, preset = record });
    }

    [HttpPost("/api/meta/whatsapp/viewer/send-text")]
    public async Task<IActionResult> SendText([FromBody] WhatsAppManualSendRequest request, CancellationToken cancellationToken)
    {
        var forbidden = EnsureCanMutate();
        if (forbidden is not null)
        {
            return forbidden;
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var actionContext = BuildActionAuditContext(request.Phone);
        var now = DateTimeOffset.UtcNow;
        var result = await _manualSendService.SendTextAsync(request.Phone!, request.Text!, cancellationToken);
        var phone = MetaWhatsAppPhoneCanonicalizer.ToCanonicalE164Br(request.Phone);

        var eventName = result.Success ? "ATENDIMENTO_MANUAL_ENVIADO" : "ATENDIMENTO_MANUAL_FALHOU";
        await _persistentLogService.AppendEventAsync(
            phone,
            eventName,
            now,
            new Dictionary<string, string?>
            {
                ["telefone"] = phone,
                ["text"] = request.Text,
                ["meta_message_id"] = result.MetaMessageId,
                ["status_code"] = result.StatusCode.ToString(),
                ["erro"] = result.Error,
                ["detalhes"] = result.Success ? "Mensagem manual enviada via visualizador." : "Falha no envio manual via visualizador.",
                ["response_body"] = result.ResponseBody,
                ["actor"] = actionContext.Actor,
                ["motivo"] = "ENVIO_MANUAL",
                ["origem"] = actionContext.Origin,
                ["traceId"] = actionContext.TraceId,
                ["session_id"] = actionContext.SessionId,
                ["conversation_phone"] = actionContext.ConversationPhone,
                ["interaction_context"] = actionContext.InteractionContext,
                ["action_at"] = now.ToString("O")
            });

        return StatusCode(
            result.Success ? StatusCodes.Status200OK : StatusCodes.Status502BadGateway,
            new
            {
                success = result.Success,
                status_code = result.StatusCode,
                phone,
                meta_message_id = result.MetaMessageId,
                meta_contact_wa_id = result.MetaContactWaId,
                error = result.Error,
                response_body = result.ResponseBody
            });
    }

    [HttpPost("/api/meta/whatsapp/viewer/close-by-phone")]
    public async Task<IActionResult> CloseByPhone([FromBody] InteractionForceCloseByPhoneRequest request)
    {
        var forbidden = EnsureCanMutate();
        if (forbidden is not null)
        {
            return forbidden;
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var normalizedReasonCode = NormalizeCloseReasonCode(request.ReasonCode);
        if (normalizedReasonCode is null)
        {
            return BadRequest(new
            {
                ok = false,
                error = "invalid_reason_code",
                detail = $"Informe reason_code válido. Opções: {string.Join(", ", CloseReasonCatalog.Keys)}"
            });
        }

        if (!string.Equals(request.Confirmation, "CONFIRMAR", StringComparison.Ordinal))
        {
            return BadRequest(new
            {
                ok = false,
                error = "close_requires_confirmation",
                detail = "Informe confirmation=CONFIRMAR para encerrar a interação."
            });
        }

        var actionContext = BuildActionAuditContext(request.Phone);
        var now = DateTimeOffset.UtcNow;
        if (!_router.TryForceCloseActiveByPhone(request.Phone!, now, out var closedInteractions))
        {
            await _persistentLogService.AppendEventAsync(
                request.Phone!,
                "INTERACAO_ENCERRAMENTO_SEM_ATIVA",
                now,
                new Dictionary<string, string?>
                {
                    ["telefone"] = request.Phone,
                    ["detalhes"] = "Tentativa de encerramento manual sem interação ativa.",
                    ["actor"] = actionContext.Actor,
                    ["motivo"] = normalizedReasonCode,
                    ["origem"] = actionContext.Origin,
                    ["traceId"] = actionContext.TraceId,
                    ["session_id"] = actionContext.SessionId,
                    ["conversation_phone"] = actionContext.ConversationPhone,
                    ["interaction_context"] = actionContext.InteractionContext,
                    ["action_at"] = now.ToString("O")
                });

            return Ok(new
            {
                ok = true,
                phone = request.Phone,
                closed = 0,
                interaction_ids = Array.Empty<string>(),
                status = "SEM_INTERACAO_ATIVA"
            });
        }

        var reasonLabel = CloseReasonCatalog[normalizedReasonCode];
        foreach (var interaction in closedInteractions)
        {
            await _persistentLogService.AppendEventAsync(
                interaction.PhoneKey ?? interaction.CustomerPhone ?? interaction.RecipientE164 ?? request.Phone,
                "INTERACAO_ENCERRADA_MANUALMENTE",
                now,
                new Dictionary<string, string?>
                {
                    ["interaction_id"] = interaction.InteractionId,
                    ["telefone"] = interaction.PhoneKey ?? interaction.CustomerPhone ?? interaction.RecipientE164 ?? request.Phone,
                    ["status"] = interaction.Status,
                    ["detalhes"] = reasonLabel,
                    ["motivo"] = normalizedReasonCode,
                    ["actor"] = actionContext.Actor,
                    ["origem"] = actionContext.Origin,
                    ["traceId"] = actionContext.TraceId,
                    ["session_id"] = actionContext.SessionId,
                    ["conversation_phone"] = actionContext.ConversationPhone,
                    ["interaction_context"] = actionContext.InteractionContext,
                    ["action_at"] = now.ToString("O")
                });
        }

        return Ok(new
        {
            ok = true,
            phone = request.Phone,
            closed = closedInteractions.Count,
            interaction_ids = closedInteractions.Select(static item => item.InteractionId).ToArray(),
            status = "CANCELADA"
        });
    }

    private ViewerActionAuditContext BuildActionAuditContext(string? phone)
    {
        Request.Cookies.TryGetValue(MetaWhatsAppViewerSessionService.SessionCookieName, out var sessionToken);
        var actorHeader = Request.Headers.TryGetValue("X-Viewer-Operator", out var opHeader) ? opHeader.ToString() : null;
        var actorQuery = Request.Query.TryGetValue("operator", out var opQuery) ? opQuery.ToString() : null;
        var actor = !string.IsNullOrWhiteSpace(actorHeader) ? actorHeader!.Trim() : actorQuery?.Trim();
        if (string.IsNullOrWhiteSpace(actor))
        {
            if (HttpContext.Items.TryGetValue("viewer.username", out var usernameObj)
                && usernameObj is string usernameFromSession
                && !string.IsNullOrWhiteSpace(usernameFromSession))
            {
                actor = usernameFromSession;
            }
        }

        if (string.IsNullOrWhiteSpace(actor))
        {
            actor = User?.Identity?.Name;
        }

        var contextLabel = $"{Request.Method} {Request.Path}";
        return new ViewerActionAuditContext(
            string.IsNullOrWhiteSpace(actor) ? "viewer_session" : actor!,
            sessionToken ?? "sem_sessao",
            HttpContext.TraceIdentifier,
            phone ?? string.Empty,
            "viewer_web",
            contextLabel);
    }

    private string? ResolveOperatorProfileFilter()
    {
        if (Request.Headers.TryGetValue("X-Viewer-Operator", out var operatorHeader))
        {
            var normalized = operatorHeader.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        if (Request.Query.TryGetValue("operator", out var operatorQuery))
        {
            var normalized = operatorQuery.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return null;
    }

    private string? ResolveUsername(string? sessionToken)
    {
        if (_viewerSessionService.TryGetSessionMetadata(sessionToken, out var metadata))
        {
            return metadata.Username;
        }

        return null;
    }

    private bool CanViewSensitiveData(string? sessionToken = null)
    {
        if (sessionToken is null)
        {
            Request.Cookies.TryGetValue(MetaWhatsAppViewerSessionService.SessionCookieName, out sessionToken);
        }

        if (_viewerSessionService.TryGetSessionMetadata(sessionToken, out var metadata))
        {
            return metadata.CanViewSensitiveData;
        }

        return true;
    }

    private void ApplySensitiveMask(IEnumerable<WhatsAppViewerConversationSummary> conversations)
    {
        if (CanViewSensitiveData())
        {
            return;
        }

        foreach (var conversation in conversations)
        {
            conversation.Phone = MaskPhone(conversation.Phone) ?? conversation.Phone;
            conversation.DisplayName = MaskText(conversation.DisplayName);
            conversation.LastMessagePreview = MaskText(conversation.LastMessagePreview) ?? conversation.LastMessagePreview;
        }
    }

    private void ApplySensitiveMask(IEnumerable<WhatsAppViewerEventItem> events)
    {
        if (CanViewSensitiveData())
        {
            return;
        }

        foreach (var evt in events)
        {
            evt.Phone = MaskPhone(evt.Phone) ?? evt.Phone;
            evt.MainText = MaskText(evt.MainText) ?? evt.MainText;
            foreach (var key in evt.Fields.Keys.ToArray())
            {
                if (key.Contains("phone", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("telefone", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("text", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("mensagem", StringComparison.OrdinalIgnoreCase))
                {
                    evt.Fields[key] = key.Contains("phone", StringComparison.OrdinalIgnoreCase) || key.Contains("telefone", StringComparison.OrdinalIgnoreCase)
                        ? MaskPhone(evt.Fields[key])
                        : MaskText(evt.Fields[key]);
                }
            }
        }
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

    private static string? MaskPhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length <= 4)
        {
            return "***";
        }

        return $"***{digits[^4..]}";
    }

    private async Task RegisterCriticalAuditAsync(string action, string? phone, string? detail)
    {
        var context = BuildActionAuditContext(phone);
        await _persistentLogService.AppendEventAsync(
            phone,
            action,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?>
            {
                ["actor"] = context.Actor,
                ["telefone"] = phone,
                ["detalhes"] = detail,
                ["traceId"] = context.TraceId,
                ["session_id"] = context.SessionId
            });
    }

    private ObjectResult? EnsureCanMutate()
    {
        var role = ResolveViewerRole(Request.Cookies.TryGetValue(MetaWhatsAppViewerSessionService.SessionCookieName, out var sessionToken) ? sessionToken : null);
        if (string.Equals(role, MetaWhatsAppViewerSessionService.RoleReadOnly, StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "Forbidden",
                errorCode = "viewer_read_only",
                detail = "Seu perfil é somente leitura e não pode executar ações de envio ou encerramento.",
                traceId = HttpContext.TraceIdentifier
            });
        }

        return null;
    }

    private string ResolveViewerRole(string? sessionToken)
    {
        if (_viewerSessionService.TryGetSessionMetadata(sessionToken, out var metadata))
        {
            return metadata.Role;
        }

        if (HttpContext.Items.TryGetValue("viewer.role", out var roleObj)
            && roleObj is string role
            && !string.IsNullOrWhiteSpace(role))
        {
            return role;
        }

        return MetaWhatsAppViewerSessionService.RoleOperator;
    }

    private static string? NormalizeCloseReasonCode(string? reasonCode)
    {
        var normalized = reasonCode?.Trim().ToUpperInvariant();
        return !string.IsNullOrWhiteSpace(normalized) && CloseReasonCatalog.ContainsKey(normalized)
            ? normalized
            : null;
    }

    private static string? ResolveRoleByCredential(string? providedCredential, string? operatorApiKey, string? readOnlyApiKey)
    {
        if (!string.IsNullOrWhiteSpace(readOnlyApiKey)
            && string.Equals(providedCredential, readOnlyApiKey, StringComparison.Ordinal))
        {
            return MetaWhatsAppViewerSessionService.RoleReadOnly;
        }

        if (!string.IsNullOrWhiteSpace(operatorApiKey)
            && string.Equals(providedCredential, operatorApiKey, StringComparison.Ordinal))
        {
            return MetaWhatsAppViewerSessionService.RoleOperator;
        }

        return null;
    }

    private readonly record struct ViewerActionAuditContext(
        string Actor,
        string SessionId,
        string TraceId,
        string ConversationPhone,
        string Origin,
        string InteractionContext);
}
