using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AplicacoesOnline.Models.MetaWhatsApp;
using Microsoft.Extensions.Options;

namespace AplicacoesOnline.Services.MetaWhatsApp;

public sealed class MetaWhatsAppFlowPreferencesService
{
    private const string FaleConoscoTipoSolicitacao = "FALE_CONOSCO";
    private const string FaleConoscoWebhookUrl = "https://braianperuzzoibr.app.n8n.cloud/webhook/wa-global-fale-conosco";

    private readonly MetaWhatsAppInteractionRouter _interactionRouter;
    private readonly IMetaWhatsAppPersistentLogService _persistentLogService;
    private readonly IOptions<N8nWebhookSecurityOptions> _webhookSecurityOptions;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MetaWhatsAppFlowPreferencesService> _logger;

    public MetaWhatsAppFlowPreferencesService(
        MetaWhatsAppInteractionRouter interactionRouter,
        IMetaWhatsAppPersistentLogService persistentLogService,
        IOptions<N8nWebhookSecurityOptions> webhookSecurityOptions,
        IHttpClientFactory httpClientFactory,
        ILogger<MetaWhatsAppFlowPreferencesService> logger)
    {
        _interactionRouter = interactionRouter;
        _persistentLogService = persistentLogService;
        _webhookSecurityOptions = webhookSecurityOptions;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<WhatsAppFlowPreferencesServiceResult> ProcessAsync(WhatsAppFlowPreferencesSubmitRequest request, string traceId)
    {
        var now = DateTimeOffset.UtcNow;
        await WriteAuditEventAsync("FLOW_PREFERENCIA_RECEBIDO", now, request.ResponderPhoneE164 ?? request.ResponderPhone ?? request.ResponderPhoneLocal, new Dictionary<string, string?>
        {
            ["trace_id"] = traceId,
            ["execution_id"] = request.ExecutionId,
            ["interaction_id"] = request.InteractionId,
            ["response_option"] = request.ResponseOption,
            ["meta_message_id"] = request.MetaMessageId,
            ["wamid"] = request.Wamid
        });

        var flowOperation = ResolveFlowOperation(request);

        var responderPhoneE164 = FirstNonEmpty(request.ResponderPhoneE164, request.ResponderPhone, request.ResponderPhoneLocal);
        responderPhoneE164 = MetaWhatsAppPhoneCanonicalizer.ToCanonicalE164Br(responderPhoneE164);
        var responderPhoneLocal = MetaWhatsAppPhoneCanonicalizer.ToBrLocalDigits(responderPhoneE164);

        var contextResolution = ResolveContext(request, responderPhoneE164, now);
        if (!contextResolution.Success)
        {
            await WriteAuditEventAsync(contextResolution.AuditEventName!, now, responderPhoneE164, new Dictionary<string, string?>
            {
                ["trace_id"] = traceId,
                ["reason"] = contextResolution.ErrorCode,
                ["execution_id"] = request.ExecutionId,
                ["interaction_id"] = request.InteractionId,
                ["responder_phone_e164"] = responderPhoneE164
            });
            return ErrorResult(contextResolution.StatusCode, contextResolution.ErrorType!, contextResolution.ErrorCode!, contextResolution.Message!, traceId, contextResolution.Details);
        }

        var interaction = contextResolution.Interaction;
        var execution = contextResolution.ExecutionContext;

        await WriteAuditEventAsync("FLOW_PREFERENCIA_CONTEXTO_RESOLVIDO", now, responderPhoneE164, new Dictionary<string, string?>
        {
            ["trace_id"] = traceId,
            ["correlation_strategy"] = contextResolution.CorrelationStrategy,
            ["execution_id"] = execution?.ExecutionId ?? request.ExecutionId,
            ["interaction_id"] = interaction?.InteractionId ?? execution?.InteractionId ?? request.InteractionId,
            ["customer_id"] = (interaction?.CustomerId ?? execution?.CustomerId)?.ToString()
        });

        var resolvedInteractionId = interaction?.InteractionId ?? execution?.InteractionId ?? request.InteractionId;
        var resolvedCustomerId = interaction?.CustomerId ?? execution?.CustomerId;
        var resolvedCustomerName = FirstNonEmpty(interaction?.CustomerName, execution?.CustomerName);
        var resolvedFlowKey = FirstNonEmpty(request.FlowKey, interaction?.FlowKey, execution?.FlowKey);
        var resolvedFlowName = FirstNonEmpty(request.FlowName, interaction?.FlowName, execution?.FlowName);
        var resolvedWorkflowName = FirstNonEmpty(interaction?.WorkflowName, execution?.WorkflowName);
        var resolvedWebhookName = FirstNonEmpty(interaction?.WebhookName, execution?.WebhookName);
        var resolvedTemplateName = FirstNonEmpty(interaction?.TemplateName, execution?.TemplateName);

        if (string.IsNullOrWhiteSpace(responderPhoneE164))
        {
            responderPhoneE164 = FirstNonEmpty(interaction?.CustomerPhone, interaction?.RecipientE164, execution?.RecipientPhoneE164, interaction?.PhoneKey, execution?.PhoneKey);
            responderPhoneE164 = MetaWhatsAppPhoneCanonicalizer.ToCanonicalE164Br(responderPhoneE164);
            responderPhoneLocal = MetaWhatsAppPhoneCanonicalizer.ToBrLocalDigits(responderPhoneE164);
        }

        if (string.Equals(flowOperation.TipoSolicitacao, FaleConoscoTipoSolicitacao, StringComparison.OrdinalIgnoreCase))
        {
            return await ProcessFaleConoscoAsync(
                request,
                traceId,
                now,
                flowOperation,
                interaction,
                execution,
                responderPhoneE164,
                responderPhoneLocal,
                resolvedInteractionId,
                resolvedCustomerId,
                resolvedCustomerName,
                resolvedFlowKey,
                resolvedFlowName);
        }

        var normalizedOption = NormalizeResponseOption(request.ResponseOption);
        if (normalizedOption is null)
        {
            await WriteAuditEventAsync("FLOW_PREFERENCIA_VALIDACAO_FALHOU", now, request.ResponderPhoneE164 ?? request.ResponderPhone ?? request.ResponderPhoneLocal, new Dictionary<string, string?>
            {
                ["trace_id"] = traceId,
                ["reason"] = "invalid_response_option",
                ["response_option"] = request.ResponseOption
            });
            return ErrorResult(StatusCodes.Status422UnprocessableEntity, "validation_error", "invalid_response_option", "response_option inválido.", traceId, new
            {
                allowed_values = new[] { "PARAR_SEM_SUBSTITUTO", "PARAR_COM_SUBSTITUTO", "CONTINUAR_COM_SUBSTITUTO" }
            });
        }

        var actions = BuildActions(normalizedOption);
        var requiresNewContact = actions.ShouldInsertNewContact;

        var validatedNewContact = ValidateNewContact(request, requiresNewContact);
        if (!validatedNewContact.Success)
        {
            await WriteAuditEventAsync("FLOW_PREFERENCIA_VALIDACAO_FALHOU", now, responderPhoneE164, new Dictionary<string, string?>
            {
                ["trace_id"] = traceId,
                ["reason"] = validatedNewContact.ErrorCode
            });
            return ErrorResult(StatusCodes.Status422UnprocessableEntity, "validation_error", validatedNewContact.ErrorCode!, validatedNewContact.Message!, traceId, validatedNewContact.Details);
        }

        var payload = new WhatsAppFlowPreferencesSubmitResponse
        {
            Ok = true,
            Status = "ok",
            TraceId = traceId,
            ResolvedContext = new WhatsAppFlowResolvedContext
            {
                ExecutionId = execution?.ExecutionId ?? request.ExecutionId,
                InteractionId = resolvedInteractionId,
                CustomerId = resolvedCustomerId,
                CustomerName = resolvedCustomerName,
                FlowKey = resolvedFlowKey,
                FlowName = resolvedFlowName,
                WorkflowName = resolvedWorkflowName,
                WebhookName = resolvedWebhookName,
                TemplateName = resolvedTemplateName
            },
            Responder = new WhatsAppFlowResponder
            {
                PhoneE164 = responderPhoneE164,
                PhoneLocalDigits = responderPhoneLocal
            },
            Selection = new WhatsAppFlowSelection
            {
                ResponseOption = normalizedOption,
                ResponseOptionLabel = BuildResponseOptionLabel(normalizedOption)
            },
            NewContact = new WhatsAppFlowNewContact
            {
                Name = validatedNewContact.Name,
                Email = validatedNewContact.Email,
                PhoneLocalDigits = validatedNewContact.PhoneLocalDigits
            },
            Actions = actions,
            DownstreamSqlHints = new WhatsAppFlowSqlHints
            {
                CustomerIdForSql = resolvedCustomerId,
                LookupNextNrSequencia = actions.ShouldInsertNewContact,
                LookupCdFuncaoFromOriginalContact = actions.ShouldInsertNewContact
            },
            Audit = new WhatsAppFlowAudit
            {
                ReceivedAt = request.Timestamp ?? now,
                CorrelationStrategy = contextResolution.CorrelationStrategy,
                Matched = contextResolution.Matched
            },
            Raw = BuildRawSummary(request)
        };

        await WriteAuditEventAsync("FLOW_PREFERENCIA_RESPONSE_NORMALIZADA", now, responderPhoneE164, new Dictionary<string, string?>
        {
            ["trace_id"] = traceId,
            ["interaction_id"] = resolvedInteractionId,
            ["customer_id"] = resolvedCustomerId?.ToString(),
            ["correlation_strategy"] = contextResolution.CorrelationStrategy,
            ["response_option"] = normalizedOption,
            ["insert_new_contact"] = actions.ShouldInsertNewContact ? "true" : "false"
        });

        return new WhatsAppFlowPreferencesServiceResult(StatusCodes.Status200OK, payload);
    }

    private async Task<WhatsAppFlowPreferencesServiceResult> ProcessFaleConoscoAsync(
        WhatsAppFlowPreferencesSubmitRequest request,
        string traceId,
        DateTimeOffset now,
        FlowOperationMetadata flowOperation,
        InteractionContext? interaction,
        ExecutionContext? execution,
        string? responderPhoneE164,
        string? responderPhoneLocal,
        string? resolvedInteractionId,
        int? resolvedCustomerId,
        string? resolvedCustomerName,
        string? resolvedFlowKey,
        string? resolvedFlowName)
    {
        var webhookPayload = new
        {
            trace_id = traceId,
            interaction_id = resolvedInteractionId,
            execution_id = execution?.ExecutionId ?? request.ExecutionId,
            flow_key = resolvedFlowKey,
            flow_name = resolvedFlowName,
            customer_id = resolvedCustomerId,
            cd_cliente = resolvedCustomerId,
            customer_name = resolvedCustomerName,
            customer_document = interaction?.CustomerDocument,
            responder_phone = FirstNonEmpty(request.ResponderPhone, request.ResponderPhoneLocal, responderPhoneLocal, responderPhoneE164),
            responder_phone_e164 = responderPhoneE164,
            phone_key = FirstNonEmpty(interaction?.PhoneKey, execution?.PhoneKey, responderPhoneE164),
            tipo_solicitacao = flowOperation.TipoSolicitacao,
            contato_escolhido = flowOperation.ContatoEscolhido,
            setor_nome = flowOperation.SetorNome,
            setor_telefone = flowOperation.SetorTelefone,
            meta_message_id = request.MetaMessageId,
            wamid = request.Wamid,
            raw_payload = request.RawPayload,
            raw_body = request.RawBody,
            contexto_resolvido = new
            {
                customer_phone = interaction?.CustomerPhone,
                recipient_phone = FirstNonEmpty(interaction?.RecipientE164, execution?.RecipientPhoneE164),
                correlation = new
                {
                    interaction_id = resolvedInteractionId,
                    execution_id = execution?.ExecutionId ?? request.ExecutionId
                },
                source = "meta_whatsapp_flow_preferences"
            },
            timestamp = request.Timestamp ?? now
        };

        await WriteAuditEventAsync("FLOW_FALE_CONOSCO_ENCAMINHAMENTO_INICIADO", now, responderPhoneE164, new Dictionary<string, string?>
        {
            ["trace_id"] = traceId,
            ["interaction_id"] = resolvedInteractionId,
            ["execution_id"] = execution?.ExecutionId ?? request.ExecutionId,
            ["contato_escolhido"] = flowOperation.ContatoEscolhido,
            ["setor_nome"] = flowOperation.SetorNome
        });

        try
        {
            var client = _httpClientFactory.CreateClient("MetaWhatsAppN8nForwarder");
            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, FaleConoscoWebhookUrl)
            {
                Content = JsonContent.Create(webhookPayload)
            };
            var authType = ApplyWebhookAuthentication(requestMessage);
            using var response = await client.SendAsync(requestMessage);
            var responseText = await response.Content.ReadAsStringAsync();
            var responsePreview = string.IsNullOrWhiteSpace(responseText)
                ? null
                : responseText[..Math.Min(responseText.Length, 400)];

            if (!response.IsSuccessStatusCode)
            {
                await WriteAuditEventAsync("FLOW_FALE_CONOSCO_ENCAMINHAMENTO_FALHOU", now, responderPhoneE164, new Dictionary<string, string?>
                {
                    ["trace_id"] = traceId,
                    ["auth_type"] = authType.ToString(),
                    ["http_status"] = ((int)response.StatusCode).ToString(),
                    ["response_preview"] = responsePreview
                });

                return ErrorResult(
                    StatusCodes.Status502BadGateway,
                    "downstream_error",
                    "fale_conosco_webhook_failed",
                    "Falha ao encaminhar o fluxo Fale com a IBR para o webhook de destino.",
                    traceId,
                    new
                    {
                        webhook_url = FaleConoscoWebhookUrl,
                        downstream_status = (int)response.StatusCode,
                        downstream_response_preview = responsePreview
                    });
            }

            await WriteAuditEventAsync("FLOW_FALE_CONOSCO_ENCAMINHADO", now, responderPhoneE164, new Dictionary<string, string?>
            {
                ["trace_id"] = traceId,
                ["auth_type"] = authType.ToString(),
                ["http_status"] = ((int)response.StatusCode).ToString(),
                ["interaction_id"] = resolvedInteractionId
            });

            var payload = new WhatsAppFlowPreferencesSubmitResponse
            {
                Ok = true,
                Status = "ok",
                TraceId = traceId,
                ResolvedContext = new WhatsAppFlowResolvedContext
                {
                    InteractionId = resolvedInteractionId,
                    ExecutionId = execution?.ExecutionId ?? request.ExecutionId,
                    CustomerId = resolvedCustomerId,
                    CustomerName = resolvedCustomerName,
                    FlowKey = resolvedFlowKey,
                    FlowName = resolvedFlowName
                },
                Responder = new WhatsAppFlowResponder
                {
                    PhoneE164 = responderPhoneE164,
                    PhoneLocalDigits = responderPhoneLocal
                },
                Selection = new WhatsAppFlowSelection
                {
                    ResponseOption = FaleConoscoTipoSolicitacao,
                    ResponseOptionLabel = "Fale com a IBR"
                },
                NewContact = new WhatsAppFlowNewContact(),
                Actions = new WhatsAppFlowActions
                {
                    ShouldApplyOptOutUpdates = false,
                    ShouldInsertNewContact = false,
                    ShouldCreatePiperunRecords = false
                },
                DownstreamSqlHints = new WhatsAppFlowSqlHints
                {
                    CustomerIdForSql = resolvedCustomerId,
                    LookupNextNrSequencia = false,
                    LookupCdFuncaoFromOriginalContact = false
                },
                Audit = new WhatsAppFlowAudit
                {
                    ReceivedAt = request.Timestamp ?? now,
                    CorrelationStrategy = "fale_conosco",
                    Matched = resolvedCustomerId is not null
                },
                Raw = new
                {
                    operation = "fale_conosco_forwarded",
                    webhook_url = FaleConoscoWebhookUrl,
                    auth_type = authType.ToString(),
                    tipo_solicitacao = flowOperation.TipoSolicitacao,
                    contato_escolhido = flowOperation.ContatoEscolhido,
                    setor_nome = flowOperation.SetorNome,
                    setor_telefone = flowOperation.SetorTelefone,
                    raw_payload = BuildSafeRawPayloadSummary(request.RawPayload),
                    raw_body = request.RawBody,
                    meta_message_id = request.MetaMessageId,
                    wamid = request.Wamid
                }
            };

            return new WhatsAppFlowPreferencesServiceResult(StatusCodes.Status200OK, payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao encaminhar Flow Fale com a IBR. TraceId={TraceId}", traceId);
            await WriteAuditEventAsync("FLOW_FALE_CONOSCO_ENCAMINHAMENTO_ERRO", now, responderPhoneE164, new Dictionary<string, string?>
            {
                ["trace_id"] = traceId,
                ["exception"] = ex.GetType().Name
            });
            return ErrorResult(
                StatusCodes.Status502BadGateway,
                "downstream_error",
                "fale_conosco_webhook_exception",
                "Erro inesperado ao encaminhar o fluxo Fale com a IBR para o webhook de destino.",
                traceId,
                new
                {
                    webhook_url = FaleConoscoWebhookUrl
                });
        }
    }

    private ContextResolutionResult ResolveContext(WhatsAppFlowPreferencesSubmitRequest request, string? responderPhoneE164, DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(request.ExecutionId)
            && _interactionRouter.TryGetExecutionContext(request.ExecutionId, out var executionContext)
            && executionContext is not null)
        {
            _interactionRouter.TryGetInteractionById(executionContext.InteractionId, now, out var interactionByExecution);
            return ContextResolutionResult.SuccessResult("execution_id", true, interactionByExecution, executionContext);
        }

        var hasExecutionId = !string.IsNullOrWhiteSpace(request.ExecutionId);

        if (!string.IsNullOrWhiteSpace(request.InteractionId))
        {
            if (_interactionRouter.TryGetInteractionById(request.InteractionId, now, out var interactionById) && interactionById is not null)
            {
                return ContextResolutionResult.SuccessResult("interaction_id", true, interactionById, null);
            }

            return ContextResolutionResult.Error(
                StatusCodes.Status404NotFound,
                "not_found",
                "interaction_not_found",
                "interaction_id informado não foi encontrado.",
                "FLOW_PREFERENCIA_VALIDACAO_FALHOU",
                new { interaction_id = request.InteractionId });
        }

        if (hasExecutionId)
        {
            return ContextResolutionResult.Error(
                StatusCodes.Status404NotFound,
                "not_found",
                "execution_context_not_found",
                "execution_id informado não foi encontrado.",
                "FLOW_PREFERENCIA_VALIDACAO_FALHOU",
                new { execution_id = request.ExecutionId });
        }

        if (string.IsNullOrWhiteSpace(responderPhoneE164))
        {
            return ContextResolutionResult.Error(
                StatusCodes.Status422UnprocessableEntity,
                "validation_error",
                "missing_context_identifier",
                "Informe execution_id, interaction_id ou telefone do respondente para correlação.",
                "FLOW_PREFERENCIA_VALIDACAO_FALHOU",
                null);
        }

        var byPhone = _interactionRouter.GetActiveInteractionsByPhone(responderPhoneE164, now);
        if (byPhone.Count == 1)
        {
            return ContextResolutionResult.SuccessResult("phone_unique_match", true, byPhone[0], null);
        }

        if (byPhone.Count > 1)
        {
            return ContextResolutionResult.Error(
                StatusCodes.Status409Conflict,
                "conflict",
                "ambiguous_phone_match",
                "Telefone associado a mais de uma interação ativa. Informe execution_id ou interaction_id.",
                "FLOW_PREFERENCIA_AMBIGUO",
                new
                {
                    responder_phone_e164 = responderPhoneE164,
                    matched_interactions = byPhone.Select(static item => item.InteractionId).ToArray()
                });
        }

        return ContextResolutionResult.Error(
            StatusCodes.Status404NotFound,
            "not_found",
            "context_not_found_by_phone",
            "Nenhuma interação ativa encontrada para o telefone informado.",
            "FLOW_PREFERENCIA_VALIDACAO_FALHOU",
            new { responder_phone_e164 = responderPhoneE164 });
    }

