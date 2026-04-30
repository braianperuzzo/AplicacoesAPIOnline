using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

ConfigureGlobalSettings(builder);
ConfigureRouteSettings(builder);
builder.Configuration.AddEnvironmentVariables();

builder.Services.AddOptions<AplicacoesOnline.Models.MetaWhatsApp.MetaWhatsAppWebhookOptions>()
    .Bind(builder.Configuration.GetSection(AplicacoesOnline.Models.MetaWhatsApp.MetaWhatsAppWebhookOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<AplicacoesOnline.Models.MetaWhatsApp.MetaWhatsAppWebhookOptions>, AplicacoesOnline.Models.MetaWhatsApp.MetaWhatsAppWebhookOptionsValidator>();
builder.Services.Configure<AplicacoesOnline.Models.MetaWhatsApp.N8nWebhookSecurityOptions>(
    builder.Configuration.GetSection(AplicacoesOnline.Models.MetaWhatsApp.N8nWebhookSecurityOptions.SectionName));
builder.Services.Configure<AplicacoesOnline.Models.MetaWhatsApp.MetaWhatsAppFlowEndpointOptions>(
    builder.Configuration.GetSection(AplicacoesOnline.Models.MetaWhatsApp.MetaWhatsAppFlowEndpointOptions.SectionName));
builder.Services.AddSingleton<AplicacoesOnline.Services.MetaWhatsApp.MetaWhatsAppInteractionRouter>();
builder.Services.AddSingleton<AplicacoesOnline.Services.MetaWhatsApp.FlowInboundContractResolver>();
builder.Services.AddSingleton<AplicacoesOnline.Services.MetaWhatsApp.MetaWhatsAppFlowPreferencesService>();
builder.Services.AddSingleton<AplicacoesOnline.Services.MetaWhatsApp.MetaWhatsAppLoginFlowService>();
builder.Services.AddSingleton<AplicacoesOnline.Services.MetaWhatsApp.MetaWhatsAppFlowCryptoService>();
builder.Services.AddSingleton<AplicacoesOnline.Services.MetaWhatsApp.MetaWhatsAppChatAuthenticationService>();
builder.Services.AddSingleton<AplicacoesOnline.Services.MetaWhatsApp.MetaWhatsAppViewerService>();
builder.Services.AddSingleton<AplicacoesOnline.Services.MetaWhatsApp.MetaWhatsAppViewerExportJobService>();
builder.Services.AddSingleton<AplicacoesOnline.Services.MetaWhatsApp.MetaWhatsAppViewerSessionService>();
builder.Services.AddSingleton<AplicacoesOnline.Services.MetaWhatsApp.MetaWhatsAppViewerIdentityService>();
builder.Services.AddSingleton<AplicacoesOnline.Services.MetaWhatsApp.MetaWhatsAppAuditReportService>();
builder.Services.AddSingleton<AplicacoesOnline.Services.MetaWhatsApp.MetaWhatsAppViewerPresetService>();
builder.Services.AddSingleton<AplicacoesOnline.Services.MetaWhatsApp.MetaWhatsAppManualSendService>();
builder.Services.AddSingleton<AplicacoesOnline.Services.MetaWhatsApp.IMetaWhatsAppSenderResolver, AplicacoesOnline.Services.MetaWhatsApp.MetaWhatsAppSenderResolver>();
builder.Services.AddSingleton<AplicacoesOnline.Services.MetaWhatsApp.IDatabaseConnectionStringProvider, AplicacoesOnline.Services.MetaWhatsApp.DatabaseConnectionStringProvider>();
var useNoOpPersistentLogService = IsEnabled(Environment.GetEnvironmentVariable("WHATSAPP_DIAG_NOOP_PERSISTENT_LOG"));
if (useNoOpPersistentLogService)
{
    builder.Services.AddSingleton<AplicacoesOnline.Services.MetaWhatsApp.IMetaWhatsAppPersistentLogService, AplicacoesOnline.Services.MetaWhatsApp.MetaWhatsAppPersistentLogNoOpService>();
}
else
{
    builder.Services.AddSingleton<AplicacoesOnline.Services.MetaWhatsApp.IMetaWhatsAppPersistentLogService, AplicacoesOnline.Services.MetaWhatsApp.MetaWhatsAppPersistentLogService>();
}
builder.Services.AddHttpClient("MetaWhatsAppN8nForwarder", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.Configure<AplicacoesOnline.Options.ShortLinksOptions>(
    builder.Configuration.GetSection(AplicacoesOnline.Options.ShortLinksOptions.SectionName));
var dataProtectionKeysPath =
    builder.Configuration["DataProtection:KeysPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "Configuracoes", "DataProtectionKeys");
try
{
    Directory.CreateDirectory(dataProtectionKeysPath);
}
catch (Exception ex)
{
    var fallbackDataProtectionKeysPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "AplicacoesOnline",
        "DataProtectionKeys");

    Directory.CreateDirectory(fallbackDataProtectionKeysPath);

    Console.Error.WriteLine(
        "[Startup][DataProtection] Falha ao criar diretório configurado '{0}'. Motivo: {1}. Usando fallback '{2}'.",
        dataProtectionKeysPath,
        ex.Message,
        fallbackDataProtectionKeysPath);

    dataProtectionKeysPath = fallbackDataProtectionKeysPath;
}
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
builder.Services.Configure<Microsoft.AspNetCore.DataProtection.DataProtectionOptions>(options =>
{
    options.ApplicationDiscriminator = "AplicacoesOnline.ShortLinks";
});
builder.Services.AddSingleton<AplicacoesOnline.Services.ShortLinks.IShortLinksService, AplicacoesOnline.Services.ShortLinks.ShortLinksService>();
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddDistributedMemoryCache();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
});
builder.Services.AddCors(options =>
{
    options.AddPolicy("WebhookCors", policy =>
    {
        policy
            .SetIsOriginAllowed(static origin =>
            {
                if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                {
                    return false;
                }

                if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return uri.Host.Equals("pipe.run", StringComparison.OrdinalIgnoreCase)
                       || uri.Host.EndsWith(".pipe.run", StringComparison.OrdinalIgnoreCase)
                       || uri.Host.Equals("piperun.com.br", StringComparison.OrdinalIgnoreCase)
                       || uri.Host.EndsWith(".piperun.com.br", StringComparison.OrdinalIgnoreCase);
            })
            .AllowAnyHeader()
            .WithMethods("GET", "POST", "OPTIONS");
    });
});

var app = builder.Build();
var logger = app.Logger;
var interactionRegisterVersion = Environment.GetEnvironmentVariable("INTERACTION_REGISTER_VERSION")
    ?? typeof(Program).Assembly.GetName().Version?.ToString()
    ?? "unknown";
var disableWhatsAppCustomMiddlewares = IsEnabled(Environment.GetEnvironmentVariable("WHATSAPP_DISABLE_CUSTOM_MIDDLEWARES"));
var disableWhatsAppAuditMiddleware = IsEnabled(Environment.GetEnvironmentVariable("WHATSAPP_DISABLE_AUDIT_MIDDLEWARE"));
var disableWhatsAppAuditBodyRead = IsEnabled(Environment.GetEnvironmentVariable("WHATSAPP_DISABLE_AUDIT_BODY_READ"));
var disableWhatsAppPreMvcDiag = IsEnabled(Environment.GetEnvironmentVariable("WHATSAPP_DISABLE_PREMVC_DIAG"));
var enableWhatsAppDiagnostic500 = IsEnabled(Environment.GetEnvironmentVariable("WHATSAPP_ENABLE_DIAGNOSTIC_500"))
    && app.Environment.IsStaging();

var logoPath = Path.Combine(builder.Environment.ContentRootPath, "Imagens", "Logotipo.png");
logger.LogInformation(
    "WhatsApp middleware flags: DisableCustom={DisableCustom}; DisableAudit={DisableAudit}; DisableAuditBodyRead={DisableAuditBodyRead}; DisablePreMvcDiag={DisablePreMvcDiag}; EnableDiagnostic500={EnableDiagnostic500}",
    disableWhatsAppCustomMiddlewares,
    disableWhatsAppAuditMiddleware,
    disableWhatsAppAuditBodyRead,
    disableWhatsAppPreMvcDiag,
    enableWhatsAppDiagnostic500);
var configDirectory = Path.Combine(builder.Environment.ContentRootPath, "Configuracoes");
var apiMetaIniPath = Path.Combine(configDirectory, "APIMeta.ini");
var apiN8nIniPath = Path.Combine(configDirectory, "APIN8N.ini");
var metaOptionsSnapshot = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AplicacoesOnline.Models.MetaWhatsApp.MetaWhatsAppWebhookOptions>>().Value;
logger.LogInformation(
    "Startup diagnostics. Environment={Environment}. ContentRoot={ContentRoot}. ConfigDirectory={ConfigDirectory}. APIMetaExists={APIMetaExists}. APIN8NExists={APIN8NExists}. APIMetaPath={APIMetaPath}. APIN8NPath={APIN8NPath}. PersistentLogDirectoryConfigured={PersistentLogDirectoryConfigured}. PersistentLogDirectoryValue={PersistentLogDirectoryValue}. NoOpPersistentLogService={NoOpPersistentLogService}",
    app.Environment.EnvironmentName,
    builder.Environment.ContentRootPath,
    configDirectory,
    File.Exists(apiMetaIniPath),
    File.Exists(apiN8nIniPath),
    apiMetaIniPath,
    apiN8nIniPath,
    !string.IsNullOrWhiteSpace(metaOptionsSnapshot.PersistentLogDirectory),
    metaOptionsSnapshot.PersistentLogDirectory,
    useNoOpPersistentLogService);
logger.LogInformation(
    "WhatsApp service registrations. Router={Router}. PersistentLogService={PersistentLogService}",
    typeof(AplicacoesOnline.Services.MetaWhatsApp.MetaWhatsAppInteractionRouter).FullName,
    useNoOpPersistentLogService
        ? typeof(AplicacoesOnline.Services.MetaWhatsApp.MetaWhatsAppPersistentLogNoOpService).FullName
        : typeof(AplicacoesOnline.Services.MetaWhatsApp.MetaWhatsAppPersistentLogService).FullName);

logger.LogInformation(
    "DataProtection key persistence configured. KeysPath={KeysPath}",
    dataProtectionKeysPath);

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        var traceId = context.TraceIdentifier;
        var isInteractionRegisterPath =
            context.Request.Path.StartsWithSegments("/webhooks/meta/whatsapp/interactions", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Path.StartsWithSegments("/api/meta/whatsapp/interactions/register", StringComparison.OrdinalIgnoreCase);
        var interactionStage = context.Items.TryGetValue("interactions.register.stage", out var stageObj)
            ? stageObj?.ToString()
            : null;
        var interactionRegisterVersionFromContext = context.Items.TryGetValue("interactions.register.version", out var versionObj)
            ? versionObj?.ToString()
            : null;
        if (isInteractionRegisterPath && string.IsNullOrWhiteSpace(interactionStage))
        {
            interactionStage = "unhandled_before_action_stage";
        }

        var resolvedInteractionRegisterVersion = string.IsNullOrWhiteSpace(interactionRegisterVersionFromContext)
            ? interactionRegisterVersion
            : interactionRegisterVersionFromContext;

        logger.LogError(
            ex,
            "Unhandled exception for {Method} {Path}. TraceId: {TraceId}. InteractionRegisterStage: {InteractionRegisterStage}. ExceptionType: {ExceptionType}. InnerExceptionType: {InnerExceptionType}. InnerMessage: {InnerMessage}",
            context.Request.Method,
            context.Request.Path,
            traceId,
            interactionStage,
            ex.GetType().FullName,
            ex.InnerException?.GetType().FullName,
            ex.InnerException?.Message);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.Headers["X-TraceId"] = traceId;
        if (!string.IsNullOrWhiteSpace(interactionStage))
        {
            context.Response.Headers["X-Interaction-Register-Stage"] = interactionStage;
        }

        if (!string.IsNullOrWhiteSpace(interactionRegisterVersion))
        {
            context.Response.Headers["X-Interaction-Register-Version"] = resolvedInteractionRegisterVersion;
        }

        var errorPayload = new Dictionary<string, object?>
        {
            ["error"] = "InternalServerError",
            ["detail"] = "An unexpected error occurred while processing the request.",
            ["traceId"] = traceId,
            ["stage"] = interactionStage,
            ["version"] = resolvedInteractionRegisterVersion
        };

        if (enableWhatsAppDiagnostic500)
        {
            errorPayload["exception_type"] = ex.GetType().FullName;
            errorPayload["inner_exception_type"] = ex.InnerException?.GetType().FullName;
            errorPayload["inner_message"] = ex.InnerException?.Message;
        }

        await context.Response.WriteAsJsonAsync(errorPayload);
    }
});

