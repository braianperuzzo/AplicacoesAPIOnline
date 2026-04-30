using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AplicacoesOnline.Models.MetaWhatsApp;
using Microsoft.Extensions.Options;

namespace AplicacoesOnline.Services.MetaWhatsApp;

public sealed class MetaWhatsAppInteractionRouter
{
    private const string BillingPendingTitleTemplate = "cobranca_financeira_titulopendente_n8n";
    private const string BillingTemplateYesRoute = "https://braianperuzzoibr.app.n8n.cloud/webhook/wa-financeiro-cobranca-vencidos-processar-sim";
    private const string BillingTemplateNoRoute = "https://braianperuzzoibr.app.n8n.cloud/webhook/wa-global-resposta-nao-padrao";
    private const string BillingTemplateFlowRoute = "https://braianperuzzoibr.app.n8n.cloud/webhook/wa-preferencias-contato-processar-flow";
    private const string BillingTemplateFallbackRoute = "https://braianperuzzoibr.app.n8n.cloud/webhook/wa-global-resposta-fora-do-padrao";
    private const string FaleConoscoTipoSolicitacao = "FALE_CONOSCO";
    private const string FaleConoscoWebhookUrl = "https://braianperuzzoibr.app.n8n.cloud/webhook/wa-global-fale-conosco";
    public const string StatusErroProcessamentoSim = "ERRO_PROCESSAMENTO_SIM";
    public const string StatusErroRespostaNao = "ERRO_RESPOSTA_NAO";
    public const string StatusErroRespostaFlow = "ERRO_RESPOSTA_FLOW";
    public const string StatusErroRespostaForaPadrao = "ERRO_RESPOSTA_FORA_PADRAO";
    public const string StatusErroOperacional = "ERRO_OPERACIONAL";

