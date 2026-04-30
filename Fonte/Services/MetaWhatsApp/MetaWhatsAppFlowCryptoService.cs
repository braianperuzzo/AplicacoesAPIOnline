using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AplicacoesOnline.Models.MetaWhatsApp;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Engines;

namespace AplicacoesOnline.Services.MetaWhatsApp;

public sealed class MetaWhatsAppFlowCryptoService
{
    private const int AesGcmTagLength = 16;
    private const int ExpectedAesGcmIvLength = 12;

    private readonly IOptions<MetaWhatsAppFlowEndpointOptions> _options;
    private readonly IWebHostEnvironment _hostEnvironment;
    private readonly ILogger<MetaWhatsAppFlowCryptoService> _logger;

    public MetaWhatsAppFlowCryptoService(
        IOptions<MetaWhatsAppFlowEndpointOptions> options,
        IWebHostEnvironment hostEnvironment,
        ILogger<MetaWhatsAppFlowCryptoService> logger)
    {
        _options = options;
        _hostEnvironment = hostEnvironment;
        _logger = logger;
    }

    public (WhatsAppFlowDataExchangeEnvelope Envelope, byte[] AesKey, byte[] InitialVector) DecryptRequest(WhatsAppFlowEncryptedRequest request)
    {
        ValidateEncryptedContract(request);

        _logger.LogInformation("Flow decrypt stage start: {Stage}", "load_private_key");
        using var rsa = LoadPrivateKey();
        _logger.LogInformation("Flow decrypt stage done: {Stage}", "load_private_key");

        _logger.LogInformation("Flow decrypt stage start: {Stage}", "base64_encrypted_aes_key");
        var encryptedAesKeyBytes = DecodeBase64OrThrow("base64_encrypted_aes_key", request.EncryptedAesKey);
        _logger.LogInformation("Flow decrypt stage done: {Stage}", "base64_encrypted_aes_key");

        _logger.LogInformation("Flow decrypt stage start: {Stage}", "rsa_decrypt_oaep_sha256");
        byte[] aesKey;
        try
        {
            aesKey = rsa.Decrypt(encryptedAesKeyBytes, RSAEncryptionPadding.OaepSHA256);
        }
        catch (Exception ex)
        {
            throw ThrowStageException("rsa_decrypt_oaep_sha256", ex);
        }

        _logger.LogInformation("Flow decrypt stage done: {Stage}", "rsa_decrypt_oaep_sha256");

        _logger.LogInformation("Flow decrypt stage start: {Stage}", "base64_initial_vector");
        var iv = DecodeBase64OrThrow("base64_initial_vector", request.InitialVector);
        _logger.LogInformation("Flow decrypt stage done: {Stage}", "base64_initial_vector");

        if (iv.Length != ExpectedAesGcmIvLength)
        {
            _logger.LogWarning(
                "Flow decrypt received IV size different from .NET AesGcm preferred nonce size. Preferred={Preferred}, Actual={Actual}. Will use BouncyCastle AES-GCM path to support protocol IV length.",
                ExpectedAesGcmIvLength,
                iv.Length);
        }

        _logger.LogInformation("Flow decrypt stage start: {Stage}", "base64_encrypted_flow_data");
        var encryptedFlowData = DecodeBase64OrThrow("base64_encrypted_flow_data", request.EncryptedFlowData);
        _logger.LogInformation("Flow decrypt stage done: {Stage}", "base64_encrypted_flow_data");

        _logger.LogInformation("Flow decrypt stage start: {Stage}", "split_ciphertext_and_tag");
        if (encryptedFlowData.Length <= AesGcmTagLength)
        {
            throw ThrowStageException(
                "split_ciphertext_and_tag",
                new CryptographicException("encrypted_flow_data inválido para AES-GCM."),
                $"EncryptedFlowDataBytes={encryptedFlowData.Length}, TagLength={AesGcmTagLength}");
        }

        var cipherTextLength = encryptedFlowData.Length - AesGcmTagLength;
        var cipherText = encryptedFlowData.AsSpan(0, cipherTextLength);
        var tag = encryptedFlowData.AsSpan(cipherTextLength, AesGcmTagLength);

        _logger.LogInformation(
            "Flow decrypt split strategy for AES-GCM payload: ciphertext + tag (final {TagLength} bytes).",
            AesGcmTagLength);
        _logger.LogInformation("Flow decrypt stage done: {Stage}", "split_ciphertext_and_tag");

        LogCryptoLengths(request, encryptedAesKeyBytes, aesKey, iv, encryptedFlowData, cipherTextLength, tag.Length, AesGcmTagLength);

        _logger.LogInformation("Flow decrypt stage start: {Stage}", "aes_gcm_decrypt");
        byte[] plainBytes;
        try
        {
            plainBytes = DecryptAesGcm(aesKey, iv, encryptedFlowData);
        }
        catch (Exception ex)
        {
            throw ThrowStageException("aes_gcm_decrypt", ex);
        }

        _logger.LogInformation("Flow decrypt stage done: {Stage}", "aes_gcm_decrypt");

        _logger.LogInformation("Flow decrypt stage start: {Stage}", "json_deserialize");
        WhatsAppFlowDataExchangeEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<WhatsAppFlowDataExchangeEnvelope>(plainBytes)
                ?? throw new JsonException("Não foi possível desserializar payload de data exchange.");
        }
        catch (Exception ex)
        {
            throw ThrowStageException("json_deserialize", ex);
        }

