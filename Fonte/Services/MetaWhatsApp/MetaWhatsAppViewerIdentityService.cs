using System.Text.Json;

namespace AplicacoesOnline.Services.MetaWhatsApp;

public sealed class MetaWhatsAppViewerIdentityService
{
    private readonly IReadOnlyList<ViewerIdentityUser> _users;

    public MetaWhatsAppViewerIdentityService(IConfiguration configuration)
    {
        var usersJson = configuration["MetaWhatsAppViewer:Identity:UsersJson"];
        if (!string.IsNullOrWhiteSpace(usersJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<ViewerIdentityUser>>(usersJson);
                _users = (parsed ?? []).Where(static item => item.IsValid()).ToArray();
                return;
            }
            catch
            {
                // fallback below
            }
        }

        _users = configuration
            .GetSection("MetaWhatsAppViewer:Identity:Users")
            .Get<List<ViewerIdentityUser>>()?
            .Where(static item => item.IsValid())
            .ToArray()
            ?? [];
    }

    public bool TryAuthenticate(string? username, string? password, out ViewerIdentityUser user)
    {
        user = ViewerIdentityUser.Empty;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return false;
        }

        user = _users.FirstOrDefault(item =>
            string.Equals(item.Username, username.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Password, password, StringComparison.Ordinal))
            ?? ViewerIdentityUser.Empty;

        return user.IsValid();
    }
}

public sealed class ViewerIdentityUser
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Role { get; set; } = MetaWhatsAppViewerSessionService.RoleOperator;
    public bool CanViewSensitiveData { get; set; } = true;

    public bool IsValid() => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);

    public static ViewerIdentityUser Empty { get; } = new();
}