app.Use(async (context, next) =>
{
    var pathValue = context.Request.Path.Value;
    if (!string.IsNullOrEmpty(pathValue) && pathValue.Contains("//", StringComparison.Ordinal))
    {
        context.Request.Path = new PathString(System.Text.RegularExpressions.Regex.Replace(pathValue, "/{2,}", "/"));
    }

    await next();
});

if (!disableWhatsAppCustomMiddlewares)
{
    if (!disableWhatsAppPreMvcDiag)
    {
        app.Use(async (context, next) =>
        {
            var isInteractionRoute =
                context.Request.Path.StartsWithSegments("/webhooks/meta/whatsapp/interactions", StringComparison.OrdinalIgnoreCase) ||
                context.Request.Path.StartsWithSegments("/api/meta/whatsapp/interactions", StringComparison.OrdinalIgnoreCase);

            if (!isInteractionRoute)
            {
                await next();
                return;
            }

            var traceId = context.TraceIdentifier;
            logger.LogInformation(
                "pre_mvc_diag.enter TraceId={TraceId} Method={Method} Path={Path} ContentType={ContentType} ContentLength={ContentLength}",
                traceId,
                context.Request.Method,
                context.Request.Path,
                context.Request.ContentType,
                context.Request.ContentLength);

            var readStage = "not_started";
            try
            {
                readStage = "read_attempt";
                var bodyProbe = await TryReadRequestBodyForAuditAsync(context.Request);
                context.Items["pre_mvc_diag.body_probe"] = bodyProbe.Success
                    ? $"ok:size={bodyProbe.BodySize}"
                    : $"fail:stage={bodyProbe.FailureStage};error={bodyProbe.ErrorMessage}";

                logger.LogInformation(
                    "pre_mvc_diag.body_probe TraceId={TraceId} Success={Success} FailureStage={FailureStage} BodySize={BodySize} Error={Error}",
                    traceId,
                    bodyProbe.Success,
                    bodyProbe.FailureStage,
                    bodyProbe.BodySize,
                    bodyProbe.ErrorMessage);
            }
            catch (Exception ex)
            {
                context.Items["pre_mvc_diag.body_probe"] = $"fail:stage={readStage};error={ex.GetType().Name}:{ex.Message}";
                logger.LogWarning(
                    ex,
                    "pre_mvc_diag.exception_before_next TraceId={TraceId} Stage={Stage} Method={Method} Path={Path}. Continuing request pipeline.",
                    traceId,
                    readStage,
                    context.Request.Method,
                    context.Request.Path);
            }

            try
            {
                await next();
                logger.LogInformation(
                    "pre_mvc_diag.exit TraceId={TraceId} Method={Method} Path={Path} StatusCode={StatusCode}",
                    traceId,
                    context.Request.Method,
                    context.Request.Path,
                    context.Response.StatusCode);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "pre_mvc_diag.exception_after_next TraceId={TraceId} Method={Method} Path={Path}",
                    traceId,
                    context.Request.Method,
                    context.Request.Path);
                throw;
            }
        });
    }

    if (!disableWhatsAppAuditMiddleware)
    {
        app.Use(async (context, next) =>
        {
            var isWhatsAppRoute =
                context.Request.Path.StartsWithSegments("/webhooks/meta/whatsapp", StringComparison.OrdinalIgnoreCase) ||
                context.Request.Path.StartsWithSegments("/api/meta/whatsapp", StringComparison.OrdinalIgnoreCase);

            if (!isWhatsAppRoute)
            {
                await next();
                return;
            }

            var requestBody = "-";
            if (!disableWhatsAppAuditBodyRead)
            {
                var requestBodyProbe = await TryReadRequestBodyForAuditAsync(context.Request);
                requestBody = requestBodyProbe.Success
                    ? requestBodyProbe.BodyPreview
                    : $"[BODY_READ_FAILED stage={requestBodyProbe.FailureStage} error={requestBodyProbe.ErrorMessage}]";
            }

            var hasApiKeyHeader = context.Request.Headers.ContainsKey("X-API-Key");
            var hasAuthorizationHeader = context.Request.Headers.ContainsKey("Authorization");
            var traceId = context.TraceIdentifier;

            try
            {
                await next();

                if (context.Response.StatusCode is >= 200 and < 400)
                {
                    logger.LogInformation(
                        "WhatsApp API tentativa de conexão concluída. Success: true. Method: {Method}. Path: {Path}. StatusCode: {StatusCode}. TraceId: {TraceId}",
                        context.Request.Method,
                        context.Request.Path,
                        context.Response.StatusCode,
                        traceId);
                    return;
                }

                logger.LogWarning(
                    "WhatsApp API tentativa de conexão falhou. Success: false. Method: {Method}. Path: {Path}. StatusCode: {StatusCode}. HasApiKeyHeader: {HasApiKeyHeader}. HasAuthorizationHeader: {HasAuthorizationHeader}. RequestBody: {RequestBody}. TraceId: {TraceId}",
                    context.Request.Method,
                    context.Request.Path,
                    context.Response.StatusCode,
                    hasApiKeyHeader,
                    hasAuthorizationHeader,
                    requestBody,
                    traceId);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "WhatsApp API tentativa de conexão lançou exceção. Success: false. Method: {Method}. Path: {Path}. HasApiKeyHeader: {HasApiKeyHeader}. HasAuthorizationHeader: {HasAuthorizationHeader}. RequestBody: {RequestBody}. TraceId: {TraceId}",
                    context.Request.Method,
                    context.Request.Path,
                    hasApiKeyHeader,
                    hasAuthorizationHeader,
                    requestBody,
                    traceId);
                throw;
            }
        });
    }
}

