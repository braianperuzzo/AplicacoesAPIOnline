# API WhatsApp (Meta Webhook) — documentação atualizada

## 1) Objetivo

Esta API recebe eventos do WhatsApp Cloud API (Meta), valida assinatura HMAC, faz correlação com interações registradas e encaminha respostas para webhooks n8n.

Além do webhook principal, ela também expõe endpoints para:

- registrar interação antes do envio;
- atualizar status da interação (enviado, falha, recusa, fora do padrão);
- registrar/consultar contexto de execução por `execution_id`;
- registrar trilha de eventos técnicos e de negócio;
- executar diagnósticos de payload/roteamento;
- validar gravação do log persistente.

---

## 2) Endpoints disponíveis

### 2.1 Webhook Meta

- `GET /webhooks/meta/whatsapp`
- `POST /webhooks/meta/whatsapp`

### 2.2 Registro e ciclo de vida de interação

- `POST /webhooks/meta/whatsapp/interactions`
- `POST /api/meta/whatsapp/interactions/register` (alias)
- `PATCH /webhooks/meta/whatsapp/interactions/{interactionId}/sent`
- `PATCH /webhooks/meta/whatsapp/interactions/{interactionId}/documents-sent`
- `PATCH /webhooks/meta/whatsapp/interactions/{interactionId}/failed`
- `PATCH /webhooks/meta/whatsapp/interactions/{interactionId}/refused`
- `PATCH /webhooks/meta/whatsapp/interactions/{interactionId}/invalid-response-sent`
- `POST /webhooks/meta/whatsapp/interactions/force-close-by-phone`
- `POST /api/meta/whatsapp/interactions/force-close-by-phone` (alias)
- `GET /webhooks/meta/whatsapp/interactions/{interactionId}/outbound/last`
- `GET /api/meta/whatsapp/interactions/{interactionId}/outbound/last` (alias)

### 2.3 Contexto de execução

- `POST /webhooks/meta/whatsapp/interactions/execution-context`
- `POST /api/meta/whatsapp/interactions/execution-context/register` (alias)
- `GET /webhooks/meta/whatsapp/interactions/execution-context/{executionId}`
- `GET /api/meta/whatsapp/interactions/execution-context/{executionId}` (alias)
- `POST /webhooks/meta/whatsapp/interactions/execution-context/query`
- `POST /api/meta/whatsapp/interactions/execution-context/query` (alias)

### 2.4 Diagnóstico técnico

- `POST /webhooks/meta/whatsapp/interactions/debug-raw`
- `POST /webhooks/meta/whatsapp/interactions/debug-parse`
- `POST /webhooks/meta/whatsapp/interactions/debug-route-resolution`
- `POST /webhooks/meta/whatsapp/logs/probe`
- `POST /api/meta/whatsapp/logs/probe` (alias)

### 2.5 Eventos genéricos e trilhas

- `POST /webhooks/meta/whatsapp/events`
- `POST /api/meta/whatsapp/events` (alias)
- `POST /webhooks/meta/whatsapp/events/invalid-payload`
- `POST /webhooks/meta/whatsapp/interactions/events`
- `POST /api/meta/whatsapp/interactions/events` (alias)
- `POST /webhooks/meta/whatsapp/invalid-payloads`

---

## 3) Segurança

## 3.1 Webhook público da Meta

### `GET /webhooks/meta/whatsapp`
Handshake via:

- `hub.mode=subscribe`
- `hub.verify_token`
- `hub.challenge`

Aceita aliases de query (`hub_mode`, `mode`, etc.).

### `POST /webhooks/meta/whatsapp`
Exige assinatura:

- Header: `X-Hub-Signature-256: sha256=<hmac_hex>`
- Chave: `MetaWhatsAppWebhook:AppSecret`

Se assinatura inválida: `401 invalid_meta_signature`.

> O webhook público da Meta não usa `X-API-Key` da API interna.

## 3.2 Rotas internas

As demais rotas seguem a autenticação global da aplicação (ex.: `X-API-Key`), conforme configuração do projeto.

---

## 4) Parser de payloads Meta (estado atual)

A API aceita:

1. Envelope oficial Meta: `entry[].changes[].value`;
2. Payload com `value` direto;
3. Formatos relacionados a coexistência (`smb_message_echoes`).

