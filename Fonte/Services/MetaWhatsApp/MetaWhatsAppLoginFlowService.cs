using System.Globalization;
using AplicacoesOnline.Models.MetaWhatsApp;
using Microsoft.Data.SqlClient;

namespace AplicacoesOnline.Services.MetaWhatsApp;

public sealed class MetaWhatsAppLoginFlowService
{
    private readonly MetaWhatsAppInteractionRouter _interactionRouter;
    private readonly IMetaWhatsAppPersistentLogService _persistentLogService;
    private readonly MetaWhatsAppChatAuthenticationService _chatAuthenticationService;
    private readonly IDatabaseConnectionStringProvider _databaseConnectionStringProvider;
    private readonly ILogger<MetaWhatsAppLoginFlowService> _logger;

    public MetaWhatsAppLoginFlowService(
        MetaWhatsAppInteractionRouter interactionRouter,
        IMetaWhatsAppPersistentLogService persistentLogService,
        MetaWhatsAppChatAuthenticationService chatAuthenticationService,
        IDatabaseConnectionStringProvider databaseConnectionStringProvider,
        ILogger<MetaWhatsAppLoginFlowService> logger)
    {
        _interactionRouter = interactionRouter;
        _persistentLogService = persistentLogService;
        _chatAuthenticationService = chatAuthenticationService;
        _databaseConnectionStringProvider = databaseConnectionStringProvider;
        _logger = logger;
    }

    public async Task<WhatsAppLoginFlowServiceResult> ProcessAsync(WhatsAppLoginFlowSubmitRequest request, string traceId)
    {
        var now = DateTimeOffset.UtcNow;
        var responderPhoneE164 = ResolveResponderPhoneE164(request);
        var responderPhoneLocal = MetaWhatsAppPhoneCanonicalizer.ToBrLocalDigits(responderPhoneE164);

        await WriteAuditEventAsync("FLOW_LOGIN_RECEBIDO", now, responderPhoneE164, new Dictionary<string, string?>
        {
            ["trace_id"] = traceId,
            ["execution_id"] = request.ExecutionId,
            ["interaction_id"] = request.InteractionId,
            ["email"] = request.Email,
            ["meta_message_id"] = request.MetaMessageId,
            ["wamid"] = request.Wamid
        });

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

            return ErrorResult(
                contextResolution.StatusCode,
                contextResolution.ErrorType!,
                contextResolution.ErrorCode!,
                contextResolution.Message!,
                traceId,
                contextResolution.Details);
        }

        var interaction = contextResolution.Interaction;
        var execution = contextResolution.ExecutionContext;

        if (string.IsNullOrWhiteSpace(responderPhoneE164))
        {
            responderPhoneE164 = FirstNonEmpty(interaction?.CustomerPhone, interaction?.RecipientE164, execution?.RecipientPhoneE164, interaction?.PhoneKey, execution?.PhoneKey);
            responderPhoneE164 = MetaWhatsAppPhoneCanonicalizer.ToCanonicalE164Br(responderPhoneE164);
            responderPhoneLocal = MetaWhatsAppPhoneCanonicalizer.ToBrLocalDigits(responderPhoneE164);
        }

        var credentialResult = await ValidateCredentialsAsync(request.Email, request.Password);
        if (!credentialResult.Success)
        {
            await WriteAuditEventAsync("FLOW_LOGIN_CREDENCIAIS_INVALIDAS", now, responderPhoneE164, new Dictionary<string, string?>
            {
                ["trace_id"] = traceId,
                ["interaction_id"] = interaction?.InteractionId ?? execution?.InteractionId,
                ["execution_id"] = execution?.ExecutionId ?? request.ExecutionId,
                ["email"] = request.Email
            });

            return ErrorResult(
                StatusCodes.Status422UnprocessableEntity,
                "validation_error",
                "invalid_credentials",
                "Email ou senha inválidos.",
                traceId,
                null);
        }

        var resolvedFlowKey = FirstNonEmpty(request.FlowKey, interaction?.FlowKey, execution?.FlowKey);
        var resolvedFlowName = FirstNonEmpty(request.FlowName, interaction?.FlowName, execution?.FlowName);
        var resolvedExecutionId = execution?.ExecutionId ?? request.ExecutionId;
        var resolvedInteractionId = interaction?.InteractionId ?? execution?.InteractionId ?? request.InteractionId;

        if (string.IsNullOrWhiteSpace(responderPhoneE164))
        {
            return ErrorResult(
                StatusCodes.Status422UnprocessableEntity,
                "validation_error",
                "missing_responder_phone",
                "Não foi possível resolver o telefone do respondente para autenticar o chat.",
                traceId,
                null);
        }