        _logger.LogInformation("Flow decrypt stage done: {Stage}", "json_deserialize");
        return (envelope, aesKey, iv);
    }

    public string EncryptResponse(MetaWhatsAppLoginFlowResponsePayload payload, byte[] aesKey, byte[] initialVector)
    {
        var flippedIv = initialVector.Select(static b => (byte)~b).ToArray();
        var plainBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var encrypted = EncryptAesGcm(aesKey, flippedIv, plainBytes);
        return Convert.ToBase64String(encrypted);
    }

    public (bool Ok, string ResolvedPath, string PublicKeyFingerprintSha256) RunCryptoDiagnosticsCheck()
    {
        var (_, resolvedPath) = LoadPrivateKeyInternal();

        using var rsa = RSA.Create();
        var pemContents = File.ReadAllText(resolvedPath, Encoding.UTF8);
        rsa.ImportFromPem(pemContents);

        ValidateKeyPair(rsa, resolvedPath);

        var publicDer = rsa.ExportSubjectPublicKeyInfo();
        var fingerprint = Convert.ToHexString(SHA256.HashData(publicDer));

        return (true, resolvedPath, fingerprint);
    }

    private RSA LoadPrivateKey()
    {
        var (rsa, _) = LoadPrivateKeyInternal();
        return rsa;
    }

    private (RSA Rsa, string ResolvedPath) LoadPrivateKeyInternal()
    {
        var configuredPath = _options.Value.PrivateKeyPath;
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            configuredPath = Environment.GetEnvironmentVariable("META_WHATSAPP_FLOW_PRIVATE_KEY_PATH");
        }

        configuredPath ??= "Rotas/MetaWhatsApp/FlowsTemplatesMeta/FlowLogin/private_key.pem";
        configuredPath = configuredPath.Replace('\\', '/');
        var resolvedPath = ResolvePrivateKeyPath(configuredPath);
        var fileExists = File.Exists(resolvedPath);

        _logger.LogInformation(
            "Flow private key diagnostics: ContentRootPath={ContentRootPath}, ConfiguredPath={ConfiguredPath}, ResolvedPath={ResolvedPath}, FileExists={FileExists}",
            _hostEnvironment.ContentRootPath,
            configuredPath,
            resolvedPath,
            fileExists);

        if (!fileExists)
        {
            throw new FileNotFoundException("Arquivo de chave privada do Flow não encontrado.", resolvedPath);
        }

        var pemContents = File.ReadAllText(resolvedPath, Encoding.UTF8);
        _logger.LogInformation("Leitura da private key concluída com sucesso. ResolvedPath={ResolvedPath}", resolvedPath);

        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pemContents);
        }
        catch (Exception ex)
        {
            throw ThrowStageException("rsa.ImportFromPem", ex);
        }

        try
        {
            ValidateKeyPair(rsa, resolvedPath);
        }
        catch (Exception ex)
        {
            throw ThrowStageException("ValidateKeyPair", ex);
        }

        _logger.LogInformation("Chave privada do WhatsApp Flow carregada com sucesso.");
        return (rsa, resolvedPath);
    }

    private string ResolvePrivateKeyPath(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            if (File.Exists(configuredPath))
            {
                return configuredPath;
            }

            var rootedAsRelative = configuredPath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(_hostEnvironment.ContentRootPath, rootedAsRelative));
        }

        return Path.GetFullPath(Path.Combine(_hostEnvironment.ContentRootPath, configuredPath));
    }

    private void ValidateEncryptedContract(WhatsAppFlowEncryptedRequest request)
    {
        ValidateStringField("EncryptedAesKey", request.EncryptedAesKey);
        ValidateStringField("InitialVector", request.InitialVector);
        ValidateStringField("EncryptedFlowData", request.EncryptedFlowData);
    }

    private void ValidateStringField(string fieldName, string? value)
    {
        var isNull = value is null;
        var isEmpty = value is not null && value.Length == 0;
        var isWhiteSpace = value is not null && string.IsNullOrWhiteSpace(value);

        _logger.LogInformation(
            "Flow crypto contract check: Field={Field}, IsNull={IsNull}, IsEmpty={IsEmpty}, IsWhiteSpace={IsWhiteSpace}",
            fieldName,
            isNull,
            isEmpty,
            isWhiteSpace);

        if (isNull || isWhiteSpace)
        {
            throw ThrowStageException(
                $"validate_contract_{fieldName}",
                new ArgumentException($"Campo obrigatório inválido: {fieldName}"));
        }
    }

    private byte[] DecodeBase64OrThrow(string stage, string value)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (Exception ex)
        {
            throw ThrowStageException(stage, ex);
        }
    }

    private void LogCryptoLengths(
        WhatsAppFlowEncryptedRequest request,
        byte[] encryptedAesKeyBytes,
        byte[] aesKey,
        byte[] iv,
        byte[] encryptedFlowData,
        int cipherTextLength,
        int tagLength,
        int configuredTagLength)
    {
        _logger.LogInformation(
            "Flow crypto lengths: EncryptedAesKeyString={EncryptedAesKeyStringLength}, InitialVectorString={InitialVectorStringLength}, EncryptedFlowDataString={EncryptedFlowDataStringLength}, EncryptedAesKeyBytes={EncryptedAesKeyBytesLength}, AesKeyBytes={AesKeyLength}, InitialVectorBytes={IvLength}, EncryptedFlowDataBytes={EncryptedFlowDataBytesLength}, CipherTextBytes={CipherTextLength}, TagBytes={TagLength}, ConfiguredTagLength={ConfiguredTagLength}",
            request.EncryptedAesKey?.Length ?? 0,
            request.InitialVector?.Length ?? 0,
            request.EncryptedFlowData?.Length ?? 0,
            encryptedAesKeyBytes.Length,
            aesKey.Length,
            iv.Length,
            encryptedFlowData.Length,
            cipherTextLength,
            tagLength,
            configuredTagLength);
    }

    private static byte[] DecryptAesGcm(byte[] aesKey, byte[] iv, byte[] encryptedFlowData)
    {
        var cipher = new GcmBlockCipher(new AesEngine());
        var parameters = new AeadParameters(new KeyParameter(aesKey), AesGcmTagLength * 8, iv);
        cipher.Init(false, parameters);

        var plainBytes = new byte[cipher.GetOutputSize(encryptedFlowData.Length)];
        var offset = cipher.ProcessBytes(encryptedFlowData, 0, encryptedFlowData.Length, plainBytes, 0);
        var final = cipher.DoFinal(plainBytes, offset);
        var total = offset + final;

        return total == plainBytes.Length ? plainBytes : plainBytes.AsSpan(0, total).ToArray();
    }

    private static byte[] EncryptAesGcm(byte[] aesKey, byte[] iv, byte[] plainBytes)
    {
        var cipher = new GcmBlockCipher(new AesEngine());
        var parameters = new AeadParameters(new KeyParameter(aesKey), AesGcmTagLength * 8, iv);
        cipher.Init(true, parameters);

        var encryptedBytes = new byte[cipher.GetOutputSize(plainBytes.Length)];
        var offset = cipher.ProcessBytes(plainBytes, 0, plainBytes.Length, encryptedBytes, 0);
        var final = cipher.DoFinal(encryptedBytes, offset);
        var total = offset + final;

        return total == encryptedBytes.Length ? encryptedBytes : encryptedBytes.AsSpan(0, total).ToArray();
    }

    private void ValidateKeyPair(RSA rsa, string resolvedPath)
    {
        var hasPrivateKey = rsa.ExportParameters(true).D is not null;
        var data = Encoding.UTF8.GetBytes("flow-keypair-check");
        var signature = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        using var publicOnlyRsa = RSA.Create();
        publicOnlyRsa.ImportParameters(rsa.ExportParameters(false));
        var verificationOk = publicOnlyRsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        _logger.LogInformation(
            "Validação do par de chaves do Flow concluída. ResolvedPath={ResolvedPath}, HasPrivateKey={HasPrivateKey}, SignatureVerification={SignatureVerification}",
            resolvedPath,
            hasPrivateKey,
            verificationOk);

        if (!hasPrivateKey || !verificationOk)
        {
            throw new CryptographicException("O par de chaves do Flow parece inválido.");
        }
    }

    private Exception ThrowStageException(string stage, Exception ex, string? extraContext = null)
    {
        if (string.IsNullOrWhiteSpace(extraContext))
        {
            _logger.LogError(ex, "Falha criptográfica no estágio {Stage}.", stage);
        }
        else
        {
            _logger.LogError(ex, "Falha criptográfica no estágio {Stage}. Contexto extra: {ExtraContext}", stage, extraContext);
        }

        return new InvalidOperationException($"Falha criptográfica no estágio '{stage}'.", ex);
    }
}
