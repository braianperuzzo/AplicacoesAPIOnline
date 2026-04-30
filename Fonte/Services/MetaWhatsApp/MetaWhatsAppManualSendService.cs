using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AplicacoesOnline.Models.MetaWhatsApp;
using Microsoft.Extensions.Options;

namespace AplicacoesOnline.Services.MetaWhatsApp;

public sealed class MetaWhatsAppManualSendService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<MetaWhatsAppWebhookOptions> _options;

    public MetaWhatsAppManualSendService(IHttpClientFactory httpClientFactory, IOptions<MetaWhatsAppWebhookOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
    }

    public async Task<WhatsAppManualSendResult> SendTextAsync(string phone, string text, CancellationToken cancellationToken)
    {
        var normalizedPhone = MetaWhatsAppPhoneCanonicalizer.ToCanonicalE164Br(phone);
        var options = _options.Value;

        if (string.IsNullOrWhiteSpace(options.ManualSendAccessToken)
            || string.IsNullOrWhiteSpace(options.ManualSendPhoneNumberId))
        {
            return new WhatsAppManualSendResult
            {
                Success = false,
                StatusCode = StatusCodes.Status500InternalServerError,
                Error = "Configuração ausente para envio manual. Configure MetaWhatsAppWebhook:ManualSendAccessToken e MetaWhatsAppWebhook:ManualSendPhoneNumberId."
            };
        }

        var graphBaseUrl = string.IsNullOrWhiteSpace(options.ManualSendGraphBaseUrl)
            ? "https://graph.facebook.com"
            : options.ManualSendGraphBaseUrl.Trim().TrimEnd('/');
        var version = string.IsNullOrWhiteSpace(options.ManualSendApiVersion)
            ? "v21.0"
            : options.ManualSendApiVersion.Trim();

        var endpoint = $"{graphBaseUrl}/{version}/{options.ManualSendPhoneNumberId.Trim()}/messages";

        var payload = new
        {
            messaging_product = "whatsapp",
            to = normalizedPhone,
            type = "text",
            text = new
            {
                body = text.Trim()
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ManualSendAccessToken.Trim());

        var client = _httpClientFactory.CreateClient("MetaWhatsAppN8nForwarder");
        using var response = await client.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        string? metaMessageId = null;
        string? metaContactWaId = null;
        string? error = null;

        try
        {
            using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(responseBody) ? "{}" : responseBody);
            var root = json.RootElement;

            if (root.TryGetProperty("messages", out var messages)
                && messages.ValueKind == JsonValueKind.Array
                && messages.GetArrayLength() > 0)
            {
                metaMessageId = messages[0].TryGetProperty("id", out var idNode)
                    ? idNode.GetString()
                    : null;
            }

            if (root.TryGetProperty("contacts", out var contacts)
                && contacts.ValueKind == JsonValueKind.Array
                && contacts.GetArrayLength() > 0)
            {
                metaContactWaId = contacts[0].TryGetProperty("wa_id", out var waIdNode)
                    ? waIdNode.GetString()
                    : null;
            }

            if (root.TryGetProperty("error", out var errorNode) && errorNode.ValueKind == JsonValueKind.Object)
            {
                error = errorNode.TryGetProperty("message", out var messageNode)
                    ? messageNode.GetString()
                    : errorNode.GetRawText();
            }
        }
        catch
        {
            if (!response.IsSuccessStatusCode)
            {
                error = responseBody;
            }
        }

        return new WhatsAppManualSendResult
        {
            Success = response.IsSuccessStatusCode,
            StatusCode = (int)response.StatusCode,
            MetaMessageId = metaMessageId,
            MetaContactWaId = metaContactWaId,
            Error = error,
            ResponseBody = responseBody
        };
    }
}
