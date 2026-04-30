using System.Text.Json;

namespace AplicacoesOnline.Services.MetaWhatsApp;

public static class MetaWhatsAppWebhookParser
{
    private static readonly HashSet<string> KnownMetaStatusValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "sent",
        "delivered",
        "read",
        "failed",
        "deleted",
        "warning"
    };

    public static ParsedWhatsAppEvent Parse(JsonElement root)
    {
        var envelope = ParseEnvelope(root);
        var message = envelope.Messages.FirstOrDefault();
        var status = envelope.Statuses.FirstOrDefault();
        var rawMessage = message?.RawMessage ?? default;
        var sourcePhoneRaw =
            GetString(rawMessage, "from")
            ?? GetString(envelope.PrimaryContact, "wa_id")
            ?? GetString(status, "recipient_id");
        var customerPhone = NormalizeE164(sourcePhoneRaw);
        var destinationDisplayPhone = NormalizeE164(envelope.PrimaryDisplayPhone);
        var destinationPhoneNumberId = envelope.PrimaryMetadataPhoneNumberId;
        var customerWaIdRaw = GetString(envelope.PrimaryContact, "wa_id");
        var customerWaId = NormalizeE164(customerWaIdRaw);
        var customerUserId = FirstNonEmpty(
            GetString(rawMessage, "from_user_id"),
            message?.FromUserId,
            GetString(envelope.PrimaryContact, "user_id"),
            GetString(status, "recipient_user_id"));
        var customerParentUserId = FirstNonEmpty(
            GetString(rawMessage, "from_parent_user_id"),
            message?.FromParentUserId,
            GetString(envelope.PrimaryContact, "parent_user_id"),
            GetString(status, "parent_recipient_user_id"));
        var customerUsername = GetString(envelope.PrimaryContact, "profile", "username");
        var statusRecipientUserId = GetString(status, "recipient_user_id");
        var statusRecipientParentUserId = GetString(status, "parent_recipient_user_id");

        var contextMessageId =
            GetString(rawMessage, "context", "id")
            ?? GetString(status, "id");
        var metaMessageId =
            GetString(rawMessage, "id")
            ?? GetString(status, "id");
        var buttonPayload =
            GetString(rawMessage, "button", "payload")
            ?? GetString(rawMessage, "interactive", "button_reply", "id")
            ?? GetString(rawMessage, "interactive", "list_reply", "id");
        var responseText =
            GetString(rawMessage, "button", "text")
            ?? GetString(rawMessage, "interactive", "button_reply", "title")
            ?? GetString(rawMessage, "interactive", "list_reply", "title")
            ?? GetString(rawMessage, "text", "body");
        var interactiveType = GetString(rawMessage, "interactive", "type");
        var flowResponseJson = GetJsonElement(rawMessage, "interactive", "nfm_reply", "response_json");
        var isFlowReply = string.Equals(interactiveType, "nfm_reply", StringComparison.OrdinalIgnoreCase)
            && flowResponseJson is not null;

        var interactionId =
            GetString(rawMessage, "context", "metadata", "interaction_id")
            ?? GetString(rawMessage, "referral", "source_id")
            ?? ExtractInteractionIdFromPayload(buttonPayload);
        var (canonicalCorrelationKey, canonicalCorrelationSource) = ResolveCanonicalCorrelationKey(
            customerParentUserId,
            customerUserId,
            customerWaId ?? customerPhone);

        return new ParsedWhatsAppEvent(
            customerPhone,
            customerWaId,
            MetaWhatsAppPhoneCanonicalizer.ToRawIdentity(sourcePhoneRaw),
            MetaWhatsAppPhoneCanonicalizer.ToRawIdentity(customerWaIdRaw),
            customerUserId,
            customerParentUserId,
            customerUsername,
            contextMessageId,
            metaMessageId,
            buttonPayload,
            interactionId,
            isFlowReply ? "FLOW" : NormalizeResponse(responseText),
            rawMessage.ValueKind == JsonValueKind.Undefined ? null : rawMessage.Clone(),
            interactiveType,
            flowResponseJson,
            isFlowReply,
            envelope.SourceShape,
            message?.Source ?? "none",
            message?.FromUserId,
            message?.FromParentUserId,
            envelope.PrimaryContactUserId,
            envelope.PrimaryContactParentUserId,
            destinationPhoneNumberId,
            destinationDisplayPhone,
            envelope.EventType,
            envelope.ParseReason,
            envelope.ParserDiagnostics,
            envelope.NormalizedPayload,
            ResolveMediaMetadata(rawMessage),
            GetString(status, "status"),
            GetString(status, "recipient_id"),
            statusRecipientUserId,
            statusRecipientParentUserId,
            GetString(status, "conversation", "id"),
            GetString(status, "pricing", "category"),
            GetFirstErrorCode(status),
            canonicalCorrelationKey,
            canonicalCorrelationSource);
    }

    public static ParsedWhatsAppEnvelope ParseEnvelope(JsonElement root)
    {
        var values = ExtractValueNodes(root).ToArray();
        var diagnostics = BuildParserDiagnostics(root, values);
        if (values.Length == 0)
        {
            var emptyParseReason = diagnostics.FoundEntry
                ? "entry_without_changes_value"
                : "invalid_payload_shape";
            return new ParsedWhatsAppEnvelope(
                Array.Empty<ParsedMessageNode>(),
                Array.Empty<JsonElement>(),
                default,
                null,
                null,
                null,
                null,
                "unknown",
                emptyParseReason,
                diagnostics with { ParserStage = "meta_envelope" },
                "none",
                BuildNormalizedPayload(root, Array.Empty<ParsedMessageNode>(), Array.Empty<JsonElement>(), emptyParseReason, diagnostics with { ParserStage = "meta_envelope" }, "none", null, null, default));
        }

        var messages = new List<ParsedMessageNode>();
        var statuses = new List<JsonElement>();
        JsonElement primaryContact = default;
        string? primaryContactUserId = null;
        string? primaryContactParentUserId = null;
        string? primaryDisplayPhone = null;
        string? primaryMetadataPhoneNumberId = null;

        foreach (var valueNode in values)
        {
            if (valueNode.Value.TryGetProperty("contacts", out var contacts)
                && contacts.ValueKind == JsonValueKind.Array)
            {
                var firstContact = contacts.EnumerateArray().FirstOrDefault();
                if (primaryContact.ValueKind == JsonValueKind.Undefined && firstContact.ValueKind != JsonValueKind.Undefined)
                {
                    primaryContact = firstContact.Clone();
                    primaryContactUserId = GetString(firstContact, "user_id");
                    primaryContactParentUserId = GetString(firstContact, "parent_user_id");
                }
            }

            if (valueNode.Value.TryGetProperty("metadata", out var metadata)
                && metadata.ValueKind == JsonValueKind.Object)
            {
                primaryDisplayPhone ??= GetString(metadata, "display_phone_number");
                primaryMetadataPhoneNumberId ??= GetString(metadata, "phone_number_id");
            }

            if (valueNode.Value.TryGetProperty("messages", out var messageArray)
                && messageArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in messageArray.EnumerateArray())
                {
                    messages.Add(new ParsedMessageNode(
                        message.Clone(),
                        valueNode.Field ?? "messages",
                        FirstNonEmpty(GetString(message, "from_user_id"), GetString(message, "from", "user_id")),
                        FirstNonEmpty(GetString(message, "from_parent_user_id"), GetString(message, "from", "parent_user_id"))));
                }
            }

            if (valueNode.Value.TryGetProperty("smb_message_echoes", out var echoArray)
                && echoArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var echo in echoArray.EnumerateArray())
                {
                    messages.Add(new ParsedMessageNode(
                        echo.Clone(),
                        "smb_message_echoes",
                        FirstNonEmpty(GetString(echo, "from_user_id"), GetString(echo, "from", "user_id")),
                        FirstNonEmpty(GetString(echo, "from_parent_user_id"), GetString(echo, "from", "parent_user_id"))));
                }
            }

            if (valueNode.Value.TryGetProperty("event", out var eventValue)
                && eventValue.ValueKind == JsonValueKind.String
                && string.Equals(eventValue.GetString(), "smb_message_echoes", StringComparison.OrdinalIgnoreCase)
                && valueNode.Value.TryGetProperty("messages", out var eventMessages)
                && eventMessages.ValueKind == JsonValueKind.Array)
            {
                foreach (var eventMessage in eventMessages.EnumerateArray())
                {
                    messages.Add(new ParsedMessageNode(
                        eventMessage.Clone(),
                        "smb_message_echoes",
                        FirstNonEmpty(GetString(eventMessage, "from_user_id"), GetString(eventMessage, "from", "user_id")),
                        FirstNonEmpty(GetString(eventMessage, "from_parent_user_id"), GetString(eventMessage, "from", "parent_user_id"))));
                }
            }

            if (valueNode.Value.TryGetProperty("statuses", out var statusesArray)
                && statusesArray.ValueKind == JsonValueKind.Array)
            {
                statuses.AddRange(statusesArray.EnumerateArray().Select(static status => status.Clone()));
            }
        }

        var hasValidStatusPayload = statuses.Any(IsRecognizableStatusNode);
        var parseReason = messages.Count > 0
            ? "ok"
            : hasValidStatusPayload
                ? "status_event"
                : statuses.Count > 0
                    ? "missing_expected_status_fields"
                    : values.Length == 0
                        ? "invalid_payload_shape"
                        : "unexpected_format";
        var eventType = messages.Count > 0
            ? "messages"
            : hasValidStatusPayload
                ? "message_status"
                : "unknown";

        var sourceShape = values.Any(static node => string.Equals(node.Shape, "direct_field_value", StringComparison.Ordinal))
            ? "direct_field_value"
            : "meta_envelope";

        diagnostics = diagnostics with
        {
            FoundStatuses = statuses.Count > 0,
            FoundMessages = messages.Count > 0,
            MissingRequiredStatusFields = statuses.Count > 0 && !hasValidStatusPayload
                ? "status"
                : null,
            ParserStage = statuses.Count > 0
                ? "meta_change_value_statuses"
                : messages.Count > 0
                    ? "meta_change_value_messages"
                    : "meta_change_value"
        };

        var normalizedPayload = BuildNormalizedPayload(
            root,
            messages,
            statuses,
            parseReason,
            diagnostics,
            sourceShape,
            primaryDisplayPhone,
            primaryMetadataPhoneNumberId,
            primaryContact);

        return new ParsedWhatsAppEnvelope(
            messages,
            statuses,
            primaryContact,
            primaryContactUserId,
            primaryContactParentUserId,
            primaryDisplayPhone,
            primaryMetadataPhoneNumberId,
            eventType,
            parseReason,
            diagnostics,
            sourceShape,
            normalizedPayload);
    }

    private static JsonElement BuildNormalizedPayload(
        JsonElement rawRoot,
        IReadOnlyCollection<ParsedMessageNode> messages,
        IReadOnlyCollection<JsonElement> statuses,
        string parseReason,
        ParserDiagnostics diagnostics,
        string sourceShape,
        string? primaryDisplayPhone,
        string? primaryMetadataPhoneNumberId,
        JsonElement primaryContact)
    {
        var dto = new
        {
            source_shape = sourceShape,
            parse_reason = parseReason,
            messages = messages.Select(message => new
            {
                source = message.Source,
                id = GetString(message.RawMessage, "id"),
                from = NormalizeE164(GetString(message.RawMessage, "from")),
                from_user_id = message.FromUserId,
                from_parent_user_id = message.FromParentUserId,
                context_message_id = GetString(message.RawMessage, "context", "id"),
                text = GetString(message.RawMessage, "text", "body"),
                button_payload =
                    GetString(message.RawMessage, "button", "payload")
                    ?? GetString(message.RawMessage, "interactive", "button_reply", "id")
                    ?? GetString(message.RawMessage, "interactive", "list_reply", "id"),
                media = ResolveMediaMetadata(message.RawMessage)
            }).ToArray(),
            statuses = statuses.Select(status => new
            {
                id = GetString(status, "id"),
                recipient_id = NormalizeE164(GetString(status, "recipient_id")),
                recipient_user_id = GetString(status, "recipient_user_id"),
                parent_recipient_user_id = GetString(status, "parent_recipient_user_id"),
                status = GetString(status, "status"),
                timestamp = GetString(status, "timestamp"),
                conversation_id = GetString(status, "conversation", "id"),
                pricing_model = GetString(status, "pricing", "pricing_model"),
                pricing_category = GetString(status, "pricing", "category"),
                errors = status.TryGetProperty("errors", out var errors) ? errors.Clone() : (JsonElement?)null
            }).ToArray(),
            metadata = new
            {
                primary_display_phone = NormalizeE164(primaryDisplayPhone),
                primary_phone_number_id = primaryMetadataPhoneNumberId,
                contact_wa_id = NormalizeE164(GetString(primaryContact, "wa_id")),
                contact_user_id = GetString(primaryContact, "user_id"),
                contact_parent_user_id = GetString(primaryContact, "parent_user_id"),
                contact_username = GetString(primaryContact, "profile", "username")
            },
            parser_diagnostics = diagnostics,
            raw_payload = rawRoot.Clone()
        };

        return JsonSerializer.SerializeToElement(dto);
    }

    private static object? ResolveMediaMetadata(JsonElement message)
    {
        var mediaType = GetString(message, "type");
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return null;
        }

        if (!message.TryGetProperty(mediaType, out var media) || media.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new
        {
            type = mediaType,
            id = GetString(media, "id"),
            mime_type = GetString(media, "mime_type"),
            sha256 = GetString(media, "sha256"),
            caption = GetString(media, "caption"),
            filename = GetString(media, "filename")
        };
    }

    private static IEnumerable<ParsedValueNode> ExtractValueNodes(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("entry", out var entries)
            && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var change in changes.EnumerateArray())
                {
                    if (!change.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var field = GetString(change, "field");
                    yield return new ParsedValueNode(field, value.Clone(), "meta_envelope");
                }
            }
        }

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("value", out var directValue)
            && directValue.ValueKind == JsonValueKind.Object)
        {
            var field = GetString(root, "field");
            yield return new ParsedValueNode(field, directValue.Clone(), "direct_field_value");
        }

        if (root.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            var seenValues = new HashSet<string>(StringComparer.Ordinal);
            foreach (var inferredValue in ExtractInferredValueNodes(root))
            {
                var key = inferredValue.Value.GetRawText();
                if (seenValues.Add(key))
                {
                    yield return inferredValue;
                }
            }
        }
    }

    private static IEnumerable<ParsedValueNode> ExtractInferredValueNodes(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var hasStatuses = node.TryGetProperty("statuses", out var statuses)
                && statuses.ValueKind == JsonValueKind.Array;
            var hasMessages = node.TryGetProperty("messages", out var messages)
                && messages.ValueKind == JsonValueKind.Array;
            var hasMetadata = node.TryGetProperty("metadata", out var metadata)
                && metadata.ValueKind == JsonValueKind.Object;
            var hasContacts = node.TryGetProperty("contacts", out var contacts)
                && contacts.ValueKind == JsonValueKind.Array;

            if (hasStatuses || hasMessages || hasMetadata || hasContacts)
            {
                yield return new ParsedValueNode("inferred", node.Clone(), "inferred_value");
            }

            foreach (var property in node.EnumerateObject())
            {
                foreach (var inferred in ExtractInferredValueNodes(property.Value))
                {
                    yield return inferred;
                }
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                foreach (var inferred in ExtractInferredValueNodes(item))
                {
                    yield return inferred;
                }
            }
        }
    }

    private static ParserDiagnostics BuildParserDiagnostics(JsonElement root, IReadOnlyCollection<ParsedValueNode> values)
    {
        var entry = default(JsonElement);
        var foundEntry = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("entry", out entry)
            && entry.ValueKind == JsonValueKind.Array;
        var foundChanges = foundEntry
            && entry.EnumerateArray().Any(static item =>
                item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("changes", out var changesNode)
                && changesNode.ValueKind == JsonValueKind.Array);
        var foundValue = values.Count > 0;
        return new ParserDiagnostics(
            foundEntry,
            foundChanges,
            foundValue,
            false,
            false,
            "initial",
            null,
            BuildPayloadStructureSummary(root));
    }

    private static string BuildPayloadStructureSummary(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return $"root:{root.ValueKind}";
        }

        return string.Join(",", root.EnumerateObject().Select(static property =>
            $"{property.Name}:{property.Value.ValueKind}"));
    }

    private static bool IsRecognizableStatusNode(JsonElement status)
    {
        var statusValue = GetString(status, "status");
        if (string.IsNullOrWhiteSpace(statusValue))
        {
            return false;
        }

        if (KnownMetaStatusValues.Contains(statusValue))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(GetString(status, "id"))
               || !string.IsNullOrWhiteSpace(GetString(status, "recipient_id"))
               || !string.IsNullOrWhiteSpace(GetString(status, "timestamp"));
    }

    private static string? GetFirstErrorCode(JsonElement status)
    {
        if (status.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!status.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var error in errors.EnumerateArray())
        {
            if (error.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var code = GetString(error, "code");
            if (!string.IsNullOrWhiteSpace(code))
            {
                return code;
            }
        }

        return null;
    }

    private static string? NormalizeE164(string? rawPhone)
        => MetaWhatsAppPhoneCanonicalizer.ToCanonicalE164Br(rawPhone);

    private static string? ExtractInteractionIdFromPayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        const string prefix = "ctx:";
        var index = payload.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var interactionId = payload[(index + prefix.Length)..].Trim();
        return string.IsNullOrWhiteSpace(interactionId) ? null : interactionId;
    }

    private static string? GetString(JsonElement element, params string[] path)
    {
        if (element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String
            ? current.GetString()
            : current.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                ? current.ToString()
                : null;
    }

    private static string? NormalizeResponse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToUpperInvariant();
        return normalized switch
        {
            "SIM" => "SIM",
            "NÃO" => "NAO",
            "NAO" => "NAO",
            _ => normalized
        };
    }

    private static JsonElement? GetJsonElement(JsonElement element, params string[] path)
    {
        if (element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.Clone();
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static (string? Key, string Source) ResolveCanonicalCorrelationKey(
        string? parentUserId,
        string? userId,
        string? waIdOrPhone)
    {
        if (!string.IsNullOrWhiteSpace(parentUserId))
        {
            return ($"parent_user_id:{parentUserId}", "parent_user_id");
        }

        if (!string.IsNullOrWhiteSpace(userId))
        {
            return ($"user_id:{userId}", "user_id");
        }

        if (!string.IsNullOrWhiteSpace(waIdOrPhone))
        {
            return ($"wa_id:{waIdOrPhone}", "wa_id");
        }

        return (null, "none");
    }
}

public sealed record ParsedWhatsAppEvent(
    string? CustomerPhone,
    string? CustomerWaId,
    string? SourcePhoneRaw,
    string? CustomerWaIdRaw,
    string? CustomerUserId,
    string? CustomerParentUserId,
    string? CustomerUsername,
    string? ContextMessageId,
    string? MetaMessageId,
    string? ButtonPayload,
    string? InteractionId,
    string? Response,
    JsonElement? Message,
    string? InteractiveType,
    JsonElement? FlowResponseJson,
    bool IsFlowReply,
    string SourceShape,
    string MessageSource,
    string? FromUserId,
    string? FromParentUserId,
    string? ContactUserId,
    string? ContactParentUserId,
    string? DestinationPhoneNumberId,
    string? DestinationDisplayPhone,
    string EventType,
    string ParseReason,
    ParserDiagnostics ParserDiagnostics,
    JsonElement NormalizedPayload,
    object? MediaMetadata,
    string? StatusValue,
    string? StatusRecipientId,
    string? StatusRecipientUserId,
    string? StatusRecipientParentUserId,
    string? StatusConversationId,
    string? StatusPricingCategory,
    string? StatusErrorCode,
    string? CanonicalCorrelationKey,
    string CanonicalCorrelationSource);

public sealed record ParsedWhatsAppEnvelope(
    IReadOnlyCollection<ParsedMessageNode> Messages,
    IReadOnlyCollection<JsonElement> Statuses,
    JsonElement PrimaryContact,
    string? PrimaryContactUserId,
    string? PrimaryContactParentUserId,
    string? PrimaryDisplayPhone,
    string? PrimaryMetadataPhoneNumberId,
    string EventType,
    string ParseReason,
    ParserDiagnostics ParserDiagnostics,
    string SourceShape,
    JsonElement NormalizedPayload);

public sealed record ParserDiagnostics(
    bool FoundEntry,
    bool FoundChanges,
    bool FoundValue,
    bool FoundStatuses,
    bool FoundMessages,
    string ParserStage,
    string? MissingRequiredStatusFields,
    string PayloadStructureSummary);

public sealed record ParsedMessageNode(
    JsonElement RawMessage,
    string Source,
    string? FromUserId,
    string? FromParentUserId);

public sealed record ParsedValueNode(
    string? Field,
    JsonElement Value,
    string Shape);