    private static readonly HashSet<string> IncompatibleTerminalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "RECUSADO",
        "CANCELADA",
        "EXPIRADA"
    };

    private static readonly HashSet<string> ConversationalActiveStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "PENDENTE_ENVIO",
        "ENVIADO",
        "AGUARDANDO_RESPOSTA"
    };
    private static readonly HashSet<string> RecoverableErrorStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        StatusErroOperacional,
        StatusErroProcessamentoSim,
        StatusErroRespostaNao,
        StatusErroRespostaFlow,
        StatusErroRespostaForaPadrao
    };

    private const string GlobalAtendimentoFlowKey = "wa_global_atendimento";
    private static readonly HashSet<string> GlobalTerminationSingleWordIntents = new(StringComparer.Ordinal)
    {
        "ENCERRAR",
        "SAIR",
        "FINALIZAR",
        "CANCELAR",
        "PARAR",
        "FECHAR"
    };
    private static readonly string[] GlobalTerminationPhraseIntents =
    {
        "NAO QUERO CONTINUAR",
        "NAO QUERO PROSSEGUIR",
        "NAO QUERO SEGUIR",
        "ENCERRAR ATENDIMENTO",
        "CANCELAR ATENDIMENTO",
        "FINALIZAR ATENDIMENTO",
        "PARAR ATENDIMENTO",
        "FECHAR ATENDIMENTO"
    };

    private readonly ConcurrentDictionary<string, InteractionContext> _interactionById = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _interactionIdByMessageId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _interactionIdByButtonPayload = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _interactionIdsByPhone = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _interactionIdsByRawWaId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _interactionIdsByRawSourcePhone = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _interactionIdsByCanonicalKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _interactionIdsByChannelInstanceKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _interactionIdsByChannelAndPhone = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _interactionIdsByChannelAndCanonicalKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ExecutionContext> _executionContextByExecutionId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _processedInboundMessageIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OutboundMessageContext> _lastOutboundByInteractionId = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, OutboundDecisionTemplateProfile> _decisionTemplates;
    private readonly ILogger<MetaWhatsAppInteractionRouter> _logger;

    public MetaWhatsAppInteractionRouter(
        ILogger<MetaWhatsAppInteractionRouter> logger,
        IOptions<MetaWhatsAppWebhookOptions> options)
    {
        _logger = logger;
        _decisionTemplates = options.Value.OutboundDecisionTemplates;
        _logger.LogInformation("MetaWhatsAppInteractionRouter construtor iniciado.");
        try
        {
            _logger.LogInformation("MetaWhatsAppInteractionRouter inicializado.");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Falha no construtor de MetaWhatsAppInteractionRouter. ExceptionType={ExceptionType}. InnerExceptionType={InnerExceptionType}. Message={Message}",
                ex.GetType().FullName,
                ex.InnerException?.GetType().FullName,
                ex.Message);
            throw;
        }
    }

    public InteractionContext Register(InteractionRegistrationRequest request, DateTimeOffset nowUtc)
    {
        var canonicalCustomerPhone = MetaWhatsAppPhoneCanonicalizer.ToCanonicalE164Br(request.CustomerPhone);
        var canonicalPhoneKey = MetaWhatsAppPhoneCanonicalizer.ToCanonicalE164Br(request.PhoneKey) ?? canonicalCustomerPhone;
        var canonicalRecipient = MetaWhatsAppPhoneCanonicalizer.ToCanonicalE164Br(request.RecipientE164);
        var canonicalWaId = MetaWhatsAppPhoneCanonicalizer.ToCanonicalE164Br(request.CustomerWaId) ?? canonicalCustomerPhone;
        var sourcePhoneRaw = MetaWhatsAppPhoneCanonicalizer.ToRawIdentity(request.CustomerPhone);
        var waIdRaw = MetaWhatsAppPhoneCanonicalizer.ToRawIdentity(request.CustomerWaId) ?? sourcePhoneRaw;
        var phoneAliases = MetaWhatsAppPhoneCanonicalizer.BuildLookupAliases(request.CustomerPhone).ToArray();
        var destinationPhoneNumberId = Sanitize(request.DestinationPhoneNumberId);
        var destinationDisplayPhone = Sanitize(request.DestinationDisplayPhone);
        var channelInstanceKey = Sanitize(request.ChannelInstanceKey)
            ?? BuildChannelInstanceKey(destinationPhoneNumberId);
        var preferredOutboundPhoneNumberId = Sanitize(request.PreferredOutboundPhoneNumberId) ?? destinationPhoneNumberId;
        var preferredOutboundDisplayPhone = Sanitize(request.PreferredOutboundDisplayPhone) ?? destinationDisplayPhone;
        var preferredOutboundUnit = Sanitize(request.PreferredOutboundUnit);
        var currentConversationPhoneNumberId = Sanitize(request.CurrentConversationPhoneNumberId) ?? destinationPhoneNumberId;
        var currentConversationDisplayPhone = Sanitize(request.CurrentConversationDisplayPhone) ?? destinationDisplayPhone;

        var interaction = new InteractionContext(
            request.InteractionId.Trim(),
            request.FlowKey.Trim(),
            Sanitize(request.Channel),
            Sanitize(request.InteractionType),
            Sanitize(request.ExpectedResponseMode),
            Sanitize(request.N8nWebhookUrl),
            Sanitize(request.RouteOnYes),
            Sanitize(request.RouteOnNo),
            Sanitize(request.RouteOnFlow),
            Sanitize(request.RouteOnFallback),
            Sanitize(request.N8nApiKey),
            Sanitize(request.RouteOnYesApiKey),
            Sanitize(request.RouteOnNoApiKey),
            Sanitize(request.RouteOnFlowApiKey),
            Sanitize(request.RouteOnFallbackApiKey),
            request.CustomerId,
            Sanitize(request.CustomerName),
            Sanitize(request.CustomerDocument),
            Sanitize(canonicalCustomerPhone),
            Sanitize(canonicalWaId),
            Sanitize(sourcePhoneRaw),
            Sanitize(waIdRaw),
            Sanitize(canonicalCustomerPhone),
            phoneAliases,
            Sanitize(request.CustomerUserId),
            Sanitize(request.CustomerParentUserId),
            Sanitize(request.CustomerUsername),
            Sanitize(canonicalPhoneKey),
            Sanitize(canonicalRecipient),
            Sanitize(request.Recipient),
            Sanitize(request.RecipientUserId),
            Sanitize(request.RecipientParentUserId),
            destinationPhoneNumberId,
            destinationDisplayPhone,
            channelInstanceKey,
            preferredOutboundPhoneNumberId,
            preferredOutboundDisplayPhone,
            preferredOutboundUnit,
            currentConversationPhoneNumberId,
            currentConversationDisplayPhone,
            Sanitize(request.FlowName),
            Sanitize(request.WorkflowName),
            Sanitize(request.WebhookName),
            SanitizeMessageType(request.MessageType),
            Sanitize(request.MessageName),
            Sanitize(request.WhatsappNodeName),
            SanitizeTemplateField(request.MessageType, request.TemplateName),
            SanitizeTemplateField(request.MessageType, request.TemplateLanguage),
            Sanitize(request.OutboundMessageId),
            Sanitize(request.ButtonPayload),
            Sanitize(request.BusinessContext?.Source),
            Sanitize(request.BusinessContext?.CompletionIntent),
            request.BusinessContext?.InitialChargeTitleIds?.Where(static item => !string.IsNullOrWhiteSpace(item)).Select(static item => item.Trim()).ToArray(),
            request.BusinessContext?.InitialChargeTitleNames?.Where(static item => !string.IsNullOrWhiteSpace(item)).Select(static item => item.Trim()).ToArray(),
            CloneAdditionalProperties(request.BusinessContext?.AdditionalProperties),
            request.ExpiresAtUtc ?? nowUtc.AddHours(24))
        {
            RegisteredAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            Status = "PENDENTE_ENVIO"
        };

        _interactionById[interaction.InteractionId] = interaction;

        if (!string.IsNullOrWhiteSpace(interaction.OutboundMessageId))
        {
            _interactionIdByMessageId[interaction.OutboundMessageId] = interaction.InteractionId;
        }

        if (!string.IsNullOrWhiteSpace(interaction.ButtonPayload))
        {
            _interactionIdByButtonPayload[interaction.ButtonPayload] = interaction.InteractionId;
        }

        foreach (var alias in interaction.PhoneAliases)
        {
            var phoneSet = _interactionIdsByPhone.GetOrAdd(alias, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
            phoneSet[interaction.InteractionId] = 1;
        }

        if (!string.IsNullOrWhiteSpace(interaction.WaIdRaw))
        {
            var waIdSet = _interactionIdsByRawWaId.GetOrAdd(interaction.WaIdRaw, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
            waIdSet[interaction.InteractionId] = 1;
        }

        if (!string.IsNullOrWhiteSpace(interaction.SourcePhoneRaw))
        {
            var sourcePhoneSet = _interactionIdsByRawSourcePhone.GetOrAdd(interaction.SourcePhoneRaw, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
            sourcePhoneSet[interaction.InteractionId] = 1;
        }

        if (!string.IsNullOrWhiteSpace(interaction.CanonicalCorrelationKey))
        {
            var canonicalSet = _interactionIdsByCanonicalKey.GetOrAdd(interaction.CanonicalCorrelationKey, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
            canonicalSet[interaction.InteractionId] = 1;
        }

        if (!string.IsNullOrWhiteSpace(interaction.ChannelInstanceKey))
        {
            AddIndex(_interactionIdsByChannelInstanceKey, interaction.ChannelInstanceKey, interaction.InteractionId);

            foreach (var alias in interaction.PhoneAliases)
            {
                AddIndex(_interactionIdsByChannelAndPhone, BuildChannelScopedKey(interaction.ChannelInstanceKey, alias), interaction.InteractionId);
            }

            if (!string.IsNullOrWhiteSpace(interaction.CanonicalCorrelationKey))
            {
                AddIndex(_interactionIdsByChannelAndCanonicalKey, BuildChannelScopedKey(interaction.ChannelInstanceKey, interaction.CanonicalCorrelationKey), interaction.InteractionId);
            }
        }

        RegisterOrUpdateOutboundMessage(interaction, nowUtc, new OutboundDecisionProfile
        {
            IsDecisionAnchor = request.IsDecisionAnchor ?? false,
            AcceptsYes = request.AcceptsYes ?? false,
            AcceptsNo = request.AcceptsNo ?? false,
            AcceptsFlow = request.AcceptsFlow ?? false,
            YesRoute = request.YesRoute,
            NoRoute = request.NoRoute,
            FlowRoute = request.FlowRoute,
            FallbackRoute = request.FallbackRoute,
            YesRouteApiKey = request.YesRouteApiKey,
            NoRouteApiKey = request.NoRouteApiKey,
            FlowRouteApiKey = request.FlowRouteApiKey,
            FallbackRouteApiKey = request.FallbackRouteApiKey
        });

        return interaction;
    }

    public ExecutionContext RegisterExecutionContext(ExecutionContextRegistrationRequest request, DateTimeOffset nowUtc)
    {
        var execution = new ExecutionContext(
            request.ExecutionId.Trim(),
            request.InteractionId.Trim(),
            Sanitize(request.FlowKey),
            request.CustomerId,
            Sanitize(request.CustomerName),
            Sanitize(request.RecipientPhoneE164),
            Sanitize(request.RecipientUserId),
            Sanitize(request.RecipientParentUserId),
            Sanitize(request.PhoneKey),
            Sanitize(request.FlowName),
            Sanitize(request.WorkflowName),
            Sanitize(request.WebhookName),
            SanitizeMessageType(request.MessageType),
            Sanitize(request.MessageName),
            SanitizeTemplateField(request.MessageType, request.TemplateName),
            SanitizeTemplateField(request.MessageType, request.TemplateLanguage),
            Sanitize(request.WhatsappNodeName),
            Sanitize(request.DestinationPhoneNumberId),
            Sanitize(request.DestinationDisplayPhone),
            Sanitize(request.ChannelInstanceKey),
            Sanitize(request.PreferredOutboundPhoneNumberId),
            Sanitize(request.PreferredOutboundDisplayPhone),
            Sanitize(request.PreferredOutboundUnit),
            request.CreatedAt ?? nowUtc);

        _executionContextByExecutionId[execution.ExecutionId] = execution;
        return execution;
    }

    public bool TryGetExecutionContext(string executionId, out ExecutionContext? executionContext)
    {
        if (string.IsNullOrWhiteSpace(executionId))
        {
            executionContext = null;
            return false;
        }

        return _executionContextByExecutionId.TryGetValue(executionId.Trim(), out executionContext);
    }

    public bool TryGetInteractionById(string interactionId, DateTimeOffset nowUtc, out InteractionContext? interaction)
    {
        CleanupExpired(nowUtc);

        if (string.IsNullOrWhiteSpace(interactionId))
        {
            interaction = null;
            return false;
        }

        return TryGetValidInteraction(interactionId.Trim(), nowUtc, out interaction);
    }

    public IReadOnlyList<InteractionContext> GetActiveInteractionsByPhone(string phone, DateTimeOffset nowUtc)
    {
        CleanupExpired(nowUtc);

        var phoneAliases = MetaWhatsAppPhoneCanonicalizer.BuildLookupAliases(phone)
            .Where(static alias => !string.IsNullOrWhiteSpace(alias))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (phoneAliases.Length == 0)
        {
            return Array.Empty<InteractionContext>();
        }

        var candidates = phoneAliases
            .Select(alias => _interactionIdsByPhone.TryGetValue(alias, out var set) ? set : null)
            .Where(set => set is not null)
            .SelectMany(set => set!.Keys)
            .Distinct(StringComparer.Ordinal)
            .Select(id => TryGetValidInteraction(id, nowUtc, out var interaction) ? interaction : null)
            .Where(interaction => interaction is not null)
            .OrderByDescending(interaction => interaction!.UpdatedAtUtc)
            .Select(interaction => interaction!)
            .ToArray();

        return candidates;
    }

    public bool TryMarkAsSent(string interactionId, InteractionSentUpdateRequest request, DateTimeOffset nowUtc, out InteractionContext? updated, out string? errorCode)
    {
        var requestedDestinationPhoneNumberId = Sanitize(request.DestinationPhoneNumberId);
        var requestedDestinationDisplayPhone = Sanitize(request.DestinationDisplayPhone);
        var requestedChannelInstanceKey = Sanitize(request.ChannelInstanceKey);
        var updatedSuccessfully = TryUpdateInteraction(
            interactionId,
            nowUtc,
            current =>
            {
                var anchoredPhoneNumberId = Sanitize(current.CurrentConversationPhoneNumberId) ?? Sanitize(current.DestinationPhoneNumberId);
                var anchoredDisplayPhone = Sanitize(current.CurrentConversationDisplayPhone) ?? Sanitize(current.DestinationDisplayPhone);
                var resolvedDestinationPhoneNumberId = anchoredPhoneNumberId ?? requestedDestinationPhoneNumberId ?? current.DestinationPhoneNumberId;
                var resolvedDestinationDisplayPhone = anchoredDisplayPhone ?? requestedDestinationDisplayPhone ?? current.DestinationDisplayPhone;
                var resolvedChannelInstanceKey = !string.IsNullOrWhiteSpace(current.ChannelInstanceKey)
                    ? current.ChannelInstanceKey
                    : !string.IsNullOrWhiteSpace(anchoredPhoneNumberId)
                        ? BuildChannelInstanceKey(anchoredPhoneNumberId)
                        : requestedChannelInstanceKey ?? BuildChannelInstanceKey(resolvedDestinationPhoneNumberId);

                return current with
                {
                    Status = string.IsNullOrWhiteSpace(request.Status) ? "ENVIADO" : request.Status.Trim(),
                    SentAtUtc = request.SentAt ?? nowUtc,
                    PhoneNumberId = Sanitize(request.PhoneNumberId) ?? current.PhoneNumberId,
                    DestinationPhoneNumberId = resolvedDestinationPhoneNumberId,
                    DestinationDisplayPhone = resolvedDestinationDisplayPhone,
                    ChannelInstanceKey = resolvedChannelInstanceKey,
                    CurrentConversationPhoneNumberId = anchoredPhoneNumberId ?? requestedDestinationPhoneNumberId ?? current.CurrentConversationPhoneNumberId,
                    CurrentConversationDisplayPhone = anchoredDisplayPhone ?? requestedDestinationDisplayPhone ?? current.CurrentConversationDisplayPhone,
                    FlowName = Sanitize(request.FlowName),
                    WorkflowName = Sanitize(request.WorkflowName),
                    WebhookName = Sanitize(request.WebhookName),
                    MessageType = SanitizeMessageType(request.MessageType) ?? current.MessageType,
                    MessageName = Sanitize(request.MessageName) ?? current.MessageName,
                    WhatsappNodeName = Sanitize(request.WhatsappNodeName) ?? current.WhatsappNodeName,
                    TemplateName = SanitizeTemplateField(request.MessageType, request.TemplateName, current.TemplateName),
                    TemplateLanguage = SanitizeTemplateField(request.MessageType, request.TemplateLanguage, current.TemplateLanguage),
                    MetaMessageId = Sanitize(request.MetaMessageId) ?? current.MetaMessageId,
                    SentCustomerId = request.CustomerId,
                    SentCustomerName = Sanitize(request.CustomerName) ?? current.SentCustomerName,
                    SentFileName = Sanitize(request.FileName) ?? current.SentFileName,
                    UpdatedAtUtc = nowUtc
                };
            },
            out updated,
            out errorCode);

        if (updatedSuccessfully && updated is not null && !string.IsNullOrWhiteSpace(updated.MetaMessageId))
        {
            _interactionIdByMessageId[updated.MetaMessageId] = updated.InteractionId;
        }

        if (updatedSuccessfully && updated is not null)
        {
            RegisterOrUpdateOutboundMessage(updated, request.SentAt ?? nowUtc, new OutboundDecisionProfile
            {
                IsDecisionAnchor = request.IsDecisionAnchor ?? false,
                AcceptsYes = request.AcceptsYes ?? false,
                AcceptsNo = request.AcceptsNo ?? false,
                AcceptsFlow = request.AcceptsFlow ?? false,
                YesRoute = request.YesRoute,
                NoRoute = request.NoRoute,
                FlowRoute = request.FlowRoute,
                FallbackRoute = request.FallbackRoute,
                YesRouteApiKey = request.YesRouteApiKey,
                NoRouteApiKey = request.NoRouteApiKey,
                FlowRouteApiKey = request.FlowRouteApiKey,
                FallbackRouteApiKey = request.FallbackRouteApiKey
            });
        }

        return updatedSuccessfully;
    }

    public bool TryMarkAsFailed(string interactionId, InteractionFailedUpdateRequest request, DateTimeOffset nowUtc, out InteractionContext? updated, out string? errorCode)
    {
        var requestedDestinationPhoneNumberId = Sanitize(request.DestinationPhoneNumberId);
        var requestedDestinationDisplayPhone = Sanitize(request.DestinationDisplayPhone);
        var requestedChannelInstanceKey = Sanitize(request.ChannelInstanceKey);
        return TryUpdateInteraction(
            interactionId,
            nowUtc,
            current =>
            {
                var anchoredPhoneNumberId = Sanitize(current.CurrentConversationPhoneNumberId) ?? Sanitize(current.DestinationPhoneNumberId);
                var anchoredDisplayPhone = Sanitize(current.CurrentConversationDisplayPhone) ?? Sanitize(current.DestinationDisplayPhone);
                var resolvedDestinationPhoneNumberId = anchoredPhoneNumberId ?? requestedDestinationPhoneNumberId ?? current.DestinationPhoneNumberId;
                var resolvedDestinationDisplayPhone = anchoredDisplayPhone ?? requestedDestinationDisplayPhone ?? current.DestinationDisplayPhone;
                var resolvedChannelInstanceKey = !string.IsNullOrWhiteSpace(current.ChannelInstanceKey)
                    ? current.ChannelInstanceKey
                    : !string.IsNullOrWhiteSpace(anchoredPhoneNumberId)
                        ? BuildChannelInstanceKey(anchoredPhoneNumberId)
                        : requestedChannelInstanceKey ?? BuildChannelInstanceKey(resolvedDestinationPhoneNumberId);

                return current with
                {
                    Status = string.IsNullOrWhiteSpace(request.Status) ? "ERRO_ENVIO" : request.Status.Trim(),
                    FailedAtUtc = request.FailedAt ?? nowUtc,
                    PhoneNumberId = Sanitize(request.PhoneNumberId) ?? current.PhoneNumberId,
                    DestinationPhoneNumberId = resolvedDestinationPhoneNumberId,
                    DestinationDisplayPhone = resolvedDestinationDisplayPhone,
                    ChannelInstanceKey = resolvedChannelInstanceKey,
                    CurrentConversationPhoneNumberId = anchoredPhoneNumberId ?? requestedDestinationPhoneNumberId ?? current.CurrentConversationPhoneNumberId,
                    CurrentConversationDisplayPhone = anchoredDisplayPhone ?? requestedDestinationDisplayPhone ?? current.CurrentConversationDisplayPhone,
                    FlowName = Sanitize(request.FlowName),
                    WorkflowName = Sanitize(request.WorkflowName),
                    WebhookName = Sanitize(request.WebhookName),
                    MessageType = SanitizeMessageType(request.MessageType),
                    MessageName = Sanitize(request.MessageName),
                    WhatsappNodeName = Sanitize(request.WhatsappNodeName),
                    TemplateName = SanitizeTemplateField(request.MessageType, request.TemplateName, current.TemplateName),
                    TemplateLanguage = SanitizeTemplateField(request.MessageType, request.TemplateLanguage, current.TemplateLanguage),
                    MetaMessageId = Sanitize(request.MetaMessageId) ?? current.MetaMessageId,
                    SentFileName = Sanitize(request.FileName),
                    ErrorMessage = Sanitize(request.ErrorMessage),
                    ErrorDetails = Sanitize(request.ErrorDetails),
                    UpdatedAtUtc = nowUtc
                };
            },
            out updated,
            out errorCode);
    }

    public bool TryMarkAsRefused(string interactionId, InteractionRefusedUpdateRequest request, DateTimeOffset nowUtc, out InteractionContext? updated, out string? errorCode)
    {
        var sanitizedInteractionId = interactionId.Trim();
        if (_interactionById.TryGetValue(sanitizedInteractionId, out var current)
            && string.Equals(current.Status, "RECUSADO", StringComparison.OrdinalIgnoreCase))
        {
            updated = current;
            errorCode = null;
            return true;
        }

        var response = string.IsNullOrWhiteSpace(request.Response)
            ? "NAO"
            : request.Response.Trim().ToUpperInvariant();

        if (response == "NÃO")
        {
            response = "NAO";
        }

        return TryUpdateInteraction(
            interactionId,
            nowUtc,
            current => current with
            {
                Status = string.IsNullOrWhiteSpace(request.Status) ? "RECUSADO" : request.Status.Trim(),
                RefusedAtUtc = request.RefusedAt ?? nowUtc,
                RefusedCustomerId = request.CustomerId,
                RefusedCustomerName = Sanitize(request.CustomerName),
                RefusedResponse = response,
                FlowName = Sanitize(request.FlowName),
                WorkflowName = Sanitize(request.WorkflowName),
                WebhookName = Sanitize(request.WebhookName),
                MessageType = SanitizeMessageType(request.MessageType),
                MessageName = Sanitize(request.MessageName),
                WhatsappNodeName = Sanitize(request.WhatsappNodeName),
                TemplateName = SanitizeTemplateField(request.MessageType, request.TemplateName, current.TemplateName),
                TemplateLanguage = SanitizeTemplateField(request.MessageType, request.TemplateLanguage, current.TemplateLanguage),
                MetaMessageId = Sanitize(request.MetaMessageId) ?? current.MetaMessageId,
                SentFileName = Sanitize(request.FileName),
                UpdatedAtUtc = nowUtc
            },
            out updated,
            out errorCode);
    }

    public bool TryMarkInvalidResponseAsSent(string interactionId, InteractionInvalidResponseSentUpdateRequest request, DateTimeOffset nowUtc, out InteractionContext? updated, out string? errorCode)
    {
        var responseType = string.IsNullOrWhiteSpace(request.ResponseType)
            ? "FORA_DO_PADRAO"
            : request.ResponseType.Trim().ToUpperInvariant();

        return TryUpdateInteraction(
            interactionId,
            nowUtc,
            current => current with
            {
                Status = string.IsNullOrWhiteSpace(request.Status) ? "AGUARDANDO_RESPOSTA" : request.Status.Trim(),
                RefusedAtUtc = request.InvalidResponseSentAt ?? nowUtc,
                RefusedCustomerId = request.CustomerId ?? current.RefusedCustomerId,
                RefusedCustomerName = Sanitize(request.CustomerName) ?? current.RefusedCustomerName,
                RefusedResponse = responseType,
                FlowName = Sanitize(request.FlowName) ?? current.FlowName,
                WorkflowName = Sanitize(request.WorkflowName) ?? current.WorkflowName,
                WebhookName = Sanitize(request.WebhookName) ?? current.WebhookName,
                MessageType = SanitizeMessageType(request.MessageType) ?? current.MessageType,
                MessageName = Sanitize(request.MessageName) ?? current.MessageName,
                WhatsappNodeName = Sanitize(request.WhatsappNodeName) ?? current.WhatsappNodeName,
                TemplateName = SanitizeTemplateField(request.MessageType, request.TemplateName, current.TemplateName),
                TemplateLanguage = SanitizeTemplateField(request.MessageType, request.TemplateLanguage, current.TemplateLanguage),
                MetaMessageId = Sanitize(request.MetaMessageId) ?? current.MetaMessageId,
                SentFileName = Sanitize(request.FileName) ?? current.SentFileName,
                ErrorDetails = Sanitize(request.ResponseRaw) ?? current.ErrorDetails,
                UpdatedAtUtc = nowUtc
            },
            out updated,
            out errorCode);
    }

    public bool TryForceCloseActiveByPhone(string phone, DateTimeOffset nowUtc, out IReadOnlyList<InteractionContext> closedInteractions)
    {
        closedInteractions = Array.Empty<InteractionContext>();

        var phoneAliases = MetaWhatsAppPhoneCanonicalizer.BuildLookupAliases(phone)
            .Where(static alias => !string.IsNullOrWhiteSpace(alias))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (phoneAliases.Length == 0)
        {
            return false;
        }

        var candidates = phoneAliases
            .Select(alias => _interactionIdsByPhone.TryGetValue(alias, out var set) ? set : null)
            .Where(set => set is not null)
            .SelectMany(set => set!.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 0)
        {
            return false;
        }

        var closed = new List<InteractionContext>();
        var immediateExpiry = nowUtc.AddSeconds(-1);

        foreach (var interactionId in candidates)
        {
            if (!TryUpdateInteraction(
                    interactionId,
                    nowUtc,
                    current => current with
                    {
                        Status = "CANCELADA",
                        UpdatedAtUtc = nowUtc,
                        ExpiresAtUtc = immediateExpiry
                    },
                    out var updated,
                    out _))
            {
                continue;
            }

            if (updated is not null)
            {
                closed.Add(updated);
            }
        }

        if (closed.Count == 0)
        {
            return false;
        }

        CleanupExpired(nowUtc);
        closedInteractions = closed;
        return true;
    }

    public void RecordDispatchResult(
        string interactionId,
        DateTimeOffset nowUtc,
        bool succeeded,
        int? httpStatusCode,
        string? errorMessage)
    {
        _ = TryUpdateInteraction(
            interactionId,
            nowUtc,
            current => current with
            {
                LastDispatchAttemptAtUtc = nowUtc,
                LastDispatchSucceeded = succeeded,
                LastDispatchHttpStatusCode = httpStatusCode,
                LastDispatchErrorMessage = Sanitize(errorMessage),
                UpdatedAtUtc = nowUtc
            },
            out _,
            out _);
    }

    public bool TryMarkSimDispatchFailed(
        string interactionId,
        DateTimeOffset nowUtc,
        string? errorMessage,
        string? errorDetails)
    {
        return TryUpdateInteraction(
            interactionId,
            nowUtc,
            current => current with
            {
                Status = StatusErroProcessamentoSim,
                FailedAtUtc = nowUtc,
                ErrorMessage = Sanitize(errorMessage),
                ErrorDetails = Sanitize(errorDetails),
                UpdatedAtUtc = nowUtc
            },
            out _,
            out _);
    }


    public InteractionContext? ResolveForAuditEvent(string? interactionId, string? customerPhone, int? customerId, string? flowKey, DateTimeOffset nowUtc)
    {
        CleanupExpired(nowUtc);

        if (!string.IsNullOrWhiteSpace(interactionId) && TryGetValidInteraction(interactionId.Trim(), nowUtc, out var byId))
        {
            return byId;
        }

        var candidates = _interactionById.Values
            .Where(interaction => interaction.ExpiresAtUtc > nowUtc)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(customerPhone))
        {
            var normalizedPhone = Sanitize(customerPhone);
            candidates = candidates
                .Where(interaction =>
                    string.Equals(interaction.PhoneKey, normalizedPhone, StringComparison.Ordinal)
                    || string.Equals(interaction.CustomerPhone, normalizedPhone, StringComparison.Ordinal)
                    || string.Equals(interaction.RecipientE164, normalizedPhone, StringComparison.Ordinal))
                .ToArray();
        }

        if (customerId.HasValue)
        {
            candidates = candidates
                .Where(interaction => interaction.CustomerId == customerId.Value)
                .ToArray();
        }

        if (!string.IsNullOrWhiteSpace(flowKey))
        {
            var normalizedFlow = flowKey.Trim();
            candidates = candidates
                .Where(interaction => string.Equals(interaction.FlowKey, normalizedFlow, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        return candidates
            .OrderByDescending(interaction => interaction.UpdatedAtUtc)
            .FirstOrDefault();
    }

    public RoutingDecision Resolve(JsonElement webhookBody, DateTimeOffset nowUtc, string? fallbackUrl)
    {
        CleanupExpired(nowUtc);

        var eventInfo = MetaWhatsAppWebhookParser.Parse(webhookBody);
        var normalizedResponse = eventInfo.Response?.Trim().ToUpperInvariant();
        if (normalizedResponse == "NÃO")
        {
            normalizedResponse = "NAO";
        }

        var correlationCandidates = new (string Strategy, string? InteractionId)[]
        {
            ("context_message_id", TryGetByMessageId(eventInfo.ContextMessageId))
        };

        foreach (var (strategy, candidate) in correlationCandidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && TryGetValidInteraction(candidate, nowUtc, out var found))
            {
                return RoutingDecision.Matched(found, eventInfo, strategy);
            }
        }

        if (TryResolveUniqueByRawIdentifier(_interactionIdsByRawWaId, eventInfo.CustomerWaIdRaw, nowUtc, out var byWaId))
        {
            return RoutingDecision.Matched(byWaId!, eventInfo, "wa_id_raw_exact");
        }

        if (TryResolveUniqueByRawIdentifier(_interactionIdsByRawSourcePhone, eventInfo.SourcePhoneRaw, nowUtc, out var bySourcePhone))
        {
            return RoutingDecision.Matched(bySourcePhone!, eventInfo, "source_phone_raw_exact");
        }

        var secondaryCandidates = new (string Strategy, string? InteractionId)[]
        {
            ("meta_message_id", TryGetByMessageId(eventInfo.MetaMessageId)),
            ("interaction_id", eventInfo.InteractionId),
            ("button_payload", TryGetByButtonPayload(eventInfo.ButtonPayload))
        };

        foreach (var (strategy, candidate) in secondaryCandidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && TryGetValidInteraction(candidate, nowUtc, out var found))
            {
                return RoutingDecision.Matched(found, eventInfo, strategy);
            }
        }

        var channelInstanceKey = BuildChannelInstanceKey(eventInfo.DestinationPhoneNumberId);
        if (!string.IsNullOrWhiteSpace(channelInstanceKey)
            && !string.IsNullOrWhiteSpace(eventInfo.CanonicalCorrelationKey)
            && _interactionIdsByChannelAndCanonicalKey.TryGetValue(BuildChannelScopedKey(channelInstanceKey, eventInfo.CanonicalCorrelationKey), out var scopedCanonicalSet))
        {
            var activeForCanonicalKey = scopedCanonicalSet.Keys
                .Select(id => TryGetValidInteraction(id, nowUtc, out var interaction) ? interaction : null)
                .Where(interaction => interaction is not null)
                .OrderByDescending(interaction => interaction!.RegisteredAtUtc)
                .ToArray();

            if (activeForCanonicalKey.Length == 1)
            {
                return RoutingDecision.Matched(activeForCanonicalKey[0]!, eventInfo, $"channel_instance+canonical_key:{eventInfo.CanonicalCorrelationSource}");
            }

            if (activeForCanonicalKey.Length > 1)
            {
                return RoutingDecision.Ambiguous(eventInfo, "channel_instance+canonical_key_ambiguous");
            }
        }

        var phoneAliases = MetaWhatsAppPhoneCanonicalizer.BuildLookupAliases(eventInfo.SourcePhoneRaw ?? eventInfo.CustomerPhone).ToArray();
        if (!string.IsNullOrWhiteSpace(channelInstanceKey)
            && phoneAliases.Length > 0
            && phoneAliases
                .Select(alias => _interactionIdsByChannelAndPhone.TryGetValue(BuildChannelScopedKey(channelInstanceKey, alias), out var set) ? set : null)
                .Where(set => set is not null)
                .SelectMany(set => set!.Keys)
                .Distinct(StringComparer.Ordinal)
                .ToArray() is { Length: > 0 } phoneInteractionIds)
        {
            var activeForPhone = phoneInteractionIds
                .Select(id => TryGetValidInteraction(id, nowUtc, out var interaction) ? interaction : null)
                .Where(interaction => interaction is not null)
                .OrderByDescending(interaction => interaction!.RegisteredAtUtc)
                .ToArray();

            if (activeForPhone.Length == 1)
            {
                return RoutingDecision.Matched(activeForPhone[0]!, eventInfo, "channel_instance+phone_alias");
            }

            if (activeForPhone.Length > 1)
            {
                return RoutingDecision.Ambiguous(eventInfo, "channel_instance+phone_alias_ambiguous");
            }
        }

        _logger.LogInformation(
            "Roteamento em fallback sem interação ativa. FallbackConfigured={FallbackConfigured}. FallbackUrl={FallbackUrl}. EventType={EventType}. ParseReason={ParseReason}. CorrelationKey={CorrelationKey}",
            !string.IsNullOrWhiteSpace(fallbackUrl),
            fallbackUrl,
            eventInfo.EventType,
            eventInfo.ParseReason,
            eventInfo.CanonicalCorrelationKey);

        return RoutingDecision.Fallback(eventInfo, fallbackUrl);
    }

    public bool TryRegisterInboundMessage(string? inboundMetaMessageId, DateTimeOffset nowUtc)
    {
        CleanupInboundMessageCache(nowUtc);

        if (string.IsNullOrWhiteSpace(inboundMetaMessageId))
        {
            return true;
        }

        return _processedInboundMessageIds.TryAdd(inboundMetaMessageId.Trim(), nowUtc);
    }

    public string NormalizeInboundResponse(string? responseRaw)
    {
        if (string.IsNullOrWhiteSpace(responseRaw))
        {
            return "FORA_DO_PADRAO";
        }

        if (IsGlobalTerminationIntent(responseRaw))
        {
            return "NAO";
        }

        var normalized = RemoveDiacritics(responseRaw).Trim().ToUpperInvariant();
        var token = new string(normalized.Where(char.IsLetter).ToArray());

        return token switch
        {
            "SIM" => "SIM",
            "NAO" => "NAO",
            "FLOW" => "FLOW",
            _ => "FORA_DO_PADRAO"
        };
    }

    public bool IsGlobalTerminationIntent(string? responseRaw)
    {
        if (string.IsNullOrWhiteSpace(responseRaw))
        {
            return false;
        }

        var normalized = NormalizeIntentText(responseRaw);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (GlobalTerminationSingleWordIntents.Contains(normalized))
        {
            return true;
        }

        var firstToken = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(firstToken) && GlobalTerminationSingleWordIntents.Contains(firstToken))
        {
            return true;
        }

        return GlobalTerminationPhraseIntents.Any(phrase => normalized.Contains(phrase, StringComparison.Ordinal));
    }

    public bool TryGetLastOutboundMessage(string interactionId, out OutboundMessageContext? outboundMessage)
    {
        outboundMessage = null;
        if (string.IsNullOrWhiteSpace(interactionId))
        {
            return false;
        }

        return _lastOutboundByInteractionId.TryGetValue(interactionId.Trim(), out outboundMessage);
    }

    public InboundRouteResolution ResolveInboundRoute(InteractionContext interaction, string? responseRaw, JsonElement? flowResponseJson = null)
    {
        var normalizedResponse = NormalizeInboundResponse(responseRaw);
        var hasGlobalTerminationIntent = string.Equals(normalizedResponse, "NAO", StringComparison.Ordinal);
        var expectedResponseMode = Sanitize(interaction.ExpectedResponseMode)?.ToUpperInvariant();
        if (string.Equals(expectedResponseMode, "FALLBACK_ONLY", StringComparison.Ordinal) && !hasGlobalTerminationIntent)
        {
            return new InboundRouteResolution(
                "FORA_DO_PADRAO",
                "FORA_DO_PADRAO",
                interaction.RouteOnFallback ?? interaction.N8nWebhookUrl,
                interaction.RouteOnFallbackApiKey ?? interaction.N8nApiKey,
                "interaction.route_on_fallback_api_key",
                null);
        }

        var hasLastOutbound = TryGetLastOutboundMessage(interaction.InteractionId, out var lastOutbound);
        var fallbackRoute = lastOutbound?.FallbackRoute ?? interaction.RouteOnFallback ?? interaction.N8nWebhookUrl;
        var fallbackApiKey = lastOutbound?.FallbackRouteApiKey ?? interaction.RouteOnFallbackApiKey ?? interaction.N8nApiKey;
        var route = fallbackRoute;
        var routeApiKey = fallbackApiKey;
        var routeApiKeyOrigin = lastOutbound?.FallbackRouteApiKey is not null
            ? "last_outbound.fallback_route_api_key"
            : "interaction.route_on_fallback_api_key";
        var routeType = "FORA_DO_PADRAO";

        if (normalizedResponse == "FLOW")
        {
            var flowRoute = lastOutbound?.FlowRoute ?? interaction.RouteOnFlow ?? interaction.RouteOnFallback ?? interaction.N8nWebhookUrl;
            var flowApiKey = lastOutbound?.FlowRouteApiKey ?? interaction.RouteOnFlowApiKey ?? interaction.RouteOnFallbackApiKey ?? interaction.N8nApiKey;
            var isFaleConoscoFlow = IsFaleConoscoFlow(flowResponseJson);
            route = isFaleConoscoFlow ? FaleConoscoWebhookUrl : flowRoute;
            routeApiKey = flowApiKey;
            routeApiKeyOrigin = lastOutbound?.FlowRouteApiKey is not null
                ? "last_outbound.flow_route_api_key"
                : !string.IsNullOrWhiteSpace(interaction.RouteOnFlowApiKey)
                    ? "interaction.route_on_flow_api_key"
                    : "interaction.route_on_fallback_api_key";
            routeType = "FLOW";
        }
        else if (normalizedResponse == "SIM")
        {
            if (hasLastOutbound
                && lastOutbound!.IsDecisionAnchor
                && lastOutbound.AcceptsYes
                && !string.IsNullOrWhiteSpace(lastOutbound.YesRoute))
            {
                route = lastOutbound.YesRoute;
                routeApiKey = lastOutbound.YesRouteApiKey ?? interaction.RouteOnYesApiKey ?? interaction.N8nApiKey;
                routeApiKeyOrigin = lastOutbound.YesRouteApiKey is not null
                    ? "last_outbound.yes_route_api_key"
                    : "interaction.route_on_yes_api_key";
                routeType = "SIM";
            }
        }
        else if (normalizedResponse == "NAO")
        {
            if (hasLastOutbound
                && lastOutbound!.IsDecisionAnchor
                && lastOutbound.AcceptsNo
                && !string.IsNullOrWhiteSpace(lastOutbound.NoRoute))
            {
                route = lastOutbound.NoRoute;
                routeApiKey = lastOutbound.NoRouteApiKey ?? interaction.RouteOnNoApiKey ?? interaction.N8nApiKey;
                routeApiKeyOrigin = lastOutbound.NoRouteApiKey is not null
                    ? "last_outbound.no_route_api_key"
                    : "interaction.route_on_no_api_key";
                routeType = "NAO";
            }
            else if (!string.IsNullOrWhiteSpace(interaction.RouteOnNo))
            {
                route = interaction.RouteOnNo;
                routeApiKey = interaction.RouteOnNoApiKey ?? interaction.N8nApiKey;
                routeApiKeyOrigin = "interaction.route_on_no_api_key";
                routeType = "NAO";
            }
            else if (string.Equals(interaction.FlowKey, GlobalAtendimentoFlowKey, StringComparison.OrdinalIgnoreCase))
            {
                route = BillingTemplateNoRoute;
                routeApiKey = interaction.RouteOnNoApiKey ?? interaction.N8nApiKey;
                routeApiKeyOrigin = "global_atendimento.no_route_override";
                routeType = "NAO";
            }
        }

        return new InboundRouteResolution(
            normalizedResponse,
            routeType,
            route,
            routeApiKey,
            routeApiKeyOrigin,
            lastOutbound);
    }

    private static bool IsFaleConoscoFlow(JsonElement? flowResponseJson)
    {
        if (flowResponseJson is null)
        {
            return false;
        }

        var tipoSolicitacao = ResolveFlowTipoSolicitacao(flowResponseJson.Value);
        return string.Equals(tipoSolicitacao, FaleConoscoTipoSolicitacao, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveFlowTipoSolicitacao(JsonElement flowResponseJson)
    {
        if (flowResponseJson.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPropertyCaseInsensitive(flowResponseJson, "tipo_solicitacao", out var tipoSolicitacaoElement)
                && tipoSolicitacaoElement.ValueKind == JsonValueKind.String)
            {
                var tipoSolicitacaoDireto = tipoSolicitacaoElement.GetString();
                if (!string.IsNullOrWhiteSpace(tipoSolicitacaoDireto))
                {
                    return tipoSolicitacaoDireto.Trim();
                }
            }

            if (TryGetPropertyCaseInsensitive(flowResponseJson, "response_json", out var responseJsonElement))
            {
                var nested = ResolveFlowTipoSolicitacao(responseJsonElement);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
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
                using var document = JsonDocument.Parse(rawText);
                return ResolveFlowTipoSolicitacao(document.RootElement);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement root, string propertyName, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
    
    private bool TryUpdateInteraction(
        string interactionId,
        DateTimeOffset nowUtc,
        Func<InteractionContext, InteractionContext> update,
        out InteractionContext? updated,
        out string? errorCode)
    {
        CleanupExpired(nowUtc);

        var sanitizedInteractionId = interactionId.Trim();
        while (true)
        {
            if (!_interactionById.TryGetValue(sanitizedInteractionId, out var current))
            {
                updated = null;
                errorCode = "not_found";
                return false;
            }

            if (IncompatibleTerminalStatuses.Contains(current.Status))
            {
                updated = current;
                errorCode = "incompatible_status";
                return false;
            }

            var candidate = update(current);
            if (_interactionById.TryUpdate(sanitizedInteractionId, candidate, current))
            {
                updated = candidate;
                errorCode = null;
                return true;
            }
        }
    }

    private string? TryGetByMessageId(string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return null;
        }

        return _interactionIdByMessageId.TryGetValue(messageId, out var interactionId)
            ? interactionId
            : null;
    }

    private string? TryGetByButtonPayload(string? buttonPayload)
    {
        if (string.IsNullOrWhiteSpace(buttonPayload))
        {
            return null;
        }

        return _interactionIdByButtonPayload.TryGetValue(buttonPayload, out var interactionId)
            ? interactionId
            : null;
    }

    private bool TryGetValidInteraction(string interactionId, DateTimeOffset nowUtc, out InteractionContext interaction)
    {
        if (_interactionById.TryGetValue(interactionId, out var found) && IsEligibleForInboundRouting(found, nowUtc))
        {
            interaction = found;
            return true;
        }

        interaction = default!;
        return false;
    }

    private static bool IsEligibleForInboundRouting(InteractionContext interaction, DateTimeOffset nowUtc)
    {
        if (interaction.ExpiresAtUtc <= nowUtc)
        {
            return false;
        }

        if (IncompatibleTerminalStatuses.Contains(interaction.Status))
        {
            return false;
        }

        if (string.Equals(interaction.FlowKey, GlobalAtendimentoFlowKey, StringComparison.OrdinalIgnoreCase))
        {
            return ConversationalActiveStatuses.Contains(interaction.Status)
                || RecoverableErrorStatuses.Contains(interaction.Status);
        }

        return true;
    }

    public bool TryGetRecoverableErrorInteractionByPhone(string? phone, DateTimeOffset nowUtc, out InteractionContext? interaction)
    {
        interaction = null;
        var aliases = MetaWhatsAppPhoneCanonicalizer.BuildLookupAliases(phone)
            .Where(static alias => !string.IsNullOrWhiteSpace(alias))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (aliases.Length == 0)
        {
            return false;
        }

        var candidates = aliases
            .Select(alias => _interactionIdsByPhone.TryGetValue(alias, out var set) ? set : null)
            .Where(static set => set is not null)
            .SelectMany(static set => set!.Keys)
            .Distinct(StringComparer.Ordinal)
            .Select(id => _interactionById.TryGetValue(id, out var found) ? found : null)
            .Where(static found => found is not null)
            .Where(found => found!.ExpiresAtUtc > nowUtc && RecoverableErrorStatuses.Contains(found.Status))
            .OrderByDescending(found => found!.UpdatedAtUtc)
            .FirstOrDefault();

        if (candidates is null)
        {
            return false;
        }

        interaction = candidates;
        return true;
    }

    private void CleanupExpired(DateTimeOffset nowUtc)
    {
        foreach (var pair in _interactionById)
        {
            if (pair.Value.ExpiresAtUtc > nowUtc)
            {
                continue;
            }

            _interactionById.TryRemove(pair.Key, out var removed);
            if (removed is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(removed.OutboundMessageId))
            {
                _interactionIdByMessageId.TryRemove(removed.OutboundMessageId, out _);
            }

            if (!string.IsNullOrWhiteSpace(removed.MetaMessageId))
            {
                _interactionIdByMessageId.TryRemove(removed.MetaMessageId, out _);
            }

            if (!string.IsNullOrWhiteSpace(removed.ButtonPayload))
            {
                _interactionIdByButtonPayload.TryRemove(removed.ButtonPayload, out _);
            }

            foreach (var alias in removed.PhoneAliases)
            {
                if (!_interactionIdsByPhone.TryGetValue(alias, out var phoneSet))
                {
                    continue;
                }

                phoneSet.TryRemove(removed.InteractionId, out _);
                if (phoneSet.IsEmpty)
                {
                    _interactionIdsByPhone.TryRemove(alias, out _);
                }
            }

            if (!string.IsNullOrWhiteSpace(removed.WaIdRaw)
                && _interactionIdsByRawWaId.TryGetValue(removed.WaIdRaw, out var waIdSet))
            {
                waIdSet.TryRemove(removed.InteractionId, out _);
                if (waIdSet.IsEmpty)
                {
                    _interactionIdsByRawWaId.TryRemove(removed.WaIdRaw, out _);
                }
            }

            if (!string.IsNullOrWhiteSpace(removed.SourcePhoneRaw)
                && _interactionIdsByRawSourcePhone.TryGetValue(removed.SourcePhoneRaw, out var sourcePhoneSet))
            {
                sourcePhoneSet.TryRemove(removed.InteractionId, out _);
                if (sourcePhoneSet.IsEmpty)
                {
                    _interactionIdsByRawSourcePhone.TryRemove(removed.SourcePhoneRaw, out _);
                }
            }

            if (!string.IsNullOrWhiteSpace(removed.CanonicalCorrelationKey)
                && _interactionIdsByCanonicalKey.TryGetValue(removed.CanonicalCorrelationKey, out var canonicalSet))
            {
                canonicalSet.TryRemove(removed.InteractionId, out _);
                if (canonicalSet.IsEmpty)
                {
                    _interactionIdsByCanonicalKey.TryRemove(removed.CanonicalCorrelationKey, out _);
                }
            }

            if (!string.IsNullOrWhiteSpace(removed.ChannelInstanceKey))
            {
                RemoveIndex(_interactionIdsByChannelInstanceKey, removed.ChannelInstanceKey, removed.InteractionId);
                foreach (var alias in removed.PhoneAliases)
                {
                    RemoveIndex(_interactionIdsByChannelAndPhone, BuildChannelScopedKey(removed.ChannelInstanceKey, alias), removed.InteractionId);
                }

                if (!string.IsNullOrWhiteSpace(removed.CanonicalCorrelationKey))
                {
                    RemoveIndex(_interactionIdsByChannelAndCanonicalKey, BuildChannelScopedKey(removed.ChannelInstanceKey, removed.CanonicalCorrelationKey), removed.InteractionId);
                }
            }

            _lastOutboundByInteractionId.TryRemove(removed.InteractionId, out _);
        }
    }

    private static void AddIndex(ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> source, string key, string interactionId)
    {
        var set = source.GetOrAdd(key, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        set[interactionId] = 1;
    }

    private static void RemoveIndex(ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> source, string key, string interactionId)
    {
        if (!source.TryGetValue(key, out var set))
        {
            return;
        }

        set.TryRemove(interactionId, out _);
        if (set.IsEmpty)
        {
            source.TryRemove(key, out _);
        }
    }

    public static string? BuildChannelInstanceKey(string? destinationPhoneNumberId)
    {
        if (string.IsNullOrWhiteSpace(destinationPhoneNumberId))
        {
            return null;
        }

        return $"whatsapp:{destinationPhoneNumberId.Trim()}";
    }

    private static string BuildChannelScopedKey(string channelInstanceKey, string scopedValue)
        => $"{channelInstanceKey}::{scopedValue}";

    private static string? Sanitize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string? SanitizeMessageType(string? value)
        => Sanitize(value)?.ToLowerInvariant();

    private static string? SanitizeTemplateField(string? messageType, string? templateField, string? currentValue = null)
    {
        var sanitizedTemplateField = Sanitize(templateField);
        var normalizedMessageType = SanitizeMessageType(messageType);

        if (string.IsNullOrWhiteSpace(normalizedMessageType))
        {
            return sanitizedTemplateField ?? currentValue;
        }

        if (string.Equals(normalizedMessageType, "template", StringComparison.Ordinal))
        {
            return sanitizedTemplateField ?? currentValue;
        }

        return null;
    }

    private void CleanupInboundMessageCache(DateTimeOffset nowUtc)
    {
        var cutoff = nowUtc.AddDays(-2);
        foreach (var pair in _processedInboundMessageIds)
        {
            if (pair.Value >= cutoff)
            {
                continue;
            }

            _processedInboundMessageIds.TryRemove(pair.Key, out _);
        }
    }

    private bool TryResolveUniqueByRawIdentifier(
        ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> source,
        string? rawIdentifier,
        DateTimeOffset nowUtc,
        out InteractionContext? interaction)
    {
        interaction = null;
        if (string.IsNullOrWhiteSpace(rawIdentifier) || !source.TryGetValue(rawIdentifier, out var candidates))
        {
            return false;
        }

        var active = candidates.Keys
            .Select(id => TryGetValidInteraction(id, nowUtc, out var found) ? found : null)
            .Where(item => item is not null)
            .OrderByDescending(item => item!.RegisteredAtUtc)
            .ToArray();

        if (active.Length != 1)
        {
            return false;
        }

        interaction = active[0];
        return true;
    }

    private void RegisterOrUpdateOutboundMessage(InteractionContext interaction, DateTimeOffset sentAtUtc, OutboundDecisionProfile payloadProfile)
    {
        if (!IsEligibleOutboundForInboundRouting(interaction.MessageType))
        {
            return;
        }

        var templateProfile = ResolveTemplateProfile(interaction.TemplateName);
        var profile = MergeProfiles(templateProfile, payloadProfile, interaction);

        var outbound = new OutboundMessageContext(
            interaction.InteractionId,
            interaction.MetaMessageId ?? interaction.OutboundMessageId,
            interaction.MessageType,
            interaction.MessageName,
            interaction.TemplateName,
            interaction.TemplateLanguage,
            interaction.WorkflowName,
            interaction.WebhookName,
            interaction.WhatsappNodeName,
            sentAtUtc,
            profile.IsDecisionAnchor,
            profile.AcceptsYes,
            profile.AcceptsNo,
            profile.AcceptsFlow,
            profile.YesRoute,
            profile.NoRoute,
            profile.FlowRoute,
            profile.FallbackRoute,
            profile.YesRouteApiKey,
            profile.NoRouteApiKey,
            profile.FlowRouteApiKey,
            profile.FallbackRouteApiKey);

        _lastOutboundByInteractionId[interaction.InteractionId] = outbound;
    }

    private static bool IsEligibleOutboundForInboundRouting(string? messageType)
        => string.Equals(messageType, "template", StringComparison.OrdinalIgnoreCase);

    private OutboundDecisionTemplateProfile? ResolveTemplateProfile(string? templateName)
    {
        if (string.IsNullOrWhiteSpace(templateName))
        {
            return null;
        }

        var normalizedTemplateName = templateName.Trim();
        if (_decisionTemplates.TryGetValue(normalizedTemplateName, out var configured))
        {
            return configured;
        }

        if (string.Equals(normalizedTemplateName, BillingPendingTitleTemplate, StringComparison.OrdinalIgnoreCase))
        {
            return new OutboundDecisionTemplateProfile
            {
                IsDecisionAnchor = true,
                AcceptsYes = true,
                AcceptsNo = true,
                AcceptsFlow = true,
                YesRoute = BillingTemplateYesRoute,
                NoRoute = BillingTemplateNoRoute,
                FlowRoute = BillingTemplateFlowRoute,
                FallbackRoute = BillingTemplateFallbackRoute
            };
        }

        return null;
    }

    private static OutboundDecisionProfile MergeProfiles(
        OutboundDecisionTemplateProfile? templateProfile,
        OutboundDecisionProfile payloadProfile,
        InteractionContext interaction)
    {
        return new OutboundDecisionProfile
        {
            IsDecisionAnchor = payloadProfile.IsDecisionAnchor || templateProfile?.IsDecisionAnchor == true,
            AcceptsYes = payloadProfile.AcceptsYes || templateProfile?.AcceptsYes == true,
            AcceptsNo = payloadProfile.AcceptsNo || templateProfile?.AcceptsNo == true,
            AcceptsFlow = payloadProfile.AcceptsFlow || templateProfile?.AcceptsFlow == true,
            YesRoute = payloadProfile.YesRoute ?? templateProfile?.YesRoute ?? interaction.RouteOnYes,
            NoRoute = payloadProfile.NoRoute ?? templateProfile?.NoRoute ?? interaction.RouteOnNo,
            FlowRoute = payloadProfile.FlowRoute ?? templateProfile?.FlowRoute ?? interaction.RouteOnFlow ?? interaction.RouteOnFallback ?? interaction.N8nWebhookUrl,
            FallbackRoute = payloadProfile.FallbackRoute ?? templateProfile?.FallbackRoute ?? interaction.RouteOnFallback ?? interaction.N8nWebhookUrl,
            YesRouteApiKey = payloadProfile.YesRouteApiKey ?? templateProfile?.YesRouteApiKey ?? interaction.RouteOnYesApiKey ?? interaction.N8nApiKey,
            NoRouteApiKey = payloadProfile.NoRouteApiKey ?? templateProfile?.NoRouteApiKey ?? interaction.RouteOnNoApiKey ?? interaction.N8nApiKey,
            FlowRouteApiKey = payloadProfile.FlowRouteApiKey ?? templateProfile?.FlowRouteApiKey ?? interaction.RouteOnFlowApiKey ?? interaction.RouteOnFallbackApiKey ?? interaction.N8nApiKey,
            FallbackRouteApiKey = payloadProfile.FallbackRouteApiKey ?? templateProfile?.FallbackRouteApiKey ?? interaction.RouteOnFallbackApiKey ?? interaction.N8nApiKey
        };
    }

    private static string RemoveDiacritics(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);

        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string NormalizeIntentText(string value)
    {
        var normalized = RemoveDiacritics(value).Trim().ToUpperInvariant();
        var builder = new StringBuilder(normalized.Length);
        var previousWasWhitespace = false;

        foreach (var character in normalized)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasWhitespace = false;
                continue;
            }

            if (char.IsWhiteSpace(character) && !previousWasWhitespace)
            {
                builder.Append(' ');
                previousWasWhitespace = true;
            }
        }

        return builder.ToString().Trim();
    }

    private static IReadOnlyDictionary<string, JsonElement>? CloneAdditionalProperties(
        IReadOnlyDictionary<string, JsonElement>? additionalProperties)
    {
        if (additionalProperties is null || additionalProperties.Count == 0)
        {
            return null;
        }

        var clone = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in additionalProperties)
        {
            clone[entry.Key] = entry.Value.Clone();
        }

        return clone;
    }
}

public sealed record InteractionContext(
    string InteractionId,
    string FlowKey,
    string? Channel,
    string? InteractionType,
    string? ExpectedResponseMode,
    string? N8nWebhookUrl,
    string? RouteOnYes,
    string? RouteOnNo,
    string? RouteOnFlow,
    string? RouteOnFallback,
    string? N8nApiKey,
    string? RouteOnYesApiKey,
    string? RouteOnNoApiKey,
    string? RouteOnFlowApiKey,
    string? RouteOnFallbackApiKey,
    int? CustomerId,
    string? CustomerName,
    string? CustomerDocument,
    string? CustomerPhone,
    string? CustomerWaId,
    string? SourcePhoneRaw,
    string? WaIdRaw,
    string? CanonicalPhone,
    IReadOnlyCollection<string> PhoneAliases,
    string? CustomerUserId,
    string? CustomerParentUserId,
    string? CustomerUsername,
    string? PhoneKey,
    string? RecipientE164,
    string? Recipient,
    string? RecipientUserId,
    string? RecipientParentUserId,
    string? DestinationPhoneNumberId,
    string? DestinationDisplayPhone,
    string? ChannelInstanceKey,
    string? PreferredOutboundPhoneNumberId,
    string? PreferredOutboundDisplayPhone,
    string? PreferredOutboundUnit,
    string? CurrentConversationPhoneNumberId,
    string? CurrentConversationDisplayPhone,
    string? FlowName,
    string? WorkflowName,
    string? WebhookName,
    string? MessageType,
    string? MessageName,
    string? WhatsappNodeName,
    string? TemplateName,
    string? TemplateLanguage,
    string? OutboundMessageId,
    string? ButtonPayload,
    string? BusinessSource,
    string? CompletionIntent,
    IReadOnlyCollection<string>? InitialChargeTitleIds,
    IReadOnlyCollection<string>? InitialChargeTitleNames,
    IReadOnlyDictionary<string, JsonElement>? BusinessAdditionalProperties,
    DateTimeOffset ExpiresAtUtc)
{
    public string? CanonicalCorrelationKey =>
        !string.IsNullOrWhiteSpace(CustomerParentUserId)
            ? $"parent_user_id:{CustomerParentUserId}"
            : !string.IsNullOrWhiteSpace(CustomerUserId)
                ? $"user_id:{CustomerUserId}"
                : !string.IsNullOrWhiteSpace(CustomerWaId)
                    ? $"wa_id:{CustomerWaId}"
                    : !string.IsNullOrWhiteSpace(CustomerPhone)
                        ? $"phone:{CustomerPhone}"
                        : null;

    public DateTimeOffset RegisteredAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string Status { get; init; } = "REGISTRADO";
    public DateTimeOffset? SentAtUtc { get; init; }
    public DateTimeOffset? FailedAtUtc { get; init; }
    public DateTimeOffset? RefusedAtUtc { get; init; }
    public string? PhoneNumberId { get; init; }
    public string? MetaMessageId { get; init; }
    public int? SentCustomerId { get; init; }
    public string? SentCustomerName { get; init; }
    public string? SentFileName { get; init; }
    public int? RefusedCustomerId { get; init; }
    public string? RefusedCustomerName { get; init; }
    public string? RefusedResponse { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorDetails { get; init; }
    public DateTimeOffset? LastDispatchAttemptAtUtc { get; init; }
    public bool? LastDispatchSucceeded { get; init; }
    public int? LastDispatchHttpStatusCode { get; init; }
    public string? LastDispatchErrorMessage { get; init; }
}

public sealed record ExecutionContext(
    string ExecutionId,
    string InteractionId,
    string? FlowKey,
    int? CustomerId,
    string? CustomerName,
    string? RecipientPhoneE164,
    string? RecipientUserId,
    string? RecipientParentUserId,
    string? PhoneKey,
    string? FlowName,
    string? WorkflowName,
    string? WebhookName,
    string? MessageType,
    string? MessageName,
    string? TemplateName,
    string? TemplateLanguage,
    string? WhatsappNodeName,
    string? DestinationPhoneNumberId,
    string? DestinationDisplayPhone,
    string? ChannelInstanceKey,
    string? PreferredOutboundPhoneNumberId,
    string? PreferredOutboundDisplayPhone,
    string? PreferredOutboundUnit,
    DateTimeOffset CreatedAt);

public sealed record RoutingDecision(
    bool IsFallback,
    bool IsAmbiguousPhoneMatch,
    string CorrelationStrategy,
    string? N8nWebhookUrl,
    InteractionContext? Interaction,
    ParsedWhatsAppEvent EventInfo)
{
    public static RoutingDecision Matched(InteractionContext interaction, ParsedWhatsAppEvent eventInfo, string correlationStrategy = "direct")
        => new(false, false, correlationStrategy, interaction.N8nWebhookUrl, interaction, eventInfo);

    public static RoutingDecision Fallback(ParsedWhatsAppEvent eventInfo, string? fallbackUrl)
        => new(true, false, "fallback_url", fallbackUrl, null, eventInfo);

    public static RoutingDecision Ambiguous(ParsedWhatsAppEvent eventInfo, string correlationStrategy)
        => new(false, true, correlationStrategy, null, null, eventInfo);
}

public sealed record OutboundMessageContext(
    string InteractionId,
    string? MetaMessageId,
    string? MessageType,
    string? MessageName,
    string? TemplateName,
    string? TemplateLanguage,
    string? WorkflowName,
    string? WebhookName,
    string? WhatsappNodeName,
    DateTimeOffset SentAtUtc,
    bool IsDecisionAnchor,
    bool AcceptsYes,
    bool AcceptsNo,
    bool AcceptsFlow,
    string? YesRoute,
    string? NoRoute,
    string? FlowRoute,
    string? FallbackRoute,
    string? YesRouteApiKey,
    string? NoRouteApiKey,
    string? FlowRouteApiKey,
    string? FallbackRouteApiKey);

public sealed record InboundRouteResolution(
    string NormalizedResponse,
    string RouteType,
    string? WebhookUrl,
    string? RouteApiKey,
    string RouteApiKeyOrigin,
    OutboundMessageContext? LastOutboundMessage);
