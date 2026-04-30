# Rota: ChatGPTDocumentos

API específica para navegação e leitura controlada de arquivos, destinada à integração via Action do ChatGPT.

## Estrutura interna

```text
Fonte/Rotas/ChatGPTDocumentos/
├─ README.md
├─ Controllers/
│  └─ DocumentsController.cs
├─ Config/
│  └─ appsettings.json
└─ OpenApi/
   └─ openapi-chatgpt-actions.json
```

## Endpoints

- `GET /api/documents/status`
  - Valida raízes configuradas e retorna disponibilidade.
- `GET /api/documents/files`
  - Lista arquivos das pastas permitidas.
- `GET /api/documents/file-content?id=<caminho-absoluto-do-arquivo>&offset=0&maxBytes=65536`
  - Lê conteúdo de arquivo permitido em blocos (paginação por bytes).
  - O `id` deve ser exatamente um item retornado por `GET /api/documents/files` (pastas/diretórios retornam `400`).

- `GET /api/documents/file-text?id=<caminho-absoluto-do-arquivo>&offset=0&maxChars=30000`
  - Extrai somente texto de arquivos suportados (`.txt`, `.md`, `.csv`, `.json`, `.xml`, `.log`, `.pdf`).
  - Para PDF, extrai a camada de texto (quando existir). PDFs escaneados (somente imagem) exigem OCR prévio.



### Paginação de conteúdo (`file-content`)

Para evitar respostas grandes demais na Action do ChatGPT, o endpoint retorna o conteúdo em blocos:

- `offset` (opcional): byte inicial da leitura. Padrão `0`.
- `maxBytes` (opcional): tamanho máximo do bloco. Padrão `65536` (64 KB), com teto lógico de `1048576` (1 MB). Para arquivos binários (ex.: PDF), o endpoint reduz automaticamente o bloco para manter a resposta abaixo do limite de payload da Action do ChatGPT e evitar `ResponseTooLargeError`.

A resposta inclui:

- `sizeBytes`: tamanho total do arquivo.
- `chunkOffset`: offset do bloco atual.
- `chunkSizeBytes`: bytes efetivamente retornados.
- `hasMoreContent`: `true` quando ainda existe conteúdo pendente.
- `nextOffset`: offset para próxima chamada (quando `hasMoreContent = true`).

Exemplo de leitura sequencial:

1. Chamar com `offset=0`.
2. Se `hasMoreContent=true`, chamar novamente com `offset=nextOffset`.
3. Repetir até `hasMoreContent=false`.


### Extração de texto (`file-text`)

Use esse endpoint quando você quer enviar **somente texto** para o ChatGPT, inclusive em PDFs com camada textual.

- `offset` (opcional): posição inicial em caracteres.
- `maxChars` (opcional): tamanho máximo do bloco textual. Padrão `30000`, teto `120000`.

Fluxo recomendado:

1. Chamar `GET /api/documents/file-text` com `offset=0`.
2. Se `hasMoreContent=true`, repetir com `offset=nextOffset`.
3. Consolidar os blocos de `content` antes de enviar ao modelo.

Comportamento de paginação:

- Se o `offset` estiver dentro do tamanho do texto extraído, o endpoint retorna normalmente o bloco solicitado.
- Se o `offset` ultrapassar `textLengthChars`, o endpoint responde `200` com `content=""`, `chunkSizeChars=0`, `hasMoreContent=false`, `warningCode="offset_beyond_text_length"` e `traceId`. Assim, a Action consegue distinguir paginação inválida de erro de autenticação.

> Observação: para PDFs escaneados sem camada de texto, o retorno pode vir vazio com `note` orientando OCR.

## Segurança

- Rota protegida por `X-API-Key` (middleware global).
- Segredo recomendado via variável de ambiente: `Security__ApiKey`.
- Leitura de arquivos restrita a `Documents:Roots`.

## Esquema de logs (erros e auditoria)

A API agora registra eventos com foco em rastreabilidade para integração ChatGPT:

- **`Information`**
  - Chamada de `status`.
  - Total de arquivos retornados em `files`.
  - Entrega de conteúdo em `file-content` (nome do arquivo, offset do bloco e tamanho).
- **`Warning`**
  - Tentativa de acesso sem `X-API-Key` válido.
  - `file-content` sem parâmetro `id`.
  - `id` inválido ou fora das pastas permitidas.
  - `offset` de `file-text` maior que `textLengthChars` (falha técnica de paginação, sem caracterizar autenticação).
- **`Error`**
  - Falha de configuração (`Documents:Roots` ausente/vazio).
  - Exceções não tratadas (capturadas por middleware global).
- **`Critical`**
  - Inicialização bloqueada por ausência de `Security:ApiKey` válida.

### Campos úteis dos logs

- `TraceId`: identificador da requisição para correlação.
- `Method` e `Path`: endpoint acessado.
- `RemoteIp`: IP de origem em rejeições de autenticação.
- `RequestedId`, `FileName`, `SizeBytes`: contexto funcional da operação.

### Resposta de erro 500

Em exceções não tratadas, a API retorna:

```json
{
  "error": "InternalServerError",
  "detail": "An unexpected error occurred while processing the request.",
  "traceId": "<id-da-requisicao>"
}
```

Use o `traceId` para localizar a entrada correspondente no provedor de logs do ASP.NET Core.

### Resposta de erro 401

Quando a chave estiver ausente ou inválida, a API retorna:

```json
{
  "error": "Unauthorized",
  "errorCode": "authentication_failed",
  "detail": "Provide a valid X-API-Key header.",
  "traceId": "<id-da-requisicao>"
}
```


## Troubleshooting (Action do GPT)

Se a Action falhar em `/api/documents/status`, verifique primeiro se a chamada está indo para **HTTPS**. Em ambientes sem redirecionamento ativo na borda, chamadas em HTTP podem falhar.

Checklist rápido:

- Confirmar que o `server url` da Action está como `https://api.redutoresibr.com.br` (com `https`).
- Reimportar o OpenAPI de `OpenApi/openapi-chatgpt-actions.json` após qualquer ajuste.
- Validar conectividade com:
  - `curl -i https://api.redutoresibr.com.br/api/documents/status` → esperado `401` sem chave (endpoint existe).
  - `curl -i http://api.redutoresibr.com.br/api/documents/status` → esperado redirecionamento para HTTPS (ou falha na borda, se não houver redirecionamento aplicado).

## Configuração da rota

Arquivo da rota:

- `Config/appsettings.json`

Exemplo:

```json
{
  "Documents": {
    "Roots": [
      "C:\\Caminho\\Permitido1",
      "C:\\Caminho\\Permitido2"
    ]
  }
}
```

Para pastas de rede (UNC) em IIS/Windows com usuário de serviço sem acesso direto, você pode configurar credenciais específicas:

```json
{
  "Documents": {
    "Roots": [
      "\\ad-server\IBR\ISO 9001 - Documentos\1 - Procedimentos",
      "\\ad-server\IBR\ISO 9001 - Documentos\2 - Instruções de trabalho"
    ],
    "NetworkCredentials": {
      "UserName": "usuario_leitura",
      "Password": "senha",
      "Domain": "IBR"
    }
  }
}
```

> Recomendado: definir `Documents__NetworkCredentials__UserName`, `Documents__NetworkCredentials__Password` e `Documents__NetworkCredentials__Domain` por variável de ambiente, em vez de manter credenciais em arquivo.

## OpenAPI

- `OpenApi/openapi-chatgpt-actions.json` é o contrato usado na Action do ChatGPT.