Campos lidos para mensagem/resposta:

- origem: `messages[].from`, `contacts[].wa_id`;
- identificação/correlação: `messages[].id`, `messages[].context.id`, `context.metadata.interaction_id`, `referral.source_id`, payload de botão/lista;
- resposta: `button.text`, `interactive.button_reply.title`, `interactive.list_reply.title`, `text.body`;
- resposta de Flow: `interactive.type = nfm_reply` com `interactive.nfm_reply.response_json`;
- status Meta: `statuses[].status`, `recipient_id`, `id`, `conversation.id`, `pricing.category`, `errors[]`, `timestamp`.

Classificação de resposta:

- `SIM`
- `NAO` (normaliza `NÃO`/`NAO`)
- `FLOW` (quando recebe `nfm_reply` com `response_json`)
- `FORA_DO_PADRAO`

Eventos de status Meta auditados:

- `META_STATUS_SENT`
- `META_STATUS_DELIVERED`
- `META_STATUS_READ`
- `META_STATUS_FAILED`

Se o evento for apenas status (sem mensagem inbound), a API audita e retorna `202 { status: "audited", reason: "status_event" }`.

---

## 5) Roteamento para n8n

Ordem de correlação da interação:

1. `context_message_id` / `meta_message_id`;
2. `interaction_id` explícito;
3. `button_payload`;
4. fallback por telefone/canonical key (com proteção contra ambiguidade).

Seleção de rota:

- `SIM` → `route_on_yes` ou `N8nWebhookUrl`
- `NAO` → `route_on_no` ou `GlobalNaoN8nWebhookUrl` ou `N8nWebhookUrl`
- `FLOW` → `route_on_flow` ou `route_on_fallback` ou `N8nWebhookUrl` (compatibilidade gradual)
- `FORA_DO_PADRAO` → `route_on_fallback` ou `N8nWebhookUrl`

Formato aceito para rotas:

- URL absoluta;
- path relativo (ex.: `/webhook/minha-rota`) — é resolvido usando base da interação ou `DefaultN8nWebhookUrl`.

A API também deduplica mensagens inbound por `meta_message_id` para evitar dispatch duplicado.

---

## 6) Contratos principais

## 6.1 Registrar interação

### `POST /webhooks/meta/whatsapp/interactions`

Campos obrigatórios:

- `interaction_id`
- `flow_key`
- ao menos uma rota: `n8n_webhook_url` ou `route_on_yes` ou `route_on_no` ou `route_on_flow` ou `route_on_fallback`
- `recipient_e164`

Campos relevantes suportados:

- cliente: `customer_id`, `customer_name`, `customer_document`, `customer_phone`, `customer_wa_id`, `customer_user_id`, `customer_parent_user_id`, `customer_username`
- destino: `phone_key`, `recipient_e164`, `recipient`, `recipient_user_id`, `recipient_parent_user_id`
- metadados: `flow_name`, `workflow_name`, `webhook_name`, `message_type`, `message_name`, `template_name`, `template_language`, `whatsapp_node_name`
- decisão: `is_decision_anchor`, `accepts_yes`, `accepts_no`, `accepts_flow`, `yes_route`, `no_route`, `flow_route`, `fallback_route` (+ respectivas api keys)
- vínculo outbound: `outbound_message_id`, `button_payload`
- expiração: `expires_at_utc`

Também aceita contrato aninhado (`customer`, `phone`, `routing`, `template`, `businessContext`) e aliases camelCase/PascalCase.

Respostas comuns:

- `200` registrado
- `400` validação
- `415` content-type inválido
- `422` payload incompatível/erro de resolução de autenticação de rota
- `500` erro operacional

Modo diagnóstico (`dry_run`):

- query `?diag=true` ou `?dry_run=true`, ou header `X-Interaction-Register-Dry-Run: true`
- retorna snapshot/preview sem persistir interação.

## 6.2 Atualizar status da interação

### `PATCH .../sent` e `.../documents-sent`
- status permitido: `ENVIADO` ou `DOCUMENTOS_ENVIADOS`
- evento persistido: `ENVIO_ACEITO_META` ou `DOCUMENTOS_ENVIADOS`

