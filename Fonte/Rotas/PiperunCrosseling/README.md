# Rota: PiperunCrosseling

API para receber webhooks de oportunidade do PipeRun, consultar últimas compras, avaliar recomendações de inversor com base no campo customizado Documento e publicar notas (`/v1/notes`) na oportunidade no próprio PipeRun.

## Visão geral rápida

- **Método/rota:** `POST /api/piperun-crosseling/webhook/oportunidade`
- **Autenticação:** header obrigatório `X-API-Key` (middleware global da API)
- **Formato:** `application/json`
- **Envio para PipeRun:** `POST https://api.pipe.run/v1/notes` com token lido de `Fonte/Configuracoes/TokenPiperun.ini`
- **Log bruto de diagnóstico:** `AplicacoesOnline/Arquivos e Documentos/PiperunCrosseling/Logs/webhook-raw.log`
- **Resposta de sucesso:** `200 OK`

## Fluxo funcional

1. O PipeRun dispara um webhook para a URL desta API.
2. A API responde `200` imediatamente com status `received` e `traceId` (evita timeout em webhook).
3. Em background, a API registra o payload enriquecido para diagnóstico em `webhook-raw.log`.
4. A API resolve `CodigoClientePlenus` e consulta as últimas compras no banco.
5. A API monta o texto iniciando em `Últimas compras do cliente ...` e envia para o PipeRun via `POST /v1/notes` com `{ "text": "...", "deal_id": "..." }`.
6. A API lê o campo customizado `Documento` (formato `CD_EMPRESA-CD_DOCUMENTO-NR_COMPL`), escolhe a tabela de estrutura com base no prefixo de `NR_COMPL` (`OV` = orçamento / `PV` = pedido), executa as consultas de cross-selling e, quando houver qualquer retorno, publica uma **nova nota** consolidada de recomendação de oferta.
7. As oportunidades são processadas em **fila** (uma por vez), com resposta HTTP imediata para o webhook e logs de sucesso/falha do POST.

## Endpoint

### `POST /api/piperun-crosseling/webhook/oportunidade`

Recebe o JSON enviado pelo PipeRun em modo avançado.

#### Headers esperados

- `Content-Type: application/json`
- `X-API-Key: <sua-chave-da-api>`

#### Requisição (exemplo mínimo)

```json
{
  "id": 5678,
  "title": "Oportunidade Exemplo",
  "company": {
    "name": "CRM PipeRun"
  },
  "person": {
    "name": "Peter",
    "contact_emails": [
      {
        "address": "contato@empresa.com"
      }
    ]
  }
}
```

#### Resposta 200 (exemplo)

```json
{
  "status": "received",
  "traceId": "<trace-id-da-requisicao>"
}
```


### `GET /api/piperun-crosseling/webhook/oportunidade`

Endpoint de diagnóstico para validar rapidamente se a rota está acessível.

Exemplo de retorno:

```json
{
  "status": "ok",
  "route": "/api/piperun-crosseling/webhook/oportunidade",
  "acceptedMethod": "POST",
  "requiredHeaders": ["X-API-Key", "Content-Type: application/json"],
  "message": "Use POST para enviar o payload do webhook. Esta rota GET é apenas para diagnóstico.",
  "traceId": "<trace-id-da-requisicao>"
}
```

#### Resposta 401

Quando o `X-API-Key` está ausente ou inválido (middleware global):

```json
{
  "error": "Unauthorized",
  "detail": "Provide a valid X-API-Key header."
}
```

## Regra do campo customizado `Documento`

- O webhook deve enviar o campo customizado `Documento` no formato `xx-yy-zz`.
- A API fatia o valor em:
  - `CD_EMPRESA` = primeira parte (`xx`)
  - `CD_DOCUMENTO` = segunda parte (`yy`)
  - `NR_COMPL` = terceira parte (`zz`)
- Se o valor vier vazio, inválido ou fora desse formato, o fluxo de cross-selling é ignorado sem impedir o restante do processamento.
- Se `NR_COMPL` iniciar com `OV`, a API usa a tabela `MVOR_ORCAMENTOITEMESTRUTURA`; se iniciar com `PV`, usa `MVPE_PEDIDOITEMESTRUTURA`.
- Se `NR_COMPL` não iniciar com `OV` nem `PV`, a nota de cross-selling não é enviada.
- A API consulta recomendações de `INVERSOR.XXLN` e também a `CLASSIFICACAO` de acoplamento elástico para o mesmo documento e para a tabela correspondente ao prefixo.
- Quando houver qualquer retorno, a API publica uma nota adicional com o cabeçalho `CROSS-SELLING AUTOMÁTICO - RECOMENDAÇÃO DE OFERTA:`.
- Cada recomendação é exibida em uma nova linha, separada por linhas em branco e iniciada com `• Para o produto ...`. Para compatibilidade com o PipeRun, a nota usa separadores HTML `<br><br>` entre o cabeçalho e cada item.
- Se não houver retorno nem para inversor nem para acoplamento elástico, nenhuma nota de cross-selling é enviada.

## Estrutura da rota