    private static FlowNewContactValidationResult ValidateNewContact(WhatsAppFlowPreferencesSubmitRequest request, bool required)
    {
        var name = request.NewContactName?.Trim();
        var email = request.NewContactEmail?.Trim();
        var phoneE164 = MetaWhatsAppPhoneCanonicalizer.ToCanonicalE164Br(request.NewContactPhone);
        var phoneLocal = MetaWhatsAppPhoneCanonicalizer.ToBrLocalDigits(phoneE164);

        if (!required)
        {
            return FlowNewContactValidationResult.Ok(null, null, null);
        }

        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(name))
        {
            errors["new_contact_name"] = ["new_contact_name é obrigatório para a opção selecionada."];
        }

        var emailValidator = new EmailAddressAttribute();
        if (string.IsNullOrWhiteSpace(email) || !emailValidator.IsValid(email))
        {
            errors["new_contact_email"] = ["new_contact_email é obrigatório e deve ser válido."];
        }

        if (string.IsNullOrWhiteSpace(phoneLocal))
        {
            errors["new_contact_phone"] = ["new_contact_phone é obrigatório e deve ser um telefone válido."];
        }

        if (errors.Count > 0)
        {
            return FlowNewContactValidationResult.Fail(
                "new_contact_required_fields_missing",
                "Dados obrigatórios do novo contato ausentes ou inválidos para a opção selecionada.",
                errors);
        }

