# Guia completo — Configuração do webhook no PipeRun

Este guia mostra **exatamente** o que preencher na tela de envio para URL do PipeRun para integrar com esta API.

---

## 1) Onde configurar no PipeRun

No PipeRun, acesse a automação de envio para URL da oportunidade e configure:

- **Tipo de envio:** `Avançado`
- **Formatação de saída:** `JSON`

---

## 2) Campos da primeira tela (Header / URL / Tipo)

Preencha os campos assim:

### URL*

Use a URL pública do endpoint:

```text
https://api.redutoresibr.com.br/api/piperun-crosseling/webhook/oportunidade
```

Exemplo real:

```text
https://api.redutoresibr.com.br/api/piperun-crosseling/webhook/oportunidade
```

### Header 1

- **Header:** `Content-Type`
- **Valor:** `application/json`

### Header 2

- **Header:** `X-API-Key`
- **Valor:** `<SUA_CHAVE_DA_API>`

> A chave deve ser a mesma configurada na API (`Security:ApiKey` / variável de ambiente `Security__ApiKey`).

### Tipo de envio

- **Valor:** `Avançado`

### Formatação de saída

- **Valor:** `JSON`

---

## 3) Campo “Adicione a expressão desejada”

No bloco de expressão avançada, você pode usar o JSON abaixo para enviar os dados principais da oportunidade.

> **Importante:** o arquivo `OpenApi/openapi-piperun-crosseling.json` **não** deve ser enviado no PipeRun. Esse arquivo serve apenas como documentação/contrato da API (para Postman, Swagger e referência técnica).
>
> No PipeRun, você deve informar somente a **expressão JSON** no campo “Adicione a expressão desejada”.

> Recomendação: começar com esse payload enxuto para facilitar validação inicial.

```json
{
  "id": id,
  "title": title,
  "status": status,
  "value": value,
  "created_at": created_at,
  "updated_at": updated_at,

  "company": {
    "id": company.id,
    "name": company.name,
    "cnpj": company.cnpj
  },

  "person": {
    "id": person.id,
    "name": person.name,
    "job_title": person.job_title,
    "contact_emails": person.contact_emails,
    "contact_phones": person.contact_phones
  },

  "pipeline": {
    "id": pipeline.id,
    "name": pipeline.name
  },

  "stage": {
    "id": stage.id,
    "name": stage.name
  },

  "tags": tags,

  "action": {
    "trigger_type": action.trigger_type,
    "pipeline": action.pipeline,
    "stage": action.stage
  }
}
```

### Teste mínimo (recomendado quando “não funciona”)

Se sua expressão completa falhar, teste primeiro com o **mínimo abaixo**:

```json
{
  "id": id,
  "title": title
}
```

Se esse teste funcionar (`200 OK`), vá adicionando blocos aos poucos (`company`, `person`, `pipeline`, etc.) até identificar qual trecho quebrou o JSON.

---

## 4) Mapeamento objetivo (campo do PipeRun → campo enviado)

| Origem no JSON PipeRun | Campo no payload enviado | Observação |
|---|---|---|
| `id` | `id` | ID da oportunidade |
| `title` | `title` | Nome/título da oportunidade |
| `status` | `status` | Situação atual |
| `value` | `value` | Valor da oportunidade |
| `company.id` | `company.id` | ID da empresa |
| `company.name` | `company.name` | Nome da empresa |
| `company.cnpj` | `company.cnpj` | CNPJ (quando houver) |
| `person.id` | `person.id` | ID da pessoa |
| `person.name` | `person.name` | Nome do contato |
| `person.job_title` | `person.job_title` | Cargo |
| `person.contact_emails` | `person.contact_emails` | Lista de emails |
| `person.contact_phones` | `person.contact_phones` | Lista de telefones |
| `pipeline.id` | `pipeline.id` | ID do funil |
| `pipeline.name` | `pipeline.name` | Nome do funil |
| `stage.id` | `stage.id` | ID da etapa |
| `stage.name` | `stage.name` | Nome da etapa |
| `tags` | `tags` | Lista de tags |
| `action.trigger_type` | `action.trigger_type` | Gatilho da automação |

---

## 5) O que esta API salva no bloco de notas

Mesmo que você envie um payload grande, o registro salvo inclui:

- Resumo de identificação:
  - `id`
  - `title`
  - `company.name`
  - `person.name`
  - `person.contact_emails[0].address`
- E também o payload completo formatado.

Saída operacional atual:

```text
AplicacoesOnline/Arquivos e Documentos/PiperunCrosseling/Logs/webhook-raw.log
```

---

## 6) Checklist de validação

1. `URL*` aponta para `/api/piperun-crosseling/webhook/oportunidade`.
2. Header `X-API-Key` preenchido corretamente.
3. Tipo `Avançado` + saída `JSON`.
4. Clicou em **Enviar teste** no PipeRun.
5. API respondeu `200 OK`.
6. O log `webhook-raw.log` registrou o recebimento e, depois, o resultado do POST de nota no PipeRun.

---

