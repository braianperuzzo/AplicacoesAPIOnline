using System.Text.Json;
using AplicacoesOnline.Models.MetaWhatsApp;
using AplicacoesOnline.Services.MetaWhatsApp;
using Microsoft.AspNetCore.Mvc;

namespace AplicacoesOnline.Controllers;

[ApiController]
public sealed class MetaWhatsAppLoginFlowController : ControllerBase
{
    private readonly MetaWhatsAppLoginFlowService _service;
    private readonly MetaWhatsAppFlowCryptoService _cryptoService;
    private readonly MetaWhatsAppChatAuthenticationService _chatAuthenticationService;
    private readonly ILogger<MetaWhatsAppLoginFlowController> _logger;

    public MetaWhatsAppLoginFlowController(
        MetaWhatsAppLoginFlowService service,
        MetaWhatsAppFlowCryptoService cryptoService,
        MetaWhatsAppChatAuthenticationService chatAuthenticationService,
        ILogger<MetaWhatsAppLoginFlowController> logger)
    {
        _service = service;
        _cryptoService = cryptoService;
        _chatAuthenticationService = chatAuthenticationService;
        _logger = logger;
    }

    [HttpPost("/api/meta/whatsapp/flows/login/endpoint")]
    [Produces("text/plain")]
    public async Task<IActionResult> HandleEncryptedFlow([FromBody] WhatsAppFlowEncryptedRequest request)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        WhatsAppFlowDataExchangeEnvelope envelope;
        byte[] aesKey;
        byte[] initialVector;

