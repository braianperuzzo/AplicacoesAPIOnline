using System.Text.Json;
using AplicacoesOnline.Models.MetaWhatsApp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AplicacoesOnline.Services.MetaWhatsApp;

public sealed class FlowInboundContractResolver
{
    private const string RouteApiKeyPlaceholder = "oSpcMQJ1535G";
    private const string ContractFaleConosco = "FALE_CONOSCO";
    private const string ContractPreferenciasContato = "PREFERENCIAS_CONTATO";
    private const string ContractPesquisaPosVendas = "PESQUISA_POS_VENDAS";
    private const string TipoSolicitacaoPesquisaSatisfacao = "PESQUISA_SATISFACAO";
    private const string CompletionIntentPesquisaPosVendas = "PESQUISA_POS_VENDAS";
    private const string CompletionIntentPreferenciasContato = "PREFERENCIAS_CONTATO";
    private const string PesquisaPosVendasFlowName = "pesquisa_atendimento_pos_vendas";
    private const string PesquisaPosVendasFlowId = "930667013107269";

    private static readonly string[] PesquisaCamposObrigatorios =
    {
        "pergunta_um",
        "resposta_pergunta_um",
        "pergunta_dois",
        "resposta_pergunta_dois",
        "nota_atendimento",
        "nota_recomendacao"
    };

    private static readonly string[] PreferenciasContatoCamposIndicativos =
    {
        "contato_escolhido",
        "setor_nome",
        "setor_telefone",
        "canal_preferido",
        "periodo_contato",
        "aceita_whatsapp",
        "aceita_ligacao",
        "aceita_email"
    };

    private readonly IReadOnlyDictionary<string, FlowContractRouteOverrideOptions> _contractOverrides;
    private readonly string? _defaultWebhookApiKey;

    public FlowInboundContractResolver(IOptions<MetaWhatsAppWebhookOptions> options, IConfiguration configuration)
    {
        _contractOverrides = options.Value.FlowContractRouteOverrides;
        _defaultWebhookApiKey = Sanitize(configuration["Security:ApiKey"]);
    }

