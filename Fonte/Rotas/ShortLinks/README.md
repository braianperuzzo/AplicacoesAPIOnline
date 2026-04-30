# API de Short Links (genérica)

## Objetivo
A rota de short links foi criada como módulo isolado, sem dependência da rota MetaWhatsApp, para reutilização em qualquer fluxo que precise encurtar e resolver URLs.

Nome sugerido do módulo/projeto: **LinkRedutores.ShortLinks**.

## Endpoints

### 1) Criar short link (interno/autenticado)
`POST /api/short-links`

Endpoint público completo para consumo interno:
`https://api.redutoresibr.com.br/api/short-links`

Headers de autenticação (já padrão da aplicação):
- `X-API-Key: <token>` **ou** `Authorization: Bearer <token>`

Payload:
```json
{
  "destination_url": "https://wa.me/5554999999999?text=Ol%C3%A1...",
  "expires_in_hours": 168,
  "category": "whatsapp_contact",
  "description": "Link curto para contato comercial",
  "metadata": {
    "customer_id": 12345,
    "flow_name": "WA - Financeiro - Cobrança de Vencidos - Disparo Inicial"
  }
}
```

Resposta de sucesso:
```json
{
  "ok": true,
  "token": "Ab3xK9Pq",
  "short_url": "https://encurtador.redutoresibr.com.br/r/Ab3xK9Pq",
  "destination_url_preview": "https://wa.me/5554999999999?text=Ol%C3%A1...",
  "expires_at_utc": "2026-04-17T12:00:00Z"
}
```

### 2) Resolver e redirecionar (público)
`GET /r/{token}`

Também aceita alias: `GET /s/{token}`.

Domínio público dos links:
- `https://encurtador.redutoresibr.com.br/r/{token}`
- `https://encurtador.redutoresibr.com.br/s/{token}`

Comportamento:
- valida existência do token no armazenamento interno
- valida payload
- valida expiração
- redireciona com HTTP `302`
- retorna erro amigável quando inválido/expirado

## Segurança
- Sem banco nesta v1 (armazenamento em arquivo JSON local).
- Token curto aleatório (não carrega payload na URL).
- Persistência em `StorageFilePath` para manter links após restart.
- Validação forte de URL de destino:
  - absoluta
  - host obrigatório
  - sem user info
  - somente `http`/`https`
  - tamanho máximo configurável

## Configuração
Seção `ShortLinks` em `appsettings.json`:

```json
"ShortLinks": {
  "PublicBaseUrl": "https://encurtador.redutoresibr.com.br",
  "RoutePrefix": "r",
  "DefaultExpirationHours": 168,
  "AllowedSchemes": ["https", "http"],
  "MaxUrlLength": 2048,
  "TokenLength": 8,
  "StorageFilePath": "Configuracoes/ShortLinks/short-links-storage.json",
  "InternalApiHosts": ["api.redutoresibr.com.br"],
  "PublicResolveHosts": ["encurtador.redutoresibr.com.br"]
}
```

## Exemplo de uso n8n (HTTP Request node)
- **Method:** `POST`
- **URL:** `https://api.redutoresibr.com.br/api/short-links`
- **Authentication:** Header Auth
- **Headers:**
  - `X-API-Key: {{$env.API_KEY}}`
  - `Content-Type: application/json`
- **Body (JSON):**
```json
{
  "destination_url": "https://wa.me/5554999999999?text=Ol%C3%A1%2C%20estou%20entrando...",
  "expires_in_hours": 168,
  "category": "whatsapp_contact",
  "description": "Fluxo financeiro",
  "metadata": {
    "customer_id": 12345,
    "flow_name": "WA - Financeiro"
  }
}
```

Depois, no próximo node, use: `{{$json["short_url"]}}`.

## Evoluções futuras previstas
- persistência em banco
- contagem de cliques
- revogação de token
- expiração customizada por regra
- aliases curtos personalizados


## Persistência de chaves do Data Protection
- As chaves são persistidas em diretório estável configurável via `DataProtection:KeysPath`.
- Padrão do projeto: `Configuracoes/DataProtectionKeys`.
- Em produção com múltiplas instâncias, configure esse caminho em volume compartilhado para manter links antigos válidos após restart/deploy.


## Cache nas respostas públicas
- Endpoint público de resolução aplica headers: `Cache-Control: no-store, no-cache, max-age=0`, `Pragma: no-cache` e `Expires: 0`.


## Allowlist futura de domínios confiáveis
- Já existe suporte opcional via `ShortLinks:TrustedHostsAllowlist` (array).
- Vazio mantém comportamento aberto; preenchido restringe destinos aos hosts permitidos.

## Separação de domínio (interno x público)
- `api.redutoresibr.com.br` deve ser usado somente para chamadas de API internas (`POST /api/short-links`).
- `encurtador.redutoresibr.com.br` deve ser usado somente para resolução pública (`/r/{token}` e `/s/{token}`).
- A API sempre retorna `short_url` baseado em `ShortLinks:PublicBaseUrl`, evitando expor domínio `api.` ao cliente final.
