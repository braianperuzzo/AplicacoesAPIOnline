using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;

namespace AplicacoesOnline.Controllers;

[ApiController]
[Route("api/piperun-crosseling")]
public class PiperunCrosselingController : ControllerBase
{
    private static readonly Lock FileWriteLock = new();
    private const int MaxRawBodyLogLength = 64_000;
    private const int RawWebhookLogResetEveryRequests = 100;
    private const int UltimasComprasLogMaxLength = 4_000;
    private static int RawWebhookRequestCounter;
    private static readonly SemaphoreSlim ProcessingQueue = new(1, 1);
    private static readonly HttpClient PiperunHttpClient = new()
    {
        BaseAddress = new Uri("https://api.pipe.run")
    };

    private readonly ILogger<PiperunCrosselingController> _logger;
    private readonly IConfiguration _configuration;
    private readonly string _rawWebhookLogFilePath;
    private readonly string _tokenPiperunFilePath;
    private static readonly Regex NonNumericRegex = new("\\D", RegexOptions.Compiled);

    public PiperunCrosselingController(IWebHostEnvironment environment, ILogger<PiperunCrosselingController> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;

        var rootDirectory = Directory.GetParent(environment.ContentRootPath)?.FullName ?? environment.ContentRootPath;
        var storageDirectory = Path.Combine(rootDirectory, "Arquivos e Documentos", "PiperunCrosseling");
        var logsDirectory = Path.Combine(storageDirectory, "Logs");
        Directory.CreateDirectory(logsDirectory);

        _rawWebhookLogFilePath = Path.Combine(logsDirectory, "webhook-raw.log");
        _tokenPiperunFilePath = Path.Combine(environment.ContentRootPath, "Configuracoes", "TokenPiperun.ini");
    }

    [HttpPost("webhook/oportunidade")]
    public async Task<IActionResult> ReceberOportunidade()
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        var contentType = Request.ContentType;
        var userAgent = Request.Headers.UserAgent.ToString();
        var host = Request.Host.ToString();
        var forwardedFor = Request.Headers["X-Forwarded-For"].ToString();
        var forwardedProto = Request.Headers["X-Forwarded-Proto"].ToString();
        var hasApiKeyHeader = Request.Headers.TryGetValue("X-API-Key", out var providedApiKey);
        var rawBody = await ReadRawBodyAsync();

        _logger.LogInformation(
            "Webhook do Piperun recebido para processamento. Method: {Method}. Path: {Path}. Host: {Host}. ContentType: {ContentType}. HasApiKeyHeader: {HasApiKeyHeader}. ApiKeyLength: {ApiKeyLength}. BodyLength: {BodyLength}. UserAgent: {UserAgent}. RemoteIp: {RemoteIp}. XForwardedFor: {XForwardedFor}. XForwardedProto: {XForwardedProto}. TraceId: {TraceId}",
            Request.Method,
            Request.Path,
            host,
            contentType,
            hasApiKeyHeader,
            GetHeaderLength(providedApiKey),
            rawBody.Length,
            userAgent,
            remoteIp,
            forwardedFor,
            forwardedProto,
            HttpContext.TraceIdentifier);

        var traceId = HttpContext.TraceIdentifier;

        _ = Task.Run(() => ProcessarOportunidadeAsync(rawBody, remoteIp, traceId, hasApiKeyHeader));

