using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace AplicacoesOnline.Services.MetaWhatsApp;

public sealed class MetaWhatsAppViewerSessionService
{
    public const string RoleReadOnly = "read_only";
    public const string RoleOperator = "operator";
    public const string SessionCookieName = "viewer_session";
    public const string CsrfCookieName = "viewer_csrf";
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(20);

    private readonly ConcurrentDictionary<string, ViewerSessionState> _sessions = new(StringComparer.Ordinal);
    private readonly IDistributedCache? _distributedCache;
    private readonly ILogger<MetaWhatsAppViewerSessionService> _logger;
    private readonly bool _preferDistributedStore;
    private readonly TimeSpan _renewThreshold;
    private readonly TimeSpan _maxLifetime;

    public MetaWhatsAppViewerSessionService(
        IConfiguration configuration,
        ILogger<MetaWhatsAppViewerSessionService> logger,
        IDistributedCache? distributedCache = null)
    {
        _logger = logger;
        _distributedCache = distributedCache;
        _preferDistributedStore = configuration.GetValue("MetaWhatsAppViewer:Session:UseDistributedCache", false);

        var renewThresholdMinutes = Math.Clamp(configuration.GetValue("MetaWhatsAppViewer:Session:RenewThresholdMinutes", 5), 1, 60);
        _renewThreshold = TimeSpan.FromMinutes(renewThresholdMinutes);

        var maxLifetimeHours = Math.Clamp(configuration.GetValue("MetaWhatsAppViewer:Session:MaxLifetimeHours", 8), 1, 72);
        _maxLifetime = TimeSpan.FromHours(maxLifetimeHours);

        _logger.LogInformation(
            "Viewer session storage configured. PreferDistributedStore={PreferDistributedStore}. DistributedCacheAvailable={DistributedCacheAvailable}. RenewThresholdMinutes={RenewThresholdMinutes}. MaxLifetimeHours={MaxLifetimeHours}",
            _preferDistributedStore,
            _distributedCache is not null,
            _renewThreshold.TotalMinutes,
            _maxLifetime.TotalHours);
    }

    public ViewerSessionHandle CreateSession(string? role = null, TimeSpan? lifetime = null)
        => CreateSession(role, username: null, canViewSensitiveData: !string.Equals(role, RoleReadOnly, StringComparison.OrdinalIgnoreCase), lifetime);

    public ViewerSessionHandle CreateSession(string? role, string? username, bool canViewSensitiveData, TimeSpan? lifetime = null)
    {
        CleanupExpiredSessions();

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var csrfToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var createdAt = DateTimeOffset.UtcNow;
        var effectiveLifetime = lifetime ?? DefaultLifetime;
        var expiresAt = createdAt.Add(effectiveLifetime);

        var session = new ViewerSessionState
        {
            Token = token,
            CsrfToken = csrfToken,
            Role = NormalizeRole(role),
            Username = string.IsNullOrWhiteSpace(username) ? null : username.Trim(),
            CanViewSensitiveData = canViewSensitiveData,
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
            LastSeenAt = createdAt
        };

        Save(session);
        return new ViewerSessionHandle(token, csrfToken, session.Role, session.Username, session.CanViewSensitiveData, expiresAt);
    }

    public bool IsValid(string? token)
    {
        var session = GetActiveSession(token, renewIfNeeded: true);
        return session is not null;
    }

    public bool ValidateCsrfToken(string? token, string? csrfToken)
    {
        var session = GetActiveSession(token, renewIfNeeded: true);
        if (session is null || string.IsNullOrWhiteSpace(csrfToken))
        {
            return false;
        }

        return string.Equals(session.CsrfToken, csrfToken.Trim(), StringComparison.Ordinal);
    }

    public bool Revoke(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var removed = _sessions.TryRemove(token, out _);
        RemoveDistributed(token);
        return removed;
    }

