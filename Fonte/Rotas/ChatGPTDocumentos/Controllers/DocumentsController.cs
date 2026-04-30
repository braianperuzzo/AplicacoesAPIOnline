using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using UglyToad.PdfPig;

namespace AplicacoesOnline.Controllers;

[ApiController]
[Route("api/documents")]
public class DocumentsController : ControllerBase
{
    private const int DefaultChunkSizeBytes = 65_536;
    private const int MaxChunkSizeBytes = 1_048_576;
    private const int MaxResponseContentChars = 90_000;
    private const int JsonEnvelopeReserveChars = 4_096;
    private const int DefaultTextChunkChars = 30_000;
    private const int MaxTextChunkChars = 120_000;
    private const int DefaultPageSize = 200;
    private const int MaxPageSize = 1_000;

    private static readonly HashSet<string> IgnoredSystemFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Thumbs.db",
        "desktop.ini",
        ".ds_store",
        ".spotlight-v100",
        ".trashes"
    };

    private static readonly HashSet<string> IgnoredSystemExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".db",
        ".tmp",
        ".lnk",
        ".ini",
        ".bak"
    };

    private static readonly HashSet<string> AllowedDocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf"
    };

    private readonly IConfiguration _configuration;
    private readonly ILogger<DocumentsController> _logger;

    private static readonly object NetworkConnectionLock = new();
    private static readonly HashSet<string> ConnectedShares = new(StringComparer.OrdinalIgnoreCase);

    public DocumentsController(IConfiguration configuration, ILogger<DocumentsController> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        _logger.LogInformation("Status check requested for Documents API. TraceId: {TraceId}", HttpContext.TraceIdentifier);

        var roots = GetConfiguredRoots();

        var result = roots.Select(root =>
        {
            EnsureNetworkShareAccessIfNeeded(root);

            var exists = Directory.Exists(root);
            var hasFiles = exists && TryHasAnyFile(root);

            return new
            {
                root,
                exists,
                hasFiles
            };
        });

        return Ok(new
        {
            serverTimeUtc = DateTime.UtcNow,
            roots = result
        });
    }

    [HttpGet("files")]
    public IActionResult ListFiles([FromQuery] int limit = DefaultPageSize, [FromQuery] int offset = 0, [FromQuery] string sortBy = "name", [FromQuery] string sortDirection = "asc")
    {
        if (offset < 0)
        {
            return BadRequest(new { error = "Query parameter 'offset' must be greater than or equal to zero." });
        }

        if (limit <= 0)
        {
            return BadRequest(new { error = "Query parameter 'limit' must be greater than zero." });
        }

        var roots = GetConfiguredRoots();
        var appliedLimit = Math.Min(limit, MaxPageSize);
        if (!TryResolveSort(sortBy, sortDirection, out var appliedSortBy, out var appliedSortDirection, out var sortError))
        {
            return BadRequest(new { error = sortError });
        }

        var allFiles = ApplySorting(
                roots
                    .SelectMany(GetAllFiles)
                    .Where(IsDocumentFile)
                    .Select(path => BuildFileEntry(path, roots)),
                appliedSortBy,
                appliedSortDirection)
            .ToArray();

        var files = allFiles
            .Skip(offset)
            .Take(appliedLimit)
            .ToArray();

        var hasMore = offset + files.Length < allFiles.Length;
        int? nextOffset = hasMore ? offset + files.Length : null;

        _logger.LogInformation(
            "File list generated with {TotalFiles} files across {TotalRoots} roots. TraceId: {TraceId}",
            allFiles.Length,
            roots.Length,
            HttpContext.TraceIdentifier);

        return Ok(new
        {
            totalFiles = allFiles.Length,
            offset,
            limitRequested = limit,
            limitApplied = appliedLimit,
            sortBy = appliedSortBy,
            sortDirection = appliedSortDirection,
            returnedFiles = files.Length,
            hasMore,
            nextOffset,
            files
        });
    }

    [HttpGet("search")]
    public IActionResult SearchFiles([FromQuery] string term, [FromQuery] int limit = DefaultPageSize, [FromQuery] int offset = 0, [FromQuery] string sortBy = "name", [FromQuery] string sortDirection = "asc")
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return BadRequest(new { error = "Query parameter 'term' is required." });
        }

        if (offset < 0)
        {
            return BadRequest(new { error = "Query parameter 'offset' must be greater than or equal to zero." });
        }

        if (limit <= 0)
        {
            return BadRequest(new { error = "Query parameter 'limit' must be greater than zero." });
        }

        var roots = GetConfiguredRoots();
        var appliedLimit = Math.Min(limit, MaxPageSize);
        if (!TryResolveSort(sortBy, sortDirection, out var appliedSortBy, out var appliedSortDirection, out var sortError))
        {
            return BadRequest(new { error = sortError });
        }

        var searchTerm = term.Trim();

        var allFiles = ApplySorting(
                roots
                    .SelectMany(GetAllFiles)
                    .Where(IsDocumentFile)
                    .Select(path => BuildFileEntry(path, roots))
                    .Where(file =>
                        file.fileName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                        file.relativePath.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)),
                appliedSortBy,
                appliedSortDirection)
            .ToArray();

        var files = allFiles
            .Skip(offset)
            .Take(appliedLimit)
            .ToArray();

        var hasMore = offset + files.Length < allFiles.Length;
        int? nextOffset = hasMore ? offset + files.Length : null;

        return Ok(new
        {
            term = searchTerm,
            totalFiles = allFiles.Length,
            offset,
            limitRequested = limit,
            limitApplied = appliedLimit,
            sortBy = appliedSortBy,
            sortDirection = appliedSortDirection,
            returnedFiles = files.Length,
            hasMore,
            nextOffset,
            files
        });
    }

    [HttpGet("file-content")]
    public IActionResult GetFileContent([FromQuery] string id, [FromQuery] long offset = 0, [FromQuery] int maxBytes = DefaultChunkSizeBytes)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            _logger.LogWarning("File content requested without 'id' query parameter. TraceId: {TraceId}", HttpContext.TraceIdentifier);
            return BadRequest(new { error = "Query parameter 'id' is required." });
        }

        if (offset < 0)
        {
            return BadRequest(new { error = "Query parameter 'offset' must be greater than or equal to zero." });
        }

        if (maxBytes <= 0)
        {
            return BadRequest(new { error = "Query parameter 'maxBytes' must be greater than zero." });
        }

        var roots = GetConfiguredRoots();
        var fullPath = Path.GetFullPath(id);

        if (IsPathAllowed(fullPath, roots) && Directory.Exists(fullPath))
        {
            _logger.LogWarning(
                "Directory was provided to file-content endpoint. RequestedId: {RequestedId}. TraceId: {TraceId}",
                id,
                HttpContext.TraceIdentifier);

            return BadRequest(new
            {
                error = "Parameter 'id' must point to a file, not a directory.",
                requestedId = id,
                hint = "Call /api/documents/files first and reuse one of the returned file ids in /api/documents/file-content."
            });
        }

        if (!IsPathAllowed(fullPath, roots) || !System.IO.File.Exists(fullPath))
        {
            _logger.LogWarning(
                "File not found or not allowed. RequestedId: {RequestedId}. TraceId: {TraceId}",
                id,
                HttpContext.TraceIdentifier);
            return NotFound(new { error = "File not found in configured folders." });
        }

        var requestedChunkSize = Math.Min(maxBytes, MaxChunkSizeBytes);
        var fileInfo = new FileInfo(fullPath);
        var totalSizeBytes = fileInfo.Length;

        if (offset > totalSizeBytes)
        {
            return BadRequest(new { error = "Query parameter 'offset' cannot be greater than the file size." });
        }

        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        var isTextFile = IsLikelyText(extension);
        var chunkSize = GetSafeChunkSizeForTransport(requestedChunkSize, isTextFile);
        var bytes = ReadFileChunk(fullPath, offset, chunkSize);
        var hasMoreContent = offset + bytes.LongLength < totalSizeBytes;
        long? nextOffset = hasMoreContent ? offset + bytes.LongLength : null;

        var result = new
        {
            id = fullPath,
            fileName = Path.GetFileName(fullPath),
            extension,
            sizeBytes = totalSizeBytes,
            chunkOffset = offset,
            chunkSizeBytes = bytes.LongLength,
            hasMoreContent,
            nextOffset,
            maxBytesRequested = maxBytes,
            maxBytesApplied = chunkSize,
            content = isTextFile
                ? Encoding.UTF8.GetString(bytes)
                : Convert.ToBase64String(bytes),
            contentEncoding = isTextFile ? "utf-8" : "base64"
        };

        _logger.LogInformation(
            "File content chunk served. FileName: {FileName}. ChunkOffset: {ChunkOffset}. ChunkSizeBytes: {ChunkSizeBytes}. TotalSizeBytes: {TotalSizeBytes}. HasMoreContent: {HasMoreContent}. TraceId: {TraceId}",
            result.fileName,
            result.chunkOffset,
            result.chunkSizeBytes,
            result.sizeBytes,
            result.hasMoreContent,
            HttpContext.TraceIdentifier);

        return Ok(result);
    }

    [HttpGet("file-text")]
    public IActionResult GetFileText([FromQuery] string id, [FromQuery] int offset = 0, [FromQuery] int maxChars = DefaultTextChunkChars)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return BadRequest(new
            {
                error = "InvalidRequest",
                errorCode = "missing_id",
                detail = "Query parameter 'id' is required.",
                traceId = HttpContext.TraceIdentifier
            });
        }

        if (offset < 0)
        {
            return BadRequest(new
            {
                error = "InvalidRequest",
                errorCode = "invalid_offset",
                detail = "Query parameter 'offset' must be greater than or equal to zero.",
                traceId = HttpContext.TraceIdentifier
            });
        }

        if (maxChars <= 0)
        {
            return BadRequest(new
            {
                error = "InvalidRequest",
                errorCode = "invalid_max_chars",
                detail = "Query parameter 'maxChars' must be greater than zero.",
                traceId = HttpContext.TraceIdentifier
            });
        }

        var roots = GetConfiguredRoots();
        var fullPath = Path.GetFullPath(id);

        if (!IsPathAllowed(fullPath, roots) || !System.IO.File.Exists(fullPath))
        {
            return NotFound(new
            {
                error = "FileNotFound",
                errorCode = "file_not_found",
                detail = "File not found in configured folders.",
                traceId = HttpContext.TraceIdentifier
            });
        }

        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        if (!CanExtractText(extension))
        {
            return BadRequest(new
            {
                error = "InvalidRequest",
                errorCode = "unsupported_text_extraction",
                detail = $"Text extraction is not supported for extension '{extension}'.",
                supportedExtensions = new[] { ".txt", ".md", ".csv", ".json", ".xml", ".log", ".pdf" },
                traceId = HttpContext.TraceIdentifier
            });
        }

        string fullText;

        try
        {
            fullText = ReadFullText(fullPath, extension);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not extract text for file {FilePath}", fullPath);
            return BadRequest(new
            {
                error = "TextExtractionFailed",
                errorCode = "text_extraction_failed",
                detail = "Could not extract text from the requested file.",
                hint = "For scanned PDFs (image-only), run OCR before requesting text extraction.",
                traceId = HttpContext.TraceIdentifier
            });
        }

        var appliedMaxChars = Math.Min(maxChars, MaxTextChunkChars);
        if (offset > fullText.Length)
        {
            _logger.LogWarning(
                "Requested text offset beyond extracted text length. FileName: {FileName}. RequestedOffset: {RequestedOffset}. TextLengthChars: {TextLengthChars}. TraceId: {TraceId}",
                Path.GetFileName(fullPath),
                offset,
                fullText.Length,
                HttpContext.TraceIdentifier);

            return Ok(new
            {
                id = fullPath,
                fileName = Path.GetFileName(fullPath),
                extension,
                textLengthChars = fullText.Length,
                chunkOffset = offset,
                chunkSizeChars = 0,
                hasMoreContent = false,
                nextOffset = (int?)null,
                maxCharsRequested = maxChars,
                maxCharsApplied = appliedMaxChars,
                content = string.Empty,
                contentEncoding = "utf-8",
                note = $"O offset solicitado ({offset}) ultrapassa o tamanho do texto extraído ({fullText.Length}). Recomece em offset=0 ou use o nextOffset retornado pela chamada anterior.",
                warningCode = "offset_beyond_text_length",
                traceId = HttpContext.TraceIdentifier
            });
        }

        var chunkLength = Math.Min(appliedMaxChars, fullText.Length - offset);
        var textChunk = chunkLength > 0 ? fullText.Substring(offset, chunkLength) : string.Empty;
        var hasMoreContent = offset + chunkLength < fullText.Length;
        int? nextOffset = hasMoreContent ? offset + chunkLength : null;

        string? note = null;
        if (extension == ".pdf" && string.IsNullOrWhiteSpace(fullText))
        {
            note = "Nenhum texto foi encontrado no PDF. Esse arquivo pode ser escaneado (somente imagem) e requer OCR.";
        }

        _logger.LogInformation(
            "File text chunk served. FileName: {FileName}. ChunkOffset: {ChunkOffset}. ChunkSizeChars: {ChunkSizeChars}. TextLengthChars: {TextLengthChars}. HasMoreContent: {HasMoreContent}. TraceId: {TraceId}",
            Path.GetFileName(fullPath),
            offset,
            textChunk.Length,
            fullText.Length,
            hasMoreContent,
            HttpContext.TraceIdentifier);

        return Ok(new
        {
            id = fullPath,
            fileName = Path.GetFileName(fullPath),
            extension,
            textLengthChars = fullText.Length,
            chunkOffset = offset,
            chunkSizeChars = textChunk.Length,
            hasMoreContent,
            nextOffset,
            maxCharsRequested = maxChars,
            maxCharsApplied = appliedMaxChars,
            content = textChunk,
            contentEncoding = "utf-8",
            note,
            warningCode = (string?)null,
            traceId = HttpContext.TraceIdentifier
        });
    }


    private static int GetSafeChunkSizeForTransport(int requestedChunkSize, bool isTextFile)
    {
        if (requestedChunkSize <= 0)
        {
            return 1;
        }

        if (isTextFile)
        {
            return requestedChunkSize;
        }

        var availableChars = Math.Max(1, MaxResponseContentChars - JsonEnvelopeReserveChars);
        var maxBase64InputBytes = (availableChars / 4) * 3;

        if (maxBase64InputBytes <= 0)
        {
            return 1;
        }

        return Math.Min(requestedChunkSize, maxBase64InputBytes);
    }
    private string[] GetConfiguredRoots()
    {
        var roots = _configuration.GetSection("Documents:Roots").Get<string[]>() ?? [];

        var normalizedRoots = roots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeRootPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedRoots.Length == 0)
        {
            _logger.LogError("Documents:Roots configuration is missing or empty.");
            throw new InvalidOperationException("Configure Documents:Roots with at least one directory.");
        }

        return normalizedRoots;
    }

    private IEnumerable<string> GetAllFiles(string root)
    {
        EnsureNetworkShareAccessIfNeeded(root);

        if (!Directory.Exists(root))
        {
            _logger.LogWarning("Configured documents root does not exist or is not reachable: {Root}", root);
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate files from root {Root}", root);
            return [];
        }
    }

    private bool TryHasAnyFile(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Take(1).Any();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not verify files for root {Root}", root);
            return false;
        }
    }

    private void EnsureNetworkShareAccessIfNeeded(string root)
    {
        if (!OperatingSystem.IsWindows() || !IsUncPath(root))
        {
            return;
        }

        var networkPath = GetUncShareRoot(root);
        if (string.IsNullOrWhiteSpace(networkPath))
        {
            return;
        }

        lock (NetworkConnectionLock)
        {
            if (ConnectedShares.Contains(networkPath))
            {
                return;
            }

            var credentials = ResolveNetworkCredentials();
            if (credentials is null)
            {
                return;
            }

            var userName = credentials.Domain is { Length: > 0 }
                ? $"{credentials.Domain}\\{credentials.UserName}"
                : credentials.UserName;

            var netResource = new NetResource
            {
                Scope = 0,
                ResourceType = 1,
                DisplayType = 0,
                Usage = 0,
                LocalName = null,
                RemoteName = networkPath,
                Comment = null,
                Provider = null
            };

            var result = WNetAddConnection2(netResource, credentials.Password, userName, 0);
            if (result == 0 || result == 1219)
            {
                ConnectedShares.Add(networkPath);
                _logger.LogInformation("Connected network share for documents root: {NetworkPath}", networkPath);
                return;
            }

            _logger.LogWarning(
                "Failed to connect network share {NetworkPath}. Win32Error: {Win32Error}",
                networkPath,
                result);
        }
    }

    private NetworkCredentials? ResolveNetworkCredentials()
    {
        var username = _configuration["Documents:NetworkCredentials:UserName"];
        var password = _configuration["Documents:NetworkCredentials:Password"];
        var domain = _configuration["Documents:NetworkCredentials:Domain"];

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        return new NetworkCredentials(username.Trim(), password, domain?.Trim());
    }

    private static string NormalizeRootPath(string path)
    {
        var trimmedPath = path.Trim();

        if (OperatingSystem.IsLinux() && trimmedPath.StartsWith("\\\\", StringComparison.Ordinal))
        {
            var linuxSmbPath = "/" + trimmedPath.Replace('\\', '/').TrimStart('/');
            return Path.GetFullPath(linuxSmbPath);
        }

        return Path.GetFullPath(trimmedPath);
    }

    private static bool IsPathAllowed(string path, IEnumerable<string> roots)
    {
        var normalizedPath = Path.GetFullPath(path);

        return roots.Any(root =>
            normalizedPath.StartsWith(AppendDirectorySeparator(root), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedPath, root, StringComparison.OrdinalIgnoreCase));
    }

    private static string AppendDirectorySeparator(string path)
    {
        if (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar))
        {
            return path;
        }

        return path + Path.DirectorySeparatorChar;
    }

    private static bool IsLikelyText(string extension)
    {
        return extension is ".txt" or ".md" or ".csv" or ".json" or ".xml" or ".log";
    }

    private static bool CanExtractText(string extension)
    {
        return IsLikelyText(extension) || extension == ".pdf";
    }

    private static string ReadFullText(string fullPath, string extension)
    {
        if (IsLikelyText(extension))
        {
            return System.IO.File.ReadAllText(fullPath, Encoding.UTF8);
        }

        if (extension == ".pdf")
        {
            return ReadPdfText(fullPath);
        }

        throw new NotSupportedException($"Text extraction is not supported for extension '{extension}'.");
    }

    private static string ReadPdfText(string fullPath)
    {
        using var document = PdfDocument.Open(fullPath);

        var builder = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            var pageText = page.Text?.Trim();
            if (string.IsNullOrWhiteSpace(pageText))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine();
            }

            builder.Append(pageText);
        }

        return builder.ToString();
    }

    private static byte[] ReadFileChunk(string fullPath, long offset, int chunkSize)
    {
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(offset, SeekOrigin.Begin);

        var bytesToRead = (int)Math.Min(chunkSize, stream.Length - offset);
        var buffer = new byte[bytesToRead];

        var totalRead = 0;
        while (totalRead < bytesToRead)
        {
            var read = stream.Read(buffer, totalRead, bytesToRead - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        if (totalRead == bytesToRead)
        {
            return buffer;
        }

        return buffer[..totalRead];
    }

    private static string GetRelativePathForDisplay(string fullPath, IEnumerable<string> roots)
    {
        var root = roots.FirstOrDefault(r => fullPath.StartsWith(r, StringComparison.OrdinalIgnoreCase));
        if (root is null)
        {
            return fullPath;
        }

        return Path.GetRelativePath(root, fullPath);
    }

    private static bool IsDocumentFile(string path)
    {
        var fileName = Path.GetFileName(path);
        if (IgnoredSystemFiles.Contains(fileName))
        {
            return false;
        }

        if (fileName.StartsWith("~$", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (fileName.StartsWith("._", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var extension = Path.GetExtension(fileName);
        if (IgnoredSystemExtensions.Contains(extension))
        {
            return false;
        }

        if (!AllowedDocumentExtensions.Contains(extension))
        {
            return false;
        }

        if (TryGetFileAttributes(path, out var attributes))
        {
            var hasSystemFlags = attributes.HasFlag(FileAttributes.System) ||
                                 attributes.HasFlag(FileAttributes.Temporary);
            if (hasSystemFlags)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetFileAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = System.IO.File.GetAttributes(path);
            return true;
        }
        catch
        {
            attributes = default;
            return false;
        }
    }

    private static bool TryResolveSort(
        string sortBy,
        string sortDirection,
        out string appliedSortBy,
        out string appliedSortDirection,
        out string? error)
    {
        appliedSortBy = string.IsNullOrWhiteSpace(sortBy) ? "name" : sortBy.Trim().ToLowerInvariant();
        appliedSortDirection = string.IsNullOrWhiteSpace(sortDirection) ? "asc" : sortDirection.Trim().ToLowerInvariant();

        if (appliedSortBy is not ("name" or "modified"))
        {
            error = "Query parameter 'sortBy' must be either 'name' or 'modified'.";
            return false;
        }

        if (appliedSortDirection is not ("asc" or "desc"))
        {
            error = "Query parameter 'sortDirection' must be either 'asc' or 'desc'.";
            return false;
        }

        error = null;
        return true;
    }

    private static IEnumerable<DocumentFileEntry> ApplySorting(
        IEnumerable<DocumentFileEntry> files,
        string sortBy,
        string sortDirection)
    {
        var ascending = sortDirection == "asc";

        return sortBy switch
        {
            "modified" when ascending => files
                .OrderBy(x => x.lastWriteTimeUtc)
                .ThenBy(x => x.relativePath, StringComparer.OrdinalIgnoreCase),
            "modified" => files
                .OrderByDescending(x => x.lastWriteTimeUtc)
                .ThenBy(x => x.relativePath, StringComparer.OrdinalIgnoreCase),
            _ when ascending => files.OrderBy(x => x.relativePath, StringComparer.OrdinalIgnoreCase),
            _ => files.OrderByDescending(x => x.relativePath, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static DocumentFileEntry BuildFileEntry(string path, IEnumerable<string> roots)
    {
        return new DocumentFileEntry(
            path,
            Path.GetFileName(path),
            GetRelativePathForDisplay(path, roots),
            Path.GetExtension(path),
            new FileInfo(path).Length,
            System.IO.File.GetLastWriteTimeUtc(path));
    }

    private static bool IsUncPath(string path)
    {
        return path.StartsWith("\\\\", StringComparison.Ordinal);
    }

    private static string? GetUncShareRoot(string uncPath)
    {
        var parts = uncPath.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        return $"\\\\{parts[0]}\\{parts[1]}";
    }

    private sealed record NetworkCredentials(string UserName, string Password, string? Domain);

    private sealed record DocumentFileEntry(
        string id,
        string fileName,
        string relativePath,
        string extension,
        long sizeBytes,
        DateTime lastWriteTimeUtc);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(NetResource netResource, string password, string username, int flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class NetResource
    {
        public int Scope;
        public int ResourceType;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }
}