app.MapOpenApi();
app.MapPost("/webhooks/meta/whatsapp/ping-post", () => Results.Ok(new { ok = true }));
app.MapGet("/diag/meta-whatsapp/bootstrap", (HttpContext httpContext) =>
{
    var serviceProvider = httpContext.RequestServices;
    static object Probe(IServiceProvider provider, Type dependencyType)
    {
        try
        {
            _ = provider.GetRequiredService(dependencyType);
            return new
            {
                dependency = dependencyType.FullName,
                resolved = true,
                exception_type = (string?)null,
                inner_exception_type = (string?)null,
                message = "resolved",
                stack_first_lines = Array.Empty<string>()
            };
        }
        catch (Exception ex)
        {
            return new
            {
                dependency = dependencyType.FullName,
                resolved = false,
                exception_type = ex.GetType().FullName,
                inner_exception_type = ex.InnerException?.GetType().FullName,
                message = ex.Message,
                stack_first_lines = (ex.StackTrace ?? string.Empty)
                    .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                    .Take(6)
                    .ToArray()
            };
        }
    }

    var dependencies = new[]
    {
        typeof(AplicacoesOnline.Services.MetaWhatsApp.MetaWhatsAppInteractionRouter),
        typeof(AplicacoesOnline.Services.MetaWhatsApp.IMetaWhatsAppPersistentLogService),
        typeof(Microsoft.Extensions.Options.IOptions<AplicacoesOnline.Models.MetaWhatsApp.MetaWhatsAppWebhookOptions>),
        typeof(Microsoft.Extensions.Options.IOptions<AplicacoesOnline.Models.MetaWhatsApp.N8nWebhookSecurityOptions>)
    };

    var result = dependencies.Select(type => Probe(serviceProvider, type)).ToArray();
    return Results.Ok(result);
});
app.MapGet("/", () =>
{
    const string html = """
<!DOCTYPE html>
<html lang="pt-BR">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Aplicações Online IBR</title>
    <link rel="icon" type="image/png" href="/logo">
    <style>
        body {
            margin: 0;
            min-height: 100vh;
            display: flex;
            align-items: center;
            justify-content: center;
            background-color: #fff;
        }

        img {
            max-width: min(70vw, 420px);
            width: 100%;
            height: auto;
        }
    </style>
</head>
<body>
    <img src="/logo" alt="Logo IBR">
</body>
</html>
""";

    return Results.Content(html, "text/html; charset=utf-8");
});