### `PATCH .../failed`
- status permitido: `ERRO_ENVIO`, `ERRO_PROCESSAMENTO_SIM`, `ERRO_RESPOSTA_NAO`, `ERRO_RESPOSTA_FLOW`, `ERRO_RESPOSTA_FORA_PADRAO`
- evento persistido: `ENVIO_FALHOU`

### `PATCH .../refused`
- status permitido: `RECUSADO`
- resposta aceita: `NAO` ou `NÃO`
- evento persistido: `RECUSA`

### `PATCH .../invalid-response-sent`
- status permitido: `AGUARDANDO_RESPOSTA`
- `response_type` permitido: `FORA_DO_PADRAO`
- evento persistido: `RESPOSTA_FORA_PADRAO_ENVIADA`

### `POST .../force-close-by-phone`
- obrigatório: `phone`
- opcional: `reason`
- ação: encerra interações ativas do telefone com status `CANCELADA` e remove do roteamento inbound
- resposta: `200` com `closed` e `interaction_ids`; quando não houver interação ativa retorna `closed=0` e `status=SEM_INTERACAO_ATIVA`

Erros comuns desses PATCH:

- `400 interaction_id_required` / validação
- `404 interaction_not_found`
- `409 incompatible_status`
- `500 unknown_error`

## 6.3 Contexto de execução

### Registrar
`POST /webhooks/meta/whatsapp/interactions/execution-context`

Obrigatórios:

- `execution_id`
- `interaction_id`

Retorna `ok=true`, `status=registered` e `execution_context`.

### Consultar
- `GET .../execution-context/{executionId}`
- `POST .../execution-context/query` com `{ "execution_id": "..." }`

Se não existir: `404 execution_context_not_found`.

## 6.4 Última outbound

### `GET /webhooks/meta/whatsapp/interactions/{interactionId}/outbound/last`

- `200` com `last_outbound`
- `404 last_outbound_not_found`

## 6.5 Eventos genéricos

### `POST /webhooks/meta/whatsapp/events`
Aceita `event_type` (lista controlada), dados de contexto e `deduplication_key` opcional.

Eventos permitidos incluem (resumo):

- criação e envio (`INTERACAO_CRIADA`, `ENVIO_ACEITO_META`, `ENVIO_FALHOU`)
- resposta do cliente e classificação
- eventos de resumo/documentos
- eventos operacionais/avisos
- `ETAPA_NAO_MAPEADA` (interação criada, mas sem template por etapa sem mapeamento de disparo)
- `CONTEXTO_INVALIDO_PREFERENCIAS` (erro de contexto no workflow **WA - Preferências de Contato - Processar Resposta do Flow**)
- `CONFIRMACAO_PENDENTE_SEM_TELEFONE` (preferência processada, mas sem telefone para confirmação final)
- `PREFERENCIA_PROCESSADA` (preferência processada e confirmação final enviada)
- `RECUSA`
- `META_STATUS_SENT|DELIVERED|READ|FAILED`

### `POST /webhooks/meta/whatsapp/events/invalid-payload`
Força `event_type=PAYLOAD_INVALIDO`.

## 6.6 Trilha de eventos de interação

### `POST /webhooks/meta/whatsapp/interactions/events`

- valida `event_type` permitido para trilha técnica;
- tenta hidratar dados do `raw_body` (interaction_id, flow, customer, phone etc.);
- tenta correlação automática quando `interaction_id` não vier explícito;
- persiste com deduplicação.

`event_type` permitidos na trilha de interação:

- `PAYLOAD_INVALIDO`
- `SEM_TITULOS_EM_ABERTO`
- `RESUMO_ENVIADO`
- `ERRO_ENVIO_RESUMO`
- `DOCUMENTOS_ENVIADOS`
- `ERRO_ENVIO_DOCUMENTOS`
- `DESTINO_INVALIDO_WHATSAPP`
- `AVISO_ERRO_OPERACIONAL_ENVIADO`
- `ERRO_AO_ENVIAR_AVISO_OPERACIONAL`
- `AVISO_SEM_TITULOS_ENVIADO`
- `ERRO_ENVIO_AVISO_SEM_TITULOS`
- `ENVIO_ACEITO_META`
- `ENVIO_FALHOU`
- `ENVIO_PENDENTE_SEM_TELEFONE`
- `CONTEXTO_INVALIDO_PREFERENCIAS` (severidade esperada: `error`)
- `CONFIRMACAO_PENDENTE_SEM_TELEFONE` (severidade esperada: `warning`)
- `PREFERENCIA_PROCESSADA` (severidade esperada: `info`)
- `ETAPA_NAO_MAPEADA` (novo: interação criada sem envio por ausência de mapeamento da etapa atual)