        var sessionExpiresAt = _chatAuthenticationService.MarkAuthenticated(
            responderPhoneE164,
            now,
            resolvedInteractionId,
            resolvedExecutionId,
            resolvedFlowKey,
            resolvedFlowName,
            credentialResult.ResolvedEmail);

        await WriteAuditEventAsync("FLOW_LOGIN_AUTENTICADO", now, responderPhoneE164, new Dictionary<string, string?>
        {
            ["trace_id"] = traceId,
            ["interaction_id"] = resolvedInteractionId,
            ["execution_id"] = resolvedExecutionId,
            ["session_expires_at"] = sessionExpiresAt.ToString("O"),
            ["email"] = credentialResult.ResolvedEmail
        });

        var payload = new WhatsAppLoginFlowSubmitResponse
        {
            Ok = true,
            Status = "ok",
            TraceId = traceId,
            Authenticated = true,
            SessionExpiresAt = sessionExpiresAt,
            ResolvedContext = new WhatsAppFlowResolvedContext
            {
                ExecutionId = resolvedExecutionId,
                InteractionId = resolvedInteractionId,
                FlowKey = resolvedFlowKey,
                FlowName = resolvedFlowName
            },
            Responder = new WhatsAppFlowResponder
            {
                PhoneE164 = responderPhoneE164,
                PhoneLocalDigits = responderPhoneLocal
            }
        };