app.MapGet("/logo", () =>
{
    if (!File.Exists(logoPath))
    {
        return Results.NotFound();
    }

    return Results.File(logoPath, "image/png");
});

app.MapGet("/favicon.ico", () =>
{
    if (!File.Exists(logoPath))
    {
        return Results.NotFound();
    }

    return Results.File(logoPath, "image/png");
});

app.MapGet("/xdd9tcltadz5o2ffr3kesg17itqcm2.html", (HttpContext context) =>
{
    if (!string.Equals(context.Request.Host.Host, "api.redutoresibr.com.br", StringComparison.OrdinalIgnoreCase))
    {
        return Results.NotFound();
    }

    return Results.Content("xdd9tcltadz5o2ffr3kesg17itqcm2", "text/html; charset=utf-8");
});

app.MapGet("/o1cf358pdchfhvvbxm2jygjojqtw4v.html", (HttpContext context) =>
{
    if (!string.Equals(context.Request.Host.Host, "encurtador.redutoresibr.com.br", StringComparison.OrdinalIgnoreCase))
    {
        return Results.NotFound();
    }

    return Results.Content("o1cf358pdchfhvvbxm2jygjojqtw4v", "text/html; charset=utf-8");
});

var apiKey = builder.Configuration["Security:ApiKey"];
if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Contains("configure", StringComparison.OrdinalIgnoreCase))
{
    var chaveApiIniPath = Path.Combine(builder.Environment.ContentRootPath, "Configuracoes", "ChaveAPI.ini");
    var hasSecurityApiKeyEnvironmentVariable = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("Security__ApiKey"));

    logger.LogCritical(
        "Security:ApiKey is not configured. API startup aborted. Environment={Environment}. ContentRoot={ContentRoot}. ChaveAPIPath={ChaveAPIPath}. ChaveAPIExists={ChaveAPIExists}. HasSecurityApiKeyEnvironmentVariable={HasSecurityApiKeyEnvironmentVariable}",
        app.Environment.EnvironmentName,
        builder.Environment.ContentRootPath,
        chaveApiIniPath,
        File.Exists(chaveApiIniPath),
        hasSecurityApiKeyEnvironmentVariable);
    throw new InvalidOperationException(
        "Configure Security:ApiKey (preferencialmente por variável de ambiente Security__ApiKey) antes de iniciar a API.");
}

