using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using AplicacoesOnline.Models.MetaWhatsApp;
using AplicacoesOnline.Services.MetaWhatsApp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AplicacoesOnline.Controllers;

[ApiController]
public sealed class MetaWhatsAppWebhookController : ControllerBase
{
    private const string OrdersTrackingFlowKey = "wa_pedidos_acompanhamento_pedidos";
    private const string OrdersTrackingTemplateName = "acompanhamento_pedidossituacao_n8n";
    private const string OrdersTrackingTemplateLanguage = "pt_BR";
    private const string PesquisaPosVendasFlowName = "pesquisa_atendimento_pos_vendas";
    private const string PesquisaPosVendasFlowId = "930667013107269";
    private const string CompletionIntentFaleConosco = "FALE_CONOSCO";
    private const string GlobalFallbackFlowKey = "wa_global_resposta_fora_padrao";
    private const string BuiltInGlobalFallbackWebhookUrl = "https://braianperuzzoibr.app.n8n.cloud/webhook/wa-global-resposta-fora-do-padrao";
    private const string BuiltInGlobalNoWebhookUrl = "https://braianperuzzoibr.app.n8n.cloud/webhook/wa-global-resposta-nao-padrao";
    private static readonly HashSet<string> AllowedCompletionIntents = new(StringComparer.Ordinal)
    {
        CompletionIntentFaleConosco,
        "PESQUISA_POS_VENDAS",
        "PREFERENCIAS_CONTATO"
    };
    private static readonly string[] PesquisaPosVendasCamposObrigatorios =
    {
        "pergunta_um",
        "resposta_pergunta_um",
        "pergunta_dois",
        "resposta_pergunta_dois",
        "nota_atendimento",
        "nota_recomendacao"
    };

    private static readonly string InteractionRegisterVersion =
        Environment.GetEnvironmentVariable("INTERACTION_REGISTER_VERSION")
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "unknown";

    private readonly IOptions<MetaWhatsAppWebhookOptions> _options;
    private readonly IOptions<N8nWebhookSecurityOptions> _webhookSecurityOptions;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MetaWhatsAppChatAuthenticationService _chatAuthenticationService;
    private readonly FlowInboundContractResolver _flowInboundContractResolver;
    private readonly ILogger<MetaWhatsAppWebhookController> _logger;
    private readonly IWebHostEnvironment _environment;
    private readonly IMetaWhatsAppSenderResolver _senderResolver;

    public MetaWhatsAppWebhookController(
        IOptions<MetaWhatsAppWebhookOptions> options,
        IOptions<N8nWebhookSecurityOptions> webhookSecurityOptions,
        IHttpClientFactory httpClientFactory,
        MetaWhatsAppChatAuthenticationService chatAuthenticationService,
        FlowInboundContractResolver flowInboundContractResolver,
        ILogger<MetaWhatsAppWebhookController> logger,
        IWebHostEnvironment environment,
        IMetaWhatsAppSenderResolver senderResolver)
    {
        _options = options;
        _webhookSecurityOptions = webhookSecurityOptions;
        _httpClientFactory = httpClientFactory;
        _chatAuthenticationService = chatAuthenticationService;
        _flowInboundContractResolver = flowInboundContractResolver;
        _logger = logger;
        _environment = environment;
        _senderResolver = senderResolver;
        _logger.LogInformation(
            "MetaWhatsAppWebhookController ativado. Environment={Environment}. VerifyTokenConfigured={VerifyTokenConfigured}. PersistentLogDirectoryConfigured={PersistentLogDirectoryConfigured}",
            _environment.EnvironmentName,
            !string.IsNullOrWhiteSpace(_options.Value.VerifyToken),
            !string.IsNullOrWhiteSpace(_options.Value.PersistentLogDirectory));
    }

