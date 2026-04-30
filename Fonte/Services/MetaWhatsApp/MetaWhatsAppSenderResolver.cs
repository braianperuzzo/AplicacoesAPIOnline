using System.Data;
using System.Globalization;
using System.Text.Json;
using AplicacoesOnline.Models.MetaWhatsApp;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace AplicacoesOnline.Services.MetaWhatsApp;

public interface IMetaWhatsAppSenderResolver
{
    MetaWhatsAppSenderResolution Resolve(MetaWhatsAppSenderResolveRequest request);
}

public sealed record MetaWhatsAppSenderResolveRequest(
    int? CustomerId,
    int? CdPessoa,
    string? FlowKey,
    InteractionContext? InteractionContext,
    ExecutionContext? ExecutionContext,
    string? StateCode,
    string? InboundDestinationPhoneNumberId = null,
    string? InboundDestinationDisplayPhone = null,
    string? InboundChannelInstanceKey = null);

public sealed record MetaWhatsAppSenderResolution(
    string SenderPhoneNumberId,
    string SenderDisplayPhone,
    string SenderUnitCode,
    string SenderUnitLabel,
    string SenderResolutionSource,
    string ResolutionReason,
    bool FallbackApplied,
    string SenderKey);

public sealed class MetaWhatsAppSenderResolver : IMetaWhatsAppSenderResolver
{
    private readonly MetaWhatsAppWebhookOptions _options;
    private readonly IDatabaseConnectionStringProvider _databaseConnectionStringProvider;
    private readonly ILogger<MetaWhatsAppSenderResolver> _logger;

    public MetaWhatsAppSenderResolver(
        IOptions<MetaWhatsAppWebhookOptions> options,
        IDatabaseConnectionStringProvider databaseConnectionStringProvider,
        ILogger<MetaWhatsAppSenderResolver> logger)
    {
        _options = options.Value;
        _databaseConnectionStringProvider = databaseConnectionStringProvider;
        _logger = logger;
    }

    public MetaWhatsAppSenderResolution Resolve(MetaWhatsAppSenderResolveRequest request)
    {
        var resolvedCustomerId = request.CustomerId ?? request.CdPessoa;

        if (TryGetPreferredSender(request.InteractionContext?.CurrentConversationPhoneNumberId, out var senderByAnchoredInteraction))
        {
            var anchoredResolution = ToResolution(
                senderByAnchoredInteraction!,
                "anchored_interaction_channel",
                "anchored_interaction_channel_preserved",
                false);
            LogResolution(request, anchoredResolution);
            return anchoredResolution;
        }

        if (TryGetPreferredSender(request.InboundDestinationPhoneNumberId, out var inboundSender))
        {
            var inboundReason = !resolvedCustomerId.HasValue
                ? "inbound_channel_preferred_for_new_interaction"
                : "inbound_channel_preserved";
            var inboundResolution = ToResolution(inboundSender!, "inbound_channel", inboundReason, false);
            LogResolution(request, inboundResolution);
            return inboundResolution;
        }

        var senderByCustomer = ResolveSenderByCustomerUnit(resolvedCustomerId);
        if (senderByCustomer is not null)
        {
            LogResolution(request, senderByCustomer);
            return senderByCustomer;
        }

        var stateCode = ResolveStateCode(request);
        if (!string.IsNullOrWhiteSpace(stateCode)
            && TryFindSenderByStateCode(stateCode!, out var senderByUf))
        {
            var resolution = ToResolution(senderByUf!, "state_code", $"context_uf:{stateCode!.ToUpperInvariant()}", false);
            LogResolution(request, resolution);
            return resolution;
        }

        if (TryGetPreferredSender(request.InteractionContext?.PreferredOutboundPhoneNumberId, out var senderByPreferred))
        {
            var resolution = ToResolution(senderByPreferred!, "preferred_outbound", "interaction_preferred_outbound", false);
            LogResolution(request, resolution);
            return resolution;
        }

        var fallbackSender = ResolveFallbackSender();
        var fallbackResolution = ToResolution(fallbackSender, "default_sender", "fallback_default_sender_only_when_inbound_missing", true);
        LogResolution(request, fallbackResolution);
        return fallbackResolution;
    }

