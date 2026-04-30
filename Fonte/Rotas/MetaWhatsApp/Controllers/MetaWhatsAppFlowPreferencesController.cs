using AplicacoesOnline.Models.MetaWhatsApp;
using AplicacoesOnline.Services.MetaWhatsApp;
using Microsoft.AspNetCore.Mvc;

namespace AplicacoesOnline.Controllers;

[ApiController]
public sealed class MetaWhatsAppFlowPreferencesController : ControllerBase
{
    private readonly MetaWhatsAppFlowPreferencesService _service;

    public MetaWhatsAppFlowPreferencesController(MetaWhatsAppFlowPreferencesService service)
    {
        _service = service;
    }

    [HttpPost("/api/meta/whatsapp/flows/preferences/submit")]
    public async Task<IActionResult> SubmitFlowPreferences([FromBody] WhatsAppFlowPreferencesSubmitRequest request)
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
}