    [HttpGet("/webhooks/meta/whatsapp")]
    public IActionResult VerifyWebhook(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        try
        {
            var resolvedMode = FirstNonEmptyQueryValue(
                mode,
                "hub.mode",
                "hub_mode",
                "mode")?.Trim();

            if (string.IsNullOrWhiteSpace(resolvedMode))
            {
                return Ok(new
                {
                    status = "ready",
                    message = "Endpoint ativo. Para validaÃ§Ã£o da Meta, envie hub.mode=subscribe, hub.verify_token e hub.challenge."
                });
            }

            if (!string.Equals(resolvedMode, "subscribe", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new
                {
                    error = "invalid_hub_mode",
                    expected = "subscribe",
                    received = resolvedMode
                });
            }

            var resolvedVerifyToken = FirstNonEmptyQueryValue(
                verifyToken,
                "hub.verify_token",
                "hub_verify_token",
                "verify_token")?.Trim();

            var resolvedChallenge = FirstNonEmptyQueryValue(
                challenge,
                "hub.challenge",
                "hub_challenge",
                "challenge");

            var (configuredVerifyToken, verifyTokenSource) = ResolveVerifyTokenConfiguration();
            var isTokenMatch = string.Equals(resolvedVerifyToken, configuredVerifyToken, StringComparison.Ordinal);

            _logger.LogInformation(
                "Meta webhook verification attempt. Mode={Mode}. VerifyTokenReceived={VerifyTokenReceived}. ReceivedTokenLength={ReceivedTokenLength}. ConfiguredTokenLength={ConfiguredTokenLength}. VerifyTokenSource={VerifyTokenSource}. TokenMatch={TokenMatch}. TraceId={TraceId}",
                resolvedMode,
                !string.IsNullOrEmpty(resolvedVerifyToken),
                resolvedVerifyToken?.Length ?? 0,
                configuredVerifyToken.Length,
                verifyTokenSource,
                isTokenMatch,
                HttpContext.TraceIdentifier);

            if (!isTokenMatch)
            {
                return StatusCode(StatusCodes.Status403Forbidden);
            }

            return Content(resolvedChallenge ?? string.Empty, "text/plain", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Falha inesperada na validaÃ§Ã£o do webhook da Meta. TraceId: {TraceId}",
                HttpContext.TraceIdentifier);

            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                error = "webhook_verification_failed",
                traceId = HttpContext.TraceIdentifier
            });
        }
    }

    private string? FirstNonEmptyQueryValue(string? preferredValue, params string[] alternativeKeys)
    {
        if (!string.IsNullOrWhiteSpace(preferredValue))
        {
            return preferredValue;
        }

        foreach (var key in alternativeKeys)
        {
            if (!Request.Query.TryGetValue(key, out var candidateValues))
            {
                continue;
            }

            var candidate = candidateValues.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private (string? Url, string Source) ResolveGlobalFallbackWebhookConfiguration()
    {
        var resolvedUrl = _options.Value.ResolveGlobalFallbackN8nWebhookUrl();
        if (!string.IsNullOrWhiteSpace(resolvedUrl))
        {
            if (!string.IsNullOrWhiteSpace(_options.Value.GlobalFallbackN8nWebhookUrl))
            {
                return (resolvedUrl, "global_fallback_n8n_webhook_url");
            }

            return (resolvedUrl, "default_n8n_webhook_url");
        }

        return (BuiltInGlobalFallbackWebhookUrl, "built_in_global_fallback");
    }

    private (string? Url, string Source) ResolveGlobalNoWebhookConfiguration()
    {
        if (!string.IsNullOrWhiteSpace(_options.Value.GlobalNaoN8nWebhookUrl))
        {
            return (_options.Value.GlobalNaoN8nWebhookUrl.Trim(), "global_nao_n8n_webhook_url");
        }

        var fallback = ResolveGlobalFallbackWebhookConfiguration();
        if (!string.IsNullOrWhiteSpace(fallback.Url))
        {
            return (fallback.Url, $"fallback:{fallback.Source}");
        }

        return (BuiltInGlobalNoWebhookUrl, "built_in_global_no");
    }

    [HttpPost("/webhooks/meta/whatsapp")]
    public async Task<IActionResult> ReceiveWebhook()
    {
        var stage = "raw_request_received";
        HttpContext.Items["meta.whatsapp.stage"] = stage;
        HttpContext.Items["interactions.register.stage"] = stage;

        string rawBody = string.Empty;
        string signature = string.Empty;
        JsonElement payload = default;
        ParsedWhatsAppEvent? parsedEventInfo = null;
        RoutingDecision? routingSnapshot = null;
        var now = DateTimeOffset.UtcNow;

        try
        {
            rawBody = await ReadRawBodyAsync();
            signature = Request.Headers["X-Hub-Signature-256"].ToString();

            if (!MetaWhatsAppSignatureValidator.IsValid(_options.Value.AppSecret, rawBody, signature))
            {
                _logger.LogWarning(
                    "Assinatura invÃ¡lida no webhook da Meta. TraceId: {TraceId}. RemoteIp: {RemoteIp}",
                    HttpContext.TraceIdentifier,
                    HttpContext.Connection.RemoteIpAddress);

                return Unauthorized(new
                {
                    error = "invalid_meta_signature",
                    detail = "Envie o header X-Hub-Signature-256 no formato sha256=<hmac_hex_do_body>, usando o AppSecret da MetaWhatsAppWebhook.",
                    traceId = HttpContext.TraceIdentifier
                });
            }
            stage = "signature_validated";
            HttpContext.Items["meta.whatsapp.stage"] = stage;
            HttpContext.Items["interactions.register.stage"] = stage;

            try
            {
                using var json = JsonDocument.Parse(rawBody);
                payload = json.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Payload invÃ¡lido no webhook da Meta. TraceId: {TraceId}", HttpContext.TraceIdentifier);
                return BadRequest(new { error = "invalid_json" });
            }
            stage = "payload_parsed";
            HttpContext.Items["meta.whatsapp.stage"] = stage;
            HttpContext.Items["interactions.register.stage"] = stage;

            if (!TryResolveDependency<MetaWhatsAppInteractionRouter>(nameof(MetaWhatsAppInteractionRouter), out var router, out var dependencyError))
            {
                return dependencyError!;
            }
            var interactionRouter = router!;

            var (resolvedGlobalFallbackWebhookUrl, resolvedGlobalFallbackSource) = ResolveGlobalFallbackWebhookConfiguration();
            var (resolvedGlobalNoWebhookUrl, resolvedGlobalNoSource) = ResolveGlobalNoWebhookConfiguration();
            var routing = interactionRouter.Resolve(payload, now, resolvedGlobalFallbackWebhookUrl);
            routingSnapshot = routing;
            parsedEventInfo = routing.EventInfo;
            var isGlobalTerminationIntent = interactionRouter.IsGlobalTerminationIntent(routing.EventInfo.Response);
            if (isGlobalTerminationIntent)
            {
                _logger.LogInformation(
                    "global_termination_intent_detected. response_raw={ResponseRaw}. correlation_strategy={CorrelationStrategy}. trace_id={TraceId}",
                    routing.EventInfo.Response,
                    routing.CorrelationStrategy,
                    HttpContext.TraceIdentifier);
            }
            stage = "event_classified";
            HttpContext.Items["meta.whatsapp.stage"] = stage;
            HttpContext.Items["interactions.register.stage"] = stage;

            await RegisterMetaStatusEventsAsync(payload, now, routing);

            if (!string.Equals(routing.EventInfo.EventType, "messages", StringComparison.Ordinal))
            {
                var statusAuditPhone = routing.Interaction?.PhoneKey
                    ?? routing.Interaction?.CustomerPhone
                    ?? routing.Interaction?.RecipientE164
                    ?? routing.EventInfo.CustomerPhone;
                await WriteInteractionLogAsync(
                    statusAuditPhone,
                    "META_STATUS_AUDITADO_SEM_DISPARO",
                    now,
                    new Dictionary<string, string?>
                    {
                        ["event_type"] = routing.EventInfo.EventType,
                        ["parse_reason"] = routing.EventInfo.ParseReason,
                        ["provider"] = "meta",
                        ["status"] = routing.EventInfo.StatusValue,
                        ["wamid"] = routing.EventInfo.MetaMessageId,
                        ["recipient_id"] = routing.EventInfo.StatusRecipientId,
                        ["conversation_id"] = routing.EventInfo.StatusConversationId,
                        ["pricing_category"] = routing.EventInfo.StatusPricingCategory,
                        ["error_code"] = routing.EventInfo.StatusErrorCode,
                        ["found_entry"] = routing.EventInfo.ParserDiagnostics.FoundEntry.ToString().ToLowerInvariant(),
                        ["found_changes"] = routing.EventInfo.ParserDiagnostics.FoundChanges.ToString().ToLowerInvariant(),
                        ["found_value"] = routing.EventInfo.ParserDiagnostics.FoundValue.ToString().ToLowerInvariant(),
                        ["found_statuses"] = routing.EventInfo.ParserDiagnostics.FoundStatuses.ToString().ToLowerInvariant(),
                        ["found_messages"] = routing.EventInfo.ParserDiagnostics.FoundMessages.ToString().ToLowerInvariant(),
                        ["parser_stage"] = routing.EventInfo.ParserDiagnostics.ParserStage,
                        ["missing_expected_status_fields"] = routing.EventInfo.ParserDiagnostics.MissingRequiredStatusFields,
                        ["payload_structure_summary"] = routing.EventInfo.ParserDiagnostics.PayloadStructureSummary,
                        ["detalhes"] = "Evento de status auditado; sem roteamento para n8n."
                    });
                return Accepted(new
                {
                    status = "audited",
                    reason = "status_event",
                    traceId = HttpContext.TraceIdentifier
                });
            }
            stage = "interaction_resolved";
            HttpContext.Items["meta.whatsapp.stage"] = stage;
            HttpContext.Items["interactions.register.stage"] = stage;

        if (!interactionRouter.TryRegisterInboundMessage(routing.EventInfo.MetaMessageId, now))
        {
            await WriteInteractionLogAsync(
                routing.EventInfo.CustomerPhone,
                "WEBHOOK_META_DEDUPLICADO",
                now,
                new Dictionary<string, string?>
                {
                    ["meta_message_id"] = routing.EventInfo.MetaMessageId,
                    ["event_type"] = routing.EventInfo.EventType,
                    ["detalhes"] = "Entrega duplicada de webhook ignorada para evitar dispatch duplicado."
                });

            return Accepted(new
            {
                status = "ignored",
                reason = "duplicate_inbound_message_id",
                traceId = HttpContext.TraceIdentifier
            });
        }

        var normalizedPayloadJson = routing.EventInfo.NormalizedPayload.ValueKind == JsonValueKind.Undefined
            ? "-"
            : routing.EventInfo.NormalizedPayload.GetRawText();
        var rawPayloadJson = payload.GetRawText();
        var correlationOutcome = routing.Interaction is not null
            ? "matched"
            : routing.IsAmbiguousPhoneMatch
                ? "ambiguous_phone_correlation"
                : string.Equals(routing.EventInfo.ParseReason, "invalid_payload_shape", StringComparison.Ordinal)
                    ? "invalid_payload"
                    : string.Equals(routing.EventInfo.ParseReason, "unexpected_format", StringComparison.Ordinal)
                        ? "unexpected_format"
                        : string.Equals(routing.EventInfo.EventType, "message_status", StringComparison.Ordinal)
                            ? "status_event_without_interaction"
                            : string.IsNullOrWhiteSpace(routing.EventInfo.CustomerPhone)
                                ? "no_messages_found"
                    : "no_active_interaction_for_identifier";

        await WriteInteractionLogAsync(
            routing.Interaction?.PhoneKey ?? routing.Interaction?.CustomerPhone ?? routing.EventInfo.CustomerPhone,
            "WEBHOOK_META_RECEBIDO",
            now,
            new Dictionary<string, string?>
            {
                ["interaction_id"] = routing.Interaction?.InteractionId ?? routing.EventInfo.InteractionId,
                ["telefone"] = routing.Interaction?.PhoneKey ?? routing.Interaction?.CustomerPhone ?? routing.EventInfo.CustomerPhone,
                ["context_message_id"] = routing.EventInfo.ContextMessageId,
                ["meta_message_id"] = routing.EventInfo.MetaMessageId ?? routing.EventInfo.ContextMessageId,
                ["resposta_raw"] = routing.EventInfo.Response,
                ["global_termination_intent"] = isGlobalTerminationIntent.ToString().ToLowerInvariant(),
                ["correlation_strategy"] = routing.CorrelationStrategy,
                ["correlation_outcome"] = correlationOutcome,
                ["event_type"] = routing.EventInfo.EventType,
                ["parse_reason"] = routing.EventInfo.ParseReason,
                ["message_source"] = routing.EventInfo.MessageSource,
                ["source_shape"] = routing.EventInfo.SourceShape,
                ["from_user_id"] = routing.EventInfo.FromUserId,
                ["from_parent_user_id"] = routing.EventInfo.FromParentUserId,
                ["contact_user_id"] = routing.EventInfo.ContactUserId,
                ["contact_parent_user_id"] = routing.EventInfo.ContactParentUserId,
                ["contact_wa_id"] = routing.EventInfo.CustomerWaId,
                ["contact_wa_id_raw"] = routing.EventInfo.CustomerWaIdRaw,
                ["contact_username"] = routing.EventInfo.CustomerUsername,
                ["source_phone"] = routing.EventInfo.CustomerPhone,
                ["source_phone_raw"] = routing.EventInfo.SourcePhoneRaw,
                ["destination_phone_number_id"] = routing.EventInfo.DestinationPhoneNumberId,
                ["destination_display_phone"] = routing.EventInfo.DestinationDisplayPhone,
                ["channel_instance_key"] = MetaWhatsAppInteractionRouter.BuildChannelInstanceKey(routing.EventInfo.DestinationPhoneNumberId),
                ["inbound_destination_phone_number_id"] = routing.EventInfo.DestinationPhoneNumberId,
                ["inbound_destination_display_phone"] = routing.EventInfo.DestinationDisplayPhone,
                ["inbound_channel_instance_key"] = MetaWhatsAppInteractionRouter.BuildChannelInstanceKey(routing.EventInfo.DestinationPhoneNumberId),
                ["status"] = routing.EventInfo.StatusValue,
                ["recipient_id"] = routing.EventInfo.StatusRecipientId,
                ["conversation_id"] = routing.EventInfo.StatusConversationId,
                ["status_error_code"] = routing.EventInfo.StatusErrorCode,
                ["canonical_correlation_key"] = routing.EventInfo.CanonicalCorrelationKey,
                ["canonical_correlation_source"] = routing.EventInfo.CanonicalCorrelationSource,
                ["found_entry"] = routing.EventInfo.ParserDiagnostics.FoundEntry.ToString().ToLowerInvariant(),
                ["found_changes"] = routing.EventInfo.ParserDiagnostics.FoundChanges.ToString().ToLowerInvariant(),
                ["found_value"] = routing.EventInfo.ParserDiagnostics.FoundValue.ToString().ToLowerInvariant(),
                ["found_statuses"] = routing.EventInfo.ParserDiagnostics.FoundStatuses.ToString().ToLowerInvariant(),
                ["found_messages"] = routing.EventInfo.ParserDiagnostics.FoundMessages.ToString().ToLowerInvariant(),
                ["parser_stage"] = routing.EventInfo.ParserDiagnostics.ParserStage,
                ["missing_expected_status_fields"] = routing.EventInfo.ParserDiagnostics.MissingRequiredStatusFields,
                ["payload_structure_summary"] = routing.EventInfo.ParserDiagnostics.PayloadStructureSummary,
                ["detalhes"] = "Webhook recebido da Meta e normalizado internamente",
                ["raw_payload_json"] = rawPayloadJson,
                ["normalized_payload_json"] = normalizedPayloadJson
            }
            );

        await RegisterMediaAuditAsync(routing, now);

        if (routing.Interaction is null)
        {
            var reason = "no_active_interaction_for_identifier";
            if (routing.IsAmbiguousPhoneMatch)
            {
                reason = "ambiguous_phone_correlation";
            }
            else if (string.Equals(routing.EventInfo.ParseReason, "invalid_payload_shape", StringComparison.Ordinal))
            {
                reason = "invalid_payload_shape";
            }
            else if (string.Equals(routing.EventInfo.ParseReason, "no_messages_found", StringComparison.Ordinal))
            {
                reason = "no_messages_found";
            }
            else if (string.Equals(routing.EventInfo.ParseReason, "unexpected_format", StringComparison.Ordinal))
            {
                reason = "unexpected_format";
            }
            else if (string.IsNullOrWhiteSpace(routing.N8nWebhookUrl))
            {
                reason = "global_fallback_not_configured";
            }

            await WriteInteractionLogAsync(
                routing.EventInfo.CustomerPhone,
                "CORRELACAO_NAO_RESOLVIDA",
                now,
                new Dictionary<string, string?>
                {
                    ["interaction_id"] = routing.EventInfo.InteractionId,
                    ["telefone"] = routing.EventInfo.CustomerPhone,
                    ["context_message_id"] = routing.EventInfo.ContextMessageId,
                    ["meta_message_id"] = routing.EventInfo.MetaMessageId ?? routing.EventInfo.ContextMessageId,
                    ["status"] = "IGNORED",
                    ["motivo"] = reason,
                    ["correlation_strategy"] = routing.CorrelationStrategy,
                    ["source_phone"] = routing.EventInfo.CustomerPhone,
                    ["source_phone_raw"] = routing.EventInfo.SourcePhoneRaw,
                    ["destination_phone_number_id"] = routing.EventInfo.DestinationPhoneNumberId,
                    ["destination_display_phone"] = routing.EventInfo.DestinationDisplayPhone,
                    ["channel_instance_key"] = MetaWhatsAppInteractionRouter.BuildChannelInstanceKey(routing.EventInfo.DestinationPhoneNumberId),
                    ["inbound_destination_phone_number_id"] = routing.EventInfo.DestinationPhoneNumberId,
                    ["inbound_destination_display_phone"] = routing.EventInfo.DestinationDisplayPhone,
                    ["inbound_channel_instance_key"] = MetaWhatsAppInteractionRouter.BuildChannelInstanceKey(routing.EventInfo.DestinationPhoneNumberId),
                    ["detalhes"] = "Webhook recebido, porÃ©m sem correlaÃ§Ã£o para interaÃ§Ã£o ativa."
                });

            if (routing.IsAmbiguousPhoneMatch)
            {
                _logger.LogWarning(
                    "Evento WhatsApp ignorado por ambiguidade de correlaÃ§Ã£o via telefone. ContextMessageId: {ContextMessageId}. Telefone: {Telefone}. TraceId: {TraceId}",
                    routing.EventInfo.ContextMessageId,
                    routing.EventInfo.CustomerPhone,
                    HttpContext.TraceIdentifier);

                return Accepted(new
                {
                    status = "ignored",
                    reason,
                    traceId = HttpContext.TraceIdentifier
                });
            }

            var isGlobalNoWithoutInteraction = isGlobalTerminationIntent;
            var globalFallbackWebhookUrl = isGlobalNoWithoutInteraction
                ? resolvedGlobalNoWebhookUrl
                : routing.N8nWebhookUrl;
            if (string.IsNullOrWhiteSpace(globalFallbackWebhookUrl))
            {
                _logger.LogWarning(
                    "Evento WhatsApp sem interação ativa e sem webhook global de fallback configurado. FallbackSource={FallbackSource}. TraceId: {TraceId}",
                    isGlobalNoWithoutInteraction ? resolvedGlobalNoSource : resolvedGlobalFallbackSource,
                    HttpContext.TraceIdentifier);

                return Accepted(new
                {
                    status = "ignored",
                    reason = isGlobalNoWithoutInteraction ? "global_no_not_configured" : "global_fallback_not_configured",
                    traceId = HttpContext.TraceIdentifier
                });
            }

            _logger.LogInformation(
                "Fallback global sem interação ativa resolvido por configuração. FallbackSource={FallbackSource}. WebhookUrl={WebhookUrl}. TraceId={TraceId}",
                isGlobalNoWithoutInteraction ? resolvedGlobalNoSource : resolvedGlobalFallbackSource,
                globalFallbackWebhookUrl,
                HttpContext.TraceIdentifier);

            if (interactionRouter.TryGetRecoverableErrorInteractionByPhone(routing.EventInfo.CustomerPhone, now, out var recoverableErrorInteraction))
            {
                _logger.LogInformation(
                    "fallback_global_used_after_error. prior_interaction_id={InteractionId}. prior_status={InteractionStatus}. fallback_source={FallbackSource}. trace_id={TraceId}",
                    recoverableErrorInteraction?.InteractionId,
                    recoverableErrorInteraction?.Status,
                    isGlobalNoWithoutInteraction ? resolvedGlobalNoSource : resolvedGlobalFallbackSource,
                    HttpContext.TraceIdentifier);
            }

            var fallbackRouteType = isGlobalNoWithoutInteraction ? "NAO" : "FORA_DO_PADRAO";
            var fallbackNormalizedResponse = isGlobalNoWithoutInteraction ? "NAO" : interactionRouter.NormalizeInboundResponse(routing.EventInfo.Response);
            var fallbackInteractionId = ResolveFallbackInteractionId(routing, now);
            var fallbackFlowKey = isGlobalNoWithoutInteraction ? "wa_global_resposta_nao_padrao" : GlobalFallbackFlowKey;
            var fallbackPhoneKey = routing.EventInfo.CustomerPhone;
            var fallbackRecipientE164 = routing.EventInfo.CustomerPhone;
            var fallbackChannelInstanceKey = MetaWhatsAppInteractionRouter.BuildChannelInstanceKey(routing.EventInfo.DestinationPhoneNumberId);
            var fallbackBody = new
            {
                InteractionId = fallbackInteractionId,
                FlowKey = fallbackFlowKey,
                DestinationPhoneNumberId = routing.EventInfo.DestinationPhoneNumberId,
                DestinationDisplayPhone = routing.EventInfo.DestinationDisplayPhone,
                ChannelInstanceKey = fallbackChannelInstanceKey,
                CurrentConversationPhoneNumberId = routing.EventInfo.DestinationPhoneNumberId,
                CurrentConversationDisplayPhone = routing.EventInfo.DestinationDisplayPhone,
                PreferredOutboundPhoneNumberId = routing.EventInfo.DestinationPhoneNumberId,
                PreferredOutboundDisplayPhone = routing.EventInfo.DestinationDisplayPhone,
                PreferredOutboundUnit = (string?)null,
                SenderResolutionSource = "inbound_channel",
                SenderResolutionReason = "inbound_channel_preferred_for_new_interaction",
                destination_phone_number_id = routing.EventInfo.DestinationPhoneNumberId,
                destination_display_phone = routing.EventInfo.DestinationDisplayPhone,
                channel_instance_key = fallbackChannelInstanceKey,
                current_conversation_phone_number_id = routing.EventInfo.DestinationPhoneNumberId,
                current_conversation_display_phone = routing.EventInfo.DestinationDisplayPhone,
                preferred_outbound_phone_number_id = routing.EventInfo.DestinationPhoneNumberId,
                preferred_outbound_display_phone = routing.EventInfo.DestinationDisplayPhone,
                preferred_outbound_unit = (string?)null,
                sender_resolution_source = "inbound_channel",
                sender_resolution_reason = "inbound_channel_preferred_for_new_interaction",
                Interaction = new
                {
                    Channel = "whatsapp",
                    InteractionType = "DESCONHECIDA",
                    ExpectedResponseMode = "FALLBACK_ONLY"
                },
                Response = fallbackNormalizedResponse,
                ResponseRaw = routing.EventInfo.Response,
                RawBody = payload,
                PhoneValue = routing.EventInfo.CustomerPhone,
                Customer = new
                {
                    Id = (int?)null,
                    Name = (string?)null,
                    Document = (string?)null,
                    UserId = routing.EventInfo.CustomerUserId,
                    ParentUserId = routing.EventInfo.CustomerParentUserId,
                    Username = routing.EventInfo.CustomerUsername
                },
                Phone = new
                {
                    PhoneKey = fallbackPhoneKey,
                    RecipientE164 = fallbackRecipientE164,
                    DestinationPhoneNumberId = routing.EventInfo.DestinationPhoneNumberId,
                    DestinationDisplayPhone = routing.EventInfo.DestinationDisplayPhone,
                    ChannelInstanceKey = fallbackChannelInstanceKey
                },
                UserIdentifiers = new
                {
                    UserId = routing.EventInfo.CustomerUserId,
                    ParentUserId = routing.EventInfo.CustomerParentUserId,
                    Username = routing.EventInfo.CustomerUsername,
                    CanonicalUserKey = routing.EventInfo.CanonicalCorrelationKey,
                    IdentifierMode = routing.EventInfo.CanonicalCorrelationSource
                },
                Identifiers = new
                {
                    CanonicalCorrelationKey = routing.EventInfo.CanonicalCorrelationKey,
                    CanonicalCorrelationSource = routing.EventInfo.CanonicalCorrelationSource,
                    CustomerWaId = routing.EventInfo.CustomerWaId,
                    CustomerWaIdRaw = routing.EventInfo.CustomerWaIdRaw,
                    SourcePhoneRaw = routing.EventInfo.SourcePhoneRaw,
                    CanonicalPhone = routing.EventInfo.CustomerPhone,
                    PhoneAliases = MetaWhatsAppPhoneCanonicalizer.BuildLookupAliases(routing.EventInfo.CustomerPhone),
                    CustomerUserId = routing.EventInfo.CustomerUserId,
                    CustomerParentUserId = routing.EventInfo.CustomerParentUserId,
                    StatusRecipientId = routing.EventInfo.StatusRecipientId,
                    StatusRecipientUserId = routing.EventInfo.StatusRecipientUserId,
                    StatusRecipientParentUserId = routing.EventInfo.StatusRecipientParentUserId
                }
            };
            var fallbackPayloadJson = SafeSerializeForLog(fallbackBody);
            _logger.LogInformation(
                "fallback_global.payload_channel_snapshot interaction_id={InteractionId} payload_json={PayloadJson}",
                fallbackInteractionId,
                fallbackPayloadJson);

            var fallbackClient = _httpClientFactory.CreateClient("MetaWhatsAppN8nForwarder");
            using var fallbackRequestMessage = new HttpRequestMessage(HttpMethod.Post, globalFallbackWebhookUrl)
            {
                Content = JsonContent.Create(fallbackBody)
            };
            var fallbackRouteApiKey = isGlobalNoWithoutInteraction
                ? ResolveConfiguredRouteApiKey(N8nWebhookRouteType.No) ?? _options.Value.GlobalNaoN8nApiKey
                : null;
            var fallbackAuth = _webhookSecurityOptions.Value.ResolveFor(
                isGlobalNoWithoutInteraction ? N8nWebhookRouteType.No : N8nWebhookRouteType.Fallback);
            var fallbackAppliedAuthType = ApplyWebhookAuthentication(fallbackRequestMessage, fallbackAuth, fallbackRouteApiKey);

            try
            {
                using var fallbackResponse = await fallbackClient.SendAsync(fallbackRequestMessage);
                var fallbackResponseBody = await fallbackResponse.Content.ReadAsStringAsync();
                var inboundChannelInstanceKey = MetaWhatsAppInteractionRouter.BuildChannelInstanceKey(routing.EventInfo.DestinationPhoneNumberId);
                await WriteInteractionLogAsync(
                    routing.EventInfo.CustomerPhone,
                    fallbackResponse.IsSuccessStatusCode ? "N8N_DISPARO_SUCESSO" : "N8N_DISPARO_FALHA",
                    DateTimeOffset.UtcNow,
                    new Dictionary<string, string?>
                    {
                        ["interaction_id"] = fallbackInteractionId,
                        ["route_type"] = fallbackRouteType,
                        ["n8n_route"] = globalFallbackWebhookUrl,
                        ["global_fallback_source"] = isGlobalNoWithoutInteraction ? resolvedGlobalNoSource : resolvedGlobalFallbackSource,
                        ["auth_mode"] = fallbackAppliedAuthType.ToString(),
                        ["http_status_code"] = ((int)fallbackResponse.StatusCode).ToString(),
                        ["n8n_response_body"] = fallbackResponseBody,
                        ["n8n_response_body_summary"] = SummarizeResponseBody(fallbackResponseBody),
                        ["flow_key"] = fallbackFlowKey,
                        ["fallback_payload_json"] = fallbackPayloadJson,
                        ["inbound_destination_phone_number_id"] = routing.EventInfo.DestinationPhoneNumberId,
                        ["inbound_destination_display_phone"] = routing.EventInfo.DestinationDisplayPhone,
                        ["inbound_channel_instance_key"] = inboundChannelInstanceKey,
                        ["current_conversation_phone_number_id"] = routing.EventInfo.DestinationPhoneNumberId,
                        ["current_conversation_display_phone"] = routing.EventInfo.DestinationDisplayPhone,
                        ["preferred_outbound_phone_number_id"] = routing.EventInfo.DestinationPhoneNumberId,
                        ["preferred_outbound_display_phone"] = routing.EventInfo.DestinationDisplayPhone,
                        ["preferred_outbound_unit"] = null,
                        ["sender_resolution_source"] = "inbound_channel",
                        ["sender_resolution_reason"] = "inbound_channel_preferred_for_new_interaction",
                        ["fallback_applied"] = "false",
                        ["detalhes"] = isGlobalNoWithoutInteraction
                            ? "Disparo global de resposta NÃO sem interação ativa."
                            : "Disparo de fallback global sem interação ativa."
                    });

                if (isGlobalNoWithoutInteraction && fallbackResponse.IsSuccessStatusCode)
                {
                    var phoneForSessionRevoke = FirstNonEmpty(
                        routing.EventInfo.CustomerPhone,
                        fallbackPhoneKey,
                        fallbackRecipientE164);
                    var revokedSession = _chatAuthenticationService.Revoke(phoneForSessionRevoke);

                    _logger.LogInformation(
                        "global_termination_chat_auth_session_revoke_without_active_interaction. interaction_id={InteractionId}. phone_e164={PhoneE164}. session_removed={SessionRemoved}. trace_id={TraceId}",
                        fallbackInteractionId,
                        phoneForSessionRevoke,
                        revokedSession,
                        HttpContext.TraceIdentifier);

                    await WriteInteractionLogAsync(
                        phoneForSessionRevoke,
                        "CHAT_AUTH_SESSION_REVOKED_GLOBAL_TERMINATION",
                        now,
                        new Dictionary<string, string?>
                        {
                            ["interaction_id"] = fallbackInteractionId,
                            ["telefone"] = phoneForSessionRevoke,
                            ["phone_e164"] = phoneForSessionRevoke,
                            ["session_removed"] = revokedSession.ToString().ToLowerInvariant(),
                            ["trace_id"] = HttpContext.TraceIdentifier,
                            ["reason"] = "global_termination_intent_without_active_interaction"
                        });
                }

                return Accepted(new
                {
                    status = fallbackResponse.IsSuccessStatusCode ? "accepted_forwarded" : "accepted_forward_failed",
                    reason = isGlobalNoWithoutInteraction
                        ? "global_no_forward_without_active_interaction"
                        : "global_fallback_forward_without_active_interaction",
                    globalFallbackSource = isGlobalNoWithoutInteraction ? resolvedGlobalNoSource : resolvedGlobalFallbackSource,
                    n8nStatusCode = (int)fallbackResponse.StatusCode,
                    traceId = HttpContext.TraceIdentifier
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Falha ao encaminhar fallback sem interação ativa para n8n. Url: {Url}. TraceId: {TraceId}",
                    globalFallbackWebhookUrl,
                    HttpContext.TraceIdentifier);

                return Accepted(new
                {
                    status = "accepted_forward_failed",
                    reason = "fallback_forward_exception",
                    traceId = HttpContext.TraceIdentifier
                });
            }
        }

        var responseRaw = isGlobalTerminationIntent ? "NAO" : routing.EventInfo.Response?.Trim();
        var inboundRoute = interactionRouter.ResolveInboundRoute(routing.Interaction, responseRaw, routing.EventInfo.FlowResponseJson);
        stage = "outbound_resolved";
        HttpContext.Items["meta.whatsapp.stage"] = stage;
        HttpContext.Items["interactions.register.stage"] = stage;
        var normalizedResponse = inboundRoute.NormalizedResponse;
        if (string.Equals(routing.Interaction.Status, MetaWhatsAppInteractionRouter.StatusErroOperacional, StringComparison.OrdinalIgnoreCase)
            || string.Equals(routing.Interaction.Status, MetaWhatsAppInteractionRouter.StatusErroProcessamentoSim, StringComparison.OrdinalIgnoreCase)
            || string.Equals(routing.Interaction.Status, MetaWhatsAppInteractionRouter.StatusErroRespostaNao, StringComparison.OrdinalIgnoreCase)
            || string.Equals(routing.Interaction.Status, MetaWhatsAppInteractionRouter.StatusErroRespostaFlow, StringComparison.OrdinalIgnoreCase)
            || string.Equals(routing.Interaction.Status, MetaWhatsAppInteractionRouter.StatusErroRespostaForaPadrao, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "customer_flow_error_recovered. interaction_id={InteractionId}. previous_status={PreviousStatus}. route_type={RouteType}. trace_id={TraceId}",
                routing.Interaction.InteractionId,
                routing.Interaction.Status,
                inboundRoute.RouteType,
                HttpContext.TraceIdentifier);
        }

        stage = "response_normalized";
        HttpContext.Items["meta.whatsapp.stage"] = stage;
        HttpContext.Items["interactions.register.stage"] = stage;
        var originalClassification = normalizedResponse;
        var isNaoResponse = string.Equals(normalizedResponse, "NAO", StringComparison.OrdinalIgnoreCase);
        var isOutOfPatternResponse = string.Equals(normalizedResponse, "FORA_DO_PADRAO", StringComparison.OrdinalIgnoreCase);
        var lastOutbound = inboundRoute.LastOutboundMessage;
        var webhookUrl = inboundRoute.WebhookUrl;
        var routeType = inboundRoute.RouteType;
        var routeApiKeyOrigin = inboundRoute.RouteApiKeyOrigin;
        var routeApiKey = inboundRoute.RouteApiKey;
        if (string.Equals(routeType, "NAO", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(webhookUrl))
        {
            webhookUrl = resolvedGlobalNoWebhookUrl;
            if (!string.IsNullOrWhiteSpace(webhookUrl))
            {
                _logger.LogInformation(
                    "Rota NAO resolvida via fallback global. InteractionId={InteractionId}. Source={Source}",
                    routing.Interaction.InteractionId,
                    resolvedGlobalNoSource);
            }
        }
        if (isGlobalTerminationIntent)
        {
            _logger.LogInformation(
                "global_termination_route_selected. interaction_id={InteractionId}. route_type={RouteType}. route_url={RouteUrl}. fallback_source={FallbackSource}. trace_id={TraceId}",
                routing.Interaction.InteractionId,
                routeType,
                webhookUrl,
                resolvedGlobalNoSource,
                HttpContext.TraceIdentifier);
        }
        var flowResolution = _flowInboundContractResolver.Resolve(
            routing.EventInfo.IsFlowReply,
            routing.Interaction.CompletionIntent,
            routing.Interaction.FlowName,
            routing.Interaction.FlowKey,
            routing.Interaction.TemplateName,
            routing.Interaction.MessageName,
            routing.EventInfo.FlowResponseJson);
        var flowSubtipoDetectado = flowResolution.FlowSubtipoDetectado;
        var flowResponseResolved = flowResolution.ParsedFlowResponse;
        var flowIdDetectado = flowResolution.FlowIdDetectado;
        var flowNameDetectado = flowResolution.FlowNameDetectado;
        var flowCompletionIntentDetectado = flowResolution.FlowCompletionIntentDetectado;
        var flowContractDetected = flowResolution.FlowContractDetected;
        var flowRouteOverridden = false;
        var flowRouteDecisionReason = flowResolution.Reason;
        var fallbackSkippedDueToFlow = false;
        var fallbackUsedDueToUnknownFlow = false;

        if (flowResolution.Handled)
        {
            webhookUrl = flowResolution.RouteOverrideUrl ?? webhookUrl;
            routeType = "FLOW";
            normalizedResponse = "FLOW";
            isOutOfPatternResponse = false;
            flowRouteOverridden = true;
            fallbackSkippedDueToFlow = string.Equals(originalClassification, "FORA_DO_PADRAO", StringComparison.OrdinalIgnoreCase);
            routeApiKey = flowResolution.RouteOverrideApiKey;
            routeApiKeyOrigin = flowResolution.RouteOverrideAuthSource ?? "flow_contract.route_override_api_key";

            _logger.LogInformation(
                "FLOW_CONTRACT_RECOGNIZED. FLOW_CONTRACT={FlowContract}. FLOW_SUBTIPO={FlowSubtipo}. FLOW_NAME={FlowName}. FLOW_ID={FlowId}. FLOW_COMPLETION_INTENT={FlowCompletionIntent}. FLOW_ROUTE_DECISION_REASON={FlowRouteDecisionReason}. FLOW_ROUTE_OVERRIDDEN={FlowRouteOverridden}. FLOW_ROUTE_FINAL={FlowRouteFinal}. InteractionId={InteractionId}. TraceId={TraceId}",
                flowContractDetected,
                flowSubtipoDetectado,
                flowNameDetectado,
                flowIdDetectado,
                flowCompletionIntentDetectado,
                flowRouteDecisionReason,
                flowRouteOverridden,
                webhookUrl,
                routing.Interaction.InteractionId,
                HttpContext.TraceIdentifier);
        }
        else if (flowResolution.IsFlowReply)
        {
            fallbackUsedDueToUnknownFlow = string.Equals(normalizedResponse, "FORA_DO_PADRAO", StringComparison.OrdinalIgnoreCase);
            flowRouteDecisionReason = !flowResolution.FlowReplyJsonValid
                ? "invalid_flow_response_json"
                : flowResolution.ContractRecognized
                    ? "invalid_flow_contract_route_config"
                    : "unknown_contract";
        }

        if (flowResolution.IsFlowReply && !flowResolution.FlowReplyJsonValid)
        {
            _logger.LogWarning(
                "FLOW_REPLY_JSON_INVALID. is_flow_reply={IsFlowReply}. flow_reply_json_valid={FlowReplyJsonValid}. parsed_flow_response={ParsedFlowResponse}. flow_subtipo_detectado={FlowSubtipoDetectado}. flow_contract_detected={FlowContractDetected}. flow_route_overridden={FlowRouteOverridden}. flow_route_decision_reason={FlowRouteDecisionReason}. flow_route_final={FlowRouteFinal}. fallback_skipped_due_to_flow={FallbackSkippedDueToFlow}. fallback_used_due_to_unknown_flow={FallbackUsedDueToUnknownFlow}. original_classification={OriginalClassification}. final_classification={FinalClassification}. interaction_id={InteractionId}. template_name={TemplateName}. message_name={MessageName}. flow_key={FlowKey}. flow_name={FlowName}",
                flowResolution.IsFlowReply,
                flowResolution.FlowReplyJsonValid,
                flowResponseResolved?.GetRawText(),
                flowSubtipoDetectado,
                flowContractDetected,
                flowRouteOverridden,
                flowRouteDecisionReason,
                webhookUrl,
                fallbackSkippedDueToFlow,
                fallbackUsedDueToUnknownFlow,
                originalClassification,
                normalizedResponse,
                routing.Interaction.InteractionId,
                routing.Interaction.TemplateName,
                routing.Interaction.MessageName,
                routing.Interaction.FlowKey,
                flowNameDetectado);
        }
        else if (flowResolution.IsFlowReply && flowResolution.ContractRecognized && !flowResolution.ContractRouteConfigValid)
        {
            _logger.LogError(
                "FLOW_CONTRACT_ROUTE_CONFIG_INVALID. contract_name={ContractName}. route_url_present={RouteUrlPresent}. route_api_key_present={RouteApiKeyPresent}. auth_source_present={AuthSourcePresent}. decision_reason_present={DecisionReasonPresent}. handled={Handled}. fallback_applied_due_to_invalid_contract_config={FallbackAppliedDueToInvalidContractConfig}. interaction_id={InteractionId}. flow_subtipo_detectado={FlowSubtipoDetectado}. flow_contract_detected={FlowContractDetected}",
                flowResolution.FlowContractDetected,
                flowResolution.RouteUrlPresent,
                flowResolution.RouteApiKeyPresent,
                flowResolution.AuthSourcePresent,
                flowResolution.DecisionReasonPresent,
                flowResolution.Handled,
                flowResolution.FallbackAppliedDueToInvalidContractConfig,
                routing.Interaction.InteractionId,
                flowSubtipoDetectado,
                flowContractDetected);
        }
        else if (flowResolution.IsFlowReply && !flowResolution.Handled)
        {
            _logger.LogWarning(
                "FLOW_CONTRACT_UNKNOWN. is_flow_reply={IsFlowReply}. flow_reply_json_valid={FlowReplyJsonValid}. parsed_flow_response={ParsedFlowResponse}. flow_subtipo_detectado={FlowSubtipoDetectado}. flow_contract_detected={FlowContractDetected}. flow_route_overridden={FlowRouteOverridden}. flow_route_decision_reason={FlowRouteDecisionReason}. flow_route_final={FlowRouteFinal}. fallback_skipped_due_to_flow={FallbackSkippedDueToFlow}. fallback_used_due_to_unknown_flow={FallbackUsedDueToUnknownFlow}. original_classification={OriginalClassification}. final_classification={FinalClassification}. interaction_id={InteractionId}. template_name={TemplateName}. message_name={MessageName}. flow_key={FlowKey}. flow_name={FlowName}",
                flowResolution.IsFlowReply,
                flowResolution.FlowReplyJsonValid,
                flowResponseResolved?.GetRawText(),
                flowSubtipoDetectado,
                flowContractDetected,
                flowRouteOverridden,
                flowRouteDecisionReason,
                webhookUrl,
                fallbackSkippedDueToFlow,
                fallbackUsedDueToUnknownFlow,
                originalClassification,
                normalizedResponse,
                routing.Interaction.InteractionId,
                routing.Interaction.TemplateName,
                routing.Interaction.MessageName,
                routing.Interaction.FlowKey,
                flowNameDetectado);
        }

        isNaoResponse = string.Equals(normalizedResponse, "NAO", StringComparison.OrdinalIgnoreCase);
        var isFlowResponse = string.Equals(normalizedResponse, "FLOW", StringComparison.OrdinalIgnoreCase);

        await WriteInteractionLogAsync(
            routing.Interaction.PhoneKey ?? routing.Interaction.CustomerPhone ?? routing.EventInfo.CustomerPhone,
            "CLIENTE_RESPONDEU",
            now,
            new Dictionary<string, string?>
            {
                ["interaction_id"] = routing.Interaction.InteractionId,
                ["telefone"] = routing.Interaction.PhoneKey ?? routing.Interaction.CustomerPhone ?? routing.EventInfo.CustomerPhone,
                ["resposta_raw"] = responseRaw,
                ["status"] = routing.Interaction.Status,
                ["meta_message_id"] = routing.EventInfo.MetaMessageId ?? routing.EventInfo.ContextMessageId,
                ["detalhes"] = "ClassificaÃ§Ã£o da resposta recebida no webhook da Meta"
            }
            );

        await WriteInteractionLogAsync(
            routing.Interaction.PhoneKey ?? routing.Interaction.CustomerPhone ?? routing.EventInfo.CustomerPhone,
            "RESPOSTA_CLASSIFICADA",
            now,
            new Dictionary<string, string?>
            {
                ["interaction_id"] = routing.Interaction.InteractionId,
                ["telefone"] = routing.Interaction.PhoneKey ?? routing.Interaction.CustomerPhone ?? routing.EventInfo.CustomerPhone,
                ["resposta_raw"] = responseRaw,
                ["classificacao"] = normalizedResponse,
                ["status"] = routing.Interaction.Status,
                ["meta_message_id"] = routing.EventInfo.MetaMessageId ?? routing.EventInfo.ContextMessageId,
                ["detalhes"] = "Resposta do cliente classificada pela API"
            });

        await WriteInteractionLogAsync(
            routing.Interaction.PhoneKey ?? routing.Interaction.CustomerPhone ?? routing.EventInfo.CustomerPhone,
            "ROTEAMENTO_INBOUND_DIAGNOSTICO",
            now,
            new Dictionary<string, string?>
            {
                ["interaction_id"] = routing.Interaction.InteractionId,
                ["parser_stage"] = routing.EventInfo.ParserDiagnostics.ParserStage,
                ["event_type"] = routing.EventInfo.EventType,
                ["interaction_status"] = routing.Interaction.Status,
                ["template_name"] = lastOutbound?.TemplateName ?? routing.Interaction.TemplateName,
                ["last_outbound_found"] = (lastOutbound is not null).ToString().ToLowerInvariant(),
                ["last_outbound_decision_anchor"] = (lastOutbound?.IsDecisionAnchor ?? false).ToString().ToLowerInvariant(),
                ["last_outbound_accepts_yes"] = (lastOutbound?.AcceptsYes ?? false).ToString().ToLowerInvariant(),
                ["last_outbound_accepts_no"] = (lastOutbound?.AcceptsNo ?? false).ToString().ToLowerInvariant(),
                ["last_outbound_accepts_flow"] = (lastOutbound?.AcceptsFlow ?? false).ToString().ToLowerInvariant(),
                ["resposta_normalizada"] = normalizedResponse,
                ["route_type"] = routeType,
                ["n8n_route"] = webhookUrl,
                ["route_api_key_origin"] = routeApiKeyOrigin,
                ["is_flow_reply"] = flowResolution.IsFlowReply.ToString().ToLowerInvariant(),
                ["flow_reply_json_valid"] = flowResolution.FlowReplyJsonValid.ToString().ToLowerInvariant(),
                ["parsed_flow_response"] = flowResponseResolved?.GetRawText(),
                ["flow_subtipo_detectado"] = flowSubtipoDetectado,
                ["flow_contract_detected"] = flowContractDetected,
                ["flow_id_detectado"] = flowIdDetectado,
                ["flow_name_detectado"] = flowNameDetectado,
                ["flow_completion_intent_detectado"] = flowCompletionIntentDetectado,
                ["flow_route_overridden"] = flowRouteOverridden.ToString().ToLowerInvariant(),
                ["flow_route_decision_reason"] = flowRouteDecisionReason,
                ["flow_route_config_issue"] = flowResolution.ContractRouteConfigIssue,
                ["flow_route_final"] = webhookUrl,
                ["flow_route_auth_source"] = flowResolution.RouteOverrideAuthSource,
                ["fallback_skipped_due_to_flow"] = fallbackSkippedDueToFlow.ToString().ToLowerInvariant(),
                ["fallback_used_due_to_unknown_flow"] = fallbackUsedDueToUnknownFlow.ToString().ToLowerInvariant(),
                ["original_classification"] = originalClassification,
                ["final_classification"] = normalizedResponse,
                ["detalhes"] = "DiagnÃ³stico completo da resoluÃ§Ã£o de rota inbound."
            });
        stage = "route_resolved";
        HttpContext.Items["meta.whatsapp.stage"] = stage;
        HttpContext.Items["interactions.register.stage"] = stage;
        if (string.IsNullOrWhiteSpace(routeApiKey) && !flowResolution.Handled)
        {
            routeApiKey = routing.Interaction.N8nApiKey;
            routeApiKeyOrigin = "interaction.n8n_api_key";
        }
        if (string.IsNullOrWhiteSpace(routeApiKey) && string.Equals(routeType, "NAO", StringComparison.OrdinalIgnoreCase))
        {
            var globalNoFallbackApiKey = ResolveConfiguredRouteApiKey(N8nWebhookRouteType.No) ?? _options.Value.GlobalNaoN8nApiKey;
            if (!string.IsNullOrWhiteSpace(globalNoFallbackApiKey))
            {
                routeApiKey = globalNoFallbackApiKey;
                routeApiKeyOrigin = "global_nao_api_key_fallback";
                _logger.LogInformation(
                    "Auth de rota NAO veio do fallback global de API key. InteractionId={InteractionId}",
                    routing.Interaction.InteractionId);
            }
        }

        await WriteInteractionLogAsync(
            routing.Interaction.PhoneKey ?? routing.Interaction.CustomerPhone ?? routing.EventInfo.CustomerPhone,
            "ROTA_N8N_RESOLVIDA",
            now,
            new Dictionary<string, string?>
            {
                ["interaction_id"] = routing.Interaction.InteractionId,
                ["telefone"] = routing.Interaction.PhoneKey ?? routing.Interaction.CustomerPhone ?? routing.EventInfo.CustomerPhone,
                ["classificacao"] = normalizedResponse,
                ["route_type"] = routeType,
                ["n8n_route"] = webhookUrl,
                ["api_key_source"] = routeApiKeyOrigin,
                ["api_key_present"] = string.IsNullOrWhiteSpace(routeApiKey) ? "false" : "true",
                ["is_flow_reply"] = flowResolution.IsFlowReply.ToString().ToLowerInvariant(),
                ["flow_reply_json_valid"] = flowResolution.FlowReplyJsonValid.ToString().ToLowerInvariant(),
                ["parsed_flow_response"] = flowResponseResolved?.GetRawText(),
                ["flow_subtipo_detectado"] = flowSubtipoDetectado,
                ["flow_contract_detected"] = flowContractDetected,
                ["flow_id_detectado"] = flowIdDetectado,
                ["flow_name_detectado"] = flowNameDetectado,
                ["flow_completion_intent_detectado"] = flowCompletionIntentDetectado,
                ["flow_route_overridden"] = flowRouteOverridden.ToString().ToLowerInvariant(),
                ["flow_route_decision_reason"] = flowRouteDecisionReason,
                ["flow_route_config_issue"] = flowResolution.ContractRouteConfigIssue,
                ["flow_route_final"] = webhookUrl,
                ["flow_route_auth_source"] = flowResolution.RouteOverrideAuthSource,
                ["fallback_skipped_due_to_flow"] = fallbackSkippedDueToFlow.ToString().ToLowerInvariant(),
                ["fallback_used_due_to_unknown_flow"] = fallbackUsedDueToUnknownFlow.ToString().ToLowerInvariant(),
                ["original_classification"] = originalClassification,
                ["final_classification"] = normalizedResponse,
                ["detalhes"] = "Rota resolvida para encaminhamento interno."
            });

        _logger.LogInformation(
            "Roteamento de resposta definido. CorrelationStrategy: {CorrelationStrategy}. RouteType: {RouteType}. InteractionId: {InteractionId}.",
            routing.CorrelationStrategy,
            string.Equals(normalizedResponse, "FLOW", StringComparison.OrdinalIgnoreCase) ? "FLOW" : isNaoResponse ? "NO" : isOutOfPatternResponse ? "FALLBACK" : "YES",
            routing.Interaction.InteractionId);

        var isPesquisaSatisfacaoConclusao = string.Equals(
            flowContractDetected,
            "PESQUISA_POS_VENDAS",
            StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            await WriteInteractionLogAsync(
                routing.Interaction.PhoneKey ?? routing.Interaction.CustomerPhone ?? routing.EventInfo.CustomerPhone,
                "N8N_DISPARO_FALHA",
                DateTimeOffset.UtcNow,
                new Dictionary<string, string?>
                {
                    ["interaction_id"] = routing.Interaction.InteractionId,
                    ["route_type"] = routeType,
                    ["n8n_route"] = "-",
                    ["api_key_source"] = routeApiKeyOrigin,
                    ["template_name"] = lastOutbound?.TemplateName ?? routing.Interaction.TemplateName,
                    ["last_outbound_found"] = (lastOutbound is not null).ToString().ToLowerInvariant(),
                    ["last_outbound_decision_anchor"] = (lastOutbound?.IsDecisionAnchor ?? false).ToString().ToLowerInvariant(),
                    ["last_outbound_accepts_yes"] = (lastOutbound?.AcceptsYes ?? false).ToString().ToLowerInvariant(),
                    ["last_outbound_accepts_no"] = (lastOutbound?.AcceptsNo ?? false).ToString().ToLowerInvariant(),
                    ["last_outbound_accepts_flow"] = (lastOutbound?.AcceptsFlow ?? false).ToString().ToLowerInvariant(),
                    ["auth_mode"] = "NONE",
                    ["http_status_code"] = "ROUTE_NOT_CONFIGURED",
                    ["erro"] = "Rota n8n nÃ£o configurada para a interaÃ§Ã£o."
                });

            _logger.LogWarning(
                "Evento {Response} recebido sem rota configurada. InteractionId: {InteractionId}. TraceId: {TraceId}",
                normalizedResponse,
                routing.Interaction.InteractionId,
                HttpContext.TraceIdentifier);

            return Accepted(new
            {
                status = "ignored",
                reason = "interaction_route_not_configured",
                flowKey = routing.Interaction.FlowKey,
                response = normalizedResponse,
                traceId = HttpContext.TraceIdentifier
            });
        }

        var forwardCurrentPhoneNumberId = FirstNonEmpty(
            routing.EventInfo.DestinationPhoneNumberId,
            routing.Interaction.CurrentConversationPhoneNumberId,
            routing.Interaction.DestinationPhoneNumberId,
            routing.Interaction.PreferredOutboundPhoneNumberId);

        var forwardCurrentDisplayPhone = FirstNonEmpty(
            routing.EventInfo.DestinationDisplayPhone,
            routing.Interaction.CurrentConversationDisplayPhone,
            routing.Interaction.DestinationDisplayPhone,
            routing.Interaction.PreferredOutboundDisplayPhone);

        var forwardChannelInstanceKey = FirstNonEmpty(
            routing.Interaction.ChannelInstanceKey,
            MetaWhatsAppInteractionRouter.BuildChannelInstanceKey(forwardCurrentPhoneNumberId));

        var forwardPreferredPhoneNumberId = FirstNonEmpty(
            routing.Interaction.PreferredOutboundPhoneNumberId,
            forwardCurrentPhoneNumberId);

        var forwardPreferredDisplayPhone = FirstNonEmpty(
            routing.Interaction.PreferredOutboundDisplayPhone,
            forwardCurrentDisplayPhone);

        var forwardPreferredUnit = routing.Interaction.PreferredOutboundUnit;

        object body = new
        {
            InteractionId = routing.Interaction.InteractionId,
            FlowKey = routing.Interaction.FlowKey,
            FlowId = flowIdDetectado,
            FlowName = flowNameDetectado,
            TemplateName = routing.Interaction.TemplateName,
            MessageName = routing.Interaction.MessageName,
            Interaction = new
            {
                Channel = routing.Interaction.Channel,
                InteractionType = routing.Interaction.InteractionType,
                ExpectedResponseMode = routing.Interaction.ExpectedResponseMode,
                BusinessSource = routing.Interaction.BusinessSource,
                CompletionIntent = routing.Interaction.CompletionIntent,
                BusinessContext = new
                {
                    Source = routing.Interaction.BusinessSource,
                    CompletionIntent = routing.Interaction.CompletionIntent,
                    InitialChargeTitleIds = routing.Interaction.InitialChargeTitleIds,
                    InitialChargeTitleNames = routing.Interaction.InitialChargeTitleNames,
                    AdditionalProperties = routing.Interaction.BusinessAdditionalProperties,
                    additional_properties = routing.Interaction.BusinessAdditionalProperties
                }
            },
            contexto_persistido = routing.Interaction.BusinessAdditionalProperties,
            Response = normalizedResponse,
            ResponseRaw = isOutOfPatternResponse ? responseRaw : null,
            InboundType = normalizedResponse,
            IsFlowReply = routing.EventInfo.IsFlowReply,
            MetaMessageId = routing.EventInfo.MetaMessageId ?? routing.EventInfo.ContextMessageId,
            DestinationPhoneNumberId = forwardCurrentPhoneNumberId,
            DestinationDisplayPhone = forwardCurrentDisplayPhone,
            ChannelInstanceKey = forwardChannelInstanceKey,
            CurrentConversationPhoneNumberId = forwardCurrentPhoneNumberId,
            CurrentConversationDisplayPhone = forwardCurrentDisplayPhone,
            PreferredOutboundPhoneNumberId = forwardPreferredPhoneNumberId,
            PreferredOutboundDisplayPhone = forwardPreferredDisplayPhone,
            PreferredOutboundUnit = forwardPreferredUnit,
            destination_phone_number_id = forwardCurrentPhoneNumberId,
            destination_display_phone = forwardCurrentDisplayPhone,
            channel_instance_key = forwardChannelInstanceKey,
            current_conversation_phone_number_id = forwardCurrentPhoneNumberId,
            current_conversation_display_phone = forwardCurrentDisplayPhone,
            preferred_outbound_phone_number_id = forwardPreferredPhoneNumberId,
            preferred_outbound_display_phone = forwardPreferredDisplayPhone,
            preferred_outbound_unit = forwardPreferredUnit,
            Flow = new
            {
                Key = routing.Interaction.FlowKey,
                Name = flowNameDetectado,
                Id = flowIdDetectado,
                InteractiveType = routing.EventInfo.InteractiveType,
                ResponseJson = flowResponseResolved
            },
            RawBody = payload,
            ProcessedAtUtc = now.UtcDateTime,
            PhoneValue = routing.Interaction.PhoneKey ?? routing.Interaction.CustomerPhone,
            Customer = new
            {
                Id = routing.Interaction.CustomerId,
                Name = routing.Interaction.CustomerName,
                Document = routing.Interaction.CustomerDocument,
                UserId = routing.Interaction.CustomerUserId ?? routing.EventInfo.CustomerUserId,
                ParentUserId = routing.Interaction.CustomerParentUserId ?? routing.EventInfo.CustomerParentUserId,
                Username = routing.Interaction.CustomerUsername ?? routing.EventInfo.CustomerUsername
            },
            Phone = new
            {
                PhoneKey = routing.Interaction.PhoneKey ?? routing.Interaction.CustomerPhone,
                RecipientE164 = routing.Interaction.RecipientE164,
                DestinationPhoneNumberId = forwardCurrentPhoneNumberId,
                DestinationDisplayPhone = forwardCurrentDisplayPhone,
                ChannelInstanceKey = forwardChannelInstanceKey,
                CurrentConversationPhoneNumberId = forwardCurrentPhoneNumberId,
                CurrentConversationDisplayPhone = forwardCurrentDisplayPhone,
                PreferredOutboundPhoneNumberId = forwardPreferredPhoneNumberId,
                PreferredOutboundDisplayPhone = forwardPreferredDisplayPhone,
                PreferredOutboundUnit = forwardPreferredUnit
            },
            UserIdentifiers = new
            {
                UserId = routing.Interaction.CustomerUserId ?? routing.EventInfo.CustomerUserId,
                ParentUserId = routing.Interaction.CustomerParentUserId ?? routing.EventInfo.CustomerParentUserId,
                Username = routing.Interaction.CustomerUsername ?? routing.EventInfo.CustomerUsername,
                CanonicalUserKey = routing.EventInfo.CanonicalCorrelationKey ?? routing.Interaction.CanonicalCorrelationKey,
                IdentifierMode = routing.EventInfo.CanonicalCorrelationSource
            },
            Identifiers = new
            {
                CanonicalCorrelationKey = routing.EventInfo.CanonicalCorrelationKey ?? routing.Interaction.CanonicalCorrelationKey,
                CanonicalCorrelationSource = routing.EventInfo.CanonicalCorrelationSource,
                CustomerWaId = routing.Interaction.CustomerWaId ?? routing.EventInfo.CustomerWaId,
                CustomerWaIdRaw = routing.EventInfo.CustomerWaIdRaw ?? routing.Interaction.WaIdRaw,
                SourcePhoneRaw = routing.EventInfo.SourcePhoneRaw ?? routing.Interaction.SourcePhoneRaw,
                CanonicalPhone = routing.Interaction.CanonicalPhone,
                PhoneAliases = routing.Interaction.PhoneAliases,
                CustomerUserId = routing.Interaction.CustomerUserId ?? routing.EventInfo.CustomerUserId,
                CustomerParentUserId = routing.Interaction.CustomerParentUserId ?? routing.EventInfo.CustomerParentUserId,
                StatusRecipientId = routing.EventInfo.StatusRecipientId,
                StatusRecipientUserId = routing.EventInfo.StatusRecipientUserId,
                StatusRecipientParentUserId = routing.EventInfo.StatusRecipientParentUserId
            }
        };

if (isPesquisaSatisfacaoConclusao)
{
    var resolvedPesquisaFlowName = FirstNonEmpty(
        flowNameDetectado,
        routing.Interaction.FlowName,
        PesquisaPosVendasFlowName);

    var resolvedPesquisaFlowId = FirstNonEmpty(
        flowIdDetectado,
        PesquisaPosVendasFlowId);

    var resolvedPesquisaUserId = FirstNonEmpty(
        routing.Interaction.CustomerUserId,
        routing.EventInfo.CustomerUserId,
        routing.Interaction.RecipientUserId,
        routing.EventInfo.StatusRecipientUserId,
        routing.EventInfo.FromUserId);

    var resolvedPesquisaParentUserId = FirstNonEmpty(
        routing.Interaction.CustomerParentUserId,
        routing.EventInfo.CustomerParentUserId,
        routing.Interaction.RecipientParentUserId,
        routing.EventInfo.StatusRecipientParentUserId,
        routing.EventInfo.FromParentUserId);

    var resolvedPesquisaUsername = FirstNonEmpty(
        routing.Interaction.CustomerUsername,
        routing.EventInfo.CustomerUsername);

    body = new
    {
        interaction_id = routing.Interaction.InteractionId,
        flow_key = routing.Interaction.FlowKey,
        flow_name = resolvedPesquisaFlowName,
        flow_id = resolvedPesquisaFlowId,
        DestinationPhoneNumberId = forwardCurrentPhoneNumberId,
        DestinationDisplayPhone = forwardCurrentDisplayPhone,
        ChannelInstanceKey = forwardChannelInstanceKey,
        CurrentConversationPhoneNumberId = forwardCurrentPhoneNumberId,
        CurrentConversationDisplayPhone = forwardCurrentDisplayPhone,
        PreferredOutboundPhoneNumberId = forwardPreferredPhoneNumberId,
        PreferredOutboundDisplayPhone = forwardPreferredDisplayPhone,
        PreferredOutboundUnit = forwardPreferredUnit,
        template_name = routing.Interaction.TemplateName,
        message_name = routing.Interaction.MessageName,
        cd_cliente = routing.Interaction.CustomerId,
        nm_cliente = routing.Interaction.CustomerName,
        customer = new
        {
            id = routing.Interaction.CustomerId,
            name = routing.Interaction.CustomerName,
            document = routing.Interaction.CustomerDocument
        },
        phone = new
        {
            phone_key = routing.Interaction.PhoneKey ?? routing.Interaction.CustomerPhone,
            recipient_e164 = routing.Interaction.RecipientE164,
            customer_phone = routing.Interaction.CustomerPhone,
            source_phone_raw = routing.EventInfo.SourcePhoneRaw ?? routing.Interaction.SourcePhoneRaw,
            wa_id = routing.Interaction.CustomerWaId ?? routing.EventInfo.CustomerWaId,
            destination_phone_number_id = forwardCurrentPhoneNumberId,
            destination_display_phone = forwardCurrentDisplayPhone,
            channel_instance_key = forwardChannelInstanceKey,
            current_conversation_phone_number_id = forwardCurrentPhoneNumberId,
            current_conversation_display_phone = forwardCurrentDisplayPhone,
            preferred_outbound_phone_number_id = forwardPreferredPhoneNumberId,
            preferred_outbound_display_phone = forwardPreferredDisplayPhone,
            preferred_outbound_unit = forwardPreferredUnit
        },
        Phone = new
        {
            PhoneKey = routing.Interaction.PhoneKey ?? routing.Interaction.CustomerPhone,
            RecipientE164 = routing.Interaction.RecipientE164,
            DestinationPhoneNumberId = forwardCurrentPhoneNumberId,
            DestinationDisplayPhone = forwardCurrentDisplayPhone,
            ChannelInstanceKey = forwardChannelInstanceKey,
            CurrentConversationPhoneNumberId = forwardCurrentPhoneNumberId,
            CurrentConversationDisplayPhone = forwardCurrentDisplayPhone,
            PreferredOutboundPhoneNumberId = forwardPreferredPhoneNumberId,
            PreferredOutboundDisplayPhone = forwardPreferredDisplayPhone,
            PreferredOutboundUnit = forwardPreferredUnit
        },
        user_id = resolvedPesquisaUserId,
        parent_user_id = resolvedPesquisaParentUserId,
        username = resolvedPesquisaUsername,
        meta_message_id = routing.EventInfo.MetaMessageId ?? routing.EventInfo.ContextMessageId,
        response = normalizedResponse,
        response_raw = responseRaw,
        flow = new
        {
            key = routing.Interaction.FlowKey,
            name = resolvedPesquisaFlowName,
            id = resolvedPesquisaFlowId,
            interactive_type = routing.EventInfo.InteractiveType,
            response_json = flowResponseResolved
        },
        flow_response_json = flowResponseResolved,
        destination_phone_number_id = forwardCurrentPhoneNumberId,
        destination_display_phone = forwardCurrentDisplayPhone,
        channel_instance_key = forwardChannelInstanceKey,
        current_conversation_phone_number_id = forwardCurrentPhoneNumberId,
        current_conversation_display_phone = forwardCurrentDisplayPhone,
        preferred_outbound_phone_number_id = forwardPreferredPhoneNumberId,
        preferred_outbound_display_phone = forwardPreferredDisplayPhone,
        preferred_outbound_unit = forwardPreferredUnit,
        raw_body = payload,
        perguntas_respostas = flowResponseResolved,
        tipo_solicitacao = flowSubtipoDetectado,
        timestamp_utc = now.UtcDateTime
    };
}
        _logger.LogInformation(
            "N8N_FORWARD_CHANNEL_CONTEXT_RESOLVED interaction_id={InteractionId} destination_phone_number_id={DestinationPhoneNumberId} destination_display_phone={DestinationDisplayPhone} channel_instance_key={ChannelInstanceKey} current_conversation_phone_number_id={CurrentConversationPhoneNumberId} preferred_outbound_phone_number_id={PreferredOutboundPhoneNumberId} preferred_outbound_display_phone={PreferredOutboundDisplayPhone} preferred_outbound_unit={PreferredOutboundUnit} route_url={RouteUrl} route_type={RouteType}",
            routing.Interaction.InteractionId,
            forwardCurrentPhoneNumberId,
            forwardCurrentDisplayPhone,
            forwardChannelInstanceKey,
            forwardCurrentPhoneNumberId,
            forwardPreferredPhoneNumberId,
            forwardPreferredDisplayPhone,
            forwardPreferredUnit,
            webhookUrl,
            routeType);
        var forwardRequestPayload = SafeSerializeForLog(body);

        var client = _httpClientFactory.CreateClient("MetaWhatsAppN8nForwarder");
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, webhookUrl)
        {
            Content = JsonContent.Create(body)
        };

        var webhookRouteType = routeType switch
        {
            "SIM" => N8nWebhookRouteType.Yes,
            "NAO" => N8nWebhookRouteType.No,
            "FLOW" => N8nWebhookRouteType.Flow,
            _ => N8nWebhookRouteType.Fallback
        };
        var resolvedAuth = _webhookSecurityOptions.Value.ResolveFor(webhookRouteType);
        if (string.IsNullOrWhiteSpace(routeApiKey)
            && !flowResolution.Handled
            && resolvedAuth.AuthType == N8nWebhookAuthType.HeaderAuth
            && !string.IsNullOrWhiteSpace(resolvedAuth.HeaderValue))
        {
            routeApiKey = resolvedAuth.HeaderValue;
            routeApiKeyOrigin = "global_webhook_security_fallback";
            _logger.LogInformation(
                "Auth do disparo veio do fallback global de webhook security. InteractionId={InteractionId}",
                routing.Interaction.InteractionId);
        }
        var appliedAuthType = ApplyWebhookAuthentication(requestMessage, resolvedAuth, routeApiKey);
        var resolvedHeaderName = string.IsNullOrWhiteSpace(resolvedAuth.HeaderName) ? "X-API-Key" : resolvedAuth.HeaderName;
        stage = "n8n_dispatch_started";
        HttpContext.Items["meta.whatsapp.stage"] = stage;
        HttpContext.Items["interactions.register.stage"] = stage;
        _logger.LogInformation(
            "Encaminhamento n8n com autenticação {AuthType}. RouteType: {RouteType}. Url: {WebhookUrl}. InteractionId: {InteractionId}. TraceId: {TraceId}",
            appliedAuthType,
            webhookRouteType,
            webhookUrl,
            routing.Interaction.InteractionId,
            HttpContext.TraceIdentifier);

        var dispatchStartedAt = DateTimeOffset.UtcNow;
        await WriteInteractionLogAsync(
            routing.Interaction.PhoneKey ?? routing.Interaction.CustomerPhone ?? routing.EventInfo.CustomerPhone,
            "N8N_DISPARO_INICIADO",
            dispatchStartedAt,
            new Dictionary<string, string?>
            {
                ["interaction_id"] = routing.Interaction.InteractionId,
                ["route_type"] = routeType,
                ["n8n_route"] = webhookUrl,
                ["auth_header_name"] = resolvedHeaderName,
                ["api_key_source"] = routeApiKeyOrigin,
                ["auth_mode"] = appliedAuthType.ToString(),
                ["http_status_code"] = "-",
                ["elapsed_ms"] = "0",
                ["n8n_response_body_summary"] = "-",
                ["flow_subtipo_detectado"] = flowSubtipoDetectado,
                ["flow_route_overridden"] = flowRouteOverridden.ToString().ToLowerInvariant(),
                ["flow_route_final"] = webhookUrl,
                ["detalhes"] = "Disparo HTTP para webhook n8n iniciado."
            });

        try
        {
            using var response = await client.SendAsync(requestMessage);
            var n8nResponseBody = await response.Content.ReadAsStringAsync();
            var elapsedMs = (DateTimeOffset.UtcNow - dispatchStartedAt).TotalMilliseconds;

            interactionRouter.RecordDispatchResult(
                                routing.Interaction.InteractionId,
                now,
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                response.IsSuccessStatusCode ? null : n8nResponseBody);

            await WriteInteractionLogAsync(
                routing.Interaction.PhoneKey ?? routing.Interaction.CustomerPhone ?? routing.EventInfo.CustomerPhone,
                "ENVIO_ACEITO_META",
                now,
                new Dictionary<string, string?>
                {
                    ["interaction_id"] = routing.Interaction.InteractionId,
                    ["telefone"] = routing.Interaction.PhoneKey ?? routing.Interaction.CustomerPhone ?? routing.EventInfo.CustomerPhone,
                    ["status"] = response.IsSuccessStatusCode ? "OK" : "ERRO_HTTP",
                    ["preferred_outbound_phone_number_id"] = routing.Interaction.PreferredOutboundPhoneNumberId,
                    ["preferred_outbound_display_phone"] = routing.Interaction.PreferredOutboundDisplayPhone,
                    ["preferred_outbound_unit"] = routing.Interaction.PreferredOutboundUnit,
                    ["sender_phone_number_id"] = routing.Interaction.PhoneNumberId,
                    ["sender_display_phone"] = routing.Interaction.CurrentConversationDisplayPhone,
                    ["sender_unit_code"] = routing.Interaction.PreferredOutboundUnit,
                    ["sender_unit_label"] = ResolveSenderUnitLabel(routing.Interaction.PreferredOutboundUnit),
                    ["resolution_reason"] = "n8n_forward_dispatch",
                    ["fallback_applied"] = "false",
                    ["destination_phone_number_id"] = routing.Interaction.DestinationPhoneNumberId ?? routing.EventInfo.DestinationPhoneNumberId,
                    ["destination_display_phone"] = routing.Interaction.DestinationDisplayPhone ?? routing.EventInfo.DestinationDisplayPhone,
                    ["channel_instance_key"] = routing.Interaction.ChannelInstanceKey ?? MetaWhatsAppInteractionRouter.BuildChannelInstanceKey(routing.EventInfo.DestinationPhoneNumberId),
                    ["http_status_code"] = ((int)response.StatusCode).ToString(),
                    ["n8n_route"] = webhookUrl,
                    ["detalhes"] = response.IsSuccessStatusCode ? "Retorno imediato do envio para n8n" : "Falha no encaminhamento para n8n",
                    ["n8n_request_json"] = forwardRequestPayload,
                    ["n8n_response_body"] = n8nResponseBody
                }
            );
            await WriteInteractionLogAsync(
                routing.Interaction.PhoneKey ?? routing.Interaction.CustomerPhone ?? routing.EventInfo.CustomerPhone,
                response.IsSuccessStatusCode ? "N8N_DISPARO_SUCESSO" : "N8N_DISPARO_FALHA",
                DateTimeOffset.UtcNow,
                new Dictionary<string, string?>
                {
                    ["interaction_id"] = routing.Interaction.InteractionId,
                    ["route_type"] = routeType,
                    ["n8n_route"] = webhookUrl,
                    ["auth_header_name"] = resolvedHeaderName,
                    ["api_key_source"] = routeApiKeyOrigin,
                    ["auth_mode"] = appliedAuthType.ToString(),
                    ["http_status_code"] = ((int)response.StatusCode).ToString(),
                    ["elapsed_ms"] = elapsedMs.ToString("F0"),
                    ["n8n_response_body"] = n8nResponseBody,
                    ["n8n_response_body_summary"] = SummarizeResponseBody(n8nResponseBody)
                });

            if (!response.IsSuccessStatusCode)
            {
                if (isNaoResponse)
                {
                    interactionRouter.TryMarkAsFailed(
                        routing.Interaction.InteractionId,
                        new InteractionFailedUpdateRequest
                        {
                            Status = MetaWhatsAppInteractionRouter.StatusErroRespostaNao,
                            FailedAt = now,
                            ErrorMessage = $"Falha HTTP {(int)response.StatusCode} ao chamar n8n",
                            ErrorDetails = n8nResponseBody
                        },
                        now,
                        out _,
                        out _);
                }
                else if (isOutOfPatternResponse)
                {
                    interactionRouter.TryMarkAsFailed(
                        routing.Interaction.InteractionId,
                        new InteractionFailedUpdateRequest
                        {
                            Status = MetaWhatsAppInteractionRouter.StatusErroRespostaForaPadrao,
                            FailedAt = now,
                            ErrorMessage = $"Falha HTTP {(int)response.StatusCode} ao chamar n8n",
                            ErrorDetails = n8nResponseBody
                        },
                        now,
                        out _,
                        out _);
                }
                else if (isFlowResponse)
                {
                    interactionRouter.TryMarkAsFailed(
                        routing.Interaction.InteractionId,
                        new InteractionFailedUpdateRequest
                        {
                            Status = MetaWhatsAppInteractionRouter.StatusErroOperacional,
                            FailedAt = now,
                            ErrorMessage = $"Falha HTTP {(int)response.StatusCode} ao chamar n8n",
                            ErrorDetails = n8nResponseBody
                        },
                        now,
                        out _,
                        out _);
                }
                else
                {
                    interactionRouter.TryMarkSimDispatchFailed(
                        routing.Interaction.InteractionId,
                        now,
                        $"Falha HTTP {(int)response.StatusCode} ao chamar n8n",
                        n8nResponseBody);
                }

                _logger.LogError(
                    "Falha ao encaminhar evento {Response} para n8n. StatusCode: {StatusCode}. Url: {Url}. TraceId: {TraceId}",
                    normalizedResponse,
                    (int)response.StatusCode,
                    webhookUrl,
                    HttpContext.TraceIdentifier);

                await WriteInteractionLogAsync(
                    routing.Interaction.PhoneKey ?? routing.Interaction.CustomerPhone ?? routing.EventInfo.CustomerPhone,
                    "ENVIO_FALHOU",
                    now,
                    new Dictionary<string, string?>
                    {
                        ["interaction_id"] = routing.Interaction.InteractionId,
                        ["telefone"] = routing.Interaction.PhoneKey ?? routing.Interaction.CustomerPhone ?? routing.EventInfo.CustomerPhone,
                        ["status"] = isNaoResponse
                            ? MetaWhatsAppInteractionRouter.StatusErroRespostaNao
                            : isFlowResponse
                                ? MetaWhatsAppInteractionRouter.StatusErroOperacional
                            : isOutOfPatternResponse
                                ? MetaWhatsAppInteractionRouter.StatusErroRespostaForaPadrao
                                : MetaWhatsAppInteractionRouter.StatusErroProcessamentoSim,
                        ["preferred_outbound_phone_number_id"] = routing.Interaction.PreferredOutboundPhoneNumberId,
                        ["preferred_outbound_display_phone"] = routing.Interaction.PreferredOutboundDisplayPhone,
                        ["preferred_outbound_unit"] = routing.Interaction.PreferredOutboundUnit,
                        ["sender_phone_number_id"] = routing.Interaction.PhoneNumberId,
                        ["sender_display_phone"] = routing.Interaction.CurrentConversationDisplayPhone,
                        ["sender_unit_code"] = routing.Interaction.PreferredOutboundUnit,
                        ["sender_unit_label"] = ResolveSenderUnitLabel(routing.Interaction.PreferredOutboundUnit),
                        ["resolution_reason"] = "n8n_forward_dispatch",
                        ["fallback_applied"] = "false",
                        ["destination_phone_number_id"] = routing.Interaction.DestinationPhoneNumberId ?? routing.EventInfo.DestinationPhoneNumberId,
                        ["destination_display_phone"] = routing.Interaction.DestinationDisplayPhone ?? routing.EventInfo.DestinationDisplayPhone,
                        ["channel_instance_key"] = routing.Interaction.ChannelInstanceKey ?? MetaWhatsAppInteractionRouter.BuildChannelInstanceKey(routing.EventInfo.DestinationPhoneNumberId),
                        ["erro"] = $"Falha HTTP {(int)response.StatusCode} ao chamar n8n",
                        ["http_status_code"] = ((int)response.StatusCode).ToString(),
                        ["n8n_route"] = webhookUrl,
                        ["n8n_request_json"] = forwardRequestPayload,
                        ["n8n_response_body"] = n8nResponseBody,
                        ["detalhes"] = "Falha HTTP no encaminhamento para n8n"
                    }
            );

                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                status = "forward_failed",
                n8nStatusCode = (int)response.StatusCode,
                interactionId = routing.Interaction.InteractionId,
                response = normalizedResponse,
                traceId = HttpContext.TraceIdentifier
            });
        }

            if (isGlobalTerminationIntent && isNaoResponse)
            {
                interactionRouter.TryMarkAsRefused(
                    routing.Interaction.InteractionId,
                    new InteractionRefusedUpdateRequest
                    {
                        Response = "NAO",
                        RefusedAt = now,
                        CustomerId = routing.Interaction.CustomerId,
                        CustomerName = routing.Interaction.CustomerName
                    },
                    now,
                    out _,
                    out _);

                var phoneForSessionRevoke = FirstNonEmpty(
                    routing.Interaction.PhoneKey,
                    routing.Interaction.CustomerPhone,
                    routing.Interaction.RecipientE164,
                    routing.EventInfo.CustomerPhone);
                var revokedSession = _chatAuthenticationService.Revoke(phoneForSessionRevoke);

                _logger.LogInformation(
                    "global_termination_chat_auth_session_revoke. interaction_id={InteractionId}. phone_e164={PhoneE164}. session_removed={SessionRemoved}. trace_id={TraceId}",
                    routing.Interaction.InteractionId,
                    phoneForSessionRevoke,
                    revokedSession,
                    HttpContext.TraceIdentifier);

                await WriteInteractionLogAsync(
                    phoneForSessionRevoke,
                    "CHAT_AUTH_SESSION_REVOKED_GLOBAL_TERMINATION",
                    now,
                    new Dictionary<string, string?>
                    {
                        ["interaction_id"] = routing.Interaction.InteractionId,
                        ["telefone"] = phoneForSessionRevoke,
                        ["phone_e164"] = phoneForSessionRevoke,
                        ["session_removed"] = revokedSession.ToString().ToLowerInvariant(),
                        ["trace_id"] = HttpContext.TraceIdentifier,
                        ["reason"] = "global_termination_intent"
                    });

                _logger.LogInformation(
                    "global_termination_interaction_closed. interaction_id={InteractionId}. final_status=RECUSADO. trace_id={TraceId}",
                    routing.Interaction.InteractionId,
                    HttpContext.TraceIdentifier);
            }

            return Ok(new
            {
                status = "forwarded",
                flowKey = routing.Interaction.FlowKey,
                response = normalizedResponse,
                traceId = HttpContext.TraceIdentifier,
                n8nStatusCode = (int)response.StatusCode,
                n8nResponseBody
            });
        }
        catch (Exception ex)
        {
            var elapsedMs = (DateTimeOffset.UtcNow - dispatchStartedAt).TotalMilliseconds;
            interactionRouter.RecordDispatchResult(
                                routing.Interaction.InteractionId,
                now,
                false,
                null,
                ex.Message);
            if (isNaoResponse)
            {
                interactionRouter.TryMarkAsFailed(
                    routing.Interaction.InteractionId,
                    new InteractionFailedUpdateRequest
                    {
                        Status = MetaWhatsAppInteractionRouter.StatusErroRespostaNao,
                        FailedAt = now,
                        ErrorMessage = "Erro ao chamar n8n",
                        ErrorDetails = ex.ToString()
                    },
                    now,
                    out _,
                    out _);
            }
            else if (isOutOfPatternResponse)
            {
                interactionRouter.TryMarkAsFailed(
                    routing.Interaction.InteractionId,
                    new InteractionFailedUpdateRequest
                    {
                        Status = MetaWhatsAppInteractionRouter.StatusErroRespostaForaPadrao,
                        FailedAt = now,
                        ErrorMessage = "Erro ao chamar n8n",
                        ErrorDetails = ex.ToString()
                    },
                    now,
                    out _,
                    out _);
            }
            else if (isFlowResponse)
            {
                interactionRouter.TryMarkAsFailed(
                    routing.Interaction.InteractionId,
                    new InteractionFailedUpdateRequest
                    {
                        Status = MetaWhatsAppInteractionRouter.StatusErroOperacional,
                        FailedAt = now,
                        ErrorMessage = "Erro ao chamar n8n",
                        ErrorDetails = ex.ToString()
                    },
                    now,
                    out _,
                    out _);
            }
            else
            {
                interactionRouter.TryMarkSimDispatchFailed(
                    routing.Interaction.InteractionId,
                    now,
                    "Erro ao chamar n8n",
                    ex.ToString());
            }

            _logger.LogError(ex,
                "ExceÃ§Ã£o ao encaminhar evento {Response} para n8n. Url: {Url}. TraceId: {TraceId}",
                normalizedResponse,
                webhookUrl,
                HttpContext.TraceIdentifier);

            await WriteInteractionLogAsync(
                routing.Interaction.PhoneKey ?? routing.Interaction.CustomerPhone ?? routing.EventInfo.CustomerPhone,
                "N8N_DISPARO_FALHA",
                DateTimeOffset.UtcNow,
                new Dictionary<string, string?>
                {
                    ["interaction_id"] = routing.Interaction.InteractionId,
                    ["route_type"] = routeType,
                    ["n8n_route"] = webhookUrl,
                    ["auth_header_name"] = resolvedHeaderName,
                    ["api_key_source"] = routeApiKeyOrigin,
                    ["auth_mode"] = appliedAuthType.ToString(),
                    ["http_status_code"] = "TIMEOUT_OR_EXCEPTION",
                    ["elapsed_ms"] = elapsedMs.ToString("F0"),
                    ["erro"] = ex.Message,
                    ["n8n_response_body_summary"] = SummarizeResponseBody(ex.Message)
                }
            );

            await WriteInteractionLogAsync(
                routing.Interaction.PhoneKey ?? routing.Interaction.CustomerPhone ?? routing.EventInfo.CustomerPhone,
                "ENVIO_FALHOU",
                now,
                new Dictionary<string, string?>
                {
                    ["interaction_id"] = routing.Interaction.InteractionId,
                    ["telefone"] = routing.Interaction.PhoneKey ?? routing.Interaction.CustomerPhone ?? routing.EventInfo.CustomerPhone,
                    ["status"] = isNaoResponse
                        ? MetaWhatsAppInteractionRouter.StatusErroRespostaNao
                        : isOutOfPatternResponse
                            ? MetaWhatsAppInteractionRouter.StatusErroRespostaForaPadrao
                            : MetaWhatsAppInteractionRouter.StatusErroProcessamentoSim,
                    ["preferred_outbound_phone_number_id"] = routing.Interaction.PreferredOutboundPhoneNumberId,
                    ["preferred_outbound_display_phone"] = routing.Interaction.PreferredOutboundDisplayPhone,
                    ["preferred_outbound_unit"] = routing.Interaction.PreferredOutboundUnit,
                    ["sender_phone_number_id"] = routing.Interaction.PhoneNumberId,
                    ["sender_display_phone"] = routing.Interaction.CurrentConversationDisplayPhone,
                    ["sender_unit_code"] = routing.Interaction.PreferredOutboundUnit,
                    ["sender_unit_label"] = ResolveSenderUnitLabel(routing.Interaction.PreferredOutboundUnit),
                    ["resolution_reason"] = "n8n_forward_dispatch_exception",
                    ["fallback_applied"] = "false",
                    ["destination_phone_number_id"] = routing.Interaction.DestinationPhoneNumberId ?? routing.EventInfo.DestinationPhoneNumberId,
                    ["destination_display_phone"] = routing.Interaction.DestinationDisplayPhone ?? routing.EventInfo.DestinationDisplayPhone,
                    ["channel_instance_key"] = routing.Interaction.ChannelInstanceKey ?? MetaWhatsAppInteractionRouter.BuildChannelInstanceKey(routing.EventInfo.DestinationPhoneNumberId),
                    ["erro"] = "Erro ao chamar n8n",
                    ["http_status_code"] = "TIMEOUT_OR_EXCEPTION",
                    ["n8n_route"] = webhookUrl,
                    ["n8n_request_json"] = forwardRequestPayload,
                    ["detalhes"] = ex.ToString()
                }
            );

            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                status = "forward_exception",
                interactionId = routing.Interaction.InteractionId,
                response = normalizedResponse,
                traceId = HttpContext.TraceIdentifier
            });
        }
        }
        catch (Exception ex)
        {
            var sanitizedBody = SanitizeForDiagnostics(rawBody);
            var payloadSummary = BuildPayloadSummary(parsedEventInfo, routingSnapshot);

            _logger.LogError(
                ex,
                "Falha no processamento do webhook de mensagens da Meta. Stage={Stage}. TraceId={TraceId}. ExceptionType={ExceptionType}. ExceptionMessage={ExceptionMessage}. InnerExceptionType={InnerExceptionType}. InnerExceptionMessage={InnerExceptionMessage}. PayloadSummary={PayloadSummary}. RawPayloadSanitized={RawPayloadSanitized}",
                stage,
                HttpContext.TraceIdentifier,
                ex.GetType().FullName,
                ex.Message,
                ex.InnerException?.GetType().FullName,
                ex.InnerException?.Message,
                payloadSummary,
                sanitizedBody);

            await WriteInteractionLogAsync(
                parsedEventInfo?.CustomerPhone ?? routingSnapshot?.Interaction?.PhoneKey,
                "WEBHOOK_META_ERRO_PROCESSAMENTO",
                now,
                new Dictionary<string, string?>
                {
                    ["stage"] = stage,
                    ["trace_id"] = HttpContext.TraceIdentifier,
                    ["exception_type"] = ex.GetType().FullName,
                    ["exception_message"] = ex.Message,
                    ["inner_exception_type"] = ex.InnerException?.GetType().FullName,
                    ["inner_exception_message"] = ex.InnerException?.Message,
                    ["stack_trace"] = ex.StackTrace,
                    ["raw_payload_sanitized"] = sanitizedBody,
                    ["payload_summary"] = payloadSummary,
                    ["detalhes"] = "Erro tratado no pipeline de mensagens do webhook da Meta sem retorno 500 genÃ©rico."
                });

            return Accepted(new
            {
                status = "ignored",
                reason = "messages_processing_error",
                stage,
                traceId = HttpContext.TraceIdentifier
            });
        }
    }

    private async Task RegisterMediaAuditAsync(RoutingDecision routing, DateTimeOffset timestamp)
    {
        if (routing.EventInfo.MediaMetadata is null)
        {
            return;
        }

        var serializedMedia = JsonSerializer.Serialize(routing.EventInfo.MediaMetadata);
        var mediaElement = JsonSerializer.Deserialize<JsonElement>(serializedMedia);
        var mediaType = TryGetString(mediaElement, "type");
        var mediaId = TryGetString(mediaElement, "id");
        var mimeType = TryGetString(mediaElement, "mime_type");
        var fileName = TryGetString(mediaElement, "filename");
        var phone = routing.Interaction?.PhoneKey ?? routing.Interaction?.CustomerPhone ?? routing.EventInfo.CustomerPhone;

        await WriteInteractionLogAsync(
            phone,
            "MIDIA_RECEBIDA",
            timestamp,
            new Dictionary<string, string?>
            {
                ["interaction_id"] = routing.Interaction?.InteractionId ?? routing.EventInfo.InteractionId,
                ["telefone"] = phone,
                ["source_phone"] = routing.EventInfo.CustomerPhone,
                ["source_phone_raw"] = routing.EventInfo.SourcePhoneRaw,
                ["destination_phone_number_id"] = routing.EventInfo.DestinationPhoneNumberId,
                ["destination_display_phone"] = routing.EventInfo.DestinationDisplayPhone,
                ["channel_instance_key"] = MetaWhatsAppInteractionRouter.BuildChannelInstanceKey(routing.EventInfo.DestinationPhoneNumberId),
                ["media_type"] = mediaType,
                ["media_id"] = mediaId,
                ["mime_type"] = mimeType,
                ["file_name"] = fileName,
                ["media_metadata_json"] = serializedMedia,
                ["detalhes"] = "Mensagem com mÃ­dia/documento detectada no webhook da Meta."
            });

        if (!_options.Value.StoreMediaContent || string.IsNullOrWhiteSpace(mediaId))
        {
            return;
        }

        var mediaResult = await DownloadMetaMediaAsync(mediaId, mimeType, fileName, CancellationToken.None);
        await WriteInteractionLogAsync(
            phone,
            mediaResult.Downloaded ? "MIDIA_PERSISTIDA" : "MIDIA_DOWNLOAD_FALHOU",
            timestamp,
            new Dictionary<string, string?>
            {
                ["interaction_id"] = routing.Interaction?.InteractionId ?? routing.EventInfo.InteractionId,
                ["telefone"] = phone,
                ["media_id"] = mediaId,
                ["mime_type"] = mimeType,
                ["storage_path"] = mediaResult.StoragePath,
                ["http_status_code"] = mediaResult.HttpStatusCode?.ToString(),
                ["erro"] = mediaResult.Error,
                ["detalhes"] = mediaResult.Downloaded
                    ? "ConteÃºdo da mÃ­dia baixado e armazenado pela API."
                    : "Falha ao baixar mÃ­dia da Meta."
            });
    }

    private async Task<MediaDownloadResult> DownloadMetaMediaAsync(string mediaId, string? mimeType, string? fileName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Value.MediaAccessToken))
        {
            return MediaDownloadResult.Failed("MediaAccessToken nÃ£o configurado.");
        }

        var storageRoot = ResolveMediaStorageDirectory();
        Directory.CreateDirectory(storageRoot);

        var client = _httpClientFactory.CreateClient("MetaWhatsAppN8nForwarder");
        using var metadataRequest = new HttpRequestMessage(HttpMethod.Get, $"https://graph.facebook.com/v23.0/{mediaId}");
        metadataRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Value.MediaAccessToken);

        using var metadataResponse = await client.SendAsync(metadataRequest, cancellationToken);
        if (!metadataResponse.IsSuccessStatusCode)
        {
            return MediaDownloadResult.Failed($"Falha ao consultar metadata da mÃ­dia {mediaId}.", (int)metadataResponse.StatusCode);
        }

        using var metadataJson = JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync(cancellationToken));
        if (!metadataJson.RootElement.TryGetProperty("url", out var urlElement) || urlElement.ValueKind != JsonValueKind.String)
        {
            return MediaDownloadResult.Failed("Resposta da Meta sem URL de download da mÃ­dia.");
        }

        var downloadUrl = urlElement.GetString();
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            return MediaDownloadResult.Failed("URL de download da mÃ­dia vazia.");
        }

        using var mediaRequest = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        mediaRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Value.MediaAccessToken);

        using var mediaResponse = await client.SendAsync(mediaRequest, cancellationToken);
        if (!mediaResponse.IsSuccessStatusCode)
        {
            return MediaDownloadResult.Failed($"Falha ao baixar conteÃºdo da mÃ­dia {mediaId}.", (int)mediaResponse.StatusCode);
        }

        var extension = ResolveFileExtension(mimeType);
        var safeName = string.IsNullOrWhiteSpace(fileName)
            ? $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}_{mediaId}{extension}"
            : SanitizeFileName(fileName);
        var finalPath = Path.Combine(storageRoot, safeName);
        await using var stream = await mediaResponse.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = System.IO.File.Create(finalPath);
        await stream.CopyToAsync(target, cancellationToken);
        return MediaDownloadResult.Succeeded(finalPath, (int)mediaResponse.StatusCode);
    }

    private string ResolveMediaStorageDirectory()
    {
        if (Path.IsPathRooted(_options.Value.MediaStorageDirectory))
        {
            return _options.Value.MediaStorageDirectory!;
        }

        var relative = string.IsNullOrWhiteSpace(_options.Value.MediaStorageDirectory)
            ? "Arquivos e Documentos/WhatsappMedia"
            : _options.Value.MediaStorageDirectory!;
        return Path.GetFullPath(Path.Combine(_environment.ContentRootPath, "..", relative));
    }

    private static string ResolveFileExtension(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return ".bin";
        }

        var normalized = mimeType.Trim().ToLowerInvariant();
        return normalized switch
        {
            "application/pdf" => ".pdf",
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "audio/ogg" => ".ogg",
            "video/mp4" => ".mp4",
            _ => ".bin"
        };
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return string.Concat(fileName.Select(ch => invalidChars.Contains(ch) ? '_' : ch));
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private N8nWebhookAuthType ApplyWebhookAuthentication(HttpRequestMessage requestMessage, ResolvedN8nWebhookAuthSettings resolvedAuth, string? routeApiKey)
    {
        if (!string.IsNullOrWhiteSpace(routeApiKey))
        {
            var headerName = string.IsNullOrWhiteSpace(resolvedAuth.HeaderName) ? "X-API-Key" : resolvedAuth.HeaderName;
            return TryApplyHeaderAuth(requestMessage, headerName, routeApiKey, "route_api_key")
                ? N8nWebhookAuthType.HeaderAuth
                : N8nWebhookAuthType.None;
        }

        switch (resolvedAuth.AuthType)
        {
            case N8nWebhookAuthType.None:
                return N8nWebhookAuthType.None;

            case N8nWebhookAuthType.HeaderAuth:
                if (string.IsNullOrWhiteSpace(resolvedAuth.HeaderName) || string.IsNullOrWhiteSpace(resolvedAuth.HeaderValue))
                {
                    _logger.LogWarning("HeaderAuth configurado sem HeaderName/HeaderValue vÃ¡lido. Encaminhamento seguirÃ¡ sem header.");
                    return N8nWebhookAuthType.None;
                }

                return TryApplyHeaderAuth(requestMessage, resolvedAuth.HeaderName, resolvedAuth.HeaderValue, "security_options")
                    ? N8nWebhookAuthType.HeaderAuth
                    : N8nWebhookAuthType.None;

            case N8nWebhookAuthType.BasicAuth:
                if (string.IsNullOrWhiteSpace(resolvedAuth.BasicAuthUsername) || string.IsNullOrWhiteSpace(resolvedAuth.BasicAuthPassword))
                {
                    _logger.LogWarning("BasicAuth configurado sem usuÃ¡rio/senha. Encaminhamento seguirÃ¡ sem Authorization.");
                    return N8nWebhookAuthType.None;
                }

                var basicRaw = $"{resolvedAuth.BasicAuthUsername}:{resolvedAuth.BasicAuthPassword}";
                var basicValue = Convert.ToBase64String(Encoding.UTF8.GetBytes(basicRaw));
                requestMessage.Headers.Remove("Authorization");
                requestMessage.Headers.TryAddWithoutValidation("Authorization", $"Basic {basicValue}");
                return N8nWebhookAuthType.BasicAuth;

            case N8nWebhookAuthType.JwtAuth:
                if (string.IsNullOrWhiteSpace(resolvedAuth.JwtSecret))
                {
                    _logger.LogWarning("JwtAuth configurado sem JwtSecret. Encaminhamento seguirÃ¡ sem Authorization.");
                    return N8nWebhookAuthType.None;
                }

                var token = BuildInternalJwtToken(resolvedAuth, DateTimeOffset.UtcNow);
                requestMessage.Headers.Remove("Authorization");
                requestMessage.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
                return N8nWebhookAuthType.JwtAuth;

            default:
                return N8nWebhookAuthType.None;
        }
    }

    private bool TryApplyHeaderAuth(HttpRequestMessage requestMessage, string headerName, string headerValue, string source)
    {
        try
        {
            requestMessage.Headers.Remove(headerName);
            requestMessage.Headers.TryAddWithoutValidation(headerName, headerValue);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Falha ao aplicar header de autenticaÃ§Ã£o no webhook n8n. HeaderName={HeaderName}. Source={Source}. TraceId={TraceId}",
                headerName,
                source,
                HttpContext.TraceIdentifier);
            return false;
        }
    }

    private string? ResolveConfiguredRouteApiKey(N8nWebhookRouteType routeType)
    {
        var resolvedAuth = _webhookSecurityOptions.Value.ResolveFor(routeType);
        if (resolvedAuth.AuthType != N8nWebhookAuthType.HeaderAuth)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(resolvedAuth.HeaderValue)
            ? null
            : resolvedAuth.HeaderValue.Trim();
    }

    private string? ResolveRouteApiKeyWithFallback(
        string routeName,
        N8nWebhookRouteType routeType,
        string? routeUrl,
        string? routeApiKey,
        string? globalN8nApiKey,
        out string authSource)
    {
        var routeApiKeyFromPayload = string.IsNullOrWhiteSpace(routeApiKey) ? null : routeApiKey.Trim();
        if (!string.IsNullOrWhiteSpace(routeApiKeyFromPayload))
        {
            authSource = "payload.route_api_key";
            return routeApiKeyFromPayload;
        }

        if (!string.IsNullOrWhiteSpace(globalN8nApiKey))
        {
            authSource = "payload.global_n8n_api_key_or_global_config";
            _logger.LogInformation(
                "interactions.register.route_resolution_auth_fallback TraceId={TraceId} RouteName={RouteName} AuthSource={AuthSource}",
                HttpContext.TraceIdentifier,
                routeName,
                authSource);
            return globalN8nApiKey.Trim();
        }

        var configuredRouteKey = ResolveConfiguredRouteApiKey(routeType);
        if (!string.IsNullOrWhiteSpace(configuredRouteKey))
        {
            authSource = "security_options.route_or_default_header_value";
            _logger.LogInformation(
                "interactions.register.route_resolution_auth_fallback TraceId={TraceId} RouteName={RouteName} AuthSource={AuthSource}",
                HttpContext.TraceIdentifier,
                routeName,
                authSource);
            return configuredRouteKey;
        }

        authSource = "not_found";
        if (!string.IsNullOrWhiteSpace(routeUrl))
        {
            _logger.LogWarning(
                "interactions.register.route_resolution_auth_missing TraceId={TraceId} RouteName={RouteName} MissingField={MissingField} AuthSource={AuthSource}",
                HttpContext.TraceIdentifier,
                routeName,
                $"{routeName}_api_key",
                authSource);
        }

        return null;
    }

    private List<RouteAuthValidationError> ValidateRouteAuthConfiguration(InteractionRegistrationRequest request)
    {
        var validationErrors = new List<RouteAuthValidationError>();
        ValidateRouteAuthForRegistration(request.RouteOnYes, request.RouteOnYesApiKey, "route_on_yes", N8nWebhookRouteType.Yes, validationErrors);
        ValidateRouteAuthForRegistration(request.RouteOnNo, request.RouteOnNoApiKey, "route_on_no", N8nWebhookRouteType.No, validationErrors);
        ValidateRouteAuthForRegistration(request.RouteOnFlow, request.RouteOnFlowApiKey, "route_on_flow", N8nWebhookRouteType.Flow, validationErrors);
        ValidateRouteAuthForRegistration(request.RouteOnFallback, request.RouteOnFallbackApiKey, "route_on_fallback", N8nWebhookRouteType.Fallback, validationErrors);
        return validationErrors;
    }

    private void ValidateRouteAuthForRegistration(
        string? routeUrl,
        string? routeApiKey,
        string routeName,
        N8nWebhookRouteType routeType,
        List<RouteAuthValidationError> validationErrors)
    {
        if (string.IsNullOrWhiteSpace(routeUrl))
        {
            return;
        }

        var resolvedAuth = _webhookSecurityOptions.Value.ResolveFor(routeType);
        if (resolvedAuth.AuthType != N8nWebhookAuthType.HeaderAuth)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(routeApiKey))
        {
            return;
        }

        validationErrors.Add(new RouteAuthValidationError(
            routeName,
            "missing_api_key_for_header_auth_route",
            $"{routeName}_api_key",
            "not_found",
            routeUrl));
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private sealed record RouteAuthValidationError(
        string route_name,
        string reason,
        string missing_field,
        string auth_source,
        string? route_url);

    private sealed record RouteResolutionResult(
        string RouteOnYesApiKeySource,
        string RouteOnNoApiKeySource,
        string RouteOnFlowApiKeySource,
        string RouteOnFallbackApiKeySource,
        List<RouteAuthValidationError> RouteResolutionErrors);

    private static bool HasValue(string? value)
        => !string.IsNullOrWhiteSpace(value);

    private string SafeSerializeForLog(object value)
    {
        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                serialization_error = true,
                exception_type = ex.GetType().FullName,
                exception_message = ex.Message
            });
        }
    }

    private void LogRouteSecurityConfigurationSnapshot()
    {
        var security = _webhookSecurityOptions.Value;
        _logger.LogInformation(
            "interactions.register.route_security_config_snapshot TraceId={TraceId} Enabled={Enabled} DefaultAuthType={DefaultAuthType} DefaultHeaderNamePresent={DefaultHeaderNamePresent} DefaultHeaderValuePresent={DefaultHeaderValuePresent} YesAuthTypePresent={YesAuthTypePresent} YesHeaderNamePresent={YesHeaderNamePresent} YesHeaderValuePresent={YesHeaderValuePresent} NoAuthTypePresent={NoAuthTypePresent} NoHeaderNamePresent={NoHeaderNamePresent} NoHeaderValuePresent={NoHeaderValuePresent} FlowAuthTypePresent={FlowAuthTypePresent} FlowHeaderNamePresent={FlowHeaderNamePresent} FlowHeaderValuePresent={FlowHeaderValuePresent} FallbackAuthTypePresent={FallbackAuthTypePresent} FallbackHeaderNamePresent={FallbackHeaderNamePresent} FallbackHeaderValuePresent={FallbackHeaderValuePresent} GlobalNaoN8nApiKeyPresent={GlobalNaoN8nApiKeyPresent}",
            HttpContext.TraceIdentifier,
            security.IsSecurityEnabled(),
            string.IsNullOrWhiteSpace(security.DefaultAuthType) ? "default(HeaderAuth)" : "configured",
            HasValue(security.DefaultHeaderName),
            HasValue(security.DefaultHeaderValue),
            HasValue(security.YesWebhookAuthType),
            HasValue(security.YesWebhookHeaderName),
            HasValue(security.YesWebhookHeaderValue),
            HasValue(security.NoWebhookAuthType),
            HasValue(security.NoWebhookHeaderName),
            HasValue(security.NoWebhookHeaderValue),
            HasValue(security.FlowWebhookAuthType),
            HasValue(security.FlowWebhookHeaderName),
            HasValue(security.FlowWebhookHeaderValue),
            HasValue(security.FallbackWebhookAuthType),
            HasValue(security.FallbackWebhookHeaderName),
            HasValue(security.FallbackWebhookHeaderValue),
            HasValue(_options.Value.GlobalNaoN8nApiKey));
    }

    private RouteResolutionResult ExecuteRouteResolution(InteractionRegistrationRequest request, ref string lastRouteStep)
    {
        lastRouteStep = "before_resolve_configured_yes";
        var yesRouteKey = ResolveConfiguredRouteApiKey(N8nWebhookRouteType.Yes);
        lastRouteStep = "after_resolve_configured_yes";

        lastRouteStep = "before_resolve_configured_no";
        var noRouteKey = ResolveConfiguredRouteApiKey(N8nWebhookRouteType.No);
        lastRouteStep = "after_resolve_configured_no";

        lastRouteStep = "before_resolve_configured_fallback";
        var fallbackRouteKey = ResolveConfiguredRouteApiKey(N8nWebhookRouteType.Fallback);
        lastRouteStep = "after_resolve_configured_fallback";

        lastRouteStep = "before_resolve_configured_flow";
        var flowRouteKey = ResolveConfiguredRouteApiKey(N8nWebhookRouteType.Flow);
        lastRouteStep = "after_resolve_configured_flow";

        lastRouteStep = "before_resolve_global_api_key";
        var globalN8nApiKey = FirstNonEmpty(
            request.N8nApiKey,
            yesRouteKey,
            noRouteKey,
            flowRouteKey,
            fallbackRouteKey,
            _options.Value.GlobalNaoN8nApiKey);
        request.N8nApiKey = globalN8nApiKey;
        lastRouteStep = "after_resolve_global_api_key";

        lastRouteStep = "before_resolve_route_on_yes_api_key";
        request.RouteOnYesApiKey = ResolveRouteApiKeyWithFallback(
            "route_on_yes",
            N8nWebhookRouteType.Yes,
            request.RouteOnYes,
            request.RouteOnYesApiKey,
            globalN8nApiKey,
            out var routeOnYesApiKeySource);
        lastRouteStep = "after_resolve_route_on_yes_api_key";

        lastRouteStep = "before_resolve_route_on_no_api_key";
        request.RouteOnNoApiKey = ResolveRouteApiKeyWithFallback(
            "route_on_no",
            N8nWebhookRouteType.No,
            request.RouteOnNo,
            request.RouteOnNoApiKey,
            globalN8nApiKey,
            out var routeOnNoApiKeySource);
        lastRouteStep = "after_resolve_route_on_no_api_key";

        lastRouteStep = "before_resolve_route_on_flow_api_key";
        request.RouteOnFlowApiKey = ResolveRouteApiKeyWithFallback(
            "route_on_flow",
            N8nWebhookRouteType.Flow,
            request.RouteOnFlow,
            request.RouteOnFlowApiKey,
            globalN8nApiKey,
            out var routeOnFlowApiKeySource);
        lastRouteStep = "after_resolve_route_on_flow_api_key";

        lastRouteStep = "before_resolve_route_on_fallback_api_key";
        request.RouteOnFallbackApiKey = ResolveRouteApiKeyWithFallback(
            "route_on_fallback",
            N8nWebhookRouteType.Fallback,
            request.RouteOnFallback,
            request.RouteOnFallbackApiKey,
            globalN8nApiKey,
            out var routeOnFallbackApiKeySource);
        lastRouteStep = "after_resolve_route_on_fallback_api_key";

        lastRouteStep = "before_validate_route_auth_configuration";
        var routeResolutionErrors = ValidateRouteAuthConfiguration(request);
        lastRouteStep = "after_validate_route_auth_configuration";

        return new RouteResolutionResult(
            routeOnYesApiKeySource,
            routeOnNoApiKeySource,
            routeOnFlowApiKeySource,
            routeOnFallbackApiKeySource,
            routeResolutionErrors);
    }

    private static string SummarizeResponseBody(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "-";
        }

        var singleLine = content.Replace(Environment.NewLine, " ").Trim();
        return singleLine.Length <= 200
            ? singleLine
            : $"{singleLine[..200]}...";
    }

    private static string SanitizeForDiagnostics(string? rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return "-";
        }

        var compact = rawBody.Replace(Environment.NewLine, " ").Trim();
        return compact.Length <= 4000
            ? compact
            : $"{compact[..4000]}...[truncated]";
    }

    private static string BuildPayloadSummary(ParsedWhatsAppEvent? parsed, RoutingDecision? routing)
    {
        var eventInfo = parsed ?? routing?.EventInfo;
        if (eventInfo is null)
        {
            return "event_info_unavailable";
        }

        return JsonSerializer.Serialize(new
        {
            event_type = eventInfo.EventType,
            parse_reason = eventInfo.ParseReason,
            interaction_id = routing?.Interaction?.InteractionId ?? eventInfo.InteractionId,
            message_id = eventInfo.MetaMessageId,
            context_message_id = eventInfo.ContextMessageId,
            customer_phone = eventInfo.CustomerPhone,
            customer_wa_id = eventInfo.CustomerWaId,
            response = eventInfo.Response,
            source_shape = eventInfo.SourceShape,
            parser_stage = eventInfo.ParserDiagnostics.ParserStage,
            found_messages = eventInfo.ParserDiagnostics.FoundMessages,
            found_statuses = eventInfo.ParserDiagnostics.FoundStatuses
        });
    }

    private static string BuildInternalJwtToken(ResolvedN8nWebhookAuthSettings auth, DateTimeOffset nowUtc)
    {
        var header = "{\"alg\":\"HS256\",\"typ\":\"JWT\"}";
        var payloadItems = new List<string>();

        if (!string.IsNullOrWhiteSpace(auth.JwtIssuer))
        {
            payloadItems.Add($"\"iss\":{JsonSerializer.Serialize(auth.JwtIssuer)}");
        }

        if (!string.IsNullOrWhiteSpace(auth.JwtAudience))
        {
            payloadItems.Add($"\"aud\":{JsonSerializer.Serialize(auth.JwtAudience)}");
        }

        payloadItems.Add($"\"iat\":{nowUtc.ToUnixTimeSeconds()}");

        if (auth.JwtRequireExpiration)
        {
            payloadItems.Add($"\"exp\":{nowUtc.AddMinutes(5).ToUnixTimeSeconds()}");
        }

        var payload = "{" + string.Join(",", payloadItems) + "}";
        var signingInput = $"{Base64UrlEncode(Encoding.UTF8.GetBytes(header))}.{Base64UrlEncode(Encoding.UTF8.GetBytes(payload))}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(auth.JwtSecret!));
        var signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
        return $"{signingInput}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private void ResolveRelativeRoutes(InteractionRegistrationRequest request)
    {
        var baseWebhookUrl = request.N8nWebhookUrl ?? _options.Value.DefaultN8nWebhookUrl;
        request.RouteOnYes = ResolveRouteValue(request.RouteOnYes, baseWebhookUrl);
        request.RouteOnNo = ResolveRouteValue(request.RouteOnNo, baseWebhookUrl);
        request.RouteOnFlow = ResolveRouteValue(request.RouteOnFlow, baseWebhookUrl);
        request.RouteOnFallback = ResolveRouteValue(request.RouteOnFallback, baseWebhookUrl);

        ValidateResolvedRoute(nameof(request.RouteOnYes), request.RouteOnYes);
        ValidateResolvedRoute(nameof(request.RouteOnNo), request.RouteOnNo);
        ValidateResolvedRoute(nameof(request.RouteOnFlow), request.RouteOnFlow);
        ValidateResolvedRoute(nameof(request.RouteOnFallback), request.RouteOnFallback);
    }

    private void ValidateResolvedRoute(string routeField, string? routeValue)
    {
        if (string.IsNullOrWhiteSpace(routeValue))
        {
            return;
        }

        if (!Uri.TryCreate(routeValue, UriKind.Absolute, out _))
        {
            ModelState.AddModelError(routeField, $"{routeField} requires an absolute URL, or a resolvable short path when N8nWebhookUrl/DefaultN8nWebhookUrl is configured.");
        }
    }

    private static string? ResolveRouteValue(string? rawRoute, string? baseWebhookUrl)
    {
        if (string.IsNullOrWhiteSpace(rawRoute))
        {
            return null;
        }

        var route = rawRoute.Trim();
        if (Uri.TryCreate(route, UriKind.Absolute, out _))
        {
            return route;
        }

        if (!Uri.TryCreate(baseWebhookUrl, UriKind.Absolute, out var absoluteBase))
        {
            return route;
        }

        var normalizedPath = route.StartsWith("/", StringComparison.Ordinal) ? route : $"/{route}";
                return $"{absoluteBase.Scheme}://{absoluteBase.Authority}{normalizedPath}";
    }

    private static void ApplyFlowSpecificRegistrationDefaults(InteractionRegistrationRequest request)
    {
        var expectedResponseMode = request.ExpectedResponseMode?.Trim();
        if (string.Equals(expectedResponseMode, "FALLBACK_ONLY", StringComparison.OrdinalIgnoreCase))
        {
            request.RouteOnYes = null;
            request.RouteOnNo = null;
            request.RouteOnYesApiKey = null;
            request.RouteOnNoApiKey = null;
            request.RouteOnFlowApiKey = null;
            request.AcceptsYes = false;
            request.AcceptsNo = false;
            request.AcceptsFlow = false;
            request.YesRoute = null;
            request.NoRoute = null;
            request.FlowRoute = null;
            request.YesRouteApiKey = null;
            request.NoRouteApiKey = null;
            request.FlowRouteApiKey = null;
        }

        if (!string.Equals(request.FlowKey?.Trim(), OrdersTrackingFlowKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        request.InteractionType = "INFORMATIVO";
        request.ExpectedResponseMode = "FALLBACK_ONLY";
        request.TemplateName ??= OrdersTrackingTemplateName;
        request.TemplateLanguage ??= OrdersTrackingTemplateLanguage;
        request.RouteOnYes = null;
        request.RouteOnNo = null;
        request.RouteOnYesApiKey = null;
        request.RouteOnNoApiKey = null;
        request.AcceptsYes = false;
        request.AcceptsNo = false;
        request.YesRoute = null;
        request.NoRoute = null;
        request.YesRouteApiKey = null;
        request.NoRouteApiKey = null;
    }

    private void NormalizeAndValidatePhones(InteractionRegistrationRequest request)
    {
        request.CustomerPhone = NormalizeE164(request.CustomerPhone);
        request.PhoneKey = NormalizeE164(request.PhoneKey) ?? request.CustomerPhone;
        request.RecipientE164 = NormalizeE164(request.RecipientE164);
        request.CustomerWaId = NormalizeE164(request.CustomerWaId) ?? request.CustomerPhone;

        if (string.IsNullOrWhiteSpace(request.RecipientE164))
        {
            ModelState.AddModelError(nameof(request.RecipientE164), "RecipientE164 Ã© obrigatÃ³rio no formato E.164 (somente dÃ­gitos, entre 8 e 15).");
        }

        if (!string.IsNullOrWhiteSpace(request.CustomerPhone) && !IsE164(request.CustomerPhone))
        {
            ModelState.AddModelError(nameof(request.CustomerPhone), "CustomerPhone deve estar no formato E.164.");
        }

        if (!string.IsNullOrWhiteSpace(request.PhoneKey) && !IsE164(request.PhoneKey))
        {
            ModelState.AddModelError(nameof(request.PhoneKey), "PhoneKey deve estar no formato E.164.");
        }
    }

    private ObjectResult StructuredValidationProblem()
    {
        var details = ModelState
            .Where(pair => pair.Value?.Errors.Count > 0)
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value!.Errors.Select(error => error.ErrorMessage).ToArray());

        return BuildError(
            StatusCodes.Status400BadRequest,
            "validation_error",
            "invalid_request_payload",
            "Falha de validaÃ§Ã£o no payload enviado.",
            details);
    }

    private ObjectResult BuildError(int statusCode, string errorType, string errorCode, string message, object? details = null)
    {
        AttachInteractionRegisterDebugHeaders();
        var payload = new
        {
            ok = false,
            error_type = errorType,
            error_code = errorCode,
            message,
            details,
            trace_id = HttpContext.TraceIdentifier
        };

        return StatusCode(statusCode, payload);
    }

    private static bool IsE164(string value)
        => value.All(char.IsDigit) && value.Length is >= 8 and <= 15;

    private static bool ShouldReturnRedundantTerminalNoop(string? errorCode, string? currentStatus, string? requestedStatus)
    {
        if (!string.Equals(errorCode, "incompatible_status", StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(currentStatus, "RECUSADO", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalizedRequestedStatus = string.IsNullOrWhiteSpace(requestedStatus)
            ? string.Empty
            : requestedStatus.Trim().ToUpperInvariant();

        return normalizedRequestedStatus switch
        {
            "ENVIADO" => false,
            "DOCUMENTOS_ENVIADOS" => false,
            "AGUARDANDO_RESPOSTA" => false,
            "PENDENTE_ENVIO" => false,
            _ => true
        };
    }



    [HttpGet("/api/meta/whatsapp/chat-auth/session")]
    public IActionResult GetChatAuthSession([FromQuery] string phone)
    {
        var normalizedPhone = NormalizeE164(phone);
        if (string.IsNullOrWhiteSpace(normalizedPhone))
        {
            return BadRequest(new { error = "invalid_phone" });
        }

        var now = DateTimeOffset.UtcNow;
        if (!_chatAuthenticationService.TryGetActive(normalizedPhone, now, out var session) || session is null)
        {
            return NotFound(new { error = "session_not_found" });
        }

        TryResolveDependency<MetaWhatsAppInteractionRouter>(nameof(MetaWhatsAppInteractionRouter), out var router, out _);
        var activeInteraction = router?.GetActiveInteractionsByPhone(normalizedPhone, now).FirstOrDefault();

        return Ok(new
        {
            phone = normalizedPhone,
            authenticated_email = session.AuthenticatedEmail,
            session_expires_at = session.ExpiresAt,
            interaction_id = session.InteractionId,
            channel_instance_key = activeInteraction?.ChannelInstanceKey,
            destination_phone_number_id = activeInteraction?.DestinationPhoneNumberId,
            preferred_outbound_phone_number_id = activeInteraction?.PreferredOutboundPhoneNumberId,
            preferred_outbound_unit = activeInteraction?.PreferredOutboundUnit
        });
    }

    [HttpGet("/api/meta/whatsapp/senders/resolve")]
    public IActionResult ResolveSender([FromQuery] int? customerId, [FromQuery] int? cdPessoa, [FromQuery] string? flowKey, [FromQuery] string? stateCode)
    {
        var resolution = _senderResolver.Resolve(new MetaWhatsAppSenderResolveRequest(customerId, cdPessoa, flowKey, null, null, stateCode));
        _ = WriteInteractionLogAsync(
            null,
            "SENDER_RESOLVIDO",
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?>
            {
                ["interaction_id"] = null,
                ["customer_id"] = customerId?.ToString(),
                ["cd_pessoa"] = cdPessoa?.ToString(),
                ["preferred_outbound_phone_number_id"] = null,
                ["preferred_outbound_display_phone"] = null,
                ["preferred_outbound_unit"] = null,
                ["sender_phone_number_id"] = resolution.SenderPhoneNumberId,
                ["sender_display_phone"] = resolution.SenderDisplayPhone,
                ["sender_unit_code"] = resolution.SenderUnitCode,
                ["resolution_reason"] = resolution.ResolutionReason,
                ["fallback_applied"] = resolution.FallbackApplied.ToString().ToLowerInvariant(),
                ["detalhes"] = "Endpoint de resolução de sender consultado."
            });
        return Ok(new
        {
            customer_id = customerId,
            cd_pessoa = cdPessoa,
            flow_key = flowKey,
            sender_phone_number_id = resolution.SenderPhoneNumberId,
            sender_display_phone = resolution.SenderDisplayPhone,
            sender_unit_code = resolution.SenderUnitCode,
            sender_unit_label = resolution.SenderUnitLabel,
            resolution_reason = resolution.ResolutionReason,
            fallback_applied = resolution.FallbackApplied
        });
    }


    [HttpPost("/api/meta/whatsapp/interactions/resolve-channel")]
    public IActionResult ResolveInteractionChannel([FromBody] JsonElement payload)
    {
        if (!TryResolveDependency<MetaWhatsAppInteractionRouter>(nameof(MetaWhatsAppInteractionRouter), out var router, out var dependencyError))
        {
            return dependencyError!;
        }

        var decision = router!.Resolve(payload, DateTimeOffset.UtcNow, _options.Value.ResolveGlobalFallbackN8nWebhookUrl());
        return Ok(new
        {
            is_fallback = decision.IsFallback,
            is_ambiguous = decision.IsAmbiguousPhoneMatch,
            correlation_strategy = decision.CorrelationStrategy,
            destination_phone_number_id = decision.EventInfo.DestinationPhoneNumberId,
            destination_display_phone = decision.EventInfo.DestinationDisplayPhone,
            channel_instance_key = MetaWhatsAppInteractionRouter.BuildChannelInstanceKey(decision.EventInfo.DestinationPhoneNumberId),
            interaction_id = decision.Interaction?.InteractionId,
            preferred_outbound_phone_number_id = decision.Interaction?.PreferredOutboundPhoneNumberId,
            preferred_outbound_unit = decision.Interaction?.PreferredOutboundUnit
        });
    }

    [HttpGet("/api/meta/whatsapp/interactions/{interactionId}/channel")]
    public IActionResult GetInteractionChannel([FromRoute] string interactionId)
    {
        if (!TryResolveDependency<MetaWhatsAppInteractionRouter>(nameof(MetaWhatsAppInteractionRouter), out var router, out var dependencyError))
        {
            return dependencyError!;
        }

        if (!router!.TryGetInteractionById(interactionId, DateTimeOffset.UtcNow, out var interaction) || interaction is null)
        {
            return NotFound(new { error = "interaction_not_found" });
        }

        _ = WriteInteractionLogAsync(
            interaction.PhoneKey ?? interaction.CustomerPhone ?? interaction.RecipientE164,
            "INTERACTION_CHANNEL_CONSULTADO",
            DateTimeOffset.UtcNow,
            new Dictionary<string, string?>
            {
                ["interaction_id"] = interaction.InteractionId,
                ["customer_id"] = interaction.CustomerId?.ToString(),
                ["cd_pessoa"] = interaction.CustomerId?.ToString(),
                ["preferred_outbound_phone_number_id"] = interaction.PreferredOutboundPhoneNumberId,
                ["preferred_outbound_display_phone"] = interaction.PreferredOutboundDisplayPhone,
                ["preferred_outbound_unit"] = interaction.PreferredOutboundUnit,
                ["sender_phone_number_id"] = interaction.PhoneNumberId,
                ["sender_display_phone"] = interaction.CurrentConversationDisplayPhone,
                ["sender_unit_code"] = interaction.PreferredOutboundUnit,
                ["sender_unit_label"] = ResolveSenderUnitLabel(interaction.PreferredOutboundUnit),
                ["resolution_reason"] = "interaction_channel_lookup",
                ["fallback_applied"] = "false",
                ["detalhes"] = "Canal/sender da interação consultado por interaction_id."
            });

        return Ok(new
        {
            interaction_id = interaction.InteractionId,
            channel_instance_key = interaction.ChannelInstanceKey,
            destination_phone_number_id = interaction.DestinationPhoneNumberId,
            destination_display_phone = interaction.DestinationDisplayPhone,
            current_conversation_phone_number_id = interaction.CurrentConversationPhoneNumberId,
            current_conversation_display_phone = interaction.CurrentConversationDisplayPhone,
            preferred_outbound_phone_number_id = interaction.PreferredOutboundPhoneNumberId,
            preferred_outbound_display_phone = interaction.PreferredOutboundDisplayPhone,
            preferred_outbound_unit = interaction.PreferredOutboundUnit,
            sender_phone_number_id = interaction.PhoneNumberId ?? interaction.PreferredOutboundPhoneNumberId,
            sender_display_phone = interaction.CurrentConversationDisplayPhone ?? interaction.PreferredOutboundDisplayPhone,
            sender_unit_code = interaction.PreferredOutboundUnit,
            sender_unit_label = ResolveSenderUnitLabel(interaction.PreferredOutboundUnit),
            resolution_reason = "interaction_channel_lookup",
            fallback_applied = false
        });
    }

    private static string? ResolveSenderUnitLabel(string? unitCode)
    {
        var normalized = string.IsNullOrWhiteSpace(unitCode)
            ? null
            : unitCode.Trim().ToUpperInvariant();

        return normalized switch
        {
            "SP" => "São Paulo",
            "RS" => "Rio Grande do Sul",
            _ => normalized
        };
    }

    private bool TryResolveSenderByPhoneNumberId(string? phoneNumberId, out MetaWhatsAppSenderOptions? sender)
    {
        sender = null;
        if (string.IsNullOrWhiteSpace(phoneNumberId))
        {
            return false;
        }

        sender = _options.Value.Senders.Values
            .Where(static item => item.Enabled)
            .FirstOrDefault(item => string.Equals(item.PhoneNumberId, phoneNumberId, StringComparison.Ordinal));

        return sender is not null;
    }

    private static string? NormalizeE164(string? rawPhone)
        => MetaWhatsAppPhoneCanonicalizer.ToCanonicalE164Br(rawPhone);

    [HttpPost("/webhooks/meta/whatsapp/interactions")]
    [HttpPost("/api/meta/whatsapp/interactions/register")]
    public async Task<IActionResult> RegisterInteraction()
    {
        LogWhatsAppEndpointDebug("interactions.register", payload: null);

        JsonElement requestBody = default;
        var rawBodyForError = "-";
        var stage = "http_request_received";
        var lastRouteStep = "not_started";
        InteractionRegistrationRequest? requestForError = null;
        HttpContext.Items["interactions.register.stage"] = stage;
        HttpContext.Items["interactions.register.version"] = InteractionRegisterVersion;
        AttachInteractionRegisterDebugHeaders();
        _logger.LogInformation(
            "interactions.register.request_received TraceId={TraceId} Path={Path} Method={Method}",
            HttpContext.TraceIdentifier,
            HttpContext.Request.Path,
            HttpContext.Request.Method);

        try
        {
            stage = "content_type_validation";
            HttpContext.Items["interactions.register.stage"] = stage;
            if (!Request.HasJsonContentType())
            {
                _logger.LogWarning(
                    "interactions.register.unsupported_media_type TraceId={TraceId} ContentType={ContentType}",
                    HttpContext.TraceIdentifier,
                    Request.ContentType);
                return BuildError(
                    StatusCodes.Status415UnsupportedMediaType,
                    "validation_error",
                    "unsupported_media_type",
                    "Content-Type deve ser application/json.");
            }

            stage = "body_read_and_parse";
            HttpContext.Items["interactions.register.stage"] = stage;
            var rawBody = await ReadRawBodyAsync();
            rawBodyForError = rawBody;
            if (string.IsNullOrWhiteSpace(rawBody))
            {
                _logger.LogWarning(
                    "interactions.register.binding_failed TraceId={TraceId} Reason={Reason}",
                    HttpContext.TraceIdentifier,
                    "request_body_null_or_empty");
                return BuildError(
                    StatusCodes.Status400BadRequest,
                    "validation_error",
                    "invalid_request_payload",
                    "Body da requisiÃ§Ã£o nÃ£o pode ser nulo.");
            }

            try
            {
                using var rawJson = JsonDocument.Parse(rawBody);
                requestBody = rawJson.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(
                    ex,
                    "interactions.register.invalid_json TraceId={TraceId}",
                    HttpContext.TraceIdentifier);
                return BuildError(
                    StatusCodes.Status400BadRequest,
                    "validation_error",
                    "invalid_json",
                    "Body contÃ©m JSON invÃ¡lido.");
            }

            stage = "model_binding_deserialization";
            HttpContext.Items["interactions.register.stage"] = stage;
            if (!TryParseInteractionRegistrationRequest(requestBody, out var request, out var parseErrorCode, out var parseErrorMessage, out var parseDetails))
            {
                _logger.LogWarning(
                    "interactions.register.binding_failed TraceId={TraceId} ErrorCode={ErrorCode} Details={Details}",
                    HttpContext.TraceIdentifier,
                    parseErrorCode,
                    JsonSerializer.Serialize(parseDetails));
                return BuildError(
                    StatusCodes.Status422UnprocessableEntity,
                    "validation_error",
                    parseErrorCode!,
                    parseErrorMessage!,
                    parseDetails);
            }
            _logger.LogInformation("interactions.register.binding_ok TraceId={TraceId}", HttpContext.TraceIdentifier);
            requestForError = request;

            stage = "payload_normalization";
            HttpContext.Items["interactions.register.stage"] = stage;
            request.NormalizeFromNestedContract();
            ApplyFlowSpecificRegistrationDefaults(request);
            NormalizeAndValidateCompletionIntent(request);
            string? senderResolutionSource = null;
            string? senderResolutionReason = null;
            _ = WriteInteractionLogAsync(
                request.PhoneKey ?? request.CustomerPhone ?? request.RecipientE164,
                "INTERACTION_REGISTER_NORMALIZATION_BEFORE",
                DateTimeOffset.UtcNow,
                new Dictionary<string, string?>
                {
                    ["interaction_id"] = request.InteractionId,
                    ["destination_phone_number_id"] = request.DestinationPhoneNumberId,
                    ["destination_display_phone"] = request.DestinationDisplayPhone,
                    ["channel_instance_key"] = request.ChannelInstanceKey,
                    ["current_conversation_phone_number_id"] = request.CurrentConversationPhoneNumberId,
                    ["current_conversation_display_phone"] = request.CurrentConversationDisplayPhone,
                    ["preferred_outbound_phone_number_id"] = request.PreferredOutboundPhoneNumberId,
                    ["preferred_outbound_display_phone"] = request.PreferredOutboundDisplayPhone,
                    ["preferred_outbound_unit"] = request.PreferredOutboundUnit,
                    ["sender_resolution_source"] = senderResolutionSource,
                    ["sender_resolution_reason"] = senderResolutionReason
                });
            request.ChannelInstanceKey ??= MetaWhatsAppInteractionRouter.BuildChannelInstanceKey(request.DestinationPhoneNumberId);
            request.CurrentConversationPhoneNumberId ??= request.DestinationPhoneNumberId;
            request.CurrentConversationDisplayPhone ??= request.DestinationDisplayPhone;
            MetaWhatsAppSenderResolution? senderResolution = null;
            if (!string.IsNullOrWhiteSpace(request.DestinationPhoneNumberId)
                && string.IsNullOrWhiteSpace(request.PreferredOutboundPhoneNumberId))
            {
                request.PreferredOutboundPhoneNumberId = request.DestinationPhoneNumberId;
                request.PreferredOutboundDisplayPhone ??= request.DestinationDisplayPhone;

                if (TryResolveSenderByPhoneNumberId(request.DestinationPhoneNumberId, out var senderOptions)
                    && senderOptions is not null)
                {
                    request.PreferredOutboundUnit ??= senderOptions.Key.ToUpperInvariant();
                    request.PreferredOutboundDisplayPhone ??= senderOptions.DisplayPhone;
                }

                senderResolutionSource = "inbound_channel";
                senderResolutionReason = "inbound_channel_preferred_for_new_interaction";
            }

            if (string.IsNullOrWhiteSpace(request.PreferredOutboundPhoneNumberId))
            {
                senderResolution = _senderResolver.Resolve(new MetaWhatsAppSenderResolveRequest(
                    request.CustomerId,
                    request.CdPessoa ?? request.CustomerId,
                    request.FlowKey,
                    null,
                    null,
                    TryResolveStateCode(request),
                    request.DestinationPhoneNumberId,
                    request.DestinationDisplayPhone,
                    request.ChannelInstanceKey));
                request.PreferredOutboundPhoneNumberId = senderResolution.SenderPhoneNumberId;
                request.PreferredOutboundDisplayPhone = senderResolution.SenderDisplayPhone;
                request.PreferredOutboundUnit = senderResolution.SenderUnitCode;
                senderResolutionSource = senderResolution.SenderResolutionSource;
                senderResolutionReason = senderResolution.ResolutionReason;

                _ = WriteInteractionLogAsync(
                    request.PhoneKey ?? request.CustomerPhone ?? request.RecipientE164,
                    "SENDER_RESOLVIDO",
                    DateTimeOffset.UtcNow,
                    new Dictionary<string, string?>
                    {
                        ["interaction_id"] = request.InteractionId,
                        ["customer_id"] = request.CustomerId?.ToString(),
                        ["cd_pessoa"] = (request.CdPessoa ?? request.CustomerId)?.ToString(),
                        ["preferred_outbound_phone_number_id"] = request.PreferredOutboundPhoneNumberId,
                        ["preferred_outbound_display_phone"] = request.PreferredOutboundDisplayPhone,
                        ["preferred_outbound_unit"] = request.PreferredOutboundUnit,
                        ["inbound_destination_phone_number_id"] = request.DestinationPhoneNumberId,
                        ["inbound_destination_display_phone"] = request.DestinationDisplayPhone,
                        ["inbound_channel_instance_key"] = request.ChannelInstanceKey,
                        ["current_conversation_phone_number_id"] = request.CurrentConversationPhoneNumberId,
                        ["current_conversation_display_phone"] = request.CurrentConversationDisplayPhone,
                        ["sender_phone_number_id"] = senderResolution.SenderPhoneNumberId,
                        ["sender_display_phone"] = senderResolution.SenderDisplayPhone,
                        ["sender_unit_code"] = senderResolution.SenderUnitCode,
                        ["sender_resolution_source"] = senderResolutionSource,
                        ["sender_resolution_reason"] = senderResolutionReason,
                        ["resolution_reason"] = senderResolution.ResolutionReason,
                        ["fallback_applied"] = senderResolution.FallbackApplied.ToString().ToLowerInvariant(),
                        ["detalhes"] = "Sender resolvido por item durante registro da interação."
                    });
            }
            else if (string.IsNullOrWhiteSpace(senderResolutionSource))
            {
                senderResolutionSource = "request_preferred_outbound";
                senderResolutionReason = "request_preferred_outbound";
            }

            _ = WriteInteractionLogAsync(
                request.PhoneKey ?? request.CustomerPhone ?? request.RecipientE164,
                "INTERACTION_REGISTER_NORMALIZATION_AFTER",
                DateTimeOffset.UtcNow,
                new Dictionary<string, string?>
                {
                    ["interaction_id"] = request.InteractionId,
                    ["destination_phone_number_id"] = request.DestinationPhoneNumberId,
                    ["destination_display_phone"] = request.DestinationDisplayPhone,
                    ["channel_instance_key"] = request.ChannelInstanceKey,
                    ["current_conversation_phone_number_id"] = request.CurrentConversationPhoneNumberId,
                    ["current_conversation_display_phone"] = request.CurrentConversationDisplayPhone,
                    ["preferred_outbound_phone_number_id"] = request.PreferredOutboundPhoneNumberId,
                    ["preferred_outbound_display_phone"] = request.PreferredOutboundDisplayPhone,
                    ["preferred_outbound_unit"] = request.PreferredOutboundUnit,
                    ["sender_resolution_source"] = senderResolutionSource,
                    ["sender_resolution_reason"] = senderResolutionReason
                });
            _logger.LogInformation(
                "interactions.register.normalization_ok TraceId={TraceId} Snapshot={Snapshot}",
                HttpContext.TraceIdentifier,
                BuildInteractionRegistrationSnapshot(request));

            stage = "contract_validation";
            HttpContext.Items["interactions.register.stage"] = stage;
            if (string.IsNullOrWhiteSpace(request.InteractionId))
            {
                ModelState.AddModelError(nameof(request.InteractionId), "InteractionId Ã© obrigatÃ³rio.");
            }

            if (string.IsNullOrWhiteSpace(request.FlowKey))
            {
                ModelState.AddModelError(nameof(request.FlowKey), "FlowKey Ã© obrigatÃ³rio.");
            }

            var validationResults = new List<ValidationResult>();
            if (!Validator.TryValidateObject(request, new ValidationContext(request), validationResults, validateAllProperties: true))
            {
                foreach (var validationResult in validationResults)
                {
                    var members = validationResult.MemberNames.Any()
                        ? validationResult.MemberNames
                        : [string.Empty];

                    foreach (var member in members)
                    {
                        ModelState.AddModelError(member, validationResult.ErrorMessage ?? "Valor invÃ¡lido.");
                    }
                }
            }

            NormalizeAndValidatePhones(request);
            ResolveRelativeRoutes(request);

            if (!ModelState.IsValid)
            {
                _logger.LogWarning(
                    "interactions.register.validation_failed TraceId={TraceId} Errors={Errors}",
                    HttpContext.TraceIdentifier,
                    JsonSerializer.Serialize(ModelState.Where(pair => pair.Value?.Errors.Count > 0)
                        .ToDictionary(
                            pair => pair.Key,
                            pair => pair.Value!.Errors.Select(error => error.ErrorMessage).ToArray())));
                return StructuredValidationProblem();
            }

            stage = "route_resolution";
            HttpContext.Items["interactions.register.stage"] = stage;
            LogRouteSecurityConfigurationSnapshot();
            var retentionHours = _options.Value.InteractionRetentionHours <= 0
                ? 24
                : _options.Value.InteractionRetentionHours;

            var nowUtc = DateTimeOffset.UtcNow;
            if (!request.ExpiresAtUtc.HasValue || request.ExpiresAtUtc.Value <= nowUtc)
            {
                if (request.ExpiresAtUtc.HasValue)
                {
                    _logger.LogWarning(
                        "interactions.register.expires_at_invalid TraceId={TraceId} InteractionId={InteractionId} ExpiresAtUtc={ExpiresAtUtc} NowUtc={NowUtc}. Applying default retention.",
                        HttpContext.TraceIdentifier,
                        request.InteractionId,
                        request.ExpiresAtUtc,
                        nowUtc);
                }

                request.ExpiresAtUtc = nowUtc.AddHours(retentionHours);
            }

            var routeResolution = ExecuteRouteResolution(request, ref lastRouteStep);
            var routeResolutionErrors = routeResolution.RouteResolutionErrors;
            if (routeResolutionErrors.Count > 0)
            {
                var firstError = routeResolutionErrors[0];
                _logger.LogWarning(
                    "interactions.register.route_resolution_failed TraceId={TraceId} RouteName={RouteName} MissingField={MissingField} AuthSource={AuthSource} Reason={Reason}",
                    HttpContext.TraceIdentifier,
                    firstError.route_name,
                    firstError.missing_field,
                    firstError.auth_source,
                    firstError.reason);
                return BuildError(
                    StatusCodes.Status422UnprocessableEntity,
                    "validation_error",
                    "missing_route_auth_configuration",
                    "Falha na resoluÃ§Ã£o de autenticaÃ§Ã£o da rota.",
                    new
                    {
                        stage,
                        last_route_step = lastRouteStep,
                        route_name = firstError.route_name,
                        reason = firstError.reason,
                        missing_field = firstError.missing_field,
                        auth_source = firstError.auth_source,
                        errors = routeResolutionErrors,
                        trace_id = HttpContext.TraceIdentifier
                    });
            }
            lastRouteStep = "before_route_resolution_ok_log";
            _logger.LogInformation(
                "interactions.register.route_resolution_ok TraceId={TraceId} Routes={Routes}",
                HttpContext.TraceIdentifier,
                SafeSerializeForLog(new
                {
                    completionIntent = request.BusinessContext?.CompletionIntent,
                    request.N8nWebhookUrl,
                    request.RouteOnYes,
                    request.RouteOnNo,
                    request.RouteOnFlow,
                    request.RouteOnFallback,
                    n8nApiKeyPresent = HasValue(request.N8nApiKey),
                    routeOnYesApiKeyPresent = HasValue(request.RouteOnYesApiKey),
                    routeOnNoApiKeyPresent = HasValue(request.RouteOnNoApiKey),
                    routeOnFlowApiKeyPresent = HasValue(request.RouteOnFlowApiKey),
                    routeOnFallbackApiKeyPresent = HasValue(request.RouteOnFallbackApiKey),
                    routeOnYesApiKeySource = routeResolution.RouteOnYesApiKeySource,
                    routeOnNoApiKeySource = routeResolution.RouteOnNoApiKeySource,
                    routeOnFlowApiKeySource = routeResolution.RouteOnFlowApiKeySource,
                    routeOnFallbackApiKeySource = routeResolution.RouteOnFallbackApiKeySource
                }));
            lastRouteStep = "after_route_resolution_ok_log";

            if (IsInteractionRegisterDryRunRequest())
            {
                stage = "dry_run_response";
                HttpContext.Items["interactions.register.stage"] = stage;
                AttachInteractionRegisterDebugHeaders();
                return Ok(new
                {
                    status = "dry_run_ok",
                    trace_id = HttpContext.TraceIdentifier,
                    stage,
                    version = InteractionRegisterVersion,
                    snapshot = BuildInteractionRegistrationSnapshot(request),
                    interaction_preview = BuildInteractionPreview(request)
                });
            }

            stage = "persistence_register_interaction";
            HttpContext.Items["interactions.register.stage"] = stage;
            if (!TryResolveDependency<MetaWhatsAppInteractionRouter>(nameof(MetaWhatsAppInteractionRouter), out var router, out var dependencyError))
            {
                return dependencyError!;
            }
            var interactionRouter = router!;

            var interaction = interactionRouter.Register(request, DateTimeOffset.UtcNow);
            _logger.LogInformation(
                "interactions.register.persistence_ok TraceId={TraceId} InteractionId={InteractionId} FlowKey={FlowKey} CompletionIntent={CompletionIntent}",
                HttpContext.TraceIdentifier,
                interaction.InteractionId,
                interaction.FlowKey,
                interaction.CompletionIntent);

            _ = WriteInteractionLogAsync(
                interaction.PhoneKey ?? interaction.CustomerPhone ?? interaction.RecipientE164,
                "INTERACAO_CRIADA",
                DateTimeOffset.UtcNow,
                new Dictionary<string, string?>
                {
                    ["interaction_id"] = interaction.InteractionId,
                    ["customer_id"] = interaction.CustomerId?.ToString(),
                    ["cd_pessoa"] = interaction.CustomerId?.ToString(),
                    ["telefone"] = interaction.PhoneKey ?? interaction.CustomerPhone ?? interaction.RecipientE164,
                    ["cliente"] = interaction.CustomerName,
                    ["fluxo"] = interaction.FlowKey,
                    ["template"] = interaction.TemplateName,
                    ["flow_name"] = interaction.FlowName,
                    ["workflow_name"] = interaction.WorkflowName,
                    ["webhook_name"] = interaction.WebhookName,
                    ["message_type"] = interaction.MessageType,
                    ["message_name"] = interaction.MessageName,
                    ["template_language"] = interaction.TemplateLanguage,
                    ["whatsapp_node_name"] = interaction.WhatsappNodeName,
                    ["status"] = interaction.Status,
                    ["completion_intent"] = interaction.CompletionIntent,
                    ["preferred_outbound_phone_number_id"] = interaction.PreferredOutboundPhoneNumberId,
                    ["preferred_outbound_display_phone"] = interaction.PreferredOutboundDisplayPhone,
                    ["preferred_outbound_unit"] = interaction.PreferredOutboundUnit,
                    ["sender_phone_number_id"] = senderResolution?.SenderPhoneNumberId ?? interaction.PreferredOutboundPhoneNumberId,
                    ["sender_display_phone"] = senderResolution?.SenderDisplayPhone ?? interaction.PreferredOutboundDisplayPhone,
                    ["sender_unit_code"] = senderResolution?.SenderUnitCode ?? interaction.PreferredOutboundUnit,
                    ["sender_unit_label"] = senderResolution?.SenderUnitLabel ?? ResolveSenderUnitLabel(interaction.PreferredOutboundUnit),
                    ["sender_resolution_source"] = senderResolutionSource ?? senderResolution?.SenderResolutionSource ?? "request_preferred_outbound",
                    ["sender_resolution_reason"] = senderResolutionReason ?? senderResolution?.ResolutionReason ?? "request_preferred_outbound",
                    ["resolution_reason"] = senderResolutionReason ?? senderResolution?.ResolutionReason ?? "request_preferred_outbound",
                    ["fallback_applied"] = senderResolution is null ? "false" : senderResolution.FallbackApplied.ToString().ToLowerInvariant(),
                    ["destination_phone_number_id"] = interaction.DestinationPhoneNumberId,
                    ["destination_display_phone"] = interaction.DestinationDisplayPhone,
                    ["channel_instance_key"] = interaction.ChannelInstanceKey,
                    ["detalhes"] = "InteraÃ§Ã£o registrada pela API",
                    ["payload_resumido"] = MetaWhatsAppPersistentLogService.BuildCompactPayload(request)
                }
                );

            stage = "response_serialization";
            HttpContext.Items["interactions.register.stage"] = stage;
            AttachInteractionRegisterDebugHeaders();
            return Ok(new
            {
                status = "registered",
                interaction = new
                {
                    interaction.InteractionId,
                    interaction.FlowKey,
                    interaction.Status,
                    interaction.CustomerId,
                    interaction.CustomerName,
                    interaction.CustomerDocument,
                    interaction.CustomerPhone,
                    interaction.PhoneKey,
                    interaction.RecipientE164,
                    interaction.FlowName,
                    interaction.WorkflowName,
                    interaction.WebhookName,
                    interaction.MessageType,
                    interaction.MessageName,
                    interaction.TemplateName,
                    interaction.TemplateLanguage,
                    interaction.WhatsappNodeName,
                    interaction.OutboundMessageId,
                    interaction.ButtonPayload,
                    interaction.N8nWebhookUrl,
                    interaction.RouteOnYes,
                    interaction.RouteOnNo,
                    interaction.RouteOnFlow,
                    interaction.RouteOnFallback,
                    interaction.Channel,
                    interaction.InteractionType,
                    interaction.ExpectedResponseMode,
                    interaction.BusinessSource,
                    interaction.CompletionIntent,
                    interaction.InitialChargeTitleIds,
                    interaction.InitialChargeTitleNames,
                    interaction.BusinessAdditionalProperties,
                    interaction.RegisteredAtUtc,
                    interaction.UpdatedAtUtc,
                    interaction.ExpiresAtUtc
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "interactions.register.persistence_failed TraceId={TraceId} Stage={Stage} LastRouteStep={LastRouteStep} ExceptionType={ExceptionType} ExceptionMessage={ExceptionMessage} InnerExceptionType={InnerExceptionType} InnerMessage={InnerMessage} RouteOnYesPresent={RouteOnYesPresent} RouteOnNoPresent={RouteOnNoPresent} RouteOnFlowPresent={RouteOnFlowPresent} RouteOnFallbackPresent={RouteOnFallbackPresent} N8nApiKeyPresent={N8nApiKeyPresent} RouteOnYesApiKeyPresent={RouteOnYesApiKeyPresent} RouteOnNoApiKeyPresent={RouteOnNoApiKeyPresent} RouteOnFlowApiKeyPresent={RouteOnFlowApiKeyPresent} RouteOnFallbackApiKeyPresent={RouteOnFallbackApiKeyPresent}",
                HttpContext.TraceIdentifier,
                stage,
                lastRouteStep,
                ex.GetType().FullName,
                ex.Message,
                ex.InnerException?.GetType().FullName,
                ex.InnerException?.Message,
                HasValue(requestForError?.RouteOnYes),
                HasValue(requestForError?.RouteOnNo),
                HasValue(requestForError?.RouteOnFlow),
                HasValue(requestForError?.RouteOnFallback),
                HasValue(requestForError?.N8nApiKey),
                HasValue(requestForError?.RouteOnYesApiKey),
                HasValue(requestForError?.RouteOnNoApiKey),
                HasValue(requestForError?.RouteOnFlowApiKey),
                HasValue(requestForError?.RouteOnFallbackApiKey));
            _logger.LogError(
                ex,
                "Erro interno ao registrar interaÃ§Ã£o WhatsApp. TraceId: {TraceId}. Body: {RequestBody}",
                HttpContext.TraceIdentifier,
                requestBody.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                    ? requestBody.GetRawText()
                    : rawBodyForError);

            AttachInteractionRegisterDebugHeaders();
            return BuildError(
                StatusCodes.Status500InternalServerError,
                "operational_error",
                "internal_registration_error",
                "Erro interno ao registrar interaÃ§Ã£o. Consulte o trace_id para diagnÃ³stico.",
                new
                {
                    stage,
                    last_route_step = lastRouteStep,
                    exception_type = ex.GetType().FullName,
                    exception_message = ex.Message,
                    inner_exception_type = ex.InnerException?.GetType().FullName,
                    inner_exception_message = ex.InnerException?.Message,
                    route_on_yes_present = HasValue(requestForError?.RouteOnYes),
                    route_on_no_present = HasValue(requestForError?.RouteOnNo),
                    route_on_flow_present = HasValue(requestForError?.RouteOnFlow),
                    route_on_fallback_present = HasValue(requestForError?.RouteOnFallback),
                    n8n_api_key_present = HasValue(requestForError?.N8nApiKey),
                    route_on_yes_api_key_present = HasValue(requestForError?.RouteOnYesApiKey),
                    route_on_no_api_key_present = HasValue(requestForError?.RouteOnNoApiKey),
                    route_on_flow_api_key_present = HasValue(requestForError?.RouteOnFlowApiKey),
                    route_on_fallback_api_key_present = HasValue(requestForError?.RouteOnFallbackApiKey),
                    version = InteractionRegisterVersion,
                    trace_id = HttpContext.TraceIdentifier
                });
        }
    }

    [HttpPost("/webhooks/meta/whatsapp/interactions/debug-raw")]
    public async Task<IActionResult> RegisterInteractionDebugRaw()
    {
        HttpContext.Items["interactions.register.stage"] = "debug_raw_read";
        HttpContext.Items["interactions.register.version"] = InteractionRegisterVersion;
        AttachInteractionRegisterDebugHeaders();

        var rawBody = await ReadRawBodyAsync();
        var preview = rawBody.Length <= 400 ? rawBody : rawBody[..400];

        return Ok(new
        {
            ok = true,
            endpoint = "debug-raw",
            trace_id = HttpContext.TraceIdentifier,
            stage = "debug_raw_read",
            version = InteractionRegisterVersion,
            content_type = Request.ContentType,
            content_length = Request.ContentLength,
            body_size = rawBody.Length,
            body_preview = preview
        });
    }

    [HttpPost("/webhooks/meta/whatsapp/interactions/debug-parse")]
    public async Task<IActionResult> RegisterInteractionDebugParse()
    {
        HttpContext.Items["interactions.register.stage"] = "debug_parse_read";
        HttpContext.Items["interactions.register.version"] = InteractionRegisterVersion;
        AttachInteractionRegisterDebugHeaders();

        var rawBody = await ReadRawBodyAsync();
        var topLevelProperties = new List<string>();
        var validJson = false;
        string? parseError = null;

        if (!string.IsNullOrWhiteSpace(rawBody))
        {
            try
            {
                using var json = JsonDocument.Parse(rawBody);
                validJson = true;
                if (json.RootElement.ValueKind == JsonValueKind.Object)
                {
                    topLevelProperties.AddRange(json.RootElement.EnumerateObject().Select(static property => property.Name));
                }
            }
            catch (JsonException ex)
            {
                parseError = ex.Message;
            }
        }

        HttpContext.Items["interactions.register.stage"] = "debug_parse_response";
        AttachInteractionRegisterDebugHeaders();
        return Ok(new
        {
            ok = true,
            endpoint = "debug-parse",
            trace_id = HttpContext.TraceIdentifier,
            stage = "debug_parse_response",
            version = InteractionRegisterVersion,
            content_type = Request.ContentType,
            content_length = Request.ContentLength,
            body_size = rawBody.Length,
            valid_json = validJson,
            top_level_properties = topLevelProperties,
            parse_error = parseError
        });
    }

    [HttpPost("/webhooks/meta/whatsapp/interactions/debug-route-resolution")]
    public async Task<IActionResult> RegisterInteractionDebugRouteResolution()
    {
        var stage = "debug_route_resolution_read";
        var lastRouteStep = "not_started";
        HttpContext.Items["interactions.register.stage"] = stage;
        HttpContext.Items["interactions.register.version"] = InteractionRegisterVersion;
        AttachInteractionRegisterDebugHeaders();

        try
        {
            var rawBody = await ReadRawBodyAsync();
            if (string.IsNullOrWhiteSpace(rawBody))
            {
                return Ok(new
                {
                    ok = false,
                    stage,
                    last_route_step = lastRouteStep,
                    routeResolutionErrors = new[] { "request_body_null_or_empty" }
                });
            }

            using var rawJson = JsonDocument.Parse(rawBody);
            var requestBody = rawJson.RootElement.Clone();

            stage = "debug_route_resolution_parse";
            HttpContext.Items["interactions.register.stage"] = stage;
            if (!TryParseInteractionRegistrationRequest(requestBody, out var request, out var parseErrorCode, out var parseErrorMessage, out var _))
            {
                return Ok(new
                {
                    ok = false,
                    stage,
                    last_route_step = lastRouteStep,
                    routeResolutionErrors = new[] { parseErrorCode, parseErrorMessage }
                });
            }

            request.NormalizeFromNestedContract();
            NormalizeAndValidatePhones(request);
            ResolveRelativeRoutes(request);
            LogRouteSecurityConfigurationSnapshot();

            stage = "debug_route_resolution_execute";
            HttpContext.Items["interactions.register.stage"] = stage;
            var routeResolution = ExecuteRouteResolution(request, ref lastRouteStep);
            var errors = routeResolution.RouteResolutionErrors
                .Select(error => new
                {
                    error.route_name,
                    error.reason,
                    error.missing_field,
                    error.auth_source,
                    error.route_url
                })
                .ToArray();

            return Ok(new
            {
                ok = errors.Length == 0,
                stage,
                last_route_step = lastRouteStep,
                yesRouteKeyPresent = HasValue(request.RouteOnYesApiKey),
                noRouteKeyPresent = HasValue(request.RouteOnNoApiKey),
                flowRouteKeyPresent = HasValue(request.RouteOnFlowApiKey),
                fallbackRouteKeyPresent = HasValue(request.RouteOnFallbackApiKey),
                globalN8nApiKeyPresent = HasValue(request.N8nApiKey),
                requestRouteOnYesApiKeyPresent = HasValue(request.RouteOnYesApiKey),
                requestRouteOnNoApiKeyPresent = HasValue(request.RouteOnNoApiKey),
                requestRouteOnFlowApiKeyPresent = HasValue(request.RouteOnFlowApiKey),
                requestRouteOnFallbackApiKeyPresent = HasValue(request.RouteOnFallbackApiKey),
                routeResolutionErrors = errors,
                exception_type = (string?)null,
                exception_message = (string?)null,
                inner_exception_type = (string?)null,
                inner_exception_message = (string?)null
            });
        }
        catch (Exception ex)
        {
            return Ok(new
            {
                ok = false,
                stage,
                last_route_step = lastRouteStep,
                yesRouteKeyPresent = false,
                noRouteKeyPresent = false,
                flowRouteKeyPresent = false,
                fallbackRouteKeyPresent = false,
                globalN8nApiKeyPresent = false,
                requestRouteOnYesApiKeyPresent = false,
                requestRouteOnNoApiKeyPresent = false,
                requestRouteOnFlowApiKeyPresent = false,
                requestRouteOnFallbackApiKeyPresent = false,
                routeResolutionErrors = Array.Empty<object>(),
                exception_type = ex.GetType().FullName,
                exception_message = ex.Message,
                inner_exception_type = ex.InnerException?.GetType().FullName,
                inner_exception_message = ex.InnerException?.Message
            });
        }
    }

    private object BuildInteractionPreview(InteractionRegistrationRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        return new
        {
            request.InteractionId,
            request.FlowKey,
            request.Status,
            request.CustomerId,
            request.CdPessoa,
            request.CustomerName,
            request.CustomerDocument,
            request.CustomerPhone,
            request.PhoneKey,
            request.RecipientE164,
            request.N8nWebhookUrl,
            request.RouteOnYes,
            request.RouteOnNo,
            request.RouteOnFlow,
            request.RouteOnFallback,
            request.Channel,
            request.InteractionType,
            request.ExpectedResponseMode,
            completion_intent = request.BusinessContext?.CompletionIntent,
            request.ExpiresAtUtc,
            preview_generated_at_utc = now
        };
    }

    private bool IsInteractionRegisterDryRunRequest()
    {
        if (Request.Query.TryGetValue("diag", out var diagQuery)
            && IsTruthyFlag(diagQuery.FirstOrDefault()))
        {
            return true;
        }

        if (Request.Query.TryGetValue("dry_run", out var dryRunQuery)
            && IsTruthyFlag(dryRunQuery.FirstOrDefault()))
        {
            return true;
        }

        if (Request.Headers.TryGetValue("X-Interaction-Register-Dry-Run", out var dryRunHeader)
            && IsTruthyFlag(dryRunHeader.FirstOrDefault()))
        {
            return true;
        }

        return false;
    }

    private static bool IsTruthyFlag(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var normalized = raw.Trim();
        return normalized.Equals("1", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("true", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("yes", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("y", StringComparison.OrdinalIgnoreCase);
    }

    private void AttachInteractionRegisterDebugHeaders()
    {
        Response.Headers["X-TraceId"] = HttpContext.TraceIdentifier;
        Response.Headers["X-Interaction-Register-Version"] = InteractionRegisterVersion;
        var stage = HttpContext.Items.TryGetValue("interactions.register.stage", out var stageObj)
            ? stageObj?.ToString()
            : null;
        if (!string.IsNullOrWhiteSpace(stage))
        {
            Response.Headers["X-Interaction-Register-Stage"] = stage;
        }
    }

    private static string BuildInteractionRegistrationSnapshot(InteractionRegistrationRequest request)
    {
        var snapshot = new
        {
            request.InteractionId,
            request.FlowKey,
            request.CustomerId,
            request.CdPessoa,
            request.CustomerName,
            request.CustomerDocument,
            request.CustomerPhone,
            request.PhoneKey,
            request.RecipientE164,
            request.DestinationPhoneNumberId,
            request.DestinationDisplayPhone,
            request.ChannelInstanceKey,
            request.PreferredOutboundPhoneNumberId,
            request.PreferredOutboundDisplayPhone,
            request.PreferredOutboundUnit,
            request.CurrentConversationPhoneNumberId,
            request.CurrentConversationDisplayPhone,
            request.FlowName,
            request.WorkflowName,
            request.WebhookName,
            request.MessageType,
            request.MessageName,
            request.TemplateName,
            request.TemplateLanguage,
            request.WhatsappNodeName,
            request.OutboundMessageId,
            request.ButtonPayload,
            request.N8nWebhookUrl,
            request.RouteOnYes,
            request.RouteOnNo,
            request.RouteOnFlow,
            request.RouteOnFallback,
            request.Channel,
            request.InteractionType,
            request.ExpectedResponseMode,
            request.Status,
            request.CreatedAt,
            request.UpdatedAt,
            request.ExpiresAtUtc,
            completion_intent = request.BusinessContext?.CompletionIntent,
            business_context = request.BusinessContext is null
                ? null
                : new
                {
                    request.BusinessContext.Source,
                    request.BusinessContext.CompletionIntent,
                    request.BusinessContext.InitialChargeTitleIds,
                    request.BusinessContext.InitialChargeTitleNames,
                    additional_property_keys = request.BusinessContext.AdditionalProperties?.Keys.ToArray()
                }
        };

        return JsonSerializer.Serialize(snapshot);
    }

    private void NormalizeAndValidateCompletionIntent(InteractionRegistrationRequest request)
    {
        var rawIntent = request.BusinessContext?.CompletionIntent;
        if (string.IsNullOrWhiteSpace(rawIntent))
        {
            return;
        }

        var normalizedIntent = NormalizeCompletionIntent(rawIntent);
        request.BusinessContext!.CompletionIntent = normalizedIntent;

        if (AllowedCompletionIntents.Contains(normalizedIntent))
        {
            return;
        }

        ModelState.AddModelError(
            "BusinessContext.CompletionIntent",
            $"CompletionIntent invÃ¡lido. Valores permitidos: {string.Join(", ", AllowedCompletionIntents)}.");
    }

    private static string NormalizeCompletionIntent(string rawIntent)
        => rawIntent.Trim()
            .ToUpperInvariant()
            .Replace('-', '_')
            .Replace(' ', '_');

    private bool TryParseInteractionRegistrationRequest(
        JsonElement requestBody,
        out InteractionRegistrationRequest request,
        out string? errorCode,
        out string? errorMessage,
        out object? details)
    {
        request = new InteractionRegistrationRequest();
        errorCode = null;
        errorMessage = null;
        details = null;

        try
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
            };

            var parsed = JsonSerializer.Deserialize<InteractionRegistrationRequest>(requestBody.GetRawText(), options);
            if (parsed is null)
            {
                errorCode = "invalid_request_payload";
                errorMessage = "Body da requisiÃ§Ã£o nÃ£o pode ser convertido para o contrato de interaÃ§Ã£o.";
                details = new Dictionary<string, string[]>
                {
                    ["body"] = ["Envie um objeto JSON vÃ¡lido conforme o contrato documentado."]
                };
                return false;
            }

            request = parsed;
            ApplyRegistrationAliases(requestBody, request);
            return true;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "Falha ao desserializar payload de registro de interaÃ§Ã£o. Path: {Path}. TraceId: {TraceId}",
                ex.Path,
                HttpContext.TraceIdentifier);
            _logger.LogWarning(
                "interactions.register.binding_failed TraceId={TraceId} Path={Path} ExceptionType={ExceptionType} InnerExceptionType={InnerExceptionType} InnerMessage={InnerMessage}",
                HttpContext.TraceIdentifier,
                ex.Path,
                ex.GetType().FullName,
                ex.InnerException?.GetType().FullName,
                ex.InnerException?.Message);

            errorCode = "invalid_request_payload";
            errorMessage = "Payload incompatÃ­vel com o contrato de registro de interaÃ§Ã£o.";
            details = new Dictionary<string, string[]>
            {
                ["json"] =
                [
                    string.IsNullOrWhiteSpace(ex.Path)
                        ? "Estrutura JSON invÃ¡lida ou tipo incompatÃ­vel."
                        : $"Campo invÃ¡lido em '{ex.Path}'."
                ]
            };
            return false;
        }
    }

    private static void ApplyRegistrationAliases(JsonElement root, InteractionRegistrationRequest request)
    {
        var interactionId = FirstNonEmpty(request.InteractionId,
            GetStringByAliases(root, "interaction_id", "interactionId", "InteractionId"));
        if (interactionId is not null)
        {
            request.InteractionId = interactionId;
        }

        var flowKey = FirstNonEmpty(request.FlowKey,
            GetStringByAliases(root, "flow_key", "flowKey", "FlowKey"));
        if (flowKey is not null)
        {
            request.FlowKey = flowKey;
        }
        request.N8nWebhookUrl = FirstNonEmpty(request.N8nWebhookUrl,
            GetStringByAliases(root, "n8n_webhook_url", "n8nWebhookUrl", "N8nWebhookUrl"));
        request.CustomerName = FirstNonEmpty(request.CustomerName,
            GetStringByAliases(root, "customer_name", "customerName", "CustomerName"));
        request.CustomerDocument = FirstNonEmpty(request.CustomerDocument,
            GetStringByAliases(root, "customer_document", "customerDocument", "CustomerDocument"));
        request.CustomerPhone = FirstNonEmpty(request.CustomerPhone,
            GetStringByAliases(root, "customer_phone", "customerPhone", "CustomerPhone"));
        request.PhoneKey = FirstNonEmpty(request.PhoneKey,
            GetStringByAliases(root, "phone_key", "phoneKey", "PhoneKey"));
        request.RecipientE164 = FirstNonEmpty(request.RecipientE164,
            GetStringByAliases(root, "recipient_e164", "recipientE164", "RecipientE164"));
        request.FlowName = FirstNonEmpty(request.FlowName,
            GetStringByAliases(root, "flow_name", "flowName", "FlowName"));
        request.WorkflowName = FirstNonEmpty(request.WorkflowName,
            GetStringByAliases(root, "workflow_name", "workflowName", "WorkflowName"));
        request.WebhookName = FirstNonEmpty(request.WebhookName,
            GetStringByAliases(root, "webhook_name", "webhookName", "WebhookName"));
        request.MessageType = FirstNonEmpty(request.MessageType,
            GetStringByAliases(root, "message_type", "messageType", "MessageType"));
        request.MessageName = FirstNonEmpty(request.MessageName,
            GetStringByAliases(root, "message_name", "messageName", "MessageName"));
        request.TemplateName = FirstNonEmpty(request.TemplateName,
            GetStringByAliases(root, "template_name", "templateName", "TemplateName"));
        request.TemplateLanguage = FirstNonEmpty(request.TemplateLanguage,
            GetStringByAliases(root, "template_language", "templateLanguage", "TemplateLanguage"));
        request.WhatsappNodeName = FirstNonEmpty(request.WhatsappNodeName,
            GetStringByAliases(root, "whatsapp_node_name", "whatsappNodeName", "WhatsappNodeName"));
        request.RouteOnYes = FirstNonEmpty(request.RouteOnYes,
            GetStringByAliases(root, "route_on_yes", "routeOnYes", "RouteOnYes"));
        request.RouteOnNo = FirstNonEmpty(request.RouteOnNo,
            GetStringByAliases(root, "route_on_no", "routeOnNo", "RouteOnNo"));
        request.RouteOnFlow = FirstNonEmpty(request.RouteOnFlow,
            GetStringByAliases(root, "route_on_flow", "routeOnFlow", "RouteOnFlow"));
        request.RouteOnFallback = FirstNonEmpty(request.RouteOnFallback,
            GetStringByAliases(root, "route_on_fallback", "routeOnFallback", "RouteOnFallback"));
        request.RouteOnYesApiKey = FirstNonEmpty(request.RouteOnYesApiKey,
            GetStringByAliases(root, "route_on_yes_api_key", "routeOnYesApiKey", "RouteOnYesApiKey"));
        request.RouteOnNoApiKey = FirstNonEmpty(request.RouteOnNoApiKey,
            GetStringByAliases(root, "route_on_no_api_key", "routeOnNoApiKey", "RouteOnNoApiKey"));
        request.RouteOnFlowApiKey = FirstNonEmpty(request.RouteOnFlowApiKey,
            GetStringByAliases(root, "route_on_flow_api_key", "routeOnFlowApiKey", "RouteOnFlowApiKey"));
        request.RouteOnFallbackApiKey = FirstNonEmpty(request.RouteOnFallbackApiKey,
            GetStringByAliases(root, "route_on_fallback_api_key", "routeOnFallbackApiKey", "RouteOnFallbackApiKey"));
        request.Channel = FirstNonEmpty(request.Channel,
            GetStringByAliases(root, "channel", "Channel"));
        request.InteractionType = FirstNonEmpty(request.InteractionType,
            GetStringByAliases(root, "interaction_type", "interactionType", "InteractionType"));
        request.ExpectedResponseMode = FirstNonEmpty(request.ExpectedResponseMode,
            GetStringByAliases(root, "expected_response_mode", "expectedResponseMode", "ExpectedResponseMode"));
        request.DestinationPhoneNumberId = FirstNonEmpty(request.DestinationPhoneNumberId,
            GetStringByAliases(root, "destination_phone_number_id", "destinationPhoneNumberId", "DestinationPhoneNumberId"));
        request.DestinationDisplayPhone = FirstNonEmpty(request.DestinationDisplayPhone,
            GetStringByAliases(root, "destination_display_phone", "destinationDisplayPhone", "DestinationDisplayPhone"));
        request.ChannelInstanceKey = FirstNonEmpty(request.ChannelInstanceKey,
            GetStringByAliases(root, "channel_instance_key", "channelInstanceKey", "ChannelInstanceKey"));
        request.PreferredOutboundPhoneNumberId = FirstNonEmpty(request.PreferredOutboundPhoneNumberId,
            GetStringByAliases(root, "preferred_outbound_phone_number_id", "preferredOutboundPhoneNumberId", "PreferredOutboundPhoneNumberId"));
        request.PreferredOutboundDisplayPhone = FirstNonEmpty(request.PreferredOutboundDisplayPhone,
            GetStringByAliases(root, "preferred_outbound_display_phone", "preferredOutboundDisplayPhone", "PreferredOutboundDisplayPhone"));
        request.PreferredOutboundUnit = FirstNonEmpty(request.PreferredOutboundUnit,
            GetStringByAliases(root, "preferred_outbound_unit", "preferredOutboundUnit", "PreferredOutboundUnit"));
        request.CurrentConversationPhoneNumberId = FirstNonEmpty(request.CurrentConversationPhoneNumberId,
            GetStringByAliases(root, "current_conversation_phone_number_id", "currentConversationPhoneNumberId", "CurrentConversationPhoneNumberId"));
        request.CurrentConversationDisplayPhone = FirstNonEmpty(request.CurrentConversationDisplayPhone,
            GetStringByAliases(root, "current_conversation_display_phone", "currentConversationDisplayPhone", "CurrentConversationDisplayPhone"));
        request.Status = FirstNonEmpty(request.Status,
            GetStringByAliases(root, "status", "Status"));
        request.CustomerId ??= GetIntByAliases(root, "customer_id", "customerId", "CustomerId");
        request.CdPessoa ??= GetIntByAliases(root, "cd_pessoa", "cdPessoa", "CdPessoa");
        request.CustomerId ??= request.CdPessoa;
        request.ExpiresAtUtc ??= GetDateTimeOffsetByAliases(root, "expires_at_utc", "expiresAtUtc", "ExpiresAtUtc", "expires_at", "expiresAt", "ExpiresAt");
        var businessContextAliases = new[] { "business_context", "businessContext", "BusinessContext" };
        var businessSource = GetNestedStringByParentAliases(
            root,
            businessContextAliases,
            "source",
            "Source");
        var initialChargeTitleIds = GetNestedStringListByParentAliases(
            root,
            businessContextAliases,
            "initial_charge_title_ids",
            "initialChargeTitleIds",
            "InitialChargeTitleIds");
        var initialChargeTitleNames = GetNestedStringListByParentAliases(
            root,
            businessContextAliases,
            "initial_charge_title_names",
            "initialChargeTitleNames",
            "InitialChargeTitleNames");

        var completionIntent = GetNestedStringByParentAliases(
            root,
            businessContextAliases,
            "completion_intent",
            "completionIntent",
            "CompletionIntent");

        var businessAdditionalProperties = GetNestedObjectByParentAliases(
            root,
            businessContextAliases,
            "additional_properties",
            "additionalProperties",
            "AdditionalProperties");
        var persistedContextFromRoot = GetObjectByAliases(
            root,
            "contexto_persistido",
            "contextoPersistido",
            "persisted_context",
            "persistedContext");

        if (!string.IsNullOrWhiteSpace(businessSource)
            || !string.IsNullOrWhiteSpace(completionIntent)
            || (initialChargeTitleIds?.Count > 0)
            || (initialChargeTitleNames?.Count > 0)
            || businessAdditionalProperties is not null
            || persistedContextFromRoot is not null)
        {
            request.BusinessContext ??= new InteractionRegistrationBusinessContextRequest();
        }

        if (!string.IsNullOrWhiteSpace(businessSource))
        {
            request.BusinessContext!.Source = FirstNonEmpty(request.BusinessContext.Source, businessSource);
        }

        if (!string.IsNullOrWhiteSpace(completionIntent))
        {
            request.BusinessContext!.CompletionIntent = FirstNonEmpty(request.BusinessContext.CompletionIntent, completionIntent);
              }

        if (initialChargeTitleIds?.Count > 0 && (request.BusinessContext?.InitialChargeTitleIds is null || request.BusinessContext.InitialChargeTitleIds.Count == 0))
        {
            request.BusinessContext!.InitialChargeTitleIds = initialChargeTitleIds;
        }

        if (initialChargeTitleNames?.Count > 0 && (request.BusinessContext?.InitialChargeTitleNames is null || request.BusinessContext.InitialChargeTitleNames.Count == 0))
        {
            request.BusinessContext!.InitialChargeTitleNames = initialChargeTitleNames;
        }

        if (request.BusinessContext is not null)
        {
            request.BusinessContext.AdditionalProperties ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            MergeIntoAdditionalProperties(request.BusinessContext.AdditionalProperties, businessAdditionalProperties);
            MergeIntoAdditionalProperties(request.BusinessContext.AdditionalProperties, persistedContextFromRoot);
            request.BusinessContext.NormalizeAdditionalProperties();
        }
    }


    private static string? TryResolveStateCode(InteractionRegistrationRequest request)
    {
        if (request.BusinessContext?.AdditionalProperties is null)
        {
            return null;
        }

        foreach (var key in new[] { "uf", "state_code", "estado", "customer_state", "customer_uf" })
        {
            if (!request.BusinessContext.AdditionalProperties.TryGetValue(key, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                return value.ToString();
            }
        }

        return null;
    }

    private static string? FirstNonEmpty(string? preferred, string? candidate)
        => string.IsNullOrWhiteSpace(preferred) ? candidate : preferred;

    private static string? GetStringByAliases(JsonElement root, params string[] aliases)
    {
        foreach (var alias in aliases)
        {
            if (!TryGetPropertyCaseInsensitive(root, alias, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                var value = property.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }

                continue;
            }

            if (property.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                return property.GetRawText();
            }
        }

        return null;
    }

    private static int? GetIntByAliases(JsonElement root, params string[] aliases)
    {
        foreach (var alias in aliases)
        {
            if (!TryGetPropertyCaseInsensitive(root, alias, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var numericValue))
            {
                return numericValue;
            }

            if (property.ValueKind == JsonValueKind.String
                && int.TryParse(property.GetString()?.Trim(), out var parsedValue))
            {
                return parsedValue;
            }
        }

        return null;
    }

    private static JsonElement? GetObjectByAliases(JsonElement root, params string[] aliases)
    {
        foreach (var alias in aliases)
        {
            if (!TryGetPropertyCaseInsensitive(root, alias, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Object)
            {
                return property.Clone();
            }
        }

        return null;
    }

    private static JsonElement? GetNestedObjectByParentAliases(JsonElement root, string[] parentAliases, params string[] childAliases)
    {
        foreach (var parentAlias in parentAliases)
        {
            if (!TryGetPropertyCaseInsensitive(root, parentAlias, out var parentElement)
                || parentElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var childAlias in childAliases)
            {
                if (!TryGetPropertyCaseInsensitive(parentElement, childAlias, out var childElement))
                {
                    continue;
                }

                if (childElement.ValueKind == JsonValueKind.Object)
                {
                    return childElement.Clone();
                }
            }
        }

        return null;
    }

    private static void MergeIntoAdditionalProperties(
        IDictionary<string, JsonElement> target,
        JsonElement? sourceObject)
    {
        if (sourceObject is null || sourceObject.Value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in sourceObject.Value.EnumerateObject())
        {
            target[property.Name] = property.Value.Clone();
        }
    }

    private static DateTimeOffset? GetDateTimeOffsetByAliases(JsonElement root, params string[] aliases)
    {
        foreach (var alias in aliases)
        {
            if (!TryGetPropertyCaseInsensitive(root, alias, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(property.GetString()?.Trim(), out var stringValue))
            {
                return stringValue;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var unixSeconds))
            {
                try
                {
                    return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement root, string name, out JsonElement value)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    [HttpPost("/webhooks/meta/whatsapp/interactions/execution-context")]
    [HttpPost("/api/meta/whatsapp/interactions/execution-context/register")]
    public IActionResult RegisterExecutionContext([FromBody] ExecutionContextRegistrationRequest request)
    {
        if (!ModelState.IsValid)
        {
            return StructuredValidationProblem();
        }

        var now = DateTimeOffset.UtcNow;
        if (!TryResolveDependency<MetaWhatsAppInteractionRouter>(nameof(MetaWhatsAppInteractionRouter), out var router, out var dependencyError))
        {
            return dependencyError!;
        }
        var interactionRouter = router!;

        var execution = interactionRouter.RegisterExecutionContext(request, now);

        _ = WriteInteractionLogAsync(
            execution.PhoneKey ?? execution.RecipientPhoneE164,
            "EXECUTION_CONTEXT_REGISTRADO",
            now,
            new Dictionary<string, string?>
            {
                ["execution_id"] = execution.ExecutionId,
                ["interaction_id"] = execution.InteractionId,
                ["telefone"] = execution.PhoneKey ?? execution.RecipientPhoneE164,
                ["fluxo"] = execution.FlowKey,
                ["cliente"] = execution.CustomerName,
                ["flow_name"] = execution.FlowName,
                ["workflow_name"] = execution.WorkflowName,
                ["webhook_name"] = execution.WebhookName,
                ["message_type"] = execution.MessageType,
                ["message_name"] = execution.MessageName,
                ["template_name"] = execution.TemplateName,
                ["template_language"] = execution.TemplateLanguage,
                ["whatsapp_node_name"] = execution.WhatsappNodeName,
                ["status"] = "REGISTERED",
                ["detalhes"] = "Contexto de execuÃ§Ã£o registrado para consulta por execution_id."
            });

        return Ok(new
        {
            ok = true,
            status = "registered",
            execution_context = execution
        });
    }

    [HttpGet("/webhooks/meta/whatsapp/interactions/execution-context/{executionId}")]
    [HttpGet("/api/meta/whatsapp/interactions/execution-context/{executionId}")]
    public IActionResult GetExecutionContextByExecutionId([FromRoute(Name = "executionId")] string executionId)
    {
        if (!TryResolveDependency<MetaWhatsAppInteractionRouter>(nameof(MetaWhatsAppInteractionRouter), out var router, out var dependencyError))
        {
            return dependencyError!;
        }
        var interactionRouter = router!;

        if (!interactionRouter.TryGetExecutionContext(executionId, out var execution))
        {
            return BuildError(
                StatusCodes.Status404NotFound,
                "not_found",
                "execution_context_not_found",
                "Contexto de execuÃ§Ã£o nÃ£o encontrado.",
                new { execution_id = executionId });
        }

        return Ok(new
        {
            ok = true,
            execution_id = execution!.ExecutionId,
            interaction_id = execution.InteractionId,
            flow_key = execution.FlowKey,
            customer_id = execution.CustomerId,
            customer_name = execution.CustomerName,
            recipient_phone_e164 = execution.RecipientPhoneE164,
            phone_key = execution.PhoneKey,
            flow_name = execution.FlowName,
            workflow_name = execution.WorkflowName,
            webhook_name = execution.WebhookName,
            message_type = execution.MessageType,
            message_name = execution.MessageName,
            template_name = execution.TemplateName,
            template_language = execution.TemplateLanguage,
            whatsapp_node_name = execution.WhatsappNodeName,
            created_at = execution.CreatedAt
        });
    }

    [HttpPost("/webhooks/meta/whatsapp/interactions/execution-context/query")]
    [HttpPost("/api/meta/whatsapp/interactions/execution-context/query")]
    public IActionResult QueryExecutionContextByExecutionId([FromBody] ExecutionContextLookupRequest request)
    {
        if (!ModelState.IsValid)
        {
            return StructuredValidationProblem();
        }

        return GetExecutionContextByExecutionId(request.ExecutionId);
    }

    [HttpGet("/webhooks/meta/whatsapp/interactions/{interactionId}/outbound/last")]
    [HttpGet("/api/meta/whatsapp/interactions/{interactionId}/outbound/last")]
    public IActionResult GetLastOutboundMessage([FromRoute(Name = "interactionId")] string interactionId)
    {
        if (string.IsNullOrWhiteSpace(interactionId))
        {
            return BuildError(StatusCodes.Status400BadRequest, "validation_error", "interaction_id_required", "O interaction_id Ã© obrigatÃ³rio.");
        }

        if (!TryResolveDependency<MetaWhatsAppInteractionRouter>(nameof(MetaWhatsAppInteractionRouter), out var router, out var dependencyError))
        {
            return dependencyError!;
        }

        if (!router!.TryGetLastOutboundMessage(interactionId, out var lastOutbound) || lastOutbound is null)
        {
            return BuildError(StatusCodes.Status404NotFound, "not_found", "last_outbound_not_found", "Ãšltima mensagem outbound nÃ£o encontrada para a interaÃ§Ã£o.", new { interaction_id = interactionId });
        }

        return Ok(new
        {
            ok = true,
            interaction_id = interactionId,
            last_outbound = lastOutbound
        });
    }

    [HttpPatch("/webhooks/meta/whatsapp/interactions/{interactionId}/sent")]
    [HttpPatch("/webhooks/meta/whatsapp/interactions/{interactionId}/documents-sent")]
    public IActionResult MarkInteractionAsSent(
        [FromRoute(Name = "interactionId")] string interactionId,
        [FromBody] InteractionSentUpdateRequest request)
    {
        LogWhatsAppEndpointDebug("interactions.sent", request);

        if (!ModelState.IsValid)
        {
            return StructuredValidationProblem();
        }

        if (string.IsNullOrWhiteSpace(interactionId))
        {
            return BuildError(StatusCodes.Status400BadRequest, "validation_error", "interaction_id_required", "O interaction_id Ã© obrigatÃ³rio.");
        }

        var now = DateTimeOffset.UtcNow;
        if (!TryResolveDependency<MetaWhatsAppInteractionRouter>(nameof(MetaWhatsAppInteractionRouter), out var router, out var dependencyError))
        {
            return dependencyError!;
        }
        var interactionRouter = router!;

        if (!interactionRouter.TryMarkAsSent(interactionId, request, now, out var updated, out var errorCode))
        {
            if (ShouldReturnRedundantTerminalNoop(errorCode, updated?.Status, request.Status ?? "ENVIADO"))
            {
                _ = WriteInteractionLogAsync(
                    updated?.PhoneKey ?? updated?.CustomerPhone ?? updated?.RecipientE164,
                    "INTERACTION_STATUS_PATCH_NOOP",
                    now,
                    new Dictionary<string, string?>
                    {
                        ["interaction_id"] = interactionId,
                        ["current_status"] = updated?.Status,
                        ["requested_status"] = request.Status ?? "ENVIADO",
                        ["endpoint"] = "interactions.sent",
                        ["detalhes"] = "PATCH redundante ignorado para interaÃ§Ã£o jÃ¡ recusada."
                    });

                return Ok(new
                {
                    ok = true,
                    interaction_id = interactionId,
                    status = updated?.Status ?? "RECUSADO",
                    noop = true
                });
            }

            return errorCode switch
            {
                "not_found" => BuildError(StatusCodes.Status404NotFound, "not_found", "interaction_not_found", "InteraÃ§Ã£o nÃ£o encontrada.", new { interaction_id = interactionId }),
                "incompatible_status" => BuildError(StatusCodes.Status409Conflict, "conflict", "incompatible_status", "O status atual da interaÃ§Ã£o nÃ£o permite esta atualizaÃ§Ã£o.", new { interaction_id = interactionId, current_status = updated?.Status }),
                _ => BuildError(StatusCodes.Status500InternalServerError, "operational_error", "unknown_error", "Erro inesperado ao atualizar interaÃ§Ã£o.")
            };
        }

        var sentEvent = string.Equals(updated!.Status, "DOCUMENTOS_ENVIADOS", StringComparison.OrdinalIgnoreCase)
            ? "DOCUMENTOS_ENVIADOS"
            : "ENVIO_ACEITO_META";

        _ = WriteInteractionLogAsync(
            updated.PhoneKey ?? updated.CustomerPhone ?? updated.RecipientE164,
            sentEvent,
            now,
            new Dictionary<string, string?>
            {
                ["interaction_id"] = updated.InteractionId,
                ["customer_id"] = updated.CustomerId?.ToString(),
                ["cd_pessoa"] = updated.CustomerId?.ToString(),
                ["telefone"] = updated.PhoneKey ?? updated.CustomerPhone ?? updated.RecipientE164,
                ["template"] = updated.TemplateName,
                ["flow_name"] = updated.FlowName,
                ["workflow_name"] = updated.WorkflowName,
                ["webhook_name"] = updated.WebhookName,
                ["message_type"] = updated.MessageType,
                ["message_name"] = updated.MessageName,
                ["template_language"] = updated.TemplateLanguage,
                ["whatsapp_node_name"] = updated.WhatsappNodeName,
                ["meta_message_id"] = updated.MetaMessageId,
                ["arquivo"] = updated.SentFileName,
                ["status"] = updated.Status,
                ["preferred_outbound_phone_number_id"] = updated.PreferredOutboundPhoneNumberId,
                ["preferred_outbound_display_phone"] = updated.PreferredOutboundDisplayPhone,
                ["preferred_outbound_unit"] = updated.PreferredOutboundUnit,
                ["sender_phone_number_id"] = updated.PhoneNumberId,
                ["sender_display_phone"] = updated.CurrentConversationDisplayPhone,
                ["sender_unit_code"] = updated.PreferredOutboundUnit,
                ["sender_unit_label"] = ResolveSenderUnitLabel(updated.PreferredOutboundUnit),
                ["resolution_reason"] = "sent_patch_callback",
                ["fallback_applied"] = "false",
                ["destination_phone_number_id"] = updated.DestinationPhoneNumberId,
                ["destination_display_phone"] = updated.DestinationDisplayPhone,
                ["channel_instance_key"] = updated.ChannelInstanceKey,
                ["detalhes"] = "AtualizaÃ§Ã£o de envio recebida no endpoint de status"
            }
            );

        return Ok(new
        {
            ok = true,
            interaction_id = updated!.InteractionId,
            status = updated.Status,
            phone_number_id = updated.PhoneNumberId
        });
    }

    [HttpPatch("/webhooks/meta/whatsapp/interactions/{interactionId}/failed")]
    public IActionResult MarkInteractionAsFailed(
        [FromRoute(Name = "interactionId")] string interactionId,
        [FromBody] InteractionFailedUpdateRequest request)
    {
        LogWhatsAppEndpointDebug("interactions.failed", request);

        if (!ModelState.IsValid)
        {
            return StructuredValidationProblem();
        }

        if (string.IsNullOrWhiteSpace(interactionId))
        {
            return BuildError(StatusCodes.Status400BadRequest, "validation_error", "interaction_id_required", "O interaction_id Ã© obrigatÃ³rio.");
        }

        var now = DateTimeOffset.UtcNow;
        if (!TryResolveDependency<MetaWhatsAppInteractionRouter>(nameof(MetaWhatsAppInteractionRouter), out var router, out var dependencyError))
        {
            return dependencyError!;
        }
        var interactionRouter = router!;

        if (!interactionRouter.TryMarkAsFailed(interactionId, request, now, out var updated, out var errorCode))
        {
            if (ShouldReturnRedundantTerminalNoop(errorCode, updated?.Status, request.Status ?? "ERRO_ENVIO"))
            {
                _ = WriteInteractionLogAsync(
                    updated?.PhoneKey ?? updated?.CustomerPhone ?? updated?.RecipientE164,
                    "INTERACTION_STATUS_PATCH_NOOP",
                    now,
                    new Dictionary<string, string?>
                    {
                        ["interaction_id"] = interactionId,
                        ["current_status"] = updated?.Status,
                        ["requested_status"] = request.Status ?? "ERRO_ENVIO",
                        ["endpoint"] = "interactions.failed",
                        ["detalhes"] = "PATCH redundante ignorado para interaÃ§Ã£o jÃ¡ recusada."
                    });

                return Ok(new
                {
                    ok = true,
                    interaction_id = interactionId,
                    status = updated?.Status ?? "RECUSADO",
                    noop = true
                });
            }

            return errorCode switch
            {
                "not_found" => BuildError(StatusCodes.Status404NotFound, "not_found", "interaction_not_found", "InteraÃ§Ã£o nÃ£o encontrada.", new { interaction_id = interactionId }),
                "incompatible_status" => BuildError(StatusCodes.Status409Conflict, "conflict", "incompatible_status", "O status atual da interaÃ§Ã£o nÃ£o permite esta atualizaÃ§Ã£o.", new { interaction_id = interactionId, current_status = updated?.Status }),
                _ => BuildError(StatusCodes.Status500InternalServerError, "operational_error", "unknown_error", "Erro inesperado ao atualizar interaÃ§Ã£o.")
            };
        }

        _ = WriteInteractionLogAsync(
            updated!.PhoneKey ?? updated.CustomerPhone ?? updated.RecipientE164,
            "ENVIO_FALHOU",
            now,
            new Dictionary<string, string?>
            {
                ["interaction_id"] = updated.InteractionId,
                ["customer_id"] = updated.CustomerId?.ToString(),
                ["cd_pessoa"] = updated.CustomerId?.ToString(),
                ["telefone"] = updated.PhoneKey ?? updated.CustomerPhone ?? updated.RecipientE164,
                ["status"] = updated.Status,
                ["flow_name"] = updated.FlowName,
                ["workflow_name"] = updated.WorkflowName,
                ["webhook_name"] = updated.WebhookName,
                ["message_type"] = updated.MessageType,
                ["message_name"] = updated.MessageName,
                ["template_name"] = updated.TemplateName,
                ["template_language"] = updated.TemplateLanguage,
                ["whatsapp_node_name"] = updated.WhatsappNodeName,
                ["meta_message_id"] = updated.MetaMessageId,
                ["arquivo"] = updated.SentFileName,
                ["erro"] = updated.ErrorMessage,
                ["detalhes"] = updated.ErrorDetails,
                ["preferred_outbound_phone_number_id"] = updated.PreferredOutboundPhoneNumberId,
                ["preferred_outbound_display_phone"] = updated.PreferredOutboundDisplayPhone,
                ["preferred_outbound_unit"] = updated.PreferredOutboundUnit,
                ["sender_phone_number_id"] = updated.PhoneNumberId,
                ["sender_display_phone"] = updated.CurrentConversationDisplayPhone,
                ["sender_unit_code"] = updated.PreferredOutboundUnit,
                ["sender_unit_label"] = ResolveSenderUnitLabel(updated.PreferredOutboundUnit),
                ["resolution_reason"] = "failed_patch_callback",
                ["fallback_applied"] = "false",
                ["destination_phone_number_id"] = updated.DestinationPhoneNumberId,
                ["destination_display_phone"] = updated.DestinationDisplayPhone,
                ["channel_instance_key"] = updated.ChannelInstanceKey,
                ["payload_resumido"] = MetaWhatsAppPersistentLogService.BuildCompactPayload(request)
            }
            );

        return Ok(new
        {
            ok = true,
            interaction_id = updated!.InteractionId,
            status = updated.Status,
            phone_number_id = updated.PhoneNumberId
        });
    }

    [HttpPatch("/webhooks/meta/whatsapp/interactions/{interactionId}/refused")]
    public IActionResult MarkInteractionAsRefused(
        [FromRoute(Name = "interactionId")] string interactionId,
        [FromBody] InteractionRefusedUpdateRequest request)
    {
        if (!ModelState.IsValid)
        {
            return StructuredValidationProblem();
        }

        if (string.IsNullOrWhiteSpace(interactionId))
        {
            return BuildError(StatusCodes.Status400BadRequest, "validation_error", "interaction_id_required", "O interaction_id Ã© obrigatÃ³rio.");
        }

        var now = DateTimeOffset.UtcNow;
        if (!TryResolveDependency<MetaWhatsAppInteractionRouter>(nameof(MetaWhatsAppInteractionRouter), out var router, out var dependencyError))
        {
            return dependencyError!;
        }
        var interactionRouter = router!;

        if (!interactionRouter.TryMarkAsRefused(interactionId, request, now, out var updated, out var errorCode))
        {
            if (ShouldReturnRedundantTerminalNoop(errorCode, updated?.Status, request.Status ?? "RECUSADO"))
            {
                _ = WriteInteractionLogAsync(
                    updated?.PhoneKey ?? updated?.CustomerPhone ?? updated?.RecipientE164,
                    "INTERACTION_STATUS_PATCH_NOOP",
                    now,
                    new Dictionary<string, string?>
                    {
                        ["interaction_id"] = interactionId,
                        ["current_status"] = updated?.Status,
                        ["requested_status"] = request.Status ?? "RECUSADO",
                        ["endpoint"] = "interactions.refused",
                        ["detalhes"] = "PATCH redundante ignorado para interaÃ§Ã£o jÃ¡ recusada."
                    });

                return Ok(new
                {
                    ok = true,
                    interaction_id = interactionId,
                    status = updated?.Status ?? "RECUSADO",
                    noop = true
                });
            }

            return errorCode switch
            {
                "not_found" => BuildError(StatusCodes.Status404NotFound, "not_found", "interaction_not_found", "InteraÃ§Ã£o nÃ£o encontrada.", new { interaction_id = interactionId }),
                "incompatible_status" => BuildError(StatusCodes.Status409Conflict, "conflict", "incompatible_status", "O status atual da interaÃ§Ã£o nÃ£o permite esta atualizaÃ§Ã£o.", new { interaction_id = interactionId, current_status = updated?.Status }),
                _ => BuildError(StatusCodes.Status500InternalServerError, "operational_error", "unknown_error", "Erro inesperado ao atualizar interaÃ§Ã£o.")
            };
        }

        _ = WriteInteractionLogAsync(
            updated!.PhoneKey ?? updated.CustomerPhone ?? updated.RecipientE164,
            "RECUSA",
            now,
            new Dictionary<string, string?>
            {
                ["interaction_id"] = updated.InteractionId,
                ["customer_id"] = updated.CustomerId?.ToString(),
                ["cd_pessoa"] = updated.CustomerId?.ToString(),
                ["telefone"] = updated.PhoneKey ?? updated.CustomerPhone ?? updated.RecipientE164,
                ["resposta_raw"] = request.Response,
                ["classificacao"] = updated.RefusedResponse,
                ["status"] = updated.Status,
                ["flow_name"] = updated.FlowName,
                ["workflow_name"] = updated.WorkflowName,
                ["webhook_name"] = updated.WebhookName,
                ["message_type"] = updated.MessageType,
                ["message_name"] = updated.MessageName,
                ["template_name"] = updated.TemplateName,
                ["template_language"] = updated.TemplateLanguage,
                ["whatsapp_node_name"] = updated.WhatsappNodeName,
                ["meta_message_id"] = updated.MetaMessageId,
                ["arquivo"] = updated.SentFileName,
                ["detalhes"] = "Cliente recusou o fluxo"
            }
            );

        return Ok(new
        {
            ok = true,
            interaction_id = updated!.InteractionId,
            status = updated.Status
        });
    }

    [HttpPatch("/webhooks/meta/whatsapp/interactions/{interactionId}/invalid-response-sent")]
    public IActionResult MarkInvalidResponseAsSent(
        [FromRoute(Name = "interactionId")] string interactionId,
        [FromBody] InteractionInvalidResponseSentUpdateRequest request)
    {
        if (!ModelState.IsValid)
        {
            return StructuredValidationProblem();
        }

        if (string.IsNullOrWhiteSpace(interactionId))
        {
            return BuildError(StatusCodes.Status400BadRequest, "validation_error", "interaction_id_required", "O interaction_id Ã© obrigatÃ³rio.");
        }

        var now = DateTimeOffset.UtcNow;
        if (!TryResolveDependency<MetaWhatsAppInteractionRouter>(nameof(MetaWhatsAppInteractionRouter), out var router, out var dependencyError))
        {
            return dependencyError!;
        }
        var interactionRouter = router!;

        if (!interactionRouter.TryMarkInvalidResponseAsSent(interactionId, request, now, out var updated, out var errorCode))
        {
            if (ShouldReturnRedundantTerminalNoop(errorCode, updated?.Status, request.Status))
            {
                _ = WriteInteractionLogAsync(
                    updated?.PhoneKey ?? updated?.CustomerPhone ?? updated?.RecipientE164,
                    "INTERACTION_STATUS_PATCH_NOOP",
                    now,
                    new Dictionary<string, string?>
                    {
                        ["interaction_id"] = interactionId,
                        ["current_status"] = updated?.Status,
                        ["requested_status"] = request.Status,
                        ["endpoint"] = "interactions.invalid_response_sent",
                        ["detalhes"] = "PATCH redundante ignorado para interaÃ§Ã£o jÃ¡ recusada."
                    });

                return Ok(new
                {
                    ok = true,
                    interaction_id = interactionId,
                    status = updated?.Status ?? "RECUSADO",
                    noop = true
                });
            }

            return errorCode switch
            {
                "not_found" => BuildError(StatusCodes.Status404NotFound, "not_found", "interaction_not_found", "InteraÃ§Ã£o nÃ£o encontrada.", new { interaction_id = interactionId }),
                "incompatible_status" => BuildError(StatusCodes.Status409Conflict, "conflict", "incompatible_status", "O status atual da interaÃ§Ã£o nÃ£o permite esta atualizaÃ§Ã£o.", new { interaction_id = interactionId, current_status = updated?.Status }),
                _ => BuildError(StatusCodes.Status500InternalServerError, "operational_error", "unknown_error", "Erro inesperado ao atualizar interaÃ§Ã£o.")
            };
        }

        _ = WriteInteractionLogAsync(
            updated!.PhoneKey ?? updated.CustomerPhone ?? updated.RecipientE164,
            "RESPOSTA_FORA_PADRAO_ENVIADA",
            now,
            new Dictionary<string, string?>
            {
                ["interaction_id"] = updated.InteractionId,
                ["customer_id"] = updated.CustomerId?.ToString(),
                ["cd_pessoa"] = updated.CustomerId?.ToString(),
                ["telefone"] = updated.PhoneKey ?? updated.CustomerPhone ?? updated.RecipientE164,
                ["resposta_raw"] = request.ResponseRaw,
                ["classificacao"] = "FORA_DO_PADRAO",
                ["status"] = updated.Status,
                ["flow_name"] = updated.FlowName,
                ["workflow_name"] = updated.WorkflowName,
                ["webhook_name"] = updated.WebhookName,
                ["message_type"] = updated.MessageType,
                ["message_name"] = updated.MessageName,
                ["template_name"] = updated.TemplateName,
                ["template_language"] = updated.TemplateLanguage,
                ["whatsapp_node_name"] = updated.WhatsappNodeName,
                ["meta_message_id"] = updated.MetaMessageId,
                ["arquivo"] = updated.SentFileName,
                ["detalhes"] = "Resposta fora do padrÃ£o tratada e retorno enviado"
            }
            );

        return Ok(new
        {
            ok = true,
            interaction_id = updated!.InteractionId,
            status = updated.Status
        });
    }

    [HttpPost("/webhooks/meta/whatsapp/interactions/force-close-by-phone")]
    [HttpPost("/api/meta/whatsapp/interactions/force-close-by-phone")]
    public IActionResult ForceCloseInteractionByPhone([FromBody] InteractionForceCloseByPhoneRequest request)
    {
        if (!ModelState.IsValid)
        {
            return StructuredValidationProblem();
        }

        if (string.IsNullOrWhiteSpace(request.Phone))
        {
            return BuildError(StatusCodes.Status400BadRequest, "validation_error", "phone_required", "O telefone Ã© obrigatÃ³rio.");
        }

        var now = DateTimeOffset.UtcNow;
        if (!TryResolveDependency<MetaWhatsAppInteractionRouter>(nameof(MetaWhatsAppInteractionRouter), out var router, out var dependencyError))
        {
            return dependencyError!;
        }

        var interactionRouter = router!;
        if (!interactionRouter.TryForceCloseActiveByPhone(request.Phone, now, out var closedInteractions))
        {
            return Ok(new
            {
                ok = true,
                phone = request.Phone,
                closed = 0,
                interaction_ids = Array.Empty<string>(),
                status = "SEM_INTERACAO_ATIVA"
            });
        }

        var reason = string.IsNullOrWhiteSpace(request.ReasonCode)
            ? "Encerramento manual por telefone"
            : request.ReasonCode.Trim();

        foreach (var interaction in closedInteractions)
        {
            _ = WriteInteractionLogAsync(
                interaction.PhoneKey ?? interaction.CustomerPhone ?? interaction.RecipientE164 ?? request.Phone,
                "INTERACAO_ENCERRADA_MANUALMENTE",
                now,
                new Dictionary<string, string?>
                {
                    ["interaction_id"] = interaction.InteractionId,
                    ["telefone"] = interaction.PhoneKey ?? interaction.CustomerPhone ?? interaction.RecipientE164 ?? request.Phone,
                    ["status"] = interaction.Status,
                    ["flow_name"] = interaction.FlowName,
                    ["workflow_name"] = interaction.WorkflowName,
                    ["webhook_name"] = interaction.WebhookName,
                    ["detalhes"] = reason
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

    private async Task<string> ReadRawBodyAsync()
    {
        if (Request.ContentLength is null or 0)
        {
            return string.Empty;
        }

        if (!Request.Body.CanRead)
        {
            return string.Empty;
        }

        Request.EnableBuffering();
        Request.Body.Position = 0;
        using var reader = new StreamReader(Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var rawBody = await reader.ReadToEndAsync();
        Request.Body.Position = 0;
        return rawBody;
    }

    private bool TryResolveDependency<TDependency>(string dependencyName, out TDependency? dependency, out IActionResult? errorResult)
        where TDependency : class
    {
        try
        {
            dependency = HttpContext.RequestServices.GetRequiredService<TDependency>();
            errorResult = null;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Falha ao resolver dependÃªncia {DependencyName}. TraceId: {TraceId}",
                dependencyName,
                HttpContext.TraceIdentifier);
            dependency = null;
            errorResult = BuildError(
                StatusCodes.Status500InternalServerError,
                "operational_error",
                "dependency_resolution_failed",
                "Falha ao resolver dependÃªncia operacional.",
                new
                {
                    dependency_name = dependencyName,
                    exception_type = ex.GetType().FullName,
                    inner_exception_type = ex.InnerException?.GetType().FullName,
                    message = ex.Message,
                    trace_id = HttpContext.TraceIdentifier
                });
            return false;
        }
    }

    private (string VerifyToken, string Source) ResolveVerifyTokenConfiguration()
    {
        var configuredVerifyToken = _options.Value.VerifyToken?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredVerifyToken))
        {
            return (configuredVerifyToken, "MetaWhatsAppWebhook:VerifyToken (IOptions)");
        }

        _logger.LogWarning(
            "VerifyToken da Meta nÃ£o configurado em MetaWhatsAppWebhook:VerifyToken.");

        return (string.Empty, "not_configured");
    }

    private async Task WriteInteractionLogAsync(string? phone, string eventName, DateTimeOffset timestamp, IReadOnlyDictionary<string, string?> fields, string? deduplicationKey = null)
    {
        try
        {
            var enrichedFields = new Dictionary<string, string?>(fields, StringComparer.OrdinalIgnoreCase);
            var canonicalPhoneForLog = FirstNonEmpty(
                NormalizeE164(TryGetFieldValue(enrichedFields, "canonical_phone")),
                NormalizeE164(TryGetFieldValue(enrichedFields, "recipient_e164")),
                NormalizeE164(TryGetFieldValue(enrichedFields, "recipient_phone_e164")),
                NormalizeE164(TryGetFieldValue(enrichedFields, "telefone")),
                NormalizeE164(TryGetFieldValue(enrichedFields, "customer_phone")),
                NormalizeE164(TryGetFieldValue(enrichedFields, "source_phone")),
                NormalizeE164(TryGetFieldValue(enrichedFields, "source_phone_raw")),
                NormalizeE164(TryGetFieldValue(enrichedFields, "contact_wa_id")),
                NormalizeE164(TryGetFieldValue(enrichedFields, "contact_wa_id_raw")),
                NormalizeE164(phone));

            if (!string.IsNullOrWhiteSpace(canonicalPhoneForLog))
            {
                enrichedFields["canonical_phone"] = canonicalPhoneForLog;
            }

            if (string.IsNullOrWhiteSpace(TryGetFieldValue(enrichedFields, "canonical_correlation_key")))
            {
                var fallbackCorrelationKey = FirstNonEmpty(
                    TryGetFieldValue(enrichedFields, "interaction_id"),
                    TryGetFieldValue(enrichedFields, "meta_message_id"),
                    TryGetFieldValue(enrichedFields, "customer_parent_user_id"),
                    TryGetFieldValue(enrichedFields, "customer_user_id"));

                if (!string.IsNullOrWhiteSpace(fallbackCorrelationKey))
                {
                    enrichedFields["canonical_correlation_key"] = fallbackCorrelationKey;
                }
            }

            var metaMessageId = TryGetFieldValue(enrichedFields, "meta_message_id");
            var interactionId = TryGetFieldValue(enrichedFields, "interaction_id");
            var status = TryGetFieldValue(enrichedFields, "status");
            var resolvedDeduplicationKey = string.IsNullOrWhiteSpace(deduplicationKey)
                ? MetaWhatsAppPersistentLogService.BuildDeduplicationKey(eventName, interactionId, metaMessageId, timestamp, status)
                : deduplicationKey;
            if (!TryResolveDependency<IMetaWhatsAppPersistentLogService>(nameof(IMetaWhatsAppPersistentLogService), out var persistentLogService, out _))
            {
                return;
            }
            var logService = persistentLogService!;

            var inserted = await logService.AppendEventAsync(canonicalPhoneForLog ?? phone, eventName, timestamp, enrichedFields, resolvedDeduplicationKey);
            if (!inserted)
            {
                _logger.LogWarning(
                    "Log persistente WhatsApp nÃ£o inserido (deduplicado ou fallback). Evento: {EventName}. Telefone: {Phone}. InteractionId: {InteractionId}",
                    eventName,
                    canonicalPhoneForLog ?? phone,
                    interactionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao escrever log persistente do WhatsApp. Evento: {EventName}", eventName);
        }
    }

    private static string? TryGetFieldValue(IReadOnlyDictionary<string, string?> fields, string key)
        => fields.TryGetValue(key, out var value) ? value : null;

    private void LogWhatsAppEndpointDebug(string endpoint, object? payload)
    {
        string payloadJson;
        try
        {
            payloadJson = payload is null ? "<null>" : JsonSerializer.Serialize(payload);
        }
        catch (Exception ex)
        {
            payloadJson = $"<serialization_failed:{ex.GetType().Name}:{ex.Message}>";
        }

        var formFilesCount = Request.HasFormContentType ? Request.Form.Files.Count : 0;
        _logger.LogInformation(
            "whatsapp.endpoint.debug Endpoint={Endpoint} TraceId={TraceId} Method={Method} Path={Path} ContentType={ContentType} ReqBody={ReqBody} ReqFile={ReqFile} ReqFiles={ReqFiles} HasFormContentType={HasFormContentType} PersistentLogBasePath={PersistentLogBasePath} TempPath={TempPath}",
            endpoint,
            HttpContext.TraceIdentifier,
            Request.Method,
            Request.Path,
            Request.ContentType,
            payloadJson,
            "null",
            formFilesCount,
            Request.HasFormContentType,
            ResolvePersistentLogDirectoryForDebug(),
            Path.GetTempPath());
    }

    private string ResolvePersistentLogDirectoryForDebug()
    {
        var configuredDirectory = _options.Value.PersistentLogDirectory;
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            configuredDirectory = configuredDirectory.Trim().Trim('"');
            return Path.IsPathRooted(configuredDirectory)
                ? Path.GetFullPath(configuredDirectory)
                : Path.GetFullPath(Path.Combine(_environment.ContentRootPath, configuredDirectory));
        }

        return Path.GetFullPath(Path.Combine(_environment.ContentRootPath, "..", "Arquivos e Documentos/WhatsappLogMensagens"));
    }

    [HttpPost("/webhooks/meta/whatsapp/logs/probe")]
    [HttpPost("/api/meta/whatsapp/logs/probe")]
    public async Task<IActionResult> ProbePersistentLog([FromQuery(Name = "reason")] string? reason)
    {
        if (!TryResolveDependency<IMetaWhatsAppPersistentLogService>(nameof(IMetaWhatsAppPersistentLogService), out var persistentLogService, out var persistentLogDependencyError))
        {
            return persistentLogDependencyError!;
        }
        var logService = persistentLogService!;

        var probe = await logService.WriteProbeAsync(reason);
        if (!probe.Success)
        {
            return BuildError(
                StatusCodes.Status500InternalServerError,
                "operational_error",
                "persistent_log_probe_failed",
                "Falha ao gravar arquivo de teste no diretÃ³rio de log persistente.",
                new
                {
                    configured_directory = probe.ConfiguredDirectory,
                    resolved_directory = probe.ResolvedDirectory,
                    probe_file_path = probe.ProbeFilePath,
                    error = probe.ErrorMessage
                });
        }

        return Ok(new
        {
            ok = true,
            configured_directory = probe.ConfiguredDirectory,
            resolved_directory = probe.ResolvedDirectory,
            probe_file_path = probe.ProbeFilePath
        });
    }

    [HttpPost("/webhooks/meta/whatsapp/events")]
    [HttpPost("/api/meta/whatsapp/events")]
    public async Task<IActionResult> ReceiveGenericEvent([FromBody] GenericWhatsAppEventRequest request)
    {
        if (!ModelState.IsValid)
        {
            return StructuredValidationProblem();
        }

        return await ProcessGenericEventAsync(request);
    }

    [HttpPost("/webhooks/meta/whatsapp/events/invalid-payload")]
    public async Task<IActionResult> ReceiveInvalidPayloadEvent([FromBody] GenericWhatsAppEventRequest request)
    {
        request.EventType = "PAYLOAD_INVALIDO";
        return await ProcessGenericEventAsync(request);
    }

    private async Task<IActionResult> ProcessGenericEventAsync(GenericWhatsAppEventRequest request)
    {

        if (!request.IsAllowedEvent())
        {
            return BuildError(StatusCodes.Status400BadRequest, "validation_error", "event_type_not_allowed", "Tipo de evento nÃ£o permitido.");
        }

        var timestamp = request.Timestamp ?? DateTimeOffset.UtcNow;
        var metaMessageId = string.IsNullOrWhiteSpace(request.MetaMessageId) ? request.Wamid : request.MetaMessageId;
        var fields = new Dictionary<string, string?>
        {
            ["interaction_id"] = request.InteractionId,
            ["telefone"] = request.Phone,
            ["cliente"] = request.Customer,
            ["fluxo"] = request.Flow,
            ["template"] = request.Template,
            ["status"] = request.Status,
            ["erro"] = request.Error,
            ["meta_message_id"] = metaMessageId,
            ["wamid"] = request.Wamid,
            ["resposta_raw"] = request.RawResponse,
            ["classificacao"] = request.Classification,
            ["detalhes"] = request.Details,
            ["payload_resumido"] = MetaWhatsAppPersistentLogService.BuildCompactPayload(new
            {
                request.EventType,
                request.Flow,
                request.Template,
                request.Phone,
                request.Customer,
                request.Status,
                request.Error,
                request.Wamid,
                request.RawResponse,
                request.Classification,
                request.Payload
            })
        };

        var deduplicationKey = ResolveEventDeduplicationKey(
            request.EventType,
            request.InteractionId,
            metaMessageId,
            timestamp,
            request.Status,
            request.DeduplicationKey);

        if (!TryResolveDependency<IMetaWhatsAppPersistentLogService>(nameof(IMetaWhatsAppPersistentLogService), out var persistentLogService, out var persistentLogDependencyError))
        {
            return persistentLogDependencyError!;
        }
        var logService = persistentLogService!;

        var inserted = await logService.AppendEventAsync(request.Phone, request.EventType, timestamp, fields, deduplicationKey);
        return Ok(new
        {
            ok = true,
            event_type = request.EventType,
            inserted,
            deduplication_key = deduplicationKey
        });
    }


    [HttpPost("/webhooks/meta/whatsapp/interactions/events")]
    [HttpPost("/api/meta/whatsapp/interactions/events")]
    public async Task<IActionResult> ReceiveInteractionEventTrail([FromBody] InteractionEventTrailRequest request)
    {
        LogWhatsAppEndpointDebug("interactions.events", request);

        HydrateInteractionEventTrailFromRawBody(request);

        if (!ModelState.IsValid)
        {
            return StructuredValidationProblem();
        }

        if (!request.IsAllowedEvent())
        {
            return BuildError(StatusCodes.Status400BadRequest, "validation_error", "event_type_not_allowed", "Tipo de evento nÃ£o permitido.");
        }

        var timestamp = request.Timestamp ?? DateTimeOffset.UtcNow;
        if (!TryResolveDependency<MetaWhatsAppInteractionRouter>(nameof(MetaWhatsAppInteractionRouter), out var router, out var dependencyError))
        {
            return dependencyError!;
        }

        var interactionRouter = router!;

        var correlatedInteraction = interactionRouter.ResolveForAuditEvent(
            request.InteractionId,
            request.RecipientPhoneE164,
            request.CustomerId,
            request.FlowKey,
            timestamp);

        var resolvedInteractionId = request.InteractionId ?? correlatedInteraction?.InteractionId;
        var resolvedPhone = request.RecipientPhoneE164
            ?? correlatedInteraction?.PhoneKey
            ?? correlatedInteraction?.CustomerPhone
            ?? correlatedInteraction?.RecipientE164;
        var resolvedCustomerName = request.CustomerName ?? correlatedInteraction?.CustomerName;
        var resolvedFlowKey = request.FlowKey ?? correlatedInteraction?.FlowKey;
        var rawBodyCanonicalPhone = request.RawBody.HasValue
            ? GetNestedStringByAliases(request.RawBody.Value, "identifiers", "canonical_phone", "canonicalPhone", "CanonicalPhone")
            : null;
        var rawBodyPhoneAliases = request.RawBody.HasValue
            ? GetNestedArrayRawTextByAliases(request.RawBody.Value, "identifiers", "phone_aliases", "phoneAliases", "PhoneAliases")
            : null;
        var resolvedCanonicalPhone = FirstNonEmpty(
            correlatedInteraction?.CanonicalPhone,
            rawBodyCanonicalPhone,
            resolvedPhone);
        var resolvedPhoneAliases = correlatedInteraction?.PhoneAliases is { Count: > 0 }
            ? JsonSerializer.Serialize(correlatedInteraction.PhoneAliases)
            : rawBodyPhoneAliases;

        var isCustomerFlowOperationalErrorEvent =
            string.Equals(request.EventType, "AVISO_ERRO_OPERACIONAL_ENVIADO", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.EventType, "ERRO_AO_ENVIAR_AVISO_OPERACIONAL", StringComparison.OrdinalIgnoreCase);

        if (isCustomerFlowOperationalErrorEvent && !string.IsNullOrWhiteSpace(resolvedInteractionId))
        {
            interactionRouter.TryMarkAsFailed(
                resolvedInteractionId,
                new InteractionFailedUpdateRequest
                {
                    Status = MetaWhatsAppInteractionRouter.StatusErroOperacional,
                    FailedAt = timestamp,
                    ErrorMessage = request.Message ?? "Falha operacional sinalizada por workflow do cliente.",
                    ErrorDetails = request.Details?.GetRawText() ?? request.RawBody?.GetRawText(),
                    FlowName = request.FlowName,
                    WebhookName = request.WebhookName
                },
                timestamp,
                out _,
                out _);
        }

        var fields = new Dictionary<string, string?>
        {
            ["interaction_id"] = resolvedInteractionId,
            ["telefone"] = resolvedPhone,
            ["cliente"] = resolvedCustomerName,
            ["fluxo"] = resolvedFlowKey,
            ["status"] = request.EventType,
            ["meta_message_id"] = request.MetaMessageId,
            ["detalhes"] = request.Message,
            ["severity"] = request.Severity,
            ["flow_name"] = request.FlowName,
            ["webhook_name"] = request.WebhookName,
            ["canonical_phone"] = resolvedCanonicalPhone,
            ["phone_aliases"] = resolvedPhoneAliases,
            ["payload_resumido"] = MetaWhatsAppPersistentLogService.BuildCompactPayload(new
            {
                request.EventType,
                request.Severity,
                request.FlowKey,
                request.FlowName,
                request.WebhookName,
                request.CustomerId,
                request.CustomerName,
                request.RecipientPhoneE164,
                request.MetaMessageId,
                request.Message,
                request.Details,
                request.RawBody
            })
        };

        if (request.Details.HasValue)
        {
            fields["details_json"] = request.Details.Value.GetRawText();
        }

        if (request.RawBody.HasValue)
        {
            fields["raw_body_json"] = request.RawBody.Value.GetRawText();
        }

        var deduplicationKey = ResolveEventDeduplicationKey(
            request.EventType,
            resolvedInteractionId,
            request.MetaMessageId,
            timestamp,
            request.Severity,
            request.DeduplicationKey);

        _logger.LogInformation(
            "Trilha de evento WhatsApp registrada. EventType: {EventType}. InteractionIdInformado: {ProvidedInteractionId}. InteractionIdCorrelacionado: {CorrelatedInteractionId}. Telefone: {Phone}.",
            request.EventType,
            request.InteractionId,
            correlatedInteraction?.InteractionId,
            resolvedPhone);

        if (!TryResolveDependency<IMetaWhatsAppPersistentLogService>(nameof(IMetaWhatsAppPersistentLogService), out var persistentLogService, out var persistentLogDependencyError))
        {
            return persistentLogDependencyError!;
        }
        var logService = persistentLogService!;

        var inserted = await logService.AppendEventAsync(resolvedPhone, request.EventType, timestamp, fields, deduplicationKey);

        return Ok(new
        {
            ok = true,
            event_type = request.EventType,
            inserted,
            deduplication_key = deduplicationKey,
            interaction_id = resolvedInteractionId,
            correlation = new
            {
                matched = correlatedInteraction is not null,
                strategy = request.InteractionId is not null
                    ? "interaction_id"
                    : correlatedInteraction is not null
                        ? "phone_customer_flow"
                        : "none"
            }
        });
    }

    private static void HydrateInteractionEventTrailFromRawBody(InteractionEventTrailRequest request)
    {
        if (!request.RawBody.HasValue || request.RawBody.Value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var rawBody = request.RawBody.Value;

        request.InteractionId = FirstNonEmpty(request.InteractionId, GetStringByAliases(rawBody, "interaction_id", "interactionId", "InteractionId"));
        request.FlowKey = FirstNonEmpty(request.FlowKey, GetStringByAliases(rawBody, "flow_key", "flowKey", "FlowKey"));
        request.CustomerName = FirstNonEmpty(request.CustomerName, GetStringByAliases(rawBody, "customer_name", "customerName", "CustomerName"));
        request.RecipientPhoneE164 = FirstNonEmpty(request.RecipientPhoneE164, GetStringByAliases(rawBody, "recipient_phone_e164", "recipientPhoneE164", "RecipientPhoneE164"));
        request.RecipientPhoneE164 = FirstNonEmpty(request.RecipientPhoneE164, GetNestedStringByAliases(rawBody, "phone", "recipient_e164", "recipientE164", "RecipientE164"));
        request.CustomerId ??= GetIntByAliases(rawBody, "customer_id", "customerId", "CustomerId");
        request.CustomerId ??= GetNestedIntByAliases(rawBody, "customer", "id", "Id");
    }

    private static string? GetNestedStringByAliases(JsonElement root, string parent, params string[] aliases)
    {
        if (!TryGetPropertyCaseInsensitive(root, parent, out var parentProperty) || parentProperty.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return GetStringByAliases(parentProperty, aliases);
    }

    private static string? GetNestedStringByParentAliases(JsonElement root, string[] parentAliases, params string[] aliases)
    {
        foreach (var parentAlias in parentAliases)
        {
            var nested = GetNestedStringByAliases(root, parentAlias, aliases);
            if (!string.IsNullOrWhiteSpace(nested))
            {
                return nested;
            }
        }

        return null;
    }

    private static List<string>? GetNestedStringListByParentAliases(JsonElement root, string[] parentAliases, params string[] aliases)
    {
        foreach (var parentAlias in parentAliases)
        {
            if (!TryGetPropertyCaseInsensitive(root, parentAlias, out var parentProperty)
                || parentProperty.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var alias in aliases)
            {
                if (!TryGetPropertyCaseInsensitive(parentProperty, alias, out var aliasProperty))
                {
                    continue;
                }

                var parsedValues = ParseFlexibleStringList(aliasProperty);
                if (parsedValues is not null)
                {
                    return parsedValues;
                }
            }
        }

        return null;
    }

    private static List<string>? ParseFlexibleStringList(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            var singleValue = ParseSingleFlexibleString(value);
            return singleValue is null ? null : new List<string> { singleValue };
        }

        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            var parsedValue = ParseSingleFlexibleString(item);
            if (!string.IsNullOrWhiteSpace(parsedValue))
            {
                result.Add(parsedValue);
            }
        }

        return result;
    }

    private static string? ParseSingleFlexibleString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null
        };
    }

    private static int? GetNestedIntByAliases(JsonElement root, string parent, params string[] aliases)
    {
        if (!TryGetPropertyCaseInsensitive(root, parent, out var parentProperty) || parentProperty.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return GetIntByAliases(parentProperty, aliases);
    }

    private static string? GetNestedArrayRawTextByAliases(JsonElement root, string parent, params string[] aliases)
    {
        if (!TryGetPropertyCaseInsensitive(root, parent, out var parentProperty) || parentProperty.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var alias in aliases)
        {
            if (!TryGetPropertyCaseInsensitive(parentProperty, alias, out var aliasProperty))
            {
                continue;
            }

            if (aliasProperty.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            return aliasProperty.GetRawText();
        }

        return null;
    }

    [HttpPost("/webhooks/meta/whatsapp/invalid-payloads")]
    public Task<IActionResult> ReceiveInvalidPayloadEventTrail([FromBody] InteractionEventTrailRequest request)
    {
        request.EventType = "PAYLOAD_INVALIDO";
        return ReceiveInteractionEventTrail(request);
    }

    private async Task RegisterMetaStatusEventsAsync(JsonElement payload, DateTimeOffset fallbackNowUtc, RoutingDecision routing)
    {
        var statuses = ExtractMetaStatuses(payload);
        foreach (var status in statuses)
        {
            var eventType = status.Status switch
            {
                "sent" => "META_STATUS_SENT",
                "delivered" => "META_STATUS_DELIVERED",
                "read" => "META_STATUS_READ",
                "failed" => "META_STATUS_FAILED",
                _ => null
            };

            if (eventType is null)
            {
                continue;
            }

            var timestamp = status.Timestamp ?? fallbackNowUtc;
            await WriteInteractionLogAsync(
                routing.Interaction?.PhoneKey ?? routing.Interaction?.CustomerPhone ?? status.RecipientId,
                eventType,
                timestamp,
                new Dictionary<string, string?>
                {
                    ["interaction_id"] = routing.Interaction?.InteractionId ?? routing.EventInfo.InteractionId,
                    ["telefone"] = routing.Interaction?.PhoneKey ?? routing.Interaction?.CustomerPhone ?? status.RecipientId,
                    ["meta_message_id"] = status.MetaMessageId,
                    ["wamid"] = status.MetaMessageId,
                    ["status"] = status.Status?.ToUpperInvariant(),
                    ["preferred_outbound_phone_number_id"] = routing.Interaction?.PreferredOutboundPhoneNumberId,
                    ["preferred_outbound_display_phone"] = routing.Interaction?.PreferredOutboundDisplayPhone,
                    ["preferred_outbound_unit"] = routing.Interaction?.PreferredOutboundUnit,
                    ["sender_phone_number_id"] = routing.Interaction?.PhoneNumberId,
                    ["sender_display_phone"] = routing.Interaction?.CurrentConversationDisplayPhone,
                    ["sender_unit_code"] = routing.Interaction?.PreferredOutboundUnit,
                    ["sender_unit_label"] = ResolveSenderUnitLabel(routing.Interaction?.PreferredOutboundUnit),
                    ["resolution_reason"] = "meta_status_webhook",
                    ["fallback_applied"] = "false",
                    ["destination_phone_number_id"] = routing.EventInfo.DestinationPhoneNumberId,
                    ["destination_display_phone"] = routing.EventInfo.DestinationDisplayPhone,
                    ["channel_instance_key"] = routing.Interaction?.ChannelInstanceKey ?? MetaWhatsAppInteractionRouter.BuildChannelInstanceKey(routing.EventInfo.DestinationPhoneNumberId),
                    ["erro"] = status.ErrorMessage,
                    ["detalhes"] = "Status posterior recebido da Meta",
                    ["payload_resumido"] = MetaWhatsAppPersistentLogService.BuildCompactPayload(status.RawStatus)
                }
            );
        }
    }

    private static string ResolveEventDeduplicationKey(
        string eventType,
        string? interactionId,
        string? metaMessageId,
        DateTimeOffset timestamp,
        string? status,
        string? explicitDeduplicationKey)
    {
        if (!string.IsNullOrWhiteSpace(explicitDeduplicationKey))
        {
            return explicitDeduplicationKey;
        }

        if (string.Equals(eventType, "AVISO_ERRO_OPERACIONAL_ENVIADO", StringComparison.OrdinalIgnoreCase))
        {
            return $"AVISO_ERRO_OPERACIONAL_ENVIADO|{interactionId?.Trim() ?? "-"}";
        }

        return MetaWhatsAppPersistentLogService.BuildDeduplicationKey(eventType, interactionId, metaMessageId, timestamp, status);
    }

    private static IEnumerable<MetaStatusEvent> ExtractMetaStatuses(JsonElement payload)
    {
        var envelope = MetaWhatsAppWebhookParser.ParseEnvelope(payload);
        foreach (var statusItem in envelope.Statuses)
        {
            var status = statusItem.TryGetProperty("status", out var statusValue) ? statusValue.GetString() : null;
            var recipientId = statusItem.TryGetProperty("recipient_id", out var recipientValue) ? recipientValue.GetString() : null;
            var metaMessageId = statusItem.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
            DateTimeOffset? timestamp = null;
            if (statusItem.TryGetProperty("timestamp", out var tsValue)
                && tsValue.ValueKind == JsonValueKind.String
                && long.TryParse(tsValue.GetString(), out var unixTs))
            {
                timestamp = DateTimeOffset.FromUnixTimeSeconds(unixTs);
            }

            string? errorMessage = null;
            if (statusItem.TryGetProperty("errors", out var errorsValue)
                && errorsValue.ValueKind == JsonValueKind.Array)
            {
                errorMessage = errorsValue.EnumerateArray()
                    .Select(error =>
                    {
                        var code = error.TryGetProperty("code", out var codeValue) ? codeValue.ToString() : null;
                        var title = error.TryGetProperty("title", out var titleValue) ? titleValue.GetString() : null;
                        return $"{code}:{title}";
                    })
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            }

            yield return new MetaStatusEvent(status, recipientId, metaMessageId, timestamp, errorMessage, statusItem.Clone());
        }
    }

    private static string? ResolveFlowTipoSolicitacao(JsonElement? flowResponseJson)
    {
        if (flowResponseJson is null)
        {
            return null;
        }

        return ResolveFlowTipoSolicitacao(flowResponseJson.Value);
    }

    private static string? ResolveFlowTipoSolicitacao(JsonElement flowResponseJson)
    {
        if (flowResponseJson.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPropertyCaseInsensitive(flowResponseJson, "tipo_solicitacao", out var tipoSolicitacaoElement)
                && tipoSolicitacaoElement.ValueKind == JsonValueKind.String)
            {
                var value = tipoSolicitacaoElement.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            if (TryGetPropertyCaseInsensitive(flowResponseJson, "response_json", out var responseJsonElement))
            {
                var nestedValue = ResolveFlowTipoSolicitacao(responseJsonElement);
                if (!string.IsNullOrWhiteSpace(nestedValue))
                {
                    return nestedValue;
                }
            }

            return null;
        }

        if (flowResponseJson.ValueKind == JsonValueKind.String)
        {
            var rawText = flowResponseJson.GetString();
            if (string.IsNullOrWhiteSpace(rawText))
            {
                return null;
            }

            try
            {
                using var jsonDocument = JsonDocument.Parse(rawText);
                return ResolveFlowTipoSolicitacao(jsonDocument.RootElement);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    private static bool IsPesquisaPosVendasFlow(
        string? flowName,
        string? flowId,
        string? messageName,
        string? templateName,
        string? flowKey)
    {
        var hasFlowIdentityMatch =
            string.Equals(flowName?.Trim(), PesquisaPosVendasFlowName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(flowId?.Trim(), PesquisaPosVendasFlowId, StringComparison.OrdinalIgnoreCase);

        if (!hasFlowIdentityMatch)
        {
            return false;
        }

        return ContainsPesquisaIdentifier(messageName)
            || ContainsPesquisaIdentifier(templateName)
            || ContainsPesquisaIdentifier(flowKey)
            || string.Equals(flowName?.Trim(), PesquisaPosVendasFlowName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsPesquisaIdentifier(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains(PesquisaPosVendasFlowName, StringComparison.OrdinalIgnoreCase);

    private static string? ResolveFlowCompletionIntent(string? interactionCompletionIntent, JsonElement? flowResponseObject)
    {
        var normalizedInteractionIntent = interactionCompletionIntent?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedInteractionIntent))
        {
            return normalizedInteractionIntent;
        }

        return ResolveFlowCompletionIntentFromPayload(flowResponseObject);
    }

    private static string? ResolveFlowCompletionIntentFromPayload(JsonElement? flowResponseObject)
    {
        if (flowResponseObject is null)
        {
            return null;
        }

        return ResolveFlowCompletionIntentFromPayload(flowResponseObject.Value);
    }

    private static string? ResolveFlowCompletionIntentFromPayload(JsonElement flowResponseObject)
    {
        if (flowResponseObject.ValueKind == JsonValueKind.Object)
        {
            var completionIntentAliases = new[] { "completion_intent", "flow_completion_profile", "business_intent" };
            foreach (var alias in completionIntentAliases)
            {
                if (!TryGetPropertyCaseInsensitive(flowResponseObject, alias, out var intentElement)
                    || intentElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = intentElement.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            if (TryGetPropertyCaseInsensitive(flowResponseObject, "response_json", out var nestedResponse))
            {
                var nestedValue = ResolveFlowCompletionIntentFromPayload(nestedResponse);
                if (!string.IsNullOrWhiteSpace(nestedValue))
                {
                    return nestedValue;
                }
            }

            return null;
        }

        if (flowResponseObject.ValueKind == JsonValueKind.String)
        {
            var rawText = flowResponseObject.GetString();
            if (string.IsNullOrWhiteSpace(rawText))
            {
                return null;
            }

            try
            {
                using var jsonDocument = JsonDocument.Parse(rawText);
                return ResolveFlowCompletionIntentFromPayload(jsonDocument.RootElement);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    private static PesquisaPosVendasEvidenceDiagnostics ResolvePesquisaPosVendasEvidenceDiagnostics(JsonElement? flowResponseObject)
    {
        if (flowResponseObject is null || flowResponseObject.Value.ValueKind != JsonValueKind.Object)
        {
            return new PesquisaPosVendasEvidenceDiagnostics(false, "-", string.Join(",", PesquisaPosVendasCamposObrigatorios));
        }

        var foundFields = new List<string>();
        var missingFields = new List<string>();

        foreach (var requiredField in PesquisaPosVendasCamposObrigatorios)
        {
            if (!TryGetPropertyCaseInsensitive(flowResponseObject.Value, requiredField, out var fieldElement)
                || fieldElement.ValueKind == JsonValueKind.Null
                || (fieldElement.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(fieldElement.GetString())))
            {
                missingFields.Add(requiredField);
                continue;
            }

            foundFields.Add(requiredField);
        }

        var hasContractEvidence = missingFields.Count == 0;
        return new PesquisaPosVendasEvidenceDiagnostics(
            hasContractEvidence,
            foundFields.Count > 0 ? string.Join(",", foundFields) : "-",
            missingFields.Count > 0 ? string.Join(",", missingFields) : "-");
    }

    private static JsonElement? ResolveFlowResponseObject(JsonElement? flowResponseJson)
    {
        if (flowResponseJson is null)
        {
            return null;
        }

        return ResolveFlowResponseObject(flowResponseJson.Value);
    }

    private static JsonElement? ResolveFlowResponseObject(JsonElement flowResponseJson)
    {
        if (flowResponseJson.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPropertyCaseInsensitive(flowResponseJson, "response_json", out var responseJsonElement))
            {
                var nested = ResolveFlowResponseObject(responseJsonElement);
                if (nested is not null)
                {
                    return nested.Value;
                }
            }

            return flowResponseJson.Clone();
        }

        if (flowResponseJson.ValueKind == JsonValueKind.String)
        {
            var rawText = flowResponseJson.GetString();
            if (string.IsNullOrWhiteSpace(rawText))
            {
                return null;
            }

            try
            {
                using var jsonDocument = JsonDocument.Parse(rawText);
                return ResolveFlowResponseObject(jsonDocument.RootElement);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    private static string? ResolveFlowId(JsonElement? flowResponseJson)
    {
        if (flowResponseJson is null)
        {
            return null;
        }

        return ResolveFlowStringField(flowResponseJson.Value, "flow_id");
    }

    private static string? ResolveFlowName(JsonElement? flowResponseJson)
    {
        if (flowResponseJson is null)
        {
            return null;
        }

        return ResolveFlowStringField(flowResponseJson.Value, "flow_name");
    }

    private static string? ResolveFlowStringField(JsonElement flowResponseJson, string fieldName)
    {
        if (flowResponseJson.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPropertyCaseInsensitive(flowResponseJson, fieldName, out var fieldElement)
                && fieldElement.ValueKind == JsonValueKind.String)
            {
                var value = fieldElement.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            if (TryGetPropertyCaseInsensitive(flowResponseJson, "response_json", out var responseJsonElement))
            {
                var nestedValue = ResolveFlowStringField(responseJsonElement, fieldName);
                if (!string.IsNullOrWhiteSpace(nestedValue))
                {
                    return nestedValue;
                }
            }

            return null;
        }

        if (flowResponseJson.ValueKind == JsonValueKind.String)
        {
            var rawText = flowResponseJson.GetString();
            if (string.IsNullOrWhiteSpace(rawText))
            {
                return null;
            }

            try
            {
                using var jsonDocument = JsonDocument.Parse(rawText);
                return ResolveFlowStringField(jsonDocument.RootElement, fieldName);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    private static string ResolveFallbackInteractionId(RoutingDecision routing, DateTimeOffset receivedAt)
    {
        if (!string.IsNullOrWhiteSpace(routing.EventInfo.InteractionId))
        {
            return routing.EventInfo.InteractionId.Trim();
        }

        var fallbackPhone = FirstNonEmpty(
            routing.EventInfo.CustomerPhone,
            routing.EventInfo.CustomerWaId,
            routing.EventInfo.SourcePhoneRaw,
            "unknown");

        var eventStamp = receivedAt.ToUnixTimeMilliseconds().ToString();
        return $"fallback:{fallbackPhone}:{eventStamp}";
    }

    private sealed record MetaStatusEvent(
        string? Status,
        string? RecipientId,
        string? MetaMessageId,
        DateTimeOffset? Timestamp,
        string? ErrorMessage,
        JsonElement RawStatus);

    private sealed record MediaDownloadResult(bool Downloaded, string? StoragePath, int? HttpStatusCode, string? Error)
    {
        public static MediaDownloadResult Succeeded(string storagePath, int? httpStatusCode)
            => new(true, storagePath, httpStatusCode, null);

        public static MediaDownloadResult Failed(string error, int? httpStatusCode = null)
            => new(false, null, httpStatusCode, error);
    }

    private sealed record PesquisaPosVendasEvidenceDiagnostics(
        bool HasContractEvidence,
        string FoundFieldsJoined,
        string MissingFieldsJoined);
}