    public FlowContractResolution Resolve(
        bool isFlowReply,
        string? interactionCompletionIntent,
        string? interactionFlowName,
        string? interactionFlowKey,
        string? interactionTemplateName,
        string? interactionMessageName,
        JsonElement? flowResponseJson)
    {
        var parsedFlowResponse = ResolveFlowResponseObject(flowResponseJson);
        var flowReplyJsonValid = parsedFlowResponse.HasValue;
        var flowSubtipoDetectado = ResolveFlowTipoSolicitacao(parsedFlowResponse);
        var flowCompletionIntentDetectado = ResolveCompletionIntent(interactionCompletionIntent, parsedFlowResponse);
        var flowIdDetectado = ResolveFlowStringField(parsedFlowResponse, "flow_id");
        var flowNameDetectado = FirstNonEmpty(interactionFlowName, ResolveFlowStringField(parsedFlowResponse, "flow_name"));
        var flowContractDetected = "UNKNOWN";
        var contractRecognized = false;
        var handled = false;
        var reason = "no_contract_match";
        string? routeOverrideUrl = null;
        string? routeOverrideApiKey = null;
        string? routeOverrideAuthSource = null;
        var contractRouteConfigValid = true;
        string? contractRouteConfigIssue = null;
        var routeUrlPresent = false;
        var routeApiKeyPresent = false;
        var authSourcePresent = false;
        var decisionReasonPresent = false;

        if (!isFlowReply)
        {
            return new FlowContractResolution(
                isFlowReply,
                flowReplyJsonValid,
                parsedFlowResponse,
                flowSubtipoDetectado,
                flowContractDetected,
                flowCompletionIntentDetectado,
                flowIdDetectado,
                flowNameDetectado,
                false,
                null,
                null,
                null,
                "not_flow_reply",
                true,
                null,
                false,
                false,
                false,
                false,
                false,
                false);
        }

        if (!flowReplyJsonValid)
        {
            return new FlowContractResolution(
                true,
                false,
                null,
                flowSubtipoDetectado,
                flowContractDetected,
                flowCompletionIntentDetectado,
                flowIdDetectado,
                flowNameDetectado,
                false,
                null,
                null,
                null,
                "invalid_flow_response_json",
                true,
                null,
                false,
                false,
                false,
                false,
                false,
                false);
        }

        if (string.Equals(flowSubtipoDetectado, ContractFaleConosco, StringComparison.OrdinalIgnoreCase))
        {
            flowContractDetected = ContractFaleConosco;
            contractRecognized = true;
            reason = "tipo_solicitacao_fale_conosco";
            ResolveContractRoute(flowContractDetected, reason, out routeOverrideUrl, out routeOverrideApiKey, out routeOverrideAuthSource, out reason, out contractRouteConfigValid, out contractRouteConfigIssue, out routeUrlPresent, out routeApiKeyPresent, out authSourcePresent, out decisionReasonPresent);
        }
        else if (IsPesquisaContract(flowSubtipoDetectado, flowCompletionIntentDetectado, flowNameDetectado, flowIdDetectado, interactionMessageName, interactionTemplateName, interactionFlowKey, parsedFlowResponse))
        {
            flowContractDetected = ContractPesquisaPosVendas;
            contractRecognized = true;
            reason = "pesquisa_contract";
            ResolveContractRoute(flowContractDetected, reason, out routeOverrideUrl, out routeOverrideApiKey, out routeOverrideAuthSource, out reason, out contractRouteConfigValid, out contractRouteConfigIssue, out routeUrlPresent, out routeApiKeyPresent, out authSourcePresent, out decisionReasonPresent);
        }
        else if (IsPreferenciasContatoContract(flowSubtipoDetectado, flowCompletionIntentDetectado, parsedFlowResponse))
        {
            flowContractDetected = ContractPreferenciasContato;
            contractRecognized = true;
            reason = "preferencias_contato_contract";
            ResolveContractRoute(flowContractDetected, reason, out routeOverrideUrl, out routeOverrideApiKey, out routeOverrideAuthSource, out reason, out contractRouteConfigValid, out contractRouteConfigIssue, out routeUrlPresent, out routeApiKeyPresent, out authSourcePresent, out decisionReasonPresent);
        }
        else
        {
            reason = "unknown_contract";
        }

        handled = contractRecognized && contractRouteConfigValid;

        if (contractRecognized && !contractRouteConfigValid)
        {
            reason = "invalid_flow_contract_route_config";
        }

        return new FlowContractResolution(
            isFlowReply,
            flowReplyJsonValid,
            parsedFlowResponse,
            flowSubtipoDetectado,
            flowContractDetected,
            flowCompletionIntentDetectado,
            flowIdDetectado,
            flowNameDetectado,
            handled,
            routeOverrideUrl,
            routeOverrideApiKey,
            routeOverrideAuthSource,
            reason,
            contractRouteConfigValid,
            contractRouteConfigIssue,
            contractRecognized,
            routeUrlPresent,
            routeApiKeyPresent,
            authSourcePresent,
            decisionReasonPresent,
            contractRecognized && !contractRouteConfigValid);
    }