    public bool TryGetSessionMetadata(string? token, out ViewerSessionMetadata metadata)
    {
        var session = GetActiveSession(token, renewIfNeeded: false);
        if (session is null)
        {
            metadata = ViewerSessionMetadata.Empty;
            return false;
        }

        metadata = new ViewerSessionMetadata(
            session.Token,
            session.Role,
            session.Username,
            session.CanViewSensitiveData,
            session.CreatedAt,
            session.ExpiresAt,
            session.LastSeenAt);
        return true;
    }

    private ViewerSessionState? GetActiveSession(string? token, bool renewIfNeeded)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var session = Load(token);
        if (session is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (session.ExpiresAt <= now)
        {
            Revoke(token);
            return null;
        }

        if (renewIfNeeded)
        {
            var remaining = session.ExpiresAt - now;
            if (remaining <= _renewThreshold)
            {
                var cappedExpiration = session.CreatedAt.Add(_maxLifetime);
                var renewedExpiration = now.Add(DefaultLifetime);
                session.ExpiresAt = renewedExpiration <= cappedExpiration ? renewedExpiration : cappedExpiration;
                session.LastSeenAt = now;
                Save(session);
            }
        }

        return session;
    }

    private ViewerSessionState? Load(string token)
    {
        if (_sessions.TryGetValue(token, out var local))
        {
            return local;
        }

        if (!_preferDistributedStore || _distributedCache is null)
        {
            return null;
        }

        var payload = _distributedCache.Get(GetCacheKey(token));
        if (payload is null || payload.Length == 0)
        {
            return null;
        }

        try
        {
            var fromDistributed = JsonSerializer.Deserialize<ViewerSessionState>(payload);
            if (fromDistributed is null)
            {
                return null;
            }

            _sessions[token] = fromDistributed;
            return fromDistributed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao desserializar sessão distribuída do visualizador.");
            return null;
        }
    }

    private void Save(ViewerSessionState state)
    {
        _sessions[state.Token] = state;

        if (!_preferDistributedStore || _distributedCache is null)
        {
            return;
        }

        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(state);
            _distributedCache.Set(
                GetCacheKey(state.Token),
                payload,
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpiration = state.ExpiresAt
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao persistir sessão distribuída do visualizador.");
        }
    }

    private void RemoveDistributed(string token)
    {
        if (!_preferDistributedStore || _distributedCache is null)
        {
            return;
        }

        try
        {
            _distributedCache.Remove(GetCacheKey(token));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao remover sessão distribuída do visualizador.");
        }
    }

    private static string GetCacheKey(string token) => $"viewer-session:{token}";

    private static string NormalizeRole(string? role)
    {
        if (string.Equals(role, RoleReadOnly, StringComparison.OrdinalIgnoreCase))
        {
            return RoleReadOnly;
        }

        return RoleOperator;
    }

    private void CleanupExpiredSessions()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in _sessions)
        {
            if (item.Value.ExpiresAt <= now)
            {
                _sessions.TryRemove(item.Key, out _);
            }
        }
    }

    private sealed class ViewerSessionState
    {
        public string Token { get; set; } = string.Empty;
        public string CsrfToken { get; set; } = string.Empty;
        public string Role { get; set; } = RoleOperator;
        public string? Username { get; set; }
        public bool CanViewSensitiveData { get; set; } = true;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
    }
}

public readonly record struct ViewerSessionHandle(string Token, string CsrfToken, string Role, string? Username, bool CanViewSensitiveData, DateTimeOffset ExpiresAt);
public readonly record struct ViewerSessionMetadata(string SessionId, string Role, string? Username, bool CanViewSensitiveData, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset LastSeenAt)
{
    public static ViewerSessionMetadata Empty { get; } = new(string.Empty, MetaWhatsAppViewerSessionService.RoleOperator, null, true, DateTimeOffset.MinValue, DateTimeOffset.MinValue, DateTimeOffset.MinValue);
}