        return FlowNewContactValidationResult.Ok(name, email, phoneLocal);
    }

    private static object BuildRawSummary(WhatsAppFlowPreferencesSubmitRequest request)
    {
        return new
        {
            meta_message_id = request.MetaMessageId,
            wamid = request.Wamid,
            raw_payload = BuildSafeRawPayloadSummary(request.RawPayload),
            received_timestamp = request.Timestamp
        };
    }

    private static object? BuildSafeRawPayloadSummary(JsonElement? rawPayload)
    {
        if (rawPayload is null)
        {
            return null;
        }

        var compact = JsonSerializer.Serialize(rawPayload.Value);
        var payloadType = rawPayload.Value.ValueKind.ToString();
        var totalLength = compact.Length;

        if (totalLength == 0)
        {
            return new
            {
                payload_type = payloadType,
                character_count = 0,
                preview = string.Empty,
                is_truncated = false
            };
        }

        var previewLength = Math.Min(120, Math.Max(1, totalLength - 1));
        var preview = $"{compact[..previewLength]}...[preview]";

        return new
        {
            payload_type = payloadType,
            character_count = totalLength,
            preview,
            is_truncated = previewLength < totalLength
        };
    }

    private static WhatsAppFlowActions BuildActions(string option)
    {
        return option switch
        {
            "PARAR_SEM_SUBSTITUTO" => new WhatsAppFlowActions
            {
                ShouldApplyOptOutUpdates = true,
                ShouldInsertNewContact = false,
                ShouldCreatePiperunRecords = true
            },
            "PARAR_COM_SUBSTITUTO" => new WhatsAppFlowActions
            {
                ShouldApplyOptOutUpdates = true,
                ShouldInsertNewContact = true,
                ShouldCreatePiperunRecords = true
            },
            "CONTINUAR_COM_SUBSTITUTO" => new WhatsAppFlowActions
            {
                ShouldApplyOptOutUpdates = false,
                ShouldInsertNewContact = true,
                ShouldCreatePiperunRecords = true
            },
            _ => throw new InvalidOperationException("Opção de resposta inválida.")
        };
    }

    private static string BuildResponseOptionLabel(string option)
        => option switch
        {
            "PARAR_SEM_SUBSTITUTO" => "Parar sem substituto",
            "PARAR_COM_SUBSTITUTO" => "Parar com substituto",
            "CONTINUAR_COM_SUBSTITUTO" => "Continuar com substituto",
            _ => option
        };

    private static string? NormalizeResponseOption(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var normalized = raw.Trim().ToUpperInvariant();
        return normalized switch
        {
            "PARAR_SEM_SUBSTITUTO" => normalized,
            "PARAR_COM_SUBSTITUTO" => normalized,
            "CONTINUAR_COM_SUBSTITUTO" => normalized,
            _ => null
        };
    }

    private static FlowOperationMetadata ResolveFlowOperation(WhatsAppFlowPreferencesSubmitRequest request)
    {
        var rawPayload = request.RawPayload;
        var tipoSolicitacao = FirstNonEmpty(
            request.TipoSolicitacao,
            GetJsonString(rawPayload, "tipo_solicitacao"),
            GetJsonString(rawPayload, "response_json", "tipo_solicitacao"),
            GetJsonString(rawPayload, "interactive", "nfm_reply", "response_json", "tipo_solicitacao"),
            FindJsonStringDeep(rawPayload, "tipo_solicitacao"));
        var contatoEscolhido = FirstNonEmpty(
            request.ContatoEscolhido,
            GetJsonString(rawPayload, "contato_escolhido"),
            GetJsonString(rawPayload, "response_json", "contato_escolhido"),
            GetJsonString(rawPayload, "interactive", "nfm_reply", "response_json", "contato_escolhido"),
            FindJsonStringDeep(rawPayload, "contato_escolhido"));
        var setorNome = FirstNonEmpty(
            request.SetorNome,
            GetJsonString(rawPayload, "setor_nome"),
            GetJsonString(rawPayload, "response_json", "setor_nome"),
            GetJsonString(rawPayload, "interactive", "nfm_reply", "response_json", "setor_nome"),
            FindJsonStringDeep(rawPayload, "setor_nome"));
        var setorTelefone = FirstNonEmpty(
            request.SetorTelefone,
            GetJsonString(rawPayload, "setor_telefone"),
            GetJsonString(rawPayload, "response_json", "setor_telefone"),
            GetJsonString(rawPayload, "interactive", "nfm_reply", "response_json", "setor_telefone"),
            FindJsonStringDeep(rawPayload, "setor_telefone"));

        return new FlowOperationMetadata(tipoSolicitacao, contatoEscolhido, setorNome, setorTelefone);
    }

    private static string? GetJsonString(JsonElement? root, params string[] path)
    {
        if (root is null || path.Length == 0)
        {
            return null;
        }

        var current = root.Value;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        if (current.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = current.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? FindJsonStringDeep(JsonElement? root, string targetPropertyName)
    {
        if (root is null || string.IsNullOrWhiteSpace(targetPropertyName))
        {
            return null;
        }

        return FindJsonStringDeepCore(root.Value, targetPropertyName.Trim(), depth: 0, maxDepth: 10);
    }

    private static string? FindJsonStringDeepCore(JsonElement element, string targetPropertyName, int depth, int maxDepth)
    {
        if (depth > maxDepth)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, targetPropertyName, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value.Trim();
                    }
                }

                var nested = FindJsonStringDeepCore(property.Value, targetPropertyName, depth + 1, maxDepth);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindJsonStringDeepCore(item, targetPropertyName, depth + 1, maxDepth);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private N8nWebhookAuthType ApplyWebhookAuthentication(HttpRequestMessage requestMessage)
    {
        var resolved = _webhookSecurityOptions.Value.ResolveFor(N8nWebhookRouteType.Flow);

        switch (resolved.AuthType)
        {
            case N8nWebhookAuthType.HeaderAuth:
            {
                if (!string.IsNullOrWhiteSpace(resolved.HeaderName)
                    && !string.IsNullOrWhiteSpace(resolved.HeaderValue))
                {
                    requestMessage.Headers.Remove(resolved.HeaderName);
                    requestMessage.Headers.TryAddWithoutValidation(resolved.HeaderName, resolved.HeaderValue);
                    return N8nWebhookAuthType.HeaderAuth;
                }

                return N8nWebhookAuthType.None;
            }
            case N8nWebhookAuthType.BasicAuth:
            {
                if (!string.IsNullOrWhiteSpace(resolved.BasicAuthUsername)
                    && !string.IsNullOrWhiteSpace(resolved.BasicAuthPassword))
                {
                    var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{resolved.BasicAuthUsername}:{resolved.BasicAuthPassword}"));
                    requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
                    return N8nWebhookAuthType.BasicAuth;
                }

                return N8nWebhookAuthType.None;
            }
            default:
                return N8nWebhookAuthType.None;
        }
    }

    private static WhatsAppFlowPreferencesServiceResult ErrorResult(int statusCode, string errorType, string errorCode, string message, string traceId, object? details)
    {
        var payload = new
        {
            ok = false,
            status = "error",
            error_type = errorType,
            error_code = errorCode,
            message,
            details,
            trace_id = traceId
        };

        return new WhatsAppFlowPreferencesServiceResult(statusCode, payload);
    }

    private async Task WriteAuditEventAsync(string eventName, DateTimeOffset now, string? phone, IReadOnlyDictionary<string, string?> fields)
    {
        try
        {
            await _persistentLogService.AppendEventAsync(phone, eventName, now, fields);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao gravar trilha de auditoria de flow preferences. EventName={EventName}", eventName);
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private sealed record ContextResolutionResult(
        bool Success,
        int StatusCode,
        string? ErrorType,
        string? ErrorCode,
        string? Message,
        string? AuditEventName,
        object? Details,
        string CorrelationStrategy,
        bool Matched,
        InteractionContext? Interaction,
        ExecutionContext? ExecutionContext)
    {
        public static ContextResolutionResult SuccessResult(string strategy, bool matched, InteractionContext? interaction, ExecutionContext? execution)
            => new(true, StatusCodes.Status200OK, null, null, null, null, null, strategy, matched, interaction, execution);

        public static ContextResolutionResult Error(int statusCode, string errorType, string errorCode, string message, string auditEventName, object? details)
            => new(false, statusCode, errorType, errorCode, message, auditEventName, details, "none", false, null, null);
    }

    private sealed record FlowNewContactValidationResult(bool Success, string? ErrorCode, string? Message, object? Details, string? Name, string? Email, string? PhoneLocalDigits)
    {
        public static FlowNewContactValidationResult Ok(string? name, string? email, string? phoneLocalDigits)
            => new(true, null, null, null, name, email, phoneLocalDigits);

        public static FlowNewContactValidationResult Fail(string errorCode, string message, object details)
            => new(false, errorCode, message, details, null, null, null);
    }

    private sealed record FlowOperationMetadata(
        string? TipoSolicitacao,
        string? ContatoEscolhido,
        string? SetorNome,
        string? SetorTelefone);
}