        return new WhatsAppLoginFlowServiceResult(StatusCodes.Status200OK, payload);
    }


    public Task<MetaWhatsAppLoginFlowResponsePayload> ProcessFlowCredentialsAsync(string? email, string? password, string traceId, bool failWhenMissingCredentials = true)
    {
        var sanitizedEmail = email?.Trim() ?? string.Empty;
        var sanitizedPassword = password ?? string.Empty;

        if (string.IsNullOrWhiteSpace(sanitizedEmail) || string.IsNullOrWhiteSpace(sanitizedPassword))
        {
            return Task.FromResult(failWhenMissingCredentials
                ? BuildInvalidCredentialResponse(sanitizedEmail)
                : BuildLoginScreenResponse(sanitizedEmail));
        }

        return ProcessFlowCredentialsCoreAsync(sanitizedEmail, sanitizedPassword, traceId);
    }

    private async Task<MetaWhatsAppLoginFlowResponsePayload> ProcessFlowCredentialsCoreAsync(string email, string password, string traceId)
    {
        var credentialResult = await ValidateCredentialsAsync(email, password);
        if (!credentialResult.Success)
        {
            await WriteAuditEventAsync("FLOW_LOGIN_FLOW_INVALIDO", DateTimeOffset.UtcNow, null, new Dictionary<string, string?>
            {
                ["trace_id"] = traceId,
                ["email"] = email
            });

            return BuildInvalidCredentialResponse(email);
        }

        var sessionExpiresAt = DateTimeOffset.UtcNow.AddHours(24);
        var authenticatedEmail = credentialResult.ResolvedEmail ?? email;

        return new MetaWhatsAppLoginFlowResponsePayload
        {
            Version = "3.0",
            Screen = "LOGIN_OK",
            Data = new
            {
                authenticated = true,
                authenticated_email = authenticatedEmail,
                session_expires_at = sessionExpiresAt,
                session_expires_at_text = BuildSessionExpiresAtText(sessionExpiresAt),
                success_message = "Para finalizar a validação da sessão, clique em Continuar abaixo."
            }
        };
    }


    private static string BuildSessionExpiresAtText(DateTimeOffset sessionExpiresAtUtc)
    {
        var brasilTimeZone = ResolveBrazilTimeZone();
        var localDateTime = TimeZoneInfo.ConvertTime(sessionExpiresAtUtc, brasilTimeZone);
        var formattedDateTime = localDateTime.ToString("dd/MM/yyyy 'às' HH:mm", CultureInfo.GetCultureInfo("pt-BR"));

        return $"🔑 Seu acesso ficará válido nesta conversa até: {formattedDateTime}. Após esse período, será necessário refazer o acesso por segurança.";
    }

    private static TimeZoneInfo ResolveBrazilTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time");
        }
    }

    public MetaWhatsAppLoginFlowResponsePayload BuildLoginScreenResponse(string? email)
    {
        var normalizedEmail = email?.Trim() ?? string.Empty;

        object initValues = string.IsNullOrWhiteSpace(normalizedEmail)
            ? new { }
            : new
            {
                email = normalizedEmail
            };

        return new MetaWhatsAppLoginFlowResponsePayload
        {
            Version = "3.0",
            Screen = "LOGIN",
            Data = new
            {
                login_failed = false,
                auth_error_text = string.Empty,
                init_values = initValues,
                error_messages = new { }
            }
        };
    }

    private static MetaWhatsAppLoginFlowResponsePayload BuildInvalidCredentialResponse(string email)
    {
        var normalizedEmail = email.Trim();

        return new MetaWhatsAppLoginFlowResponsePayload
        {
            Version = "3.0",
            Screen = "LOGIN",
            Data = new
            {
                login_failed = true,
                auth_error_text = string.Empty,
                init_values = new
                {
                    email = normalizedEmail
                },
                error_messages = new
                {
                    password = "⚠️ Alguma informação está errada. Clique em 📝 Cadastro ou 🔑 Nova Senha."
                }
            }
        };
    }
    private async Task<CredentialValidationResult> ValidateCredentialsAsync(string email, string password)
    {
        if (!_databaseConnectionStringProvider.TryBuildConnectionString(out var connectionString, out var missingKeys))
        {
            _logger.LogWarning(
                "Validação de login não executada por configuração de banco incompleta. MissingKeys={MissingKeys}",
                string.Join(",", missingKeys));
            return CredentialValidationResult.Fail();
        }

        const string sql = """
SELECT TOP 1
    DS_EMAIL,
    DS_SENHA
FROM _USR_CONF_SITE_CADASTROS
WHERE LOWER(LTRIM(RTRIM(DS_EMAIL))) = LOWER(LTRIM(RTRIM(@email)));
""";

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@email", email.Trim());

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return CredentialValidationResult.Fail();
        }

        var resolvedEmail = reader.IsDBNull(0) ? null : reader.GetString(0)?.Trim();
        var storedHash = reader.IsDBNull(1) ? null : reader.GetString(1)?.Trim();
        if (string.IsNullOrWhiteSpace(storedHash))
        {
            return CredentialValidationResult.Fail();
        }

        var compatibleHash = storedHash.Replace("$2y$", "$2a$", StringComparison.Ordinal);
        var passwordMatches = BCrypt.Net.BCrypt.Verify(password, compatibleHash);
        return passwordMatches
            ? CredentialValidationResult.Ok(resolvedEmail)
            : CredentialValidationResult.Fail();
    }

    private ContextResolutionResult ResolveContext(WhatsAppLoginFlowSubmitRequest request, string? responderPhoneE164, DateTimeOffset now)
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
                "FLOW_LOGIN_VALIDACAO_FALHOU",
                new { interaction_id = request.InteractionId });
        }

        if (hasExecutionId)
        {
            return ContextResolutionResult.Error(
                StatusCodes.Status404NotFound,
                "not_found",
                "execution_context_not_found",
                "execution_id informado não foi encontrado.",
                "FLOW_LOGIN_VALIDACAO_FALHOU",
                new { execution_id = request.ExecutionId });
        }

        if (string.IsNullOrWhiteSpace(responderPhoneE164))
        {
            return ContextResolutionResult.Error(
                StatusCodes.Status422UnprocessableEntity,
                "validation_error",
                "missing_context_identifier",
                "Informe execution_id, interaction_id ou telefone do respondente para correlação.",
                "FLOW_LOGIN_VALIDACAO_FALHOU",
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
                "FLOW_LOGIN_AMBIGUO",
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
            "FLOW_LOGIN_VALIDACAO_FALHOU",
            new { responder_phone_e164 = responderPhoneE164 });
    }

    private async Task WriteAuditEventAsync(string eventName, DateTimeOffset now, string? phone, IReadOnlyDictionary<string, string?> fields)
    {
        try
        {
            await _persistentLogService.AppendEventAsync(phone, eventName, now, fields);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao gravar trilha de auditoria de flow login. EventName={EventName}", eventName);
        }
    }

    private static string? ResolveResponderPhoneE164(WhatsAppLoginFlowSubmitRequest request)
    {
        var phone = FirstNonEmpty(request.ResponderPhoneE164, request.ResponderPhone, request.ResponderPhoneLocal);
        return MetaWhatsAppPhoneCanonicalizer.ToCanonicalE164Br(phone);
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static WhatsAppLoginFlowServiceResult ErrorResult(int statusCode, string errorType, string errorCode, string message, string traceId, object? details)
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

        return new WhatsAppLoginFlowServiceResult(statusCode, payload);
    }

    private sealed record CredentialValidationResult(bool Success, string? ResolvedEmail)
    {
        public static CredentialValidationResult Ok(string? resolvedEmail) => new(true, resolvedEmail);
        public static CredentialValidationResult Fail() => new(false, null);
    }

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
}
