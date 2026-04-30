using AplicacoesOnline.Models.MetaWhatsApp;

namespace AplicacoesOnline.Services.MetaWhatsApp;

public sealed class MetaWhatsAppPersistentLogNoOpService : IMetaWhatsAppPersistentLogService
{
    public Task<bool> AppendEventAsync(string? rawPhone, string eventName, DateTimeOffset timestamp, IReadOnlyDictionary<string, string?> fields, string? deduplicationKey = null)
        => Task.FromResult(true);

    public Task<PersistentLogProbeResult> WriteProbeAsync(string? reason = null)
        => Task.FromResult(new PersistentLogProbeResult(true, "NOOP", "NOOP", "NOOP", null));
}
