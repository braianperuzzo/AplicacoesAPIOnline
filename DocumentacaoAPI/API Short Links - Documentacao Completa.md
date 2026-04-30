# API Short Links - Documentação Completa

## Visão geral
A **API de Short Links** foi implementada como módulo separado e genérico para encurtamento e resolução de links em domínio próprio.

- Domínio público: `https://encurtador.redutoresibr.com.br`
- Rotas públicas: `GET /r/{token}` e `GET /s/{token}`
- Rota interna autenticada: `POST https://api.redutoresibr.com.br/api/short-links`
- Sem dependência de encurtador externo
- Sem banco de dados na V1 (token autossuficiente)

## Objetivos de arquitetura
1. Desacoplamento do módulo MetaWhatsApp.
2. Reuso em vários contextos (WhatsApp, e-mail, SMS, CRM, n8n etc.).
3. Token opaco com proteção de dados via Data Protection.
4. Redirecionamento seguro com validação forte da URL de destino.

## Endpoints

### POST /api/short-links
Cria um short link.

Endpoint completo de criação interna:
`https://api.redutoresibr.com.br/api/short-links`

**Autenticação:**
- `X-API-Key: <token>` ou
- `Authorization: Bearer <token>`

**Request exemplo:**
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

**Response exemplo:**
```json
{
  "ok": true,
  "token": "Ab3xK9Pq",
  "short_url": "https://encurtador.redutoresibr.com.br/r/Ab3xK9Pq",
  "destination_url_preview": "https://wa.me/5554999999999?text=Ol%C3%A1...",
  "expires_at_utc": "2026-04-17T12:00:00Z"
}
```

### GET /r/{token} (ou /s/{token})
Resolve o token e redireciona com HTTP `302`.

Endpoints públicos completos:
- `https://encurtador.redutoresibr.com.br/r/{token}`
- `https://encurtador.redutoresibr.com.br/s/{token}`

Comportamento:
- valida token
- valida token no armazenamento interno
- valida expiração
- valida URL de destino
- redireciona para URL final

Erros amigáveis:
- `short_link_invalid`
- `short_link_expired`

## Segurança
- Token curto aleatório (padrão 8 caracteres).
- Resolução baseada em armazenamento interno persistido em arquivo JSON.
- Validação de destino:
  - URL absoluta
  - host obrigatório
  - sem user info
  - apenas esquemas permitidos (`http` e `https`)
  - tamanho máximo configurável

## Configuração
Seção em `appsettings.json`:

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

## Exemplo de uso no n8n
1. Montar URL longa (`wa.me` com mensagem pré-preenchida).
2. Chamar `POST /api/short-links`.
3. Usar retorno `short_url` no envio ao cliente.
4. Clique do cliente resolve para URL original.

Exemplo de URL do node HTTP Request:
`https://api.redutoresibr.com.br/api/short-links`

Exemplo de body no node HTTP Request:
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

## Evolução futura
- persistência em banco
- contagem de cliques
- revogação de token
- expiração customizada
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

## Requisitos de infraestrutura (produção)
- A aplicação deve responder em **dois hosts**: `api.redutoresibr.com.br` e `encurtador.redutoresibr.com.br`.
- Proxy reverso (IIS/Nginx/ALB) deve encaminhar ambos os hosts para a aplicação.
- DNS deve apontar ambos os hosts para o mesmo destino da API (ou para destinos equivalentes que cheguem na mesma aplicação).
- Certificado HTTPS deve cobrir os dois hosts (SAN/wildcard adequado).
- Se existir `X-Forwarded-Host` no proxy, ele deve ser preservado para que a validação de host por rota funcione corretamente.
