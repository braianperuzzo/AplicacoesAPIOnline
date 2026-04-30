using Microsoft.Data.SqlClient;

namespace AplicacoesOnline.Services.MetaWhatsApp;

public interface IDatabaseConnectionStringProvider
{
    bool TryBuildConnectionString(out string connectionString, out IReadOnlyCollection<string> missingKeys);
}

public sealed class DatabaseConnectionStringProvider : IDatabaseConnectionStringProvider
{
    private static readonly string[] RequiredKeys =
    [
        "DB_HOST",
        "DB_NAME",
        "DB_USER",
        "DB_PASS"
    ];

    private readonly IConfiguration _configuration;

    public DatabaseConnectionStringProvider(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public bool TryBuildConnectionString(out string connectionString, out IReadOnlyCollection<string> missingKeys)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        foreach (var key in RequiredKeys)
        {
            var value = _configuration[key];
            if (string.IsNullOrWhiteSpace(value))
            {
                missing.Add(key);
                continue;
            }

            resolved[key] = value.Trim();
        }

        if (missing.Count > 0)
        {
            connectionString = string.Empty;
            missingKeys = missing;
            return false;
        }

        connectionString = new SqlConnectionStringBuilder
        {
            DataSource = resolved["DB_HOST"],
            InitialCatalog = resolved["DB_NAME"],
            UserID = resolved["DB_USER"],
            Password = resolved["DB_PASS"],
            Encrypt = false,
            TrustServerCertificate = true,
            ConnectTimeout = 15
        }.ConnectionString;

        missingKeys = Array.Empty<string>();
        return true;
    }
}