var viewerSessionService = app.Services.GetRequiredService<AplicacoesOnline.Services.MetaWhatsApp.MetaWhatsAppViewerSessionService>();

app.UseHttpsRedirection();
app.UseForwardedHeaders();
app.UseCors("WebhookCors");
app.UseStaticFiles();

app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var isWebhookOportunidadePath =
        path.Equals("/api/piperun-crosseling/webhook/oportunidade", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/piperun-crosseling/webhook/oportunidade/", StringComparison.OrdinalIgnoreCase);

    var isWebhookOportunidadeProbeMethod =
        HttpMethods.IsGet(context.Request.Method) ||
        HttpMethods.IsHead(context.Request.Method) ||
        HttpMethods.IsOptions(context.Request.Method);

    var isWebhookOportunidadePublicProbe =
        isWebhookOportunidadePath && isWebhookOportunidadeProbeMethod;

    var isCommonBrowserProbe =
        path == "/favicon.ico" ||
        path == "/Service-Worker.min.js";

    var isMetaWebhookPath =
        path.Equals("/webhooks/meta/whatsapp", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/webhooks/meta/whatsapp/", StringComparison.OrdinalIgnoreCase);

    var isMetaWhatsAppFlowEndpointPath =
        path.Equals("/api/meta/whatsapp/flows/login/endpoint", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/meta/whatsapp/flows/login/endpoint/", StringComparison.OrdinalIgnoreCase);

    var isShortLinkResolvePath =
        path.StartsWithSegments("/r", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/s", StringComparison.OrdinalIgnoreCase);

    var isPublicRoute =
        path == "/" ||
        path == "/health" ||
        path.Equals("/whatsapp/viewer", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/meta/whatsapp/viewer/config", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/diag/meta-whatsapp/bootstrap", StringComparison.OrdinalIgnoreCase) ||
        path == "/logo" ||
        path == "/favicon.ico" ||
        path.Equals("/xdd9tcltadz5o2ffr3kesg17itqcm2.html", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/o1cf358pdchfhvvbxm2jygjojqtw4v.html", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/openapi") ||
        isWebhookOportunidadePublicProbe ||
        isMetaWebhookPath ||
        isMetaWhatsAppFlowEndpointPath ||
        isShortLinkResolvePath;

    if (isPublicRoute)
    {
        await next();
        return;
    }

    if (isCommonBrowserProbe)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var isViewerSessionLoginPath = path.Equals("/api/meta/whatsapp/viewer/session", StringComparison.OrdinalIgnoreCase)
        && HttpMethods.IsPost(context.Request.Method);
    if (isViewerSessionLoginPath)
    {
        await next();
        return;
    }

    var isViewerApiPath = path.StartsWithSegments("/api/meta/whatsapp/viewer", StringComparison.OrdinalIgnoreCase);
    var hasViewerSessionCookie = context.Request.Cookies.TryGetValue(AplicacoesOnline.Services.MetaWhatsApp.MetaWhatsAppViewerSessionService.SessionCookieName, out var viewerSessionToken);
    var isViewerSessionValid = hasViewerSessionCookie && viewerSessionService.IsValid(viewerSessionToken);
    var isMutationMethod =
        HttpMethods.IsPost(context.Request.Method) ||
        HttpMethods.IsPut(context.Request.Method) ||
        HttpMethods.IsPatch(context.Request.Method) ||
        HttpMethods.IsDelete(context.Request.Method);

    if (isViewerApiPath && isViewerSessionValid)
    {
        viewerSessionService.TryGetSessionMetadata(viewerSessionToken, out var viewerSessionMetadata);
        var viewerRole = string.IsNullOrWhiteSpace(viewerSessionMetadata.Role)
            ? AplicacoesOnline.Services.MetaWhatsApp.MetaWhatsAppViewerSessionService.RoleOperator
            : viewerSessionMetadata.Role;
        context.Items["viewer.role"] = viewerRole;
        context.Items["viewer.username"] = viewerSessionMetadata.Username;
        context.Items["viewer.can_view_sensitive_data"] = viewerSessionMetadata.CanViewSensitiveData;

        if (isMutationMethod)
        {
            if (string.Equals(viewerRole, AplicacoesOnline.Services.MetaWhatsApp.MetaWhatsAppViewerSessionService.RoleReadOnly, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "Viewer mutation blocked due to role permission. Method={Method} Path={Path} Role={Role} TraceId={TraceId}",
                    context.Request.Method,
                    path,
                    viewerRole,
                    context.TraceIdentifier);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Forbidden",
                    errorCode = "viewer_read_only",
                    detail = "Seu perfil é somente leitura e não pode executar ações de envio ou encerramento.",
                    traceId = context.TraceIdentifier
                });
                return;
            }

            if (!IsAllowedMutationSource(context.Request))
            {
                logger.LogWarning(
                    "Viewer mutation blocked due to invalid origin/referer. Method={Method} Path={Path} Origin={Origin} Referer={Referer} TraceId={TraceId}",
                    context.Request.Method,
                    path,
                    context.Request.Headers.Origin.ToString(),
                    context.Request.Headers.Referer.ToString(),
                    context.TraceIdentifier);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Forbidden",
                    errorCode = "origin_or_referer_invalid",
                    detail = "A origem da requisição não é permitida para rotas de mutação.",
                    traceId = context.TraceIdentifier
                });
                return;
            }

            var csrfHeader = context.Request.Headers.TryGetValue("X-CSRF-Token", out var csrfValues)
                ? csrfValues.ToString()
                : string.Empty;
            if (!viewerSessionService.ValidateCsrfToken(viewerSessionToken, csrfHeader))
            {
                logger.LogWarning(
                    "Viewer mutation blocked due to CSRF validation failure. Method={Method} Path={Path} TraceId={TraceId}",
                    context.Request.Method,
                    path,
                    context.TraceIdentifier);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Forbidden",
                    errorCode = "csrf_validation_failed",
                    detail = "Informe um X-CSRF-Token válido para a sessão autenticada.",
                    traceId = context.TraceIdentifier
                });
                return;
            }
        }

        await next();
        return;
    }

    var hasApiKeyHeader = context.Request.Headers.TryGetValue("X-API-Key", out var providedApiKey);
    var isApiKeyValid = hasApiKeyHeader && string.Equals(providedApiKey, apiKey, StringComparison.Ordinal);

    var hasAuthorizationHeader = context.Request.Headers.TryGetValue("Authorization", out var authorizationHeader);
    var providedBearerToken = hasAuthorizationHeader
        ? authorizationHeader.ToString()
            .Replace("Bearer ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim()
        : string.Empty;
    var isBearerValid = !string.IsNullOrWhiteSpace(providedBearerToken) && string.Equals(providedBearerToken, apiKey, StringComparison.Ordinal);

    if (!isApiKeyValid && !isBearerValid)
    {
        var reason = hasApiKeyHeader || hasAuthorizationHeader ? "invalid_token" : "missing_token";

        logger.LogWarning(
            "Unauthorized request blocked for {Method} {Path}. Reason: {Reason}. HasApiKeyHeader: {HasApiKeyHeader}. HasAuthorizationHeader: {HasAuthorizationHeader}. RemoteIp: {RemoteIp}. TraceId: {TraceId}",
            context.Request.Method,
            path,
            reason,
            hasApiKeyHeader,
            hasAuthorizationHeader,
            context.Connection.RemoteIpAddress,
            context.TraceIdentifier);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "Unauthorized",
            errorCode = "authentication_failed",
            detail = "Provide a valid X-API-Key header or Authorization: Bearer <token>.",
            traceId = context.TraceIdentifier
        });
        return;
    }

    await next();
});