    private void LogResolution(MetaWhatsAppSenderResolveRequest request, MetaWhatsAppSenderResolution resolution)
    {
        _logger.LogInformation(
            "meta.sender.resolve customer_id={CustomerId} cd_pessoa={CdPessoa} flow_key={FlowKey} inbound_destination_phone_number_id={InboundDestinationPhoneNumberId} inbound_destination_display_phone={InboundDestinationDisplayPhone} inbound_channel_instance_key={InboundChannelInstanceKey} preferred_outbound_phone_number_id={PreferredOutboundPhoneNumberId} preferred_outbound_display_phone={PreferredOutboundDisplayPhone} preferred_outbound_unit={PreferredOutboundUnit} current_conversation_phone_number_id={CurrentConversationPhoneNumberId} current_conversation_display_phone={CurrentConversationDisplayPhone} sender_phone_number_id={SenderPhoneNumberId} sender_display_phone={SenderDisplayPhone} sender_unit_code={SenderUnitCode} sender_resolution_source={SenderResolutionSource} sender_resolution_reason={ResolutionReason} fallback_applied={FallbackApplied}",
            request.CustomerId,
            request.CdPessoa,
            request.FlowKey,
            request.InboundDestinationPhoneNumberId,
            request.InboundDestinationDisplayPhone,
            request.InboundChannelInstanceKey,
            request.InteractionContext?.PreferredOutboundPhoneNumberId,
            request.InteractionContext?.PreferredOutboundDisplayPhone,
            request.InteractionContext?.PreferredOutboundUnit,
            request.InteractionContext?.CurrentConversationPhoneNumberId,
            request.InteractionContext?.CurrentConversationDisplayPhone,
            resolution.SenderPhoneNumberId,
            resolution.SenderDisplayPhone,
            resolution.SenderUnitCode,
            resolution.SenderResolutionSource,
            resolution.ResolutionReason,
            resolution.FallbackApplied);
    }

