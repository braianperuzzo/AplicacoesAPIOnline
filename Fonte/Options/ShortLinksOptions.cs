namespace AplicacoesOnline.Options;

public class ShortLinksOptions
{
    public const string SectionName = "ShortLinks";

    public string PublicBaseUrl { get; set; } = "https://encurtador.redutoresibr.com.br";

    public string RoutePrefix { get; set; } = "r";

    public int DefaultExpirationHours { get; set; } = 168;

    public string[] AllowedSchemes { get; set; } = ["https", "http"];

    public int MaxUrlLength { get; set; } = 2048;

    public int TokenLength { get; set; } = 8;

    public string StorageFilePath { get; set; } = "Configuracoes/ShortLinks/short-links-storage.json";

    // Hosts permitidos para criação interna (POST /api/short-links).
    // Vazio = sem restrição por host.
    public string[] InternalApiHosts { get; set; } = [];

    // Hosts permitidos para resolução pública (GET /r/{token} e /s/{token}).
    // Vazio = sem restrição por host.
    public string[] PublicResolveHosts { get; set; } = [];

    // Vazio = sem restrição de host (comportamento atual).
    // Preenchido = somente hosts confiáveis permitidos para destination_url.
    public string[] TrustedHostsAllowlist { get; set; } = [];
}