    private void ResolveContractRoute(
        string contractName,
        string defaultReason,
        out string? routeOverrideUrl,
        out string? routeOverrideApiKey,
        out string? routeOverrideAuthSource,
        out string finalReason,
        out bool contractRouteConfigValid,
        out string? contractRouteConfigIssue,
        out bool routeUrlPresent,
        out bool routeApiKeyPresent,
        out bool authSourcePresent,
        out bool decisionReasonPresent)
    {
        routeOverrideUrl = null;
        routeOverrideApiKey = null;
        routeOverrideAuthSource = null;
        finalReason = defaultReason;
        contractRouteConfigValid = false;
        contractRouteConfigIssue = "no_contract_match";
        routeUrlPresent = false;
        routeApiKeyPresent = false;
        authSourcePresent = false;
        decisionReasonPresent = false;

        if (!_contractOverrides.TryGetValue(contractName, out var configuredOverride))
        {
            finalReason = "invalid_flow_contract_route_config";
            contractRouteConfigIssue = "missing_route_url";
            return;
        }

        routeOverrideUrl = Sanitize(configuredOverride.RouteUrl);
        routeOverrideApiKey = ResolveRouteApiKey(configuredOverride.RouteApiKey);
        routeOverrideAuthSource = FirstNonEmpty(Sanitize(configuredOverride.RouteOverrideAuthSource), $"flow_contract.{contractName}.route_api_key");
        var configuredDecisionReason = Sanitize(configuredOverride.DecisionReason);
        finalReason = FirstNonEmpty(configuredDecisionReason, defaultReason) ?? defaultReason;
        routeUrlPresent = !string.IsNullOrWhiteSpace(routeOverrideUrl);
        routeApiKeyPresent = !string.IsNullOrWhiteSpace(routeOverrideApiKey);
        authSourcePresent = !string.IsNullOrWhiteSpace(routeOverrideAuthSource);
        decisionReasonPresent = !string.IsNullOrWhiteSpace(configuredDecisionReason);

        if (!routeUrlPresent)
        {
            contractRouteConfigIssue = "missing_route_url";
            finalReason = "invalid_flow_contract_route_config";
            return;
        }

        if (!routeApiKeyPresent)
        {
            contractRouteConfigIssue = "missing_route_api_key";
            finalReason = "invalid_flow_contract_route_config";
            return;
        }

        if (!authSourcePresent)
        {
            contractRouteConfigIssue = "missing_auth_source";
            finalReason = "invalid_flow_contract_route_config";
            return;
        }

        if (!decisionReasonPresent)
        {
            contractRouteConfigIssue = "missing_decision_reason";
            finalReason = "invalid_flow_contract_route_config";
            return;
        }

        contractRouteConfigValid = true;
        contractRouteConfigIssue = null;
    }

    private static string? Sanitize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private string? ResolveRouteApiKey(string? configuredRouteApiKey)
    {
        var sanitized = Sanitize(configuredRouteApiKey);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return null;
        }