        return Ok(new
        {
            status = "received",
            traceId
        });
    }

    [HttpGet("webhook/oportunidade")]
    public IActionResult VerificarWebhookOportunidade()
    {
        _logger.LogInformation(
            "Sonda GET recebida no webhook de oportunidade. RemoteIp: {RemoteIp}. TraceId: {TraceId}",
            HttpContext.Connection.RemoteIpAddress,
            HttpContext.TraceIdentifier);

        return Ok(new
        {
            status = "ok",
            route = "/api/piperun-crosseling/webhook/oportunidade",
            acceptedMethod = "POST",
            requiredHeaders = new[] { "X-API-Key", "Content-Type: application/json" },
            message = "Use POST para enviar o payload do webhook. Esta rota GET é apenas para diagnóstico.",
            traceId = HttpContext.TraceIdentifier
        });
    }

    private async Task<string> ReadRawBodyAsync()
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static int GetHeaderLength(StringValues value)
    {
        return value.Count == 0 ? 0 : value.ToString().Length;
    }

    private static bool TryParsePayload(string rawBody, out JsonElement payload, out string? parseError)
    {
        try
        {
            using var json = JsonDocument.Parse(rawBody);
            payload = json.RootElement.Clone();
            parseError = null;
            return true;
        }
        catch (JsonException ex)
        {
            payload = default;
            parseError = $"Verifique a expressão JSON no PipeRun. Erro: {ex.Message}";
            return false;
        }
    }

    private void SaveRawWebhookLog(string rawBody, string? remoteIp, string traceId, bool hasApiKeyHeader)
    {
        var truncatedBody = rawBody.Length <= MaxRawBodyLogLength
            ? rawBody
            : rawBody[..MaxRawBodyLogLength] + "\n... [TRUNCADO]";

        var bloco = new StringBuilder();
        bloco.AppendLine("============================================================");
        bloco.AppendLine($"Recebido em (UTC): {DateTime.UtcNow:O}");
        bloco.AppendLine($"TraceId: {traceId}");
        bloco.AppendLine($"RemoteIp: {remoteIp}");
        bloco.AppendLine($"HasApiKeyHeader: {hasApiKeyHeader}");
        bloco.AppendLine($"BodyLength: {rawBody.Length}");
        bloco.AppendLine("RawBody:");
        bloco.AppendLine(truncatedBody);
        bloco.AppendLine();

        AppendRawWebhookLog(bloco.ToString(), incrementRequestCounter: true);
    }

    private void SaveRawWebhookPostResultLog(string opportunityId, string traceId, string tipoNota, PostNotaResultado resultado)
    {
        var bloco = new StringBuilder();
        bloco.AppendLine("-------------------- RESULTADO POST PIPERUN --------------------");
        bloco.AppendLine($"Recebido em (UTC): {DateTime.UtcNow:O}");
        bloco.AppendLine($"TraceId: {traceId}");
        bloco.AppendLine($"OpportunityId: {opportunityId}");
        bloco.AppendLine($"TipoNota: {tipoNota}");
        bloco.AppendLine($"TentativaPost: {resultado.TentativaRealizada}");
        bloco.AppendLine($"PostConcluidoComSucesso: {resultado.Sucesso}");
        bloco.AppendLine($"StatusCode: {(resultado.StatusCode.HasValue ? resultado.StatusCode.Value.ToString() : "n/a")}");

        if (!string.IsNullOrWhiteSpace(resultado.Erro))
        {
            bloco.AppendLine($"Erro: {TruncateForLog(resultado.Erro)}");
        }

        if (!string.IsNullOrWhiteSpace(resultado.ResponseBody))
        {
            bloco.AppendLine($"Response: {TruncateForLog(resultado.ResponseBody)}");
        }

        bloco.AppendLine();

        AppendRawWebhookLog(bloco.ToString(), incrementRequestCounter: false);
    }

    private void AppendRawWebhookLog(string bloco, bool incrementRequestCounter)
    {
        lock (FileWriteLock)
        {
            if (incrementRequestCounter)
            {
                RawWebhookRequestCounter++;

                if (RawWebhookRequestCounter >= RawWebhookLogResetEveryRequests)
                {
                    if (System.IO.File.Exists(_rawWebhookLogFilePath))
                    {
                        System.IO.File.Delete(_rawWebhookLogFilePath);
                    }

                    RawWebhookRequestCounter = 0;
                }
            }

            System.IO.File.AppendAllText(_rawWebhookLogFilePath, bloco, Encoding.UTF8);
        }
    }

    private async Task ProcessarOportunidadeAsync(string rawBody, string? remoteIp, string traceId, bool hasApiKeyHeader)
    {
        await ProcessingQueue.WaitAsync();

        try
        {
            if (string.IsNullOrWhiteSpace(rawBody))
            {
                _logger.LogWarning(
                    "Webhook do Piperun recebido com payload vazio. RemoteIp: {RemoteIp}. TraceId: {TraceId}",
                    remoteIp,
                    traceId);

                return;
            }

            if (!TryParsePayload(rawBody, out var payload, out var parseError))
            {
                _logger.LogWarning(
                    "Webhook do Piperun com JSON inválido. RemoteIp: {RemoteIp}. TraceId: {TraceId}. ParseError: {ParseError}",
                    remoteIp,
                    traceId,
                    parseError);

                return;
            }

            if (payload.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning(
                    "Webhook do Piperun com payload fora do formato esperado (JSON object). TraceId: {TraceId}",
                    traceId);

                return;
            }

            var id = TryGetNestedValue(payload, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                _logger.LogWarning(
                    "Webhook do Piperun sem id da oportunidade no payload. RemoteIp: {RemoteIp}. TraceId: {TraceId}",
                    remoteIp,
                    traceId);

                return;
            }

            var payloadFiltrado = BuildFilteredPayload(payload);
            var codigoClientePlenus = ObterCodigoClientePlenus(
                payloadFiltrado.company.cnpj,
                payloadFiltrado.company.codigoClientePlenus,
                traceId);

            var payloadEnriquecido = new
            {
                payloadFiltrado.id,
                company = new
                {
                    payloadFiltrado.company.id,
                    payloadFiltrado.company.cnpj,
                    CodigoClientePlenus = codigoClientePlenus
                },
                payloadFiltrado.fields
            };

            var formattedPayload = JsonSerializer.Serialize(payloadEnriquecido, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            SaveRawWebhookLog(formattedPayload, remoteIp, traceId, hasApiKeyHeader);

            _logger.LogInformation(
                "Webhook do Piperun processado com sucesso. OpportunityId: {OpportunityId}. RemoteIp: {RemoteIp}. TraceId: {TraceId}",
                id,
                remoteIp,
                traceId);

            await ExecutarConsultaUltimasComprasAsync(id, codigoClientePlenus, traceId);
            await ExecutarFluxoCrossSellingOfertasAsync(id, payloadFiltrado.documento, traceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Falha ao processar webhook do Piperun em background. TraceId: {TraceId}",
                traceId);
        }
        finally
        {
            ProcessingQueue.Release();
        }
    }

    private async Task ExecutarConsultaUltimasComprasAsync(string opportunityId, string codigoClientePlenus, string traceId)
    {
        if (string.IsNullOrWhiteSpace(opportunityId))
        {
            _logger.LogWarning("Consulta de últimas compras não executada: OpportunityId ausente. TraceId: {TraceId}", traceId);
            return;
        }

        try
        {
            var nomePessoa = "NÃO INFORMADO";
            IReadOnlyCollection<CompraRegistro> compras = Array.Empty<CompraRegistro>();

            if (!string.IsNullOrWhiteSpace(codigoClientePlenus) && !codigoClientePlenus.Equals("nao-encontrado", StringComparison.OrdinalIgnoreCase))
            {
                var comprasEncontradas = BuscarUltimasCompras(codigoClientePlenus, traceId);
                compras = comprasEncontradas;
                nomePessoa = comprasEncontradas.FirstOrDefault()?.NmPessoa ?? nomePessoa;
            }

            var textoUltimasCompras = BuildUltimasComprasTexto(codigoClientePlenus, nomePessoa, compras);
            var resultadoPost = await PostarNotaNoPiperunAsync(opportunityId, textoUltimasCompras, traceId);
            SaveRawWebhookPostResultLog(opportunityId, traceId, "ultimas-compras", resultadoPost);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Falha ao executar fluxo de últimas compras para envio ao Piperun. OpportunityId: {OpportunityId}. TraceId: {TraceId}",
                opportunityId,
                traceId);
        }
    }

    private static string TruncateForLog(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        return normalized.Length <= UltimasComprasLogMaxLength
            ? normalized
            : normalized[..UltimasComprasLogMaxLength] + "... [TRUNCADO]";
    }

    private List<CompraRegistro> BuscarUltimasCompras(string codigoClientePlenus, string traceId)
    {
        if (!TryBuildDatabaseConnectionString(traceId, "consulta de últimas compras", out var connectionString))
        {
            return new List<CompraRegistro>();
        }

        const string query = @"
SELECT TOP 10
    PEDIDO.CD_DOCUMENTO,
    PEDIDO.NR_COMPL,
    PEDIDO.DT_EMISSAO,
    PEDIDO.NM_PESSOA,
    VENDEDOR.NM_PESSOA AS NM_VENDEDOR,
    ITEM.CD_PRODUTO,
    ITEM.VL_QUANTIDADE AS VL_QUANTIDADE,
    ITEM.VL_PRODUTOS AS VL_PRODUTO,
    PRODUTO.DS_PRODUTO,
    PRODUTO.DS_REFERENCIA
FROM MVPE_PEDIDO AS PEDIDO
INNER JOIN MVPE_PEDIDOITEM AS ITEM ON PEDIDO.CD_EMPRESA = ITEM.CD_EMPRESA AND PEDIDO.NR_COMPL = ITEM.NR_COMPL AND PEDIDO.CD_DOCUMENTO = ITEM.CD_DOCUMENTO
INNER JOIN MMPR_PRODUTO AS PRODUTO ON PEDIDO.CD_EMPRESA = PRODUTO.CD_EMPRESA AND ITEM.CD_PRODUTO = PRODUTO.CD_PRODUTO
LEFT JOIN MBAD_PESSOA AS VENDEDOR ON VENDEDOR.CD_PESSOA = PEDIDO.DS_ATRIBUTO8
WHERE PEDIDO.CD_PESSOA = @CodigoClientePlenus
  AND PEDIDO.DT_EMISSAO >= DATEADD(MONTH, -12, CAST(GETDATE() AS DATE))
  AND PEDIDO.CD_TIPO IN (1, 2, 8, 10, 11)
  AND PRODUTO.CD_PRODCONFIG <> 'IA'
  AND PRODUTO.DS_REFERENCIA NOT LIKE '%2.WS%'
  AND PRODUTO.DS_REFERENCIA NOT LIKE 'MS.%'
  AND PRODUTO.CD_PRODCONFIG IS NOT NULL
  AND PRODUTO.ID_STATUS = 0
  AND PEDIDO.ID_STATUS IN (0, 1, 2, 5)
  AND PEDIDO.CD_ETAPA IN (1, 2, 3, 4, 5, 7, 8, 9, 10, 11, 12, 13, 15, 16, 17, 18, 19)
ORDER BY PEDIDO.DT_EMISSAO DESC;";

        var compras = new List<CompraRegistro>();

        using var connection = new SqlConnection(connectionString);
        using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@CodigoClientePlenus", codigoClientePlenus);

        connection.Open();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            compras.Add(new CompraRegistro(
                reader["CD_DOCUMENTO"]?.ToString(),
                reader["NR_COMPL"]?.ToString(),
                reader["DT_EMISSAO"] is DateTime dtEmissao ? dtEmissao.ToString("dd/MM/yyyy") : reader["DT_EMISSAO"]?.ToString(),
                reader["NM_PESSOA"]?.ToString(),
                reader["NM_VENDEDOR"]?.ToString(),
                reader["CD_PRODUTO"]?.ToString(),
                reader["DS_REFERENCIA"]?.ToString(),
                reader["DS_PRODUTO"]?.ToString(),
                reader["VL_PRODUTO"] is DBNull ? null : Convert.ToDecimal(reader["VL_PRODUTO"]),
                reader["VL_QUANTIDADE"] is DBNull ? null : Convert.ToDecimal(reader["VL_QUANTIDADE"])));
        }

        _logger.LogInformation("Consulta de últimas compras concluída. CodigoCliente: {CodigoCliente}. Total: {Total}. TraceId: {TraceId}", codigoClientePlenus, compras.Count, traceId);

        return compras;
    }

    private string BuildUltimasComprasTexto(string codigoClientePlenus, string nomePessoa, IReadOnlyCollection<CompraRegistro> compras)
    {
        var cliente = string.IsNullOrWhiteSpace(codigoClientePlenus) ? "nao-encontrado" : codigoClientePlenus;
        var nome = string.IsNullOrWhiteSpace(nomePessoa) ? "NÃO INFORMADO" : nomePessoa.Trim();
        const string quebraVisual = "<br>";
        var separadorBloco = $"{quebraVisual}{quebraVisual}";

        var linhas = new List<string>
        {
            $"Últimas compras do cliente {cliente} - {nome} (CROSS-SELLING AUTOMÁTICO):"
        };

        if (compras.Count == 0)
        {
            linhas.Add("Sem dados de últimas compras encontrados.");
        }
        else
        {
            var cultura = new CultureInfo("pt-BR");

            foreach (var compra in compras.Take(10))
            {
                var valorProduto = compra.VlProduto.HasValue
                    ? compra.VlProduto.Value.ToString("N2", cultura)
                    : "0,00";

                linhas.Add($"• {compra.CdProduto ?? string.Empty} ({compra.DsReferencia ?? string.Empty}) - {compra.DsProduto ?? string.Empty} - Valor Unitário: R${valorProduto} - Quantidade: {compra.VlQuantidade?.ToString(cultura) ?? string.Empty}{quebraVisual}Documento: {compra.CdDocumento ?? string.Empty}/{compra.NrCompl ?? string.Empty} - {compra.DtEmissao ?? string.Empty} - Vendedor: {compra.NmVendedor ?? string.Empty}");
            }
        }

        return string.Join(separadorBloco, linhas);
    }

    private async Task ExecutarFluxoCrossSellingOfertasAsync(string opportunityId, string? documentoCustomizado, string traceId)
    {
        if (string.IsNullOrWhiteSpace(opportunityId))
        {
            _logger.LogWarning("Fluxo de cross-selling não executado: OpportunityId ausente. TraceId: {TraceId}", traceId);
            return;
        }

        if (!TryParseDocumentoReferencia(documentoCustomizado, out var documentoReferencia))
        {
            _logger.LogInformation(
                "Fluxo de cross-selling ignorado: campo Documento ausente ou fora do formato xx-yy-zz. Documento: {Documento}. TraceId: {TraceId}",
                documentoCustomizado,
                traceId);
            return;
        }

        try
        {
            if (!TryResolveEstruturaTableName(documentoReferencia, out var estruturaTableName))
            {
                _logger.LogInformation(
                    "Fluxo de cross-selling ignorado: NR_COMPL fora dos prefixos suportados OV/PV. Documento: {CdEmpresa}-{CdDocumento}-{NrCompl}. TraceId: {TraceId}",
                    documentoReferencia.CdEmpresa,
                    documentoReferencia.CdDocumento,
                    documentoReferencia.NrCompl,
                    traceId);
                return;
            }

            var recomendacoes = BuscarRecomendacoesAcoplamentoElastico(documentoReferencia, estruturaTableName, traceId)
                .Concat(BuscarRecomendacoesInversor(documentoReferencia, estruturaTableName, traceId))
                .ToList();

            if (recomendacoes.Count == 0)
            {
                _logger.LogInformation(
                    "Fluxo de cross-selling sem recomendações para o documento {CdEmpresa}-{CdDocumento}-{NrCompl}. TraceId: {TraceId}",
                    documentoReferencia.CdEmpresa,
                    documentoReferencia.CdDocumento,
                    documentoReferencia.NrCompl,
                    traceId);
                return;
            }

            var textoNota = BuildCrossSellingOfertasTexto(recomendacoes);
            var resultadoPost = await PostarNotaNoPiperunAsync(opportunityId, textoNota, traceId);
            SaveRawWebhookPostResultLog(opportunityId, traceId, "cross-selling-ofertas", resultadoPost);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Falha ao executar fluxo de cross-selling. OpportunityId: {OpportunityId}. TraceId: {TraceId}",
                opportunityId,
                traceId);
        }
    }

    private List<CrossSellingOfertaRegistro> BuscarRecomendacoesAcoplamentoElastico(DocumentoReferencia documentoReferencia, string estruturaTableName, string traceId)
    {
        if (!TryBuildDatabaseConnectionString(traceId, "consulta de cross-selling do acoplamento elástico", out var connectionString))
        {
            return new List<CrossSellingOfertaRegistro>();
        }

        var query = $@"
WITH ESTRUTURA_BASE AS (
    SELECT
        ESTRUTURA.CD_EMPRESA,
        ESTRUTURA.CD_DOCUMENTO,
        ESTRUTURA.NR_COMPL,
        ESTRUTURA.CD_PRODUTO,
        PRODUTO.CD_PRODCONFIG,
        ESTRUTURA.NM_VARIAVEL,
        ESTRUTURA.DS_VARIAVEL.value('(/VariavelOpcaoSimples/Valor)[1]', 'varchar(4000)') AS RESPOSTA_SELETOR
    FROM {estruturaTableName} AS ESTRUTURA
    INNER JOIN MMPR_PRODUTO AS PRODUTO
        ON PRODUTO.CD_EMPRESA = ESTRUTURA.CD_EMPRESA
       AND PRODUTO.CD_PRODUTO = ESTRUTURA.CD_PRODUTO
    WHERE ESTRUTURA.CD_EMPRESA = @CdEmpresa
      AND ESTRUTURA.CD_DOCUMENTO = @CdDocumento
      AND ESTRUTURA.NR_COMPL = @NrCompl
),
VARIAVEIS_PRODUTO AS (
    SELECT
        ESTRUTURA_BASE.CD_PRODUTO,
        MAX(CASE
                WHEN ESTRUTURA_BASE.NM_VARIAVEL = CONCAT(ESTRUTURA_BASE.CD_PRODCONFIG, 'LN')
                THEN ESTRUTURA_BASE.RESPOSTA_SELETOR
            END) AS LINHA,
        MAX(CASE
                WHEN ESTRUTURA_BASE.NM_VARIAVEL = CONCAT(ESTRUTURA_BASE.CD_PRODCONFIG, 'AS')
                THEN ESTRUTURA_BASE.RESPOSTA_SELETOR
            END) AS SAIDA,
        MAX(CASE
                WHEN ESTRUTURA_BASE.NM_VARIAVEL = CONCAT(ESTRUTURA_BASE.CD_PRODCONFIG, 'CM')
                THEN ESTRUTURA_BASE.RESPOSTA_SELETOR
            END) AS CARCACA,
        MAX(CASE
                WHEN ESTRUTURA_BASE.NM_VARIAVEL = CONCAT(ESTRUTURA_BASE.CD_PRODCONFIG, 'CP')
                THEN ESTRUTURA_BASE.RESPOSTA_SELETOR
            END) AS CONSTRUCAO
    FROM ESTRUTURA_BASE
    GROUP BY
        ESTRUTURA_BASE.CD_PRODUTO
)
SELECT
    VARIAVEIS_PRODUTO.CD_PRODUTO,
    CASE
        WHEN VARIAVEIS_PRODUTO.LINHA IN ('3.PB', '3.PBL', '3.SA', '3.SB', '3.SBL')
            THEN 'GS/RIC'
        WHEN VARIAVEIS_PRODUTO.LINHA IN ('1.M', '1.C', '1.FR')
            THEN 'GR'
        WHEN VARIAVEIS_PRODUTO.SAIDA IN ('ES', 'ES30', 'ED', 'ED30')
             OR VARIAVEIS_PRODUTO.CARCACA LIKE 'EE%'
             OR VARIAVEIS_PRODUTO.CONSTRUCAO LIKE 'EE%'
            THEN 'GR'
        ELSE ''
    END AS CLASSIFICACAO
FROM VARIAVEIS_PRODUTO;";

        var recomendacoes = new List<CrossSellingOfertaRegistro>();

        using var connection = new SqlConnection(connectionString);
        using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@CdEmpresa", System.Data.SqlDbType.VarChar, 20).Value = documentoReferencia.CdEmpresa;
        command.Parameters.Add("@CdDocumento", System.Data.SqlDbType.Int).Value = documentoReferencia.CdDocumento;
        command.Parameters.Add("@NrCompl", System.Data.SqlDbType.VarChar, 20).Value = documentoReferencia.NrCompl;

        connection.Open();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var classificacao = reader["CLASSIFICACAO"]?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(classificacao))
            {
                continue;
            }

            recomendacoes.Add(new CrossSellingOfertaRegistro(
                reader["CD_PRODUTO"]?.ToString()?.Trim(),
                $"ACOPLAMENTO ELÁSTICO {classificacao}"));
        }

        _logger.LogInformation(
            "Consulta de cross-selling do acoplamento elástico concluída. Estrutura: {EstruturaTableName}. Documento: {CdEmpresa}-{CdDocumento}-{NrCompl}. Total: {Total}. TraceId: {TraceId}",
            estruturaTableName,
            documentoReferencia.CdEmpresa,
            documentoReferencia.CdDocumento,
            documentoReferencia.NrCompl,
            recomendacoes.Count,
            traceId);

        return recomendacoes;
    }

    private List<CrossSellingOfertaRegistro> BuscarRecomendacoesInversor(DocumentoReferencia documentoReferencia, string estruturaTableName, string traceId)
    {
        if (!TryBuildDatabaseConnectionString(traceId, "consulta de cross-selling do inversor", out var connectionString))
        {
            return new List<CrossSellingOfertaRegistro>();
        }

        var query = $@"
SELECT DISTINCT
    ESTRUTURA.CD_PRODUTO,
    INVERSOR.XXLN
FROM {estruturaTableName} AS ESTRUTURA
INNER JOIN (
    SELECT 
        DADOS_PRODUTO.CD_PRODUTO,
        MAX(
            CASE 
                WHEN DADOS_PRODUTO.NM_VARIAVEL = 'MOPT' 
                THEN DADOS_PRODUTO.DS_VARIAVEL.value('(/VariavelOpcaoSimples/Valor)[1]', 'varchar(4000)')
            END
        ) AS MOPT,
        MAX(
            CASE 
                WHEN DADOS_PRODUTO.NM_VARIAVEL = 'MOPL' 
                THEN DADOS_PRODUTO.DS_VARIAVEL.value('(/VariavelOpcaoSimples/Valor)[1]', 'varchar(4000)')
            END
        ) AS MOPL
    FROM {estruturaTableName} AS DADOS_PRODUTO
    WHERE DADOS_PRODUTO.CD_EMPRESA = @CdEmpresa
      AND DADOS_PRODUTO.CD_DOCUMENTO = @CdDocumento
      AND DADOS_PRODUTO.NR_COMPL = @NrCompl
    GROUP BY DADOS_PRODUTO.CD_PRODUTO
) AS CONFIG_INVERSOR_PRODUTO
    ON CONFIG_INVERSOR_PRODUTO.CD_PRODUTO = ESTRUTURA.CD_PRODUTO
INNER JOIN _USR_CONF_INVT AS INVERSOR
    ON INVERSOR.MOPT = CONFIG_INVERSOR_PRODUTO.MOPT
   AND INVERSOR.MOPL = CONFIG_INVERSOR_PRODUTO.MOPL
WHERE ESTRUTURA.CD_EMPRESA = @CdEmpresa
  AND ESTRUTURA.CD_DOCUMENTO = @CdDocumento
  AND ESTRUTURA.NR_COMPL = @NrCompl
  AND ESTRUTURA.CD_PRODUTO IN (
      SELECT FILTRO_PRODUTO.CD_PRODUTO
      FROM {estruturaTableName} AS FILTRO_PRODUTO
      WHERE FILTRO_PRODUTO.CD_EMPRESA = @CdEmpresa
        AND FILTRO_PRODUTO.CD_DOCUMENTO = @CdDocumento
        AND FILTRO_PRODUTO.NR_COMPL = @NrCompl
      GROUP BY FILTRO_PRODUTO.CD_PRODUTO
      HAVING 
          SUM(
              CASE 
                  WHEN FILTRO_PRODUTO.NM_VARIAVEL = 'MOLN'
                   AND FILTRO_PRODUTO.DS_VARIAVEL.value('(/VariavelOpcaoSimples/Valor)[1]', 'varchar(4000)') = '3.I'
                  THEN 1 ELSE 0
              END
          ) > 0
          AND
          SUM(
              CASE 
                  WHEN FILTRO_PRODUTO.NM_VARIAVEL = 'MOTP'
                   AND FILTRO_PRODUTO.DS_VARIAVEL.value('(/VariavelOpcaoSimples/Valor)[1]', 'varchar(4000)') IN ('T', 'F')
                  THEN 1 ELSE 0
              END
          ) > 0
  )
  AND (
        (ESTRUTURA.NM_VARIAVEL = 'MOLN'
         AND ESTRUTURA.DS_VARIAVEL.value('(/VariavelOpcaoSimples/Valor)[1]', 'varchar(4000)') = '3.I')
     OR (ESTRUTURA.NM_VARIAVEL = 'MOTP'
         AND ESTRUTURA.DS_VARIAVEL.value('(/VariavelOpcaoSimples/Valor)[1]', 'varchar(4000)') IN ('T', 'F'))
  );";

        var recomendacoes = new List<CrossSellingOfertaRegistro>();

        using var connection = new SqlConnection(connectionString);
        using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@CdEmpresa", System.Data.SqlDbType.VarChar, 20).Value = documentoReferencia.CdEmpresa;
        command.Parameters.Add("@CdDocumento", System.Data.SqlDbType.Int).Value = documentoReferencia.CdDocumento;
        command.Parameters.Add("@NrCompl", System.Data.SqlDbType.VarChar, 20).Value = documentoReferencia.NrCompl;

        connection.Open();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var modeloInversor = reader["XXLN"]?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(modeloInversor))
            {
                continue;
            }

            recomendacoes.Add(new CrossSellingOfertaRegistro(
                reader["CD_PRODUTO"]?.ToString()?.Trim(),
                $"INVERSOR DE FREQUÊNCIA INVT MODELO {modeloInversor}"));
        }

        _logger.LogInformation(
            "Consulta de cross-selling do inversor concluída. Estrutura: {EstruturaTableName}. Documento: {CdEmpresa}-{CdDocumento}-{NrCompl}. Total: {Total}. TraceId: {TraceId}",
            estruturaTableName,
            documentoReferencia.CdEmpresa,
            documentoReferencia.CdDocumento,
            documentoReferencia.NrCompl,
            recomendacoes.Count,
            traceId);

        return recomendacoes;
    }

    private static bool TryParseDocumentoReferencia(string? documentoCustomizado, out DocumentoReferencia documentoReferencia)
    {
        documentoReferencia = default!;

        if (string.IsNullOrWhiteSpace(documentoCustomizado))
        {
            return false;
        }

        var partes = documentoCustomizado
            .Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (partes.Length != 3 || !int.TryParse(partes[1], out var cdDocumento))
        {
            return false;
        }

        documentoReferencia = new DocumentoReferencia(partes[0], cdDocumento, partes[2]);
        return true;
    }

    private static bool TryResolveEstruturaTableName(DocumentoReferencia documentoReferencia, out string estruturaTableName)
    {
        if (documentoReferencia.NrCompl.StartsWith("OV", StringComparison.OrdinalIgnoreCase))
        {
            estruturaTableName = "MVOR_ORCAMENTOITEMESTRUTURA";
            return true;
        }

        if (documentoReferencia.NrCompl.StartsWith("PV", StringComparison.OrdinalIgnoreCase))
        {
            estruturaTableName = "MVPE_PEDIDOITEMESTRUTURA";
            return true;
        }

        estruturaTableName = string.Empty;
        return false;
    }

    private static string BuildCrossSellingOfertasTexto(IReadOnlyCollection<CrossSellingOfertaRegistro> recomendacoes)
    {
        const string quebraHtml = "<br>";
        var itensFormatados = recomendacoes
            .Where(recomendacao => !string.IsNullOrWhiteSpace(recomendacao.CdProduto) && !string.IsNullOrWhiteSpace(recomendacao.DescricaoOferta))
            .GroupBy(recomendacao => new
            {
                CdProduto = recomendacao.CdProduto!.Trim(),
                DescricaoOferta = recomendacao.DescricaoOferta.Trim()
            })
            .Select(grupo => $"• Para o produto {grupo.Key.CdProduto}: {grupo.Key.DescricaoOferta}")
            .ToList();

        if (itensFormatados.Count == 0)
        {
            return "CROSS-SELLING AUTOMÁTICO - RECOMENDAÇÃO DE OFERTA:";
        }

        var separador = $"{quebraHtml}{quebraHtml}";
        return string.Join(separador, new[]
        {
            "CROSS-SELLING AUTOMÁTICO - RECOMENDAÇÃO DE OFERTA:",
            string.Join(separador, itensFormatados)
        });
    }

    private bool TryBuildDatabaseConnectionString(string traceId, string contextoOperacao, out string connectionString)
    {
        var host = ResolveDatabaseConfigValue(("database:host", "database:host"), ("DB_HOST", "DB_HOST"));
        var database = ResolveDatabaseConfigValue(("database:nome", "database:nome"), ("DB_NAME", "DB_NAME"));
        var user = ResolveDatabaseConfigValue(("database:usuario", "database:usuario"), ("DB_USER", "DB_USER"));
        var password = ResolveDatabaseConfigValue(("database:senha", "database:senha"), ("DB_PASS", "DB_PASS"));

        if (string.IsNullOrWhiteSpace(host.Value) || string.IsNullOrWhiteSpace(database.Value) || string.IsNullOrWhiteSpace(user.Value) || string.IsNullOrWhiteSpace(password.Value))
        {
            _logger.LogError(
                "Operação {ContextoOperacao} não executada por configuração de banco incompleta. Host: {Host}. Database: {Database}. User: {User}. Password: {Password}. TraceId: {TraceId}",
                contextoOperacao,
                !string.IsNullOrWhiteSpace(host.Value),
                !string.IsNullOrWhiteSpace(database.Value),
                !string.IsNullOrWhiteSpace(user.Value),
                !string.IsNullOrWhiteSpace(password.Value),
                traceId);

            connectionString = string.Empty;
            return false;
        }

        connectionString = new SqlConnectionStringBuilder
        {
            DataSource = host.Value,
            InitialCatalog = database.Value,
            UserID = user.Value,
            Password = password.Value,
            Encrypt = false,
            TrustServerCertificate = true,
            ConnectTimeout = 15
        }.ConnectionString;

        return true;
    }

    private async Task<PostNotaResultado> PostarNotaNoPiperunAsync(string opportunityId, string textoUltimasCompras, string traceId)
    {
        var token = LerTokenPiperun(traceId);
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogError(
                "Token do Piperun não localizado. OpportunityId: {OpportunityId}. TraceId: {TraceId}",
                opportunityId,
                traceId);
            return new PostNotaResultado(false, false, null, null, "Token do Piperun não localizado");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/notes");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("Token", token);

            var payload = new NotaPiperunPayload(textoUltimasCompras, opportunityId);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await PiperunHttpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Nota enviada ao Piperun com sucesso. OpportunityId: {OpportunityId}. StatusCode: {StatusCode}. TraceId: {TraceId}. Response: {Response}",
                    opportunityId,
                    (int)response.StatusCode,
                    traceId,
                    TruncateForLog(responseBody));

                return new PostNotaResultado(true, true, (int)response.StatusCode, responseBody, null);
            }

            _logger.LogError(
                "Falha ao enviar nota ao Piperun. OpportunityId: {OpportunityId}. StatusCode: {StatusCode}. TraceId: {TraceId}. Response: {Response}",
                opportunityId,
                (int)response.StatusCode,
                traceId,
                TruncateForLog(responseBody));

            return new PostNotaResultado(true, false, (int)response.StatusCode, responseBody, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Erro inesperado ao enviar nota ao Piperun. OpportunityId: {OpportunityId}. TraceId: {TraceId}",
                opportunityId,
                traceId);

            return new PostNotaResultado(true, false, null, null, ex.Message);
        }
    }

    private string? LerTokenPiperun(string traceId)
    {
        try
        {
            if (!System.IO.File.Exists(_tokenPiperunFilePath))
            {
                _logger.LogError("Arquivo de token do Piperun não encontrado em {TokenPath}. TraceId: {TraceId}", _tokenPiperunFilePath, traceId);
                return null;
            }

            foreach (var line in System.IO.File.ReadLines(_tokenPiperunFilePath))
            {
                if (!line.StartsWith("PIPE_TOKEN", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var index = line.IndexOf('=');
                if (index < 0)
                {
                    continue;
                }

                var value = line[(index + 1)..].Trim().Trim('"').Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            _logger.LogError("Chave PIPE_TOKEN não encontrada em {TokenPath}. TraceId: {TraceId}", _tokenPiperunFilePath, traceId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao ler token do Piperun em {TokenPath}. TraceId: {TraceId}", _tokenPiperunFilePath, traceId);
            return null;
        }
    }

    private sealed record DocumentoReferencia(string CdEmpresa, int CdDocumento, string NrCompl);

    private sealed record CompraRegistro(
        string? CdDocumento,
        string? NrCompl,
        string? DtEmissao,
        string? NmPessoa,
        string? NmVendedor,
        string? CdProduto,
        string? DsReferencia,
        string? DsProduto,
        decimal? VlProduto,
        decimal? VlQuantidade);

    private sealed record CrossSellingOfertaRegistro(
        string? CdProduto,
        string DescricaoOferta);

    private sealed record PostNotaResultado(
        bool TentativaRealizada,
        bool Sucesso,
        int? StatusCode,
        string? ResponseBody,
        string? Erro);

    private static PayloadFiltrado BuildFilteredPayload(JsonElement payload)
    {
        var companyId = TryGetNestedValue(payload, "company", "id");
        var cnpj = ResolveCnpj(payload);
        var codigoClientePlenus = ResolveCodigoClientePlenus(payload);
        var opportunityId = TryGetNestedValue(payload, "id");
        var title = TryGetNestedValue(payload, "title");
        var documentValue = ExtractDocumentoFieldValue(payload);

        return new PayloadFiltrado(
            ParseAsLongOrNull(opportunityId),
            title,
            new EmpresaPayload(ParseAsLongOrNull(companyId), cnpj, codigoClientePlenus),
            documentValue,
            new[] { new CampoPayload(763558, documentValue) });
    }

    private static string? ResolveCnpj(JsonElement payload)
    {
        var rawCnpj =
            TryGetNestedValue(payload, "company", "cnpj") ??
            TryGetNestedValue(payload, "cnpj");

        return NormalizeToNumericOnly(rawCnpj);
    }

    private static string? ResolveCodigoClientePlenus(JsonElement payload)
    {
        return
            TryGetNestedValue(payload, "company", "CodigoClientePlenus") ??
            TryGetNestedValue(payload, "company", "codigoClientePlenus") ??
            TryGetNestedValue(payload, "CodigoClientePlenus") ??
            TryGetNestedValue(payload, "codigoClientePlenus");
    }

    private string ObterCodigoClientePlenus(string? cnpj, string? codigoClienteNoPayload, string traceId)
    {
        _logger.LogInformation(
            "Iniciando resolução do CodigoClientePlenus. CnpjRecebido: {Cnpj}. CodigoClienteNoPayload: {CodigoPayload}. TraceId: {TraceId}",
            cnpj,
            codigoClienteNoPayload,
            traceId);

        if (!string.IsNullOrWhiteSpace(codigoClienteNoPayload) &&
            !codigoClienteNoPayload.Equals("nao-encontrado", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "CodigoClientePlenus recebido no payload e reaproveitado sem nova consulta ao banco. Codigo: {Codigo}. TraceId: {TraceId}",
                codigoClienteNoPayload,
                traceId);

            return codigoClienteNoPayload.Trim();
        }

        if (string.IsNullOrWhiteSpace(cnpj))
        {
            _logger.LogWarning(
                "Fallback de CodigoClientePlenus aplicado: CNPJ ausente no payload. Resultado: nao-encontrado. TraceId: {TraceId}",
                traceId);
            return "nao-encontrado";
        }

        if (!string.IsNullOrWhiteSpace(codigoClienteNoPayload) &&
            codigoClienteNoPayload.Equals("nao-encontrado", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "CodigoClientePlenus no payload veio como marcador de fallback (nao-encontrado); será feita nova consulta usando CNPJ {Cnpj}. TraceId: {TraceId}",
                cnpj,
                traceId);
        }

        try
        {
            var host = ResolveDatabaseConfigValue(
                ("database:host", "database:host"),
                ("DB_HOST", "DB_HOST"));

            var database = ResolveDatabaseConfigValue(
                ("database:nome", "database:nome"),
                ("DB_NAME", "DB_NAME"));

            var user = ResolveDatabaseConfigValue(
                ("database:usuario", "database:usuario"),
                ("DB_USER", "DB_USER"));

            var password = ResolveDatabaseConfigValue(
                ("database:senha", "database:senha"),
                ("DB_PASS", "DB_PASS"));

            _logger.LogInformation(
                "Diagnóstico de chaves de banco para consulta do CodigoClientePlenus. HostKey: {HostKey}. DatabaseKey: {DatabaseKey}. UserKey: {UserKey}. PasswordKey: {PasswordKey}. TraceId: {TraceId}",
                host.KeyUsed ?? "não encontrada",
                database.KeyUsed ?? "não encontrada",
                user.KeyUsed ?? "não encontrada",
                password.KeyUsed ?? "não encontrada",
                traceId);

            if (string.IsNullOrWhiteSpace(host.Value) || string.IsNullOrWhiteSpace(database.Value) || string.IsNullOrWhiteSpace(user.Value) || string.IsNullOrWhiteSpace(password.Value))
            {
                _logger.LogError(
                    "Fallback de CodigoClientePlenus por configuração incompleta do banco. HostConfigurado: {HostConfigurado}. BancoConfigurado: {BancoConfigurado}. UsuarioConfigurado: {UsuarioConfigurado}. SenhaConfigurada: {SenhaConfigurada}. Cnpj: {Cnpj}. Resultado: nao-encontrado. TraceId: {TraceId}",
                    !string.IsNullOrWhiteSpace(host.Value),
                    !string.IsNullOrWhiteSpace(database.Value),
                    !string.IsNullOrWhiteSpace(user.Value),
                    !string.IsNullOrWhiteSpace(password.Value),
                    cnpj,
                    traceId);

                return "nao-encontrado";
            }

            var connectionString = new SqlConnectionStringBuilder
            {
                DataSource = host.Value,
                InitialCatalog = database.Value,
                UserID = user.Value,
                Password = password.Value,
                Encrypt = false,
                TrustServerCertificate = true,
                ConnectTimeout = 15
            }.ConnectionString;

            using var connection = new SqlConnection(connectionString);
            connection.Open();

            const string query = @"
                SELECT TOP 1 CD_PESSOA
                FROM MBAD_PESSOA
                WHERE (
                    REPLACE(REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(NR_CPFCNPJ)), '.', ''), '/', ''), '-', ''), ' ', '') = @cnpj
                    OR REPLACE(LTRIM(RTRIM(NR_CPFCNPJ)), ' ', '') = @cnpj
                )
                AND NULLIF(LTRIM(RTRIM(CONVERT(VARCHAR(50), CD_PESSOA))), '') IS NOT NULL";

            using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@cnpj", cnpj);

            var result = command.ExecuteScalar();
            var codigo = Convert.ToString(result)?.Trim();

            if (string.IsNullOrWhiteSpace(codigo))
            {
                _logger.LogWarning("CodigoClientePlenus não encontrado para CNPJ {Cnpj}. TraceId: {TraceId}", cnpj, traceId);
                return "nao-encontrado";
            }

            _logger.LogInformation("CodigoClientePlenus encontrado para CNPJ {Cnpj}: {Codigo}. TraceId: {TraceId}", cnpj, codigo, traceId);
            return codigo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao consultar CodigoClientePlenus para CNPJ {Cnpj}. TraceId: {TraceId}", cnpj, traceId);
            return "nao-encontrado";
        }
    }

    private (string? Value, string? KeyUsed) ResolveDatabaseConfigValue(params (string Key, string Alias)[] keyCandidates)
    {
        foreach (var (key, alias) in keyCandidates)
        {
            var value = _configuration[key];

            if (!string.IsNullOrWhiteSpace(value))
            {
                return (value, alias);
            }
        }

        return (null, null);
    }

    private sealed record PayloadFiltrado(long? id, string? title, EmpresaPayload company, string? documento, CampoPayload[] fields);
    private sealed record EmpresaPayload(long? id, string? cnpj, string? codigoClientePlenus);
    private sealed record CampoPayload(int id, string? valor);
    private sealed record NotaPiperunPayload([property: JsonPropertyName("text")] string Text, [property: JsonPropertyName("deal_id")] string DealId);

    private static long? ParseAsLongOrNull(string? value)
    {
        return long.TryParse(value, out var parsedValue) ? parsedValue : null;
    }

    private static string? NormalizeToNumericOnly(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return NonNumericRegex.Replace(value, string.Empty);
    }

    private static string? ExtractDocumentoFieldValue(JsonElement payload)
    {
        if (!payload.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? valueByName = null;

        foreach (var field in fields.EnumerateArray())
        {
            var value = TryGetNestedValue(field, "valor");

            if (TryGetFieldId(field, out var fieldId) && fieldId == 763558)
            {
                return value;
            }

            if (valueByName is null)
            {
                var fieldName = TryGetNestedValue(field, "nome");
                if (fieldName is not null && fieldName.Equals("Documento", StringComparison.OrdinalIgnoreCase))
                {
                    valueByName = value;
                }
            }
        }

        return valueByName;
    }

    private static bool TryGetFieldId(JsonElement field, out int fieldId)
    {
        fieldId = -1;

        if (!field.TryGetProperty("id", out var idElement))
        {
            return false;
        }

        fieldId = idElement.ValueKind switch
        {
            JsonValueKind.Number when idElement.TryGetInt32(out var numericId) => numericId,
            JsonValueKind.String when int.TryParse(idElement.GetString(), out var stringId) => stringId,
            _ => -1
        };

        return fieldId >= 0;
    }

    private static string? TryGetNestedValue(JsonElement payload, params string[] path)
    {
        var current = payload;

        foreach (var segment in path)
        {
            if (current.ValueKind == JsonValueKind.Array)
            {
                if (!int.TryParse(segment, out var index) || index < 0 || index >= current.GetArrayLength())
                {
                    return null;
                }

                current = current[index];
                continue;
            }

            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => current.GetRawText()
        };
    }

}