```text
Fonte/Rotas/PiperunCrosseling/
├─ README.md
├─ GUIA-CONFIGURACAO-PIPERUN.md
├─ Controllers/
│  └─ PiperunCrosselingController.cs
├─ Config/
│  └─ appsettings.json
├─ OpenApi/
│  └─ openapi-piperun-crosseling.json
Saída em runtime (fora da pasta publicada):
`AplicacoesOnline/Arquivos e Documentos/PiperunCrosseling/`
└─ `Logs/webhook-raw.log`
```

## Arquivos gerados

Não há mais geração de arquivo `.txt` por oportunidade.

O log contínuo é salvo em `Logs/webhook-raw.log` para diagnóstico (com truncamento e rotação simples), incluindo o resultado do envio da nota para o PipeRun.

## Operação e observabilidade

- A API grava log de `Information` quando recebe o webhook (incluindo tamanho do payload, headers e `traceId`) e quando finaliza processamento assíncrono.
- A API grava o payload bruto no arquivo `webhook-raw.log` (truncado em 64 KB) para diagnóstico rápido de conectividade/estrutura.
- A cada 100 webhooks recebidos, o arquivo `webhook-raw.log` é apagado automaticamente e reiniciado.
- Em payload vazio ou JSON inválido, a API grava `Warning` no processamento em background, mantendo o `200` para o remetente.
- Em erro inesperado, o middleware global retorna `500` com `traceId` para correlação.
- Escrita no arquivo é serializada com `lock` para evitar concorrência simultânea.


## Ativação de logs detalhados em incidentes

Quando houver incidente em `ObterCodigoClientePlenus` (cliente não encontrado, fallback inesperado ou falha de configuração), ajuste os níveis de log por ambiente:

- **Development**: `Information` para `AplicacoesOnline.Controllers.PiperunCrosselingController`.
- **Staging/Production**: `Warning` no dia a dia; elevar temporariamente para `Information` durante investigação e reverter após estabilização.

Arquivos de configuração padrão:

- `Fonte/appsettings.Development.json` → `Information`
- `Fonte/appsettings.Staging.json` → `Warning`
- `Fonte/appsettings.Production.json` → `Warning`
- `Fonte/appsettings.json` (base) → `Information`

Com `Information` habilitado, os logs do método `ObterCodigoClientePlenus` passam a registrar explicitamente:

- CNPJ recebido no início da resolução (`CnpjRecebido`).
- Motivo de fallback/erro de configuração (ex.: CNPJ ausente, configuração de banco incompleta).
- Resultado da consulta (`encontrado`/`não encontrado`) com correlação por `TraceId`.

## Teste rápido com cURL

```bash
curl -i -X POST "https://SEU_HOST/api/piperun-crosseling/webhook/oportunidade" \
  -H "Content-Type: application/json" \
  -H "X-API-Key: SUA_CHAVE" \
  -d '{
    "id": 5678,
    "title": "Oportunidade Exemplo",
    "company": {"name": "CRM PipeRun"},
    "person": {
      "name": "Peter",
      "contact_emails": [{"address": "contato@empresa.com"}]
    }
  }'
```

## Guia de configuração no PipeRun

Para configurar os campos do webhook no PipeRun (incluindo exatamente o que preencher em Header/URL/Expressão Avançada), siga o arquivo:

- [`GUIA-CONFIGURACAO-PIPERUN.md`](./GUIA-CONFIGURACAO-PIPERUN.md)

> Observação: o arquivo `OpenApi/openapi-piperun-crosseling.json` é apenas documentação da API e não deve ser "colado" na expressão avançada do PipeRun.


## Configuração de banco para `CodigoClientePlenus`

A convenção **recomendada e oficial** para novas configurações é usar as chaves:

- `database:host`
- `database:nome`
- `database:usuario`
- `database:senha`

Para compatibilidade com ambientes legados, a API também aceita as chaves antigas:

- `DB_HOST`
- `DB_NAME`
- `DB_USER`
- `DB_PASS`

Ordem de leitura no controller:

1. tenta `database:*`;
2. se vazio, faz fallback para `DB_*`.

Nos logs da aplicação, são registrados quais nomes de chave foram resolvidos (sem expor o valor da senha), para diagnóstico rápido em produção.

## Troubleshooting

- **`401 Unauthorized`**: confira o valor do header `X-API-Key` em `Security:ApiKey`. O nome do header é tratado de forma case-insensitive pelo ASP.NET Core (`X-API-Key`, `x-api-key`, `X-Api-Key`).
- **`400 Bad Request - Invalid Hostname` em HTML**: normalmente este erro vem do proxy/balanceador/IIS **antes** da API (não é resposta da aplicação). Verifique binding do host público (`api.redutoresibr.com.br`), DNS e regra de encaminhamento para a aplicação.
- Se o `GET` de diagnóstico responder JSON e o `POST` falhar, revise método HTTP, headers e corpo JSON enviados pelo PipeRun/Postman.
- Se o PipeRun retornar `GuzzleHttp\Exception\ConnectException` com `cURL error 28` (`Connection timed out after 30002 milliseconds`), trate como problema de conectividade externa (DNS/firewall/WAF/proxy/upstream), pois a aplicação não chegou a responder HTTP.
- Se o PipeRun falhar com expressão grande, teste primeiro com payload mínimo (`{"id": id, "title": title}`) e adicione blocos gradualmente.