        return string.Equals(sanitized, RouteApiKeyPlaceholder, StringComparison.OrdinalIgnoreCase)
            ? _defaultWebhookApiKey
            : sanitized;
    }

    private static bool IsPreferenciasContatoContract(string? subtipo, string? completionIntent, JsonElement? flowResponseObject)
    {
        if (string.Equals(subtipo, ContractPreferenciasContato, StringComparison.OrdinalIgnoreCase)
            || string.Equals(completionIntent, CompletionIntentPreferenciasContato, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!flowResponseObject.HasValue || flowResponseObject.Value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var field in PreferenciasContatoCamposIndicativos)
        {
            if (!string.IsNullOrWhiteSpace(ResolveStringProperty(flowResponseObject.Value, field)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPesquisaContract(
        string? subtipo,
        string? completionIntent,
        string? flowName,
        string? flowId,
        string? messageName,
        string? templateName,
        string? flowKey,
        JsonElement? flowResponseObject)
    {
        var isPesquisaTipo = string.Equals(subtipo, TipoSolicitacaoPesquisaSatisfacao, StringComparison.OrdinalIgnoreCase);
        var isPesquisaCompletionIntent = string.Equals(completionIntent, CompletionIntentPesquisaPosVendas, StringComparison.OrdinalIgnoreCase);
        var isPesquisaFlowIdentity = IsPesquisaPosVendasFlow(flowName, flowId, messageName, templateName, flowKey);
        var hasContractEvidence = HasRequiredFields(flowResponseObject, PesquisaCamposObrigatorios);

        return isPesquisaTipo && (isPesquisaCompletionIntent || isPesquisaFlowIdentity || hasContractEvidence);
    }

    private static bool IsPesquisaPosVendasFlow(
        string? flowName,
        string? flowId,
        string? messageName,
        string? templateName,
        string? flowKey)
    {
        if (!string.IsNullOrWhiteSpace(flowName)
            && flowName.Contains(PesquisaPosVendasFlowName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(flowId)
            && string.Equals(flowId, PesquisaPosVendasFlowId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(messageName)
            && messageName.Contains("pesquisa", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(templateName)
            && templateName.Contains("pesquisa", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(flowKey)
            && flowKey.Contains("pesquisa", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasRequiredFields(JsonElement? payload, IReadOnlyCollection<string> fields)
    {
        if (!payload.HasValue || payload.Value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return fields.All(field => !string.IsNullOrWhiteSpace(ResolveStringProperty(payload.Value, field)));
    }

    private static string? ResolveFlowTipoSolicitacao(JsonElement? flowResponseObject)
    {
        if (!flowResponseObject.HasValue)
        {
            return null;
        }

        return ResolveStringProperty(flowResponseObject.Value, "tipo_solicitacao");
    }

    private static string? ResolveCompletionIntent(string? interactionCompletionIntent, JsonElement? flowResponseObject)
    {
        if (!string.IsNullOrWhiteSpace(interactionCompletionIntent))
        {
            return interactionCompletionIntent.Trim().ToUpperInvariant();
        }

        if (!flowResponseObject.HasValue || flowResponseObject.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var aliases = new[] { "completion_intent", "flow_completion_profile", "business_intent" };
        foreach (var alias in aliases)
        {
            var value = ResolveStringProperty(flowResponseObject.Value, alias);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? ResolveFlowStringField(JsonElement? flowResponseObject, string fieldName)
    {
        if (!flowResponseObject.HasValue)
        {
            return null;
        }

        return ResolveStringProperty(flowResponseObject.Value, fieldName);
    }

    private static JsonElement? ResolveFlowResponseObject(JsonElement? flowResponseJson)
    {
        if (!flowResponseJson.HasValue)
        {
            return null;
        }

        return ResolveFlowResponseObject(flowResponseJson.Value);
    }

    private static JsonElement? ResolveFlowResponseObject(JsonElement flowResponseJson)
    {
        if (flowResponseJson.ValueKind == JsonValueKind.Object)
        {
            if (flowResponseJson.TryGetProperty("response_json", out var nestedResponseJson))
            {
                var nestedObject = ResolveFlowResponseObject(nestedResponseJson);
                if (nestedObject.HasValue)
                {
                    return nestedObject;
                }
            }

            return flowResponseJson.Clone();
        }

        if (flowResponseJson.ValueKind == JsonValueKind.String)
        {
            var jsonText = flowResponseJson.GetString();
            if (string.IsNullOrWhiteSpace(jsonText))
            {
                return null;
            }

            try
            {
                using var parsed = JsonDocument.Parse(jsonText);
                return ResolveFlowResponseObject(parsed.RootElement);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static string? ResolveStringProperty(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    var raw = property.Value.GetString();
                    return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim().ToUpperInvariant();
                }

                if (property.Value.ValueKind == JsonValueKind.Number
                    || property.Value.ValueKind == JsonValueKind.True
                    || property.Value.ValueKind == JsonValueKind.False)
                {
                    return property.Value.ToString();
                }
            }
        }

        return null;
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();
}

public sealed record FlowContractResolution(
    bool IsFlowReply,
    bool FlowReplyJsonValid,
    JsonElement? ParsedFlowResponse,
    string? FlowSubtipoDetectado,
    string FlowContractDetected,
    string? FlowCompletionIntentDetectado,
    string? FlowIdDetectado,
    string? FlowNameDetectado,
    bool Handled,
    string? RouteOverrideUrl,
    string? RouteOverrideApiKey,
    string? RouteOverrideAuthSource,
    string Reason,
    bool ContractRouteConfigValid,
    string? ContractRouteConfigIssue,
    bool ContractRecognized,
    bool RouteUrlPresent,
    bool RouteApiKeyPresent,
    bool AuthSourcePresent,
    bool DecisionReasonPresent,
    bool FallbackAppliedDueToInvalidContractConfig);