Eventos de preferências de contato (workflow `WA - Preferências de Contato - Processar Resposta do Flow`):

- `CONTEXTO_INVALIDO_PREFERENCIAS`: usar quando a API receber a resposta do Flow, mas não conseguir devolver contexto suficiente para o n8n continuar (`severity=error`).
- `CONFIRMACAO_PENDENTE_SEM_TELEFONE`: usar quando a preferência for processada sem telefone disponível para disparar a confirmação final ao cliente (`severity=warning`).
- `PREFERENCIA_PROCESSADA`: usar quando a preferência for processada com sucesso e a confirmação final tiver sido enviada ao cliente (`severity=info`).

Exemplo de payload para etapa não mapeada:

```json
{
  "event_type": "ETAPA_NAO_MAPEADA",
  "severity": "warning",
  "timestamp": "2026-04-06T12:00:00Z",
  "interaction_id": "int_123",
  "flow_key": "cobranca_whatsapp",
  "customer_id": 98765,
  "customer_name": "Cliente Exemplo",
  "recipient_phone_e164": "+5511999999999",
  "message": "Template não enviado porque a etapa atual do pedido não possui mapeamento de disparo.",
  "details": {
    "pedido_id": "PED-456",
    "etapa_atual": "AGUARDANDO_APROVACAO",
    "acao": "nao_enviado"
  }
}
```

### `POST /webhooks/meta/whatsapp/invalid-payloads`
Força `event_type=PAYLOAD_INVALIDO` na trilha.

---

## 7) Endpoints de diagnóstico

### `POST /interactions/debug-raw`
Retorna tamanho do body, preview e metadados de content-type.

### `POST /interactions/debug-parse`
Valida JSON e retorna propriedades de topo encontradas.

### `POST /interactions/debug-route-resolution`
Executa parsing/normalização e mostra erros de resolução de rota/auth sem registrar interação.

### `POST /logs/probe`
Testa escrita no diretório de log persistente; útil para validar permissões/caminho.

---

## 8) Log persistente e deduplicação

A API registra trilha por telefone e interação com campos de auditoria (payload resumido, ids, status, rota, erros, etc.).

Há deduplicação por chave composta (evento/interação/wamid/timestamp/status), com exceção tratada para alguns eventos operacionais quando necessário.

### 8.1 Impacto operacional em dashboards, filtros e relatórios

- Atualizar filtros de `event_type` para incluir `ETAPA_NAO_MAPEADA` como categoria de não envio operacional.
- Manter `ENVIO_PENDENTE_SEM_TELEFONE` exclusivamente para ausência/invalidade de telefone.
- Se houver agrupamento por classes de evento, incluir `ETAPA_NAO_MAPEADA` no grupo de "não enviado por regra de negócio/mapeamento".
- Revisar alertas e métricas de taxa de envio para não contabilizar `ETAPA_NAO_MAPEADA` como falha técnica de transporte (`ENVIO_FALHOU`).

---

## 9) Configurações principais

Seção `MetaWhatsAppWebhook`:

- `VerifyToken`
- `AppSecret`
- `DefaultN8nWebhookUrl`
- `GlobalNaoN8nWebhookUrl`
- `GlobalNaoN8nApiKey`
- `InteractionRetentionHours`
- `PersistentLogDirectory`
- `StoreMediaContent`
- `MediaStorageDirectory`
- `MediaAccessToken`
- `OutboundDecisionTemplates`

Seção `N8nWebhookSecurity`:

- políticas de chave para chamadas internas aos webhooks n8n.

---

## 10) Boas práticas de integração

- sempre registrar interação antes de enviar template/outbound;
- enviar `outbound_message_id` e/ou `button_payload` para correlação forte;
- configurar rotas específicas para SIM/NAO/fallback;
- usar `execution_id` para rastrear jornada entre n8n e API;
- monitorar logs persistentes e eventos `META_STATUS_*` para observabilidade;
- usar endpoints de diagnóstico quando houver dúvida de payload/contrato.
