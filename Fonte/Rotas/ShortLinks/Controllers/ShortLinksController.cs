using AplicacoesOnline.Models.ShortLinks;
using AplicacoesOnline.Options;
using AplicacoesOnline.Services.ShortLinks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AplicacoesOnline.Rotas.ShortLinks.Controllers;

[ApiController]
public class ShortLinksController : ControllerBase
{
    private readonly IShortLinksService _shortLinksService;
    private readonly IOptions<ShortLinksOptions> _options;
    private readonly ILogger<ShortLinksController> _logger;

    public ShortLinksController(
        IShortLinksService shortLinksService,
        IOptions<ShortLinksOptions> options,
        ILogger<ShortLinksController> logger)
    {
        _shortLinksService = shortLinksService;
        _options = options;
        _logger = logger;
    }

    [HttpPost("/api/short-links")]
    public IActionResult Create([FromBody] ShortLinkCreateRequest request)
    {
        if (!IsCurrentHostAllowed(_options.Value.InternalApiHosts))
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            var modelErrors = ModelState
                .Where(x => x.Value?.Errors.Count > 0)
                .ToDictionary(
                    x => x.Key,
                    x => x.Value!.Errors.Select(e => string.IsNullOrWhiteSpace(e.ErrorMessage) ? "invalid_value" : e.ErrorMessage).ToArray(),
                    StringComparer.OrdinalIgnoreCase);

            return BadRequest(new ApiErrorResponse
            {
                Ok = false,
                Error = "validation_error",
                Detail = "Payload inválido para criação do short link.",
                TraceId = HttpContext.TraceIdentifier,
                Errors = modelErrors
            });
        }

        var (response, validationErrors) = _shortLinksService.Create(request);
        if (validationErrors is not null)
        {
            return BadRequest(new ApiErrorResponse
            {
                Ok = false,
                Error = "validation_error",
                Detail = "Não foi possível criar o short link com os dados informados.",
                TraceId = HttpContext.TraceIdentifier,
                Errors = validationErrors
            });
        }

        return Ok(response);
    }

    [AllowAnonymous]
    [HttpGet("/r/{token}")]
    [HttpGet("/s/{token}")]
    public IActionResult ResolveAndRedirect([FromRoute] string token)
    {
        if (!IsCurrentHostAllowed(_options.Value.PublicResolveHosts))
        {
            return NotFound();
        }

        ApplyNoStoreCacheHeaders();

        var resolved = _shortLinksService.Resolve(token);
        if (resolved.Success && !string.IsNullOrWhiteSpace(resolved.DestinationUrl))
        {
            return Redirect(resolved.DestinationUrl);
        }

        if (resolved.Expired)
        {
            return BadRequest(new ApiErrorResponse
            {
                Ok = false,
                Error = "short_link_expired",
                Detail = "Este link curto expirou. Solicite um novo link.",
                TraceId = HttpContext.TraceIdentifier
            });
        }

        _logger.LogInformation("Short link inválido acessado. TokenLength={TokenLength} TraceId={TraceId}", token?.Length ?? 0, HttpContext.TraceIdentifier);
        return BadRequest(new ApiErrorResponse
        {
            Ok = false,
            Error = "short_link_invalid",
            Detail = "Este link curto é inválido ou foi corrompido.",
            TraceId = HttpContext.TraceIdentifier
        });
    }
    private void ApplyNoStoreCacheHeaders()
    {
        Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.Expires = "0";
    }

    private bool IsCurrentHostAllowed(string[] allowedHosts)
    {
        if (allowedHosts is null || allowedHosts.Length == 0)
        {
            return true;
        }

        var currentHost = Request.Host.Host;
        if (string.IsNullOrWhiteSpace(currentHost))
        {
            return false;
        }

        return allowedHosts.Any(allowedHost =>
            !string.IsNullOrWhiteSpace(allowedHost)
            && string.Equals(currentHost, allowedHost.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
