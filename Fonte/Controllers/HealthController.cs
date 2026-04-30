using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace AplicacoesOnline.Controllers;

[ApiController]
public class HealthController : ControllerBase
{
    private readonly EndpointDataSource _endpointDataSource;

    public HealthController(EndpointDataSource endpointDataSource)
    {
        _endpointDataSource = endpointDataSource;
    }

    [HttpGet("/health")]
    public IActionResult Health() => Ok("OK");

    [HttpGet("/health/routes")]
    public IActionResult Routes()
    {
        var routes = _endpointDataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => new
            {
                route = endpoint.RoutePattern.RawText,
                methods = endpoint.Metadata
                    .OfType<HttpMethodMetadata>()
                    .SelectMany(metadata => metadata.HttpMethods)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(method => method)
                    .ToArray()
            })
            .OrderBy(entry => entry.route)
            .ToArray();

        return Ok(new
        {
            totalRoutes = routes.Length,
            routes
        });
    }
}
