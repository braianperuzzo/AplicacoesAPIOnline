# Meta WhatsApp - Fluxo do Viewer

## Fluxo principal (UI -> endpoints -> logs)

1. **UI**
   - A rota `GET /whatsapp/viewer` entrega a página estática em `wwwroot/whatsapp-viewer/index.html`.
   - O front-end carrega `viewer.css` e `viewer.js`.
   - Na inicialização, o JS consulta `GET /api/meta/whatsapp/viewer/config` para dados dinâmicos mínimos (ex.: logotipo em `data URI`).

2. **Endpoints consumidos pela UI**
   - Sessão
     - `POST /api/meta/whatsapp/viewer/session`
     - `DELETE /api/meta/whatsapp/viewer/session`
   - Conversas
     - `GET /api/meta/whatsapp/viewer/conversations`
     - `GET /api/meta/whatsapp/viewer/conversations/{phone}`
     - `GET /api/meta/whatsapp/viewer/conversations/{phone}/aggregates`
     - `GET /api/meta/whatsapp/viewer/conversations/{phone}/export`
   - Atualização em tempo real
     - `GET /api/meta/whatsapp/viewer/stream`
   - Ações manuais
     - `POST /api/meta/whatsapp/viewer/send-text`
     - `POST /api/meta/whatsapp/viewer/close-by-phone`

3. **Logs e rastreabilidade**
   - Os endpoints usam os serviços do módulo para buscar histórico e estado de interação.
  - O histórico persistente é recuperado pelo serviço de log (`IMetaWhatsAppPersistentLogService`) e agregado para montar timeline, leitura incremental e exportações.
  - A timeline detalhada usa cursor estável (`timestamp + id`) para paginação consistente mesmo com novos eventos chegando em paralelo.

## Login do Viewer (usuários, senhas e 401)

- O endpoint de login é `POST /api/meta/whatsapp/viewer/session`.
- O Viewer aceita autenticação de **duas formas**:
  1. `X-API-Key` (ou `Authorization: Bearer <token>`) usando:
     - `Security:ApiKey` (perfil `operator`)
     - `Security:ReadOnlyApiKey` (perfil `read_only`)
  2. `username + password` via configuração de identidade em:
     - `MetaWhatsAppViewer:Identity:Users` (array) **ou**
     - `MetaWhatsAppViewer:Identity:UsersJson` (json serializado)

### Onde criar usuários e senhas

Defina no `appsettings.*.json` (ou variáveis de ambiente equivalentes), por exemplo:

```json
{
  "MetaWhatsAppViewer": {
    "Identity": {
      "Users": [
        {
          "Username": "operador.ana",
          "Password": "SENHA_FORTE_AQUI",
          "Role": "operator",
          "CanViewSensitiveData": true
        },
        {
          "Username": "auditoria.leitura",
          "Password": "SENHA_LEITURA_AQUI",
          "Role": "read_only",
          "CanViewSensitiveData": false
        }
      ]
    }
  }
}
```

### Sintoma comum de 401

- `401` em `/api/meta/whatsapp/viewer/session`: credenciais inválidas ou não configuradas.
- `401` em endpoints de dados (`/stream`, `/conversations`, etc.): sessão expirada/ausente (cookie `viewer_session`).
- O endpoint `/api/meta/whatsapp/viewer/config` é público e não exige login.

## Limites operacionais recomendados (viewer/stream)

- **Frequência de polling**: manter ciclo base em ~4s por aba ativa (evitar <2s para não aumentar contenção de I/O e CPU no parse incremental).
- **Paginação do stream**: usar `page_size` entre **100 e 200 eventos** por request; teto suportado no backend é 500.
- **Draining incremental**: quando `has_more=true`, continuar o consumo em lotes curtos (ex.: até 3 páginas por ciclo no front) para evitar payloads gigantes.
- **Volume de logs por diretório**: manter os `.txt` ativos da pasta do viewer idealmente abaixo de algumas centenas (rotação diária/semanal recomendada).
- **Tamanho de arquivo**: para manter leitura incremental previsível, preferir rotação antes de ~50 MB por arquivo de log.