app.UseAuthorization();
app.MapControllers();
app.Run();

static void ConfigureRouteSettings(WebApplicationBuilder builder)
{
    var rotasDirectory = Path.Combine(builder.Environment.ContentRootPath, "Rotas");
    if (!Directory.Exists(rotasDirectory))
    {
        return;
    }

    var environmentName = builder.Environment.EnvironmentName;

    foreach (var rotaDirectory in Directory.EnumerateDirectories(rotasDirectory).OrderBy(path => path))
    {
        var configDirectory = Path.Combine(rotaDirectory, "Config");
        if (!Directory.Exists(configDirectory))
        {
            continue;
        }

        builder.Configuration
            .AddJsonFile(Path.Combine(configDirectory, "appsettings.json"), optional: true, reloadOnChange: true)
            .AddJsonFile(
                Path.Combine(configDirectory, $"appsettings.{environmentName}.json"),
                optional: true,
                reloadOnChange: true);
    }
}

static async Task<string> ReadRequestBodyForAuditAsync(HttpRequest request)
{
    if (request.ContentLength is null or 0 || !request.Body.CanRead)
    {
        return "-";
    }

    request.EnableBuffering();
    if (request.Body.CanSeek)
    {
        request.Body.Position = 0;
    }

    using var reader = new StreamReader(request.Body, leaveOpen: true);
    var rawBody = await reader.ReadToEndAsync();
    if (request.Body.CanSeek)
    {
        request.Body.Position = 0;
    }

    if (string.IsNullOrWhiteSpace(rawBody))
    {
        return "-";
    }

    const int maxLength = 8000;
    var normalized = rawBody.ReplaceLineEndings(" ");
    return normalized.Length <= maxLength
        ? normalized
        : $"{normalized[..maxLength]}...[TRUNCATED]";
}