    private MetaWhatsAppSenderResolution? ResolveSenderByCustomerUnit(int? customerId)
    {
        if (!customerId.HasValue)
        {
            return null;
        }

        try
        {
            if (!_databaseConnectionStringProvider.TryBuildConnectionString(out var connectionString, out var missingKeys))
            {
                _logger.LogWarning(
                    "Resolução de sender sem lookup de unidade: configuração de banco incompleta. MissingKeys={MissingKeys}",
                    string.Join(",", missingKeys));
                return null;
            }

            using var connection = new SqlConnection(connectionString);
            connection.Open();

            const string sql = """
SELECT TOP 1 VENDEDOR.CD_ESTADO
FROM MBAD_PESSOA AS PESSOA
INNER JOIN _USR_AD_PESSOA AS TABELAPESSOA
    ON PESSOA.CD_PESSOA = TABELAPESSOA.CD_PESSOA
INNER JOIN MBAD_PESSOA AS VENDEDOR
    ON TABELAPESSOA.CD_VENDEDOR = VENDEDOR.CD_PESSOA
INNER JOIN MBAD_ESTADO AS ESTADO
    ON VENDEDOR.CD_ESTADO = ESTADO.CD_ESTADO
   AND ESTADO.CD_PAIS = '1058'
WHERE PESSOA.CD_PESSOA = @customerId;
""";

            using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@customerId", SqlDbType.Int).Value = customerId.Value;

            var scalar = command.ExecuteScalar()?.ToString();
            if (int.TryParse(scalar, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cdEstado))
            {
                if (cdEstado == 35 && TryGetSender("sp", out var spSender))
                {
                    return ToResolution(spSender!, "customer_unit", $"customer_vendor_state_sql:{cdEstado}", false);
                }

                if (TryGetSender("rs", out var rsSender))
                {
                    return ToResolution(rsSender!, "customer_unit", $"customer_vendor_state_sql:{cdEstado}", false);
                }

                if (TryFindSenderByStateCode("RS", out var rsByState))
                {
                    return ToResolution(rsByState!, "customer_unit", $"customer_vendor_state_sql:{cdEstado}", false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Falha ao resolver UF real da unidade para customerId={CustomerId}. Seguindo para fallback de resolução de sender.",
                customerId.Value);
        }

        return null;
    }

    private MetaWhatsAppSenderOptions ResolveFallbackSender()
    {
        if (TryGetSender(_options.DefaultSenderKey, out var configuredDefault))
        {
            return configuredDefault!;
        }

        if (TryGetSender("rs", out var rsSender))
        {
            return rsSender!;
        }

        return _options.Senders.Values.First(sender => sender.Enabled);
    }

    private bool TryFindSenderByStateCode(string stateCode, out MetaWhatsAppSenderOptions? sender)
    {
        sender = _options.Senders.Values
            .Where(static item => item.Enabled)
            .FirstOrDefault(item => item.SupportsStateCode(stateCode));
        return sender is not null;
    }

    private bool TryGetPreferredSender(string? phoneNumberId, out MetaWhatsAppSenderOptions? sender)
    {
        sender = _options.Senders.Values
            .Where(static item => item.Enabled)
            .FirstOrDefault(item => string.Equals(item.PhoneNumberId, phoneNumberId, StringComparison.Ordinal));
        return sender is not null;
    }

    private bool TryGetSender(string? senderKey, out MetaWhatsAppSenderOptions? sender)
    {
        sender = null;
        if (string.IsNullOrWhiteSpace(senderKey))
        {
            return false;
        }

        if (!_options.Senders.TryGetValue(senderKey.Trim(), out var configured) || !configured.Enabled)
        {
            return false;
        }

        sender = configured;
        return true;
    }

    private static string? ResolveStateCode(MetaWhatsAppSenderResolveRequest request)
    {
        var explicitStateCode = NormalizeStateCode(request.StateCode);
        if (!string.IsNullOrWhiteSpace(explicitStateCode))
        {
            return explicitStateCode;
        }

        return NormalizeStateCode(
            TryGetAdditionalProperty(request.InteractionContext?.BusinessAdditionalProperties, "uf")
            ?? TryGetAdditionalProperty(request.InteractionContext?.BusinessAdditionalProperties, "state_code")
            ?? TryGetAdditionalProperty(request.InteractionContext?.BusinessAdditionalProperties, "estado")
            ?? TryGetAdditionalProperty(request.InteractionContext?.BusinessAdditionalProperties, "customer_state")
            ?? TryGetAdditionalProperty(request.InteractionContext?.BusinessAdditionalProperties, "customer_uf"));
    }

    private static string? NormalizeStateCode(string? stateCode)
    {
        if (string.IsNullOrWhiteSpace(stateCode))
        {
            return null;
        }

        return stateCode.Trim().ToUpperInvariant();
    }

    private static string? TryGetAdditionalProperty(IReadOnlyDictionary<string, JsonElement>? source, string key)
    {
        if (source is null || !source.TryGetValue(key, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => null
        };
    }

    private static MetaWhatsAppSenderResolution ToResolution(MetaWhatsAppSenderOptions sender, string source, string reason, bool fallbackApplied)
    {
        var unitCode = sender.Key.ToUpperInvariant();
        var unitLabel = unitCode switch
        {
            "SP" => "São Paulo",
            "RS" => "Rio Grande do Sul",
            _ => unitCode
        };

        return new MetaWhatsAppSenderResolution(
            sender.PhoneNumberId,
            sender.DisplayPhone,
            unitCode,
            unitLabel,
            source,
            reason,
            fallbackApplied,
            sender.Key);
    }
}
