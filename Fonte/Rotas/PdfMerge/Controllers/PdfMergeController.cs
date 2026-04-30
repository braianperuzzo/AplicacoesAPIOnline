using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using System.Text.Json.Serialization;

namespace AplicacoesOnline.Controllers;

[ApiController]
public class PdfMergeController : ControllerBase
{
    private readonly ILogger<PdfMergeController> _logger;

    public PdfMergeController(ILogger<PdfMergeController> logger)
    {
        _logger = logger;
    }

    [HttpPost("/merge-pdf")]
    [Consumes("application/json")]
    [Produces("application/pdf", "application/json")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(FileContentResult))]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest, "application/json")]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError, "application/json")]
    public IActionResult MergePdf([FromBody] MergePdfRequest? request)
    {
        if (request is null)
        {
            return BuildBadRequest("Body da requisição é obrigatório.", null);
        }

        if (request.Files is null || request.Files.Count == 0)
        {
            return BuildBadRequest("Informe ao menos um arquivo PDF no campo 'files'.", request.RequestId);
        }

        var outputFileName = BuildOutputFileName(request.OutputFileName, request.Customer?.Name);
        var orderedFiles = request.Files.OrderBy(file => file.Order).ToArray();

        var invalidFiles = new List<string>();
        var validStreams = new List<(MergePdfFile file, MemoryStream stream)>();

        for (var i = 0; i < orderedFiles.Length; i++)
        {
            var file = orderedFiles[i];
            var label = $"index={i}, order={file.Order}, file_name={file.FileName ?? "(sem nome)"}";

            if (string.IsNullOrWhiteSpace(file.ContentBase64))
            {
                invalidFiles.Add($"Arquivo sem conteúdo base64 ({label}).");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(file.MimeType) && !string.Equals(file.MimeType, "application/pdf", StringComparison.OrdinalIgnoreCase))
            {
                invalidFiles.Add($"Mime type inválido para PDF ({label}).");
                continue;
            }

            try
            {
                var bytes = Convert.FromBase64String(file.ContentBase64);
                if (!LooksLikePdf(bytes))
                {
                    invalidFiles.Add($"Conteúdo não parece ser PDF ({label}).");
                    continue;
                }

                validStreams.Add((file, new MemoryStream(bytes, writable: false)));
            }
            catch (FormatException)
            {
                invalidFiles.Add($"Base64 inválido ({label}).");
            }
        }

        if (invalidFiles.Count > 0 && !request.IgnoreInvalidFiles)
        {
            return BuildBadRequest($"Foram encontrados arquivos inválidos: {string.Join(" ", invalidFiles)}", request.RequestId);
        }

        if (validStreams.Count == 0)
        {
            return BuildBadRequest("Nenhum PDF válido foi recebido para merge.", request.RequestId);
        }

        try
        {
            using var mergedDocument = new PdfDocument();

            foreach (var (_, stream) in validStreams)
            {
                stream.Position = 0;
                using var inputDocument = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
                for (var pageIndex = 0; pageIndex < inputDocument.PageCount; pageIndex++)
                {
                    mergedDocument.AddPage(inputDocument.Pages[pageIndex]);
                }
            }

            using var outputStream = new MemoryStream();
            mergedDocument.Save(outputStream, closeStream: false);

            Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{outputFileName}\"");

            _logger.LogInformation(
                "PDF merge concluído. RequestId: {RequestId}. Cliente: {CustomerId}. ArquivosRecebidos: {TotalFiles}. ArquivosMesclados: {MergedFiles}. TraceId: {TraceId}",
                request.RequestId,
                request.Customer?.Id,
                request.Files.Count,
                validStreams.Count,
                HttpContext.TraceIdentifier);

            return File(outputStream.ToArray(), "application/pdf", outputFileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Falha ao mesclar PDFs. RequestId: {RequestId}. TraceId: {TraceId}",
                request.RequestId,
                HttpContext.TraceIdentifier);

            return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse
            {
                Error = true,
                Message = "Erro interno ao processar merge de PDFs.",
                RequestId = request.RequestId
            });
        }
        finally
        {
            foreach (var (_, stream) in validStreams)
            {
                stream.Dispose();
            }
        }
    }

    private static string BuildOutputFileName(string? outputFileName, string? customerName)
    {
        var fileName = string.IsNullOrWhiteSpace(outputFileName)
            ? $"Titulos em Debito - {customerName ?? "cliente"}.pdf"
            : outputFileName.Trim();

        if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".pdf";
        }

        return fileName;
    }

    private static bool LooksLikePdf(byte[] bytes)
    {
        if (bytes.Length < 5)
        {
            return false;
        }

        return bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46 && bytes[4] == 0x2D;
    }

    private IActionResult BuildBadRequest(string message, string? requestId)
    {
        return BadRequest(new ErrorResponse
        {
            Error = true,
            Message = message,
            RequestId = requestId
        });
    }

    public sealed class MergePdfRequest
    {
        [JsonPropertyName("request_id")]
        public string? RequestId { get; set; }
        [JsonPropertyName("customer")]
        public CustomerPayload? Customer { get; set; }
        [JsonPropertyName("output_file_name")]
        public string? OutputFileName { get; set; }
        [JsonPropertyName("ignore_invalid_files")]
        public bool IgnoreInvalidFiles { get; set; }
        [JsonPropertyName("files")]
        public List<MergePdfFile> Files { get; set; } = new();
    }

    public sealed class CustomerPayload
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
        [JsonPropertyName("name")]
        public string? Name { get; set; }
        [JsonPropertyName("document")]
        public string? Document { get; set; }
        [JsonPropertyName("phone")]
        public string? Phone { get; set; }
    }

    public sealed class MergePdfFile
    {
        [JsonPropertyName("order")]
        public int Order { get; set; }
        [JsonPropertyName("kind")]
        public string? Kind { get; set; }
        [JsonPropertyName("title_index")]
        public int? TitleIndex { get; set; }
        [JsonPropertyName("file_name")]
        public string? FileName { get; set; }
        [JsonPropertyName("mime_type")]
        public string? MimeType { get; set; }
        [JsonPropertyName("content_base64")]
        public string ContentBase64 { get; set; } = string.Empty;
    }

    public sealed class ErrorResponse
    {
        [JsonPropertyName("error")]
        public bool Error { get; set; }
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
        [JsonPropertyName("request_id")]
        public string? RequestId { get; set; }
    }
}