static bool IsAllowedMutationSource(HttpRequest request)
{
    var host = request.Host.Host;
    if (string.IsNullOrWhiteSpace(host))
    {
        return false;
    }

    if (TryValidateSourceHeader(request.Headers.Origin.ToString(), host))
    {
        return true;
    }

    return TryValidateSourceHeader(request.Headers.Referer.ToString(), host);
}

static bool TryValidateSourceHeader(string sourceHeader, string host)
{
    if (string.IsNullOrWhiteSpace(sourceHeader))
    {
        return false;
    }

    if (!Uri.TryCreate(sourceHeader, UriKind.Absolute, out var uri))
    {
        return false;
    }

    if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    if (string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);
}

static async Task<(bool Success, string BodyPreview, int BodySize, string? FailureStage, string? ErrorMessage)> TryReadRequestBodyForAuditAsync(HttpRequest request)
{
    try
    {
        var bodyPreview = await ReadRequestBodyForAuditAsync(request);
        var bodySize = bodyPreview == "-" ? 0 : bodyPreview.Length;
        return (true, bodyPreview, bodySize, null, null);
    }
    catch (Exception ex)
    {
        return (false, "-", 0, "read_request_body_for_audit", ex.GetType().Name + ": " + ex.Message);
    }
}

static bool IsEnabled(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    var normalized = value.Trim();
    return normalized.Equals("1", StringComparison.OrdinalIgnoreCase)
           || normalized.Equals("true", StringComparison.OrdinalIgnoreCase)
           || normalized.Equals("yes", StringComparison.OrdinalIgnoreCase)
           || normalized.Equals("on", StringComparison.OrdinalIgnoreCase);
}

static void ConfigureGlobalSettings(WebApplicationBuilder builder)
{
    var configDirectory = Path.Combine(builder.Environment.ContentRootPath, "Configuracoes");
    var iniFiles = new[]
    {
        "ChaveAPI.ini",
        "BancoDados.ini",
        "APIMeta.ini",
        "APIN8N.ini"
    };

    foreach (var iniFile in iniFiles)
    {
        TryAddIniFile(builder.Configuration, Path.Combine(configDirectory, iniFile));
    }

    builder.Configuration.AddEnvironmentVariables();
}

static void TryAddIniFile(ConfigurationManager configuration, string filePath)
{
    try
    {
        configuration.AddIniFile(filePath, optional: true, reloadOnChange: true);
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
    {
        Console.Error.WriteLine(
            $"[startup-config-warning] Não foi possível carregar '{filePath}'. " +
            $"Tipo: {ex.GetType().Name}. Mensagem: {ex.Message}");
    }
}