## 7) Erros comuns e como corrigir

### 401 Unauthorized

- Causa: `X-API-Key` ausente ou inválido.
- Correção: revisar Header 2 e confirmar chave no servidor.

### Webhook sem payload útil

- Causa: expressão avançada gerou payload vazio, nulo ou malformado.
- Correção: revisar o JSON da expressão e validar novamente no **JSON Preview** antes de salvar.

### 400/415 por JSON malformado no PipeRun

- Causa comum: expressão com sintaxe inválida, campo duplicado, ou valor transformado em texto em vez de objeto/lista.
- Correção:
  - manter `Tipo de envio = Avançado` e `Formatação de saída = JSON`;
  - não colar o conteúdo do arquivo OpenAPI nesse campo;
  - usar o botão **Enviar teste** e validar o painel **JSON Preview**.


### Exemplo de erro registrado para JSON inválido

Mesmo quando a expressão estiver malformada, o endpoint responde `200` rapidamente e registra o erro de parsing no log de diagnóstico. Exemplo de mensagem registrada:

```json
{
  "ParseError": "Verifique a expressão JSON no PipeRun. Erro: ...",
  "traceId": "..."
}
```

### 404 Not Found

- Causa: URL errada.
- Correção: confirmar caminho completo `/api/piperun-crosseling/webhook/oportunidade`.

### PipeRun exibindo status (0)

Quando o PipeRun mostra `O end-point solicitado retornou o status (0)`, normalmente a requisição **não chegou a receber resposta HTTP** (erro de rede, DNS, TLS/certificado, timeout ou bloqueio de firewall/WAF).

### Postman funciona, mas PipeRun falha

Se no Postman funciona e no PipeRun retorna `status (0)`, normalmente o problema é de **acesso externo** ao host (não de JSON):

- DNS público apontando para destino diferente do seu teste local/corporativo (split DNS);
- bloqueio por firewall/WAF para origem externa;
- timeout no balanceador/reverso antes de chegar na aplicação.

Validação recomendada (a partir de rede externa):

```bash
curl -i https://api.redutoresibr.com.br/api/piperun-crosseling/webhook/oportunidade
```

Se aparecer erro de upstream/timeout (ex.: `503 Service Unavailable` com mensagem de conexão), ajuste infraestrutura de borda (proxy/load balancer/firewall), pois a requisição não está chegando na aplicação.

### Erro específico: `GuzzleHttp\Exception\ConnectException` + `cURL error 28`

Quando o PipeRun retorna algo como:

```text
cURL error 28: Connection timed out after 30002 milliseconds
```

isso indica **timeout de conexão TCP/TLS em ~30s**, antes de qualquer resposta HTTP da aplicação.

Na prática, as causas mais comuns são:

- rota de rede até o host bloqueada para o ambiente do PipeRun;
- regra de firewall/WAF sem allowlist para origem externa;
- `AAAA` (IPv6) publicado no DNS, mas infraestrutura não responde corretamente em IPv6;
- proxy/reverse proxy sem upstream saudável para a aplicação.

Ações objetivas:

1. Validar DNS público (`A`/`AAAA`) e remover `AAAA` temporariamente se o stack IPv6 não estiver pronto.
2. Verificar logs do gateway (Nginx/Envoy/Cloudflare/IIS) para timeout upstream no mesmo horário do teste.
3. Garantir allowlist das origens do PipeRun no firewall/WAF (quando houver política restritiva).
4. Testar de uma rede externa (não corporativa/VPN) com `curl -v` para conferir etapa de conexão/TLS.

Com os logs atuais do endpoint, quando a chamada chega você verá também:

- `X-Forwarded-For`;
- `X-Forwarded-Proto`;
- `traceId` para correlação com logs de gateway/reverse proxy.

Checklist rápido:

1. Testar `GET` no endpoint no navegador/Postman:
   - `https://api.redutoresibr.com.br/api/piperun-crosseling/webhook/oportunidade`
   - Deve retornar `200` com JSON de diagnóstico.
2. Garantir porta `443` liberada (origem PipeRun -> destino).
3. Confirmar certificado TLS válido (cadeia completa, sem expiração).
4. Testar o mesmo payload em uma ferramenta externa (`curl`/Postman) com os mesmos headers.
5. Revisar se não há redirecionamento/bloqueio por proxy, CDN ou firewall.

A API passou a registrar logs de diagnóstico com:

- método, rota, host e content-type;
- presença e tamanho do header `X-API-Key`;
- tamanho do payload;
- `user-agent`, IP remoto e `traceId`.

Com isso, você consegue confirmar no servidor se o webhook chegou e em que formato.

---

## 8) Sugestão de payload alternativo (com todos os campos)

Se quiser, também é possível enviar **o objeto inteiro** da oportunidade no modo avançado:

```json
{
  "id": id,
  "company": {
    "id": company.id,
    "cnpj": company.cnpj
  },
  "fields": fields
}
```

Esse formato é útil quando você quer registrar o payload enriquecido no `webhook-raw.log` para análise posterior.