        try
        {
            (envelope, aesKey, initialVector) = _cryptoService.DecryptRequest(request);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var cryptoStage = ResolveCryptoStage(ex, "decrypt_request");
            _logger.LogWarning(
                ex,
                "Falha no estágio {Stage} do endpoint criptográfico do WhatsApp Flow. TraceId={TraceId}",
                cryptoStage,
                HttpContext.TraceIdentifier);

            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                ok = false,
                error = "flow_crypto_error",
                stage = cryptoStage,
                trace_id = HttpContext.TraceIdentifier,
                detail = ex.Message,
                inner_detail = ex.InnerException?.Message
            });
        }

        MetaWhatsAppLoginFlowResponsePayload flowPayload;
        try
        {
            flowPayload = await BuildFlowResponseAsync(envelope, HttpContext.TraceIdentifier);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Falha no estágio {Stage} do endpoint de login do WhatsApp Flow. TraceId={TraceId}",
                "build_flow_response",
                HttpContext.TraceIdentifier);

            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                ok = false,
                error = "flow_login_processing_error",
                stage = "build_flow_response",
                trace_id = HttpContext.TraceIdentifier,
                detail = ex.Message,
                inner_detail = ex.InnerException?.Message
            });
        }

        try
        {
            var encryptedResponse = _cryptoService.EncryptResponse(flowPayload, aesKey, initialVector);
            return new ContentResult
            {
                StatusCode = StatusCodes.Status200OK,
                ContentType = "text/plain",
                Content = encryptedResponse
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Falha no estágio {Stage} do endpoint criptográfico do WhatsApp Flow. TraceId={TraceId}",
                "encrypt_response",
                HttpContext.TraceIdentifier);

            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                ok = false,
                error = "flow_crypto_error",
                stage = "encrypt_response",
                trace_id = HttpContext.TraceIdentifier,
                detail = ex.Message,
                inner_detail = ex.InnerException?.Message
            });
        }
    }

    [HttpGet("/api/meta/whatsapp/flows/login/crypto-check")]
    public IActionResult CryptoCheck()
    {
        try
        {
            var result = _cryptoService.RunCryptoDiagnosticsCheck();
            return Ok(new
            {
                ok = result.Ok,
                resolved_key_path = result.ResolvedPath,
                public_key_fingerprint_sha256 = result.PublicKeyFingerprintSha256,
                trace_id = HttpContext.TraceIdentifier
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var stage = ResolveCryptoStage(ex, "crypto_check");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                ok = false,
                error = "flow_crypto_error",
                stage,
                trace_id = HttpContext.TraceIdentifier,
                detail = ex.Message,
                inner_detail = ex.InnerException?.Message
            });
        }
    }

    [HttpPost("/api/meta/whatsapp/flows/login/crypto-debug")]
    public IActionResult CryptoDebug([FromBody] WhatsAppFlowEncryptedRequest request)
    {
        return Ok(new
        {
            ok = true,
            trace_id = HttpContext.TraceIdentifier,
            fields = new
            {
                encrypted_aes_key = BuildFieldDebug(request.EncryptedAesKey),
                initial_vector = BuildFieldDebug(request.InitialVector),
                encrypted_flow_data = BuildFieldDebug(request.EncryptedFlowData)
            }
        });
    }

    [HttpPost("/api/meta/whatsapp/flows/login/submit")]
    public async Task<IActionResult> SubmitFlowLogin([FromBody] WhatsAppLoginFlowSubmitRequest request)
    {
        if (!ModelState.IsValid)
        {
            var details = ModelState
                .Where(pair => pair.Value?.Errors.Count > 0)
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value!.Errors.Select(error => error.ErrorMessage).ToArray());

            return StatusCode(StatusCodes.Status422UnprocessableEntity, new
            {
                ok = false,
                status = "error",
                error_type = "validation_error",
                error_code = "invalid_request_payload",
                message = "Falha de validação no payload enviado.",
                details,
                trace_id = HttpContext.TraceIdentifier
            });
        }

        var result = await _service.ProcessAsync(request, HttpContext.TraceIdentifier);
        return StatusCode(result.StatusCode, result.Payload);
    }

    [HttpPost("/api/meta/whatsapp/chat-auth/session")]
    public IActionResult GetActiveSession([FromBody] WhatsAppChatAuthSessionRequest request)
    {
        if (!ModelState.IsValid)
        {
            var details = ModelState
                .Where(pair => pair.Value?.Errors.Count > 0)
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value!.Errors.Select(error => error.ErrorMessage).ToArray());

            return StatusCode(StatusCodes.Status422UnprocessableEntity, new
            {
                ok = false,
                status = "error",
                error_type = "validation_error",
                error_code = "invalid_request_payload",
                message = "Falha de validação no payload enviado.",
                details,
                trace_id = HttpContext.TraceIdentifier
            });
        }

        var normalizedPhone = MetaWhatsAppPhoneCanonicalizer.ToCanonicalE164Br(request.PhoneE164);
        var now = DateTimeOffset.UtcNow;
        var hasActiveSession = _chatAuthenticationService.TryGetActive(normalizedPhone, now, out var state);

        _logger.LogInformation(
            "Consulta de sessão de autenticação do chat. phone_e164={PhoneE164}. authenticated={Authenticated}. request_interaction_id={RequestInteractionId}. request_flow_key={RequestFlowKey}. request_flow_name={RequestFlowName}. trace_id={TraceId}",
            normalizedPhone,
            hasActiveSession,
            request.InteractionId,
            request.FlowKey,
            request.FlowName,
            HttpContext.TraceIdentifier);

        if (!hasActiveSession || state is null)
        {
            return Ok(new WhatsAppChatAuthSessionResponse
            {
                Ok = true,
                Authenticated = false,
                AuthenticatedEmail = null,
                SessionExpiresAt = null,
                PhoneE164 = normalizedPhone ?? string.Empty
            });
        }

        return Ok(new WhatsAppChatAuthSessionResponse
        {
            Ok = true,
            Authenticated = true,
            AuthenticatedEmail = state.AuthenticatedEmail,
            SessionExpiresAt = state.ExpiresAt,
            InteractionId = state.InteractionId,
            ExecutionId = state.ExecutionId,
            FlowKey = state.FlowKey,
            FlowName = state.FlowName,
            PhoneE164 = state.PhoneE164
        });
    }

    [HttpPost("/api/meta/whatsapp/flows/login/complete")]
    public IActionResult CompleteFlowLogin([FromBody] WhatsAppLoginFlowCompleteRequest request)
    {
        if (!ModelState.IsValid)
        {
            var details = ModelState
                .Where(pair => pair.Value?.Errors.Count > 0)
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value!.Errors.Select(error => error.ErrorMessage).ToArray());

            return StatusCode(StatusCodes.Status422UnprocessableEntity, new
            {
                ok = false,
                status = "error",
                error_type = "validation_error",
                error_code = "invalid_request_payload",
                message = "Falha de validação no payload enviado.",
                details,
                trace_id = HttpContext.TraceIdentifier
            });
        }

        if (!request.Authenticated)
        {
            return StatusCode(StatusCodes.Status422UnprocessableEntity, new
            {
                ok = false,
                status = "error",
                error_type = "validation_error",
                error_code = "authenticated_must_be_true",
                message = "O campo authenticated deve ser true para concluir a autenticação.",
                trace_id = HttpContext.TraceIdentifier
            });
        }

        var normalizedPhone = MetaWhatsAppPhoneCanonicalizer.ToCanonicalE164Br(request.ResponderPhoneE164);
        if (string.IsNullOrWhiteSpace(normalizedPhone))
        {
            return StatusCode(StatusCodes.Status422UnprocessableEntity, new
            {
                ok = false,
                status = "error",
                error_type = "validation_error",
                error_code = "missing_responder_phone",
                message = "Informe responder_phone_e164 em formato válido.",
                trace_id = HttpContext.TraceIdentifier
            });
        }

        var now = DateTimeOffset.UtcNow;
        var sessionExpiresAt = _chatAuthenticationService.MarkAuthenticated(
            normalizedPhone,
            now,
            request.InteractionId,
            request.ExecutionId,
            request.FlowKey,
            request.FlowName,
            request.AuthenticatedEmail);

        _logger.LogInformation(
            "Sessão de autenticação do chat persistida via complete. phone_e164={PhoneE164}. interaction_id={InteractionId}. execution_id={ExecutionId}. flow_key={FlowKey}. flow_name={FlowName}. authenticated_email={AuthenticatedEmail}. session_expires_at={SessionExpiresAt}. trace_id={TraceId}",
            normalizedPhone,
            request.InteractionId,
            request.ExecutionId,
            request.FlowKey,
            request.FlowName,
            request.AuthenticatedEmail,
            sessionExpiresAt,
            HttpContext.TraceIdentifier);

        return Ok(new WhatsAppLoginFlowCompleteResponse
        {
            Ok = true,
            Status = "ok",
            TraceId = HttpContext.TraceIdentifier,
            Authenticated = true,
            AuthenticatedEmail = request.AuthenticatedEmail,
            SessionExpiresAt = sessionExpiresAt
        });
    }

    private async Task<MetaWhatsAppLoginFlowResponsePayload> BuildFlowResponseAsync(WhatsAppFlowDataExchangeEnvelope envelope, string traceId)
    {
        var action = (envelope.Action ?? string.Empty).Trim().ToLowerInvariant();
        var data = envelope.Data;
        var email = TryReadString(data, "email");
        var password = TryReadString(data, "password");

        return action switch
        {
            "init" => _service.BuildLoginScreenResponse(string.Empty),
            "ping" => BuildHealthCheckResponse(),
            "data_exchange" => await _service.ProcessFlowCredentialsAsync(email, password, traceId, failWhenMissingCredentials: false),
            "complete" => BuildCompletedNoOpResponse(),
            _ => _service.BuildLoginScreenResponse(email)
        };
    }


    private static MetaWhatsAppLoginFlowResponsePayload BuildHealthCheckResponse()
    {
        return new MetaWhatsAppLoginFlowResponsePayload
        {
            Version = "3.0",
            Screen = null,
            Data = new
            {
                status = "active"
            }
        };
    }

    private static MetaWhatsAppLoginFlowResponsePayload BuildCompletedNoOpResponse()
    {
        return new MetaWhatsAppLoginFlowResponsePayload
        {
            Version = "3.0",
            Screen = null,
            Data = new
            {
                status = "completed"
            }
        };
    }

    private static object BuildFieldDebug(string? value)
    {
        var hasValue = !string.IsNullOrWhiteSpace(value);

        return new
        {
            filled = hasValue,
            raw_length = value?.Length ?? 0,
            decoded_length = TryGetDecodedLength(value)
        };
    }

    private static int? TryGetDecodedLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(value).Length;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadString(JsonElement? data, string propertyName)
    {
        if (data is null || data.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!data.Value.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.GetString();
    }

    private static string ResolveCryptoStage(Exception ex, string fallback)
    {
        var cursor = ex;
        while (cursor is not null)
        {
            var message = cursor.Message ?? string.Empty;
            var marker = "Falha criptográfica no estágio '";
            var markerIndex = message.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex >= 0)
            {
                var start = markerIndex + marker.Length;
                var end = message.IndexOf('\'', start);
                if (end > start)
                {
                    return message[start..end];
                }
            }

            cursor = cursor.InnerException;
        }

        return fallback;
    }
}
