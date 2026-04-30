using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace AplicacoesOnline.Services.MetaWhatsApp;

public sealed class MetaWhatsAppChatAuthenticationService
{
    private static readonly TimeSpan DefaultSessionDuration = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, ChatAuthenticationState> _sessions = new(StringComparer.Ordinal);
    private readonly IDistributedCache? _distributedCache;
    private readonly ILogger<MetaWhatsAppChatAuthenticationService> _logger;
    private readonly bool _preferDistributedStore;

    public MetaWhatsAppChatAuthenticationService(
        IConfiguration configuration,
        ILogger<MetaWhatsAppChatAuthenticationService> logger,
        IDistributedCache? distributedCache = null)
    {
        _logger = logger;
        _distributedCache = distributedCache;
        _preferDistributedStore = configuration.GetValue("MetaWhatsAppWebhook:ChatAuthentication:UseDistributedCache", true);
    }

    public DateTimeOffset MarkAuthenticated(
        string phoneE164,
        DateTimeOffset nowUtc,
        string? interactionId,
        string? executionId,
        string? flowKey,
        string? flowName,
        string? authenticatedEmail)
    {
        var normalizedPhone = NormalizePhone(phoneE164);
        var expiresAt = nowUtc.Add(DefaultSessionDuration);
        var state = new ChatAuthenticationState
        {
            PhoneE164 = normalizedPhone,
            InteractionId = Normalize(interactionId),
            ExecutionId = Normalize(executionId),
            FlowKey = Normalize(flowKey),
            FlowName = Normalize(flowName),
            AuthenticatedEmail = Normalize(authenticatedEmail),
            AuthenticatedAt = nowUtc,
            ExpiresAt = expiresAt
        };

        Save(state);
        return expiresAt;
    }

    public bool TryGetActive(string? phoneE164, DateTimeOffset nowUtc, out ChatAuthenticationState? state)
    {
        state = null;
        var normalizedPhone = NormalizePhone(phoneE164);
        if (string.IsNullOrWhiteSpace(normalizedPhone))
        {
            return false;
        }

        state = Load(normalizedPhone);
        if (state is null)
        {
            return false;
        }

        if (state.ExpiresAt <= nowUtc)
        {
            Revoke(normalizedPhone);
            state = null;
            return false;
        }

        return true;
    }

    public bool Revoke(string? phoneE164)
    {
        var normalizedPhone = NormalizePhone(phoneE164);
        if (string.IsNullOrWhiteSpace(normalizedPhone))
        {
            return false;
        }

        var removed = _sessions.TryRemove(normalizedPhone, out _);
        RemoveDistributed(normalizedPhone);
        return removed;
    }

    private ChatAuthenticationState? Load(string phoneE164)
    {
        if (_sessions.TryGetValue(phoneE164, out var local))
        {
            return local;
        }

        if (!_preferDistributedStore || _distributedCache is null)
        {
            return null;
        }

        var payload = _distributedCache.Get(GetCacheKey(phoneE164));
        if (payload is null || payload.Length == 0)
        {
            return null;
        }

        try
        {
            var fromDistributed = JsonSerializer.Deserialize<ChatAuthenticationState>(payload);
            if (fromDistributed is null)
            {
                return null;
            }

            _sessions[phoneE164] = fromDistributed;
            return fromDistributed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao desserializar sessão de autenticação do chat.");
            return null;
        }
    }

    private void Save(ChatAuthenticationState state)
    {
        _sessions[state.PhoneE164] = state;

        if (!_preferDistributedStore || _distributedCache is null)
        {
            return;
        }

        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(state);
            _distributedCache.Set(
                GetCacheKey(state.PhoneE164),
                payload,
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpiration = state.ExpiresAt
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao persistir sessão de autenticação do chat.");
        }
    }

    private void RemoveDistributed(string phoneE164)
    {
        if (!_preferDistributedStore || _distributedCache is null)
        {
            return;
        }

        try
        {
            _distributedCache.Remove(GetCacheKey(phoneE164));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao remover sessão de autenticação do chat.");
        }
    }

    private static string GetCacheKey(string phoneE164)
        => $"meta-whatsapp-chat-auth:{phoneE164}";

    private static string NormalizePhone(string? phone)
        => MetaWhatsAppPhoneCanonicalizer.ToCanonicalE164Br(phone) ?? string.Empty;

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class ChatAuthenticationState
{
    public string PhoneE164 { get; set; } = string.Empty;
    public string? InteractionId { get; set; }
    public string? ExecutionId { get; set; }
    public string? FlowKey { get; set; }
    public string? FlowName { get; set; }
    public string? AuthenticatedEmail { get; set; }
    public DateTimeOffset AuthenticatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
