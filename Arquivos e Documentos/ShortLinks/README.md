# API ShortLinks (LinkRedutores.ShortLinks) — Documentação Completa

## 1) O que é esta API
A **API ShortLinks** é um módulo genérico de encurtamento e resolução de URLs, criado para funcionar de forma independente de qualquer fluxo específico (ex.: WhatsApp), permitindo reutilização em diferentes cenários como n8n, CRM, e-mail e automações internas.

Ela foi projetada para:
- gerar links curtos com domínio próprio;
- redirecionar com segurança para a URL de destino;
- proteger o conteúdo do token sem expor dados sensíveis;
- funcionar na V1 **sem banco de dados**.

---

## 2) O que a API faz
### Criação de short link (interno/autenticado)
- Endpoint: `POST /api/short-links`
- Recebe URL de destino + metadados opcionais.
- Gera token opaco com Data Protection.
- Retorna URL curta pública (ex.: `https://encurtador.redutoresibr.com.br/r/{token}`).

### Resolução de short link (público)
- Endpoints: `GET /r/{token}` e `GET /s/{token}`
- Valida token e expiração.
- Redireciona com HTTP `302` para a URL original quando válido.
- Retorna erro amigável quando inválido/expirado.

---

## 3) Arquitetura e componentes
- **Controller**: `Fonte/Rotas/ShortLinks/Controllers/ShortLinksController.cs`
- **Service**: `Fonte/Services/ShortLinks/ShortLinksService.cs`
- **Models**: `Fonte/Models/ShortLinks/*`
- **Options**: `Fonte/Options/ShortLinksOptions.cs`
- **Config**: `Fonte/appsettings.json` (`ShortLinks` e `DataProtection`)

Responsabilidades do serviço:
1. validar request;
2. montar payload do token;
3. proteger/desproteger payload (`IDataProtector`);
4. montar short URL pública;
5. resolver token e validar expiração;
6. retornar URL final para redirect.

---

## 4) Segurança
### 4.1 Token opaco
O token não expõe payload legível em URL. O conteúdo é protegido por `IDataProtector` e codificado em Base64Url.

### 4.2 Validação de URL de destino
A API valida:
- URL absoluta;
- host obrigatório;
- sem credenciais (`userInfo`);
- esquemas permitidos (`http` e `https`);
- tamanho máximo configurável;
- allowlist opcional de hosts (`TrustedHostsAllowlist`).

### 4.3 Persistência de chaves Data Protection
As chaves são persistidas em caminho estável configurável:
- `DataProtection:KeysPath`
- padrão: `Configuracoes/DataProtectionKeys`

> Em ambiente com múltiplas instâncias, configurar volume/pasta compartilhada para manter tokens válidos após restart/deploy/escala.

### 4.4 Cache em endpoint público
Para evitar cache indevido de respostas públicas, o endpoint de resolução aplica:
- `Cache-Control: no-store, no-cache, max-age=0`
- `Pragma: no-cache`
- `Expires: 0`

### 4.5 Logs e privacidade
Logs da API evitam exposição de dados sensíveis:
- não registram token completo;
- evitam registrar URL completa com dados do cliente;
- priorizam campos reduzidos (ex.: host, categoria, trace id, status).

---

## 5) Endpoints
## 5.1 POST /api/short-links
### Autenticação
Segue autenticação padrão da aplicação:
- `X-API-Key: <token>` ou
- `Authorization: Bearer <token>`

### Request exemplo
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

### Response exemplo
```json
{
  "ok": true,
  "token": "<TOKEN_OPACO>",
  "short_url": "https://encurtador.redutoresibr.com.br/r/<TOKEN_OPACO>",
  "destination_url_preview": "https://wa.me/5554999999999?text=Ol%C3%A1...",
  "expires_at_utc": "2026-04-17T12:00:00Z"
}
```

---

## 5.2 GET /r/{token} e GET /s/{token}
### Sucesso
- Resolve token válido e responde com redirect `302`.

### Erro amigável (exemplos)
```json
{
  "ok": false,
  "error": "short_link_invalid",
  "detail": "Este link curto é inválido ou foi corrompido.",
  "trace_id": "..."
}
```

```json
{
  "ok": false,
  "error": "short_link_expired",
  "detail": "Este link curto expirou. Solicite um novo link.",
  "trace_id": "..."
}
```

---

## 6) Configuração
Exemplo em `appsettings.json`:

```json
{
  "ShortLinks": {
    "PublicBaseUrl": "https://encurtador.redutoresibr.com.br",
    "RoutePrefix": "r",
    "DefaultExpirationHours": 168,
    "AllowedSchemes": ["https", "http"],
    "MaxUrlLength": 2048,
    "TrustedHostsAllowlist": []
  },
  "DataProtection": {
    "KeysPath": "Configuracoes/DataProtectionKeys"
  }
}
```

---

## 7) Exemplo de uso no n8n
1. Fluxo n8n monta URL longa (`wa.me` com mensagem pronta).
2. Chama `POST /api/short-links`.
3. Recebe `short_url`.
4. Envia `short_url` para cliente.
5. Cliente clica e API redireciona para destino original.

Body exemplo no node HTTP Request:
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

---

## 8) Logs operacionais desta pasta
- `Logs/short-links-api.log`: arquivo de referência para padrão de eventos.
- A pasta serve como evidência operacional e documentação de observabilidade da API.

---

## 9) Evolução futura
- persistência em banco;
- contagem de cliques;
- revogação de tokens;
- expiração por regra de negócio;
- aliases personalizados.
