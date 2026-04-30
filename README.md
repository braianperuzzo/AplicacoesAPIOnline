# AplicacoesOnline

Plataforma de APIs voltada ao hospedamento de integrações online em um único host ASP.NET Core, com organização modular por domínio de rota, configuração isolada e contratos OpenAPI independentes por integração.

## Objetivo

O **AplicacoesOnline** centraliza integrações e serviços online da IBR em uma única API, mantendo separação clara entre cada integração hospedada. A arquitetura permite adicionar novas integrações sem impactar o restante da aplicação, mantendo isolamento de configuração, código e documentação.

## Escopo da aplicação

O repositório reúne a infraestrutura principal da API e as integrações hospedadas, incluindo:

* pipeline global de aplicação ASP.NET Core,
* endpoints públicos e operacionais,
* autenticação por chave de API,
* política de CORS para cenários de webhook,
* tratamento global de exceções,
* organização modular por rotas de integração,
* contratos OpenAPI específicos por domínio,
* estrutura de publicação centralizada para produção.

## Funcionalidades implantadas

### Endpoints globais

A aplicação expõe endpoints compartilhados utilizados para diagnóstico e operação:

* `GET /`: página HTML simples com identidade visual institucional,
* `GET /logo`: entrega do logotipo da aplicação,
* `GET /favicon.ico`: entrega do ícone da aplicação,
* `GET /health`: verificação rápida de disponibilidade da API,
* `GET /health/routes`: inventário das rotas registradas com seus métodos HTTP.

### Integração ChatGPTDocumentos

A rota `ChatGPTDocumentos` disponibiliza leitura controlada de arquivos locais para uso em integrações via Action do ChatGPT.

Capacidades disponíveis:

* validação das raízes de documentos configuradas,
* listagem de arquivos autorizados,
* leitura paginada por bytes,
* leitura textual paginada por caracteres,
* suporte à extração de texto de PDFs textuais.

Também existem controles para limitar payload de resposta, restringir leitura às pastas autorizadas em `Documents:Roots` e registrar logs operacionais com `TraceId`.

### Integração PiperunCrosseling

A rota `PiperunCrosseling` recebe webhooks de oportunidades provenientes do PipeRun, consulta as últimas compras do cliente e publica automaticamente uma nota na própria oportunidade via API do PipeRun.

Capacidades disponíveis:

* recebimento de payload JSON de oportunidades,
* extração de campos principais do webhook,
* consulta do histórico recente de compras no Plenus,
* publicação automática de nota na oportunidade do PipeRun,
* endpoint auxiliar para diagnóstico da rota.

A implementação inclui proteção contra concorrência de escrita, tratamento de payload vazio, validação de JSON e retorno com `traceId` para correlação com logs.

### Integração ShortLinks

A rota `ShortLinks` disponibiliza encurtamento e resolução de URLs com domínio público dedicado, sem dependência de encurtadores externos.

Capacidades disponíveis:

* criação de short link interno autenticado (`POST /api/short-links`),
* resolução pública e redirecionamento via `GET /r/{token}` e `GET /s/{token}`,
* armazenamento persistido em arquivo JSON (`StorageFilePath`) para manter tokens após restart,
* validação rígida da URL de destino (host obrigatório, sem user info, esquemas permitidos e tamanho máximo),
* suporte a expiração configurável e metadados por link.

### Integração PdfMerge

A rota `PdfMerge` permite receber múltiplos PDFs em base64, ordenar e devolver um único PDF consolidado.

Capacidades disponíveis:

* endpoint autenticado `POST /merge-pdf`,
* ordenação por `order` antes da mesclagem,
* validação de base64 e assinatura `%PDF-`,
* opção para ignorar arquivos inválidos (`ignore_invalid_files=true`) ou falhar com erro explícito,
* retorno binário (`application/pdf`) pronto para integrações como envio em WhatsApp.

### Integração MetaWhatsApp

A rota `MetaWhatsApp` concentra o webhook oficial da Meta, operações de interação e recursos operacionais para visualização e controle do fluxo.

Capacidades disponíveis:

* validação do webhook (`GET /webhooks/meta/whatsapp`),
* recebimento assinado de eventos da Meta (`POST /webhooks/meta/whatsapp`),
* registro e atualização do ciclo de vida de interações (abertura, envio, falha, recusa e fechamento),
* endpoints de apoio para viewer, preferências de fluxo e diagnóstico técnico.

### Segurança e governança

A aplicação possui controles centrais de acesso e exposição, incluindo:

* exigência de `Security:ApiKey` válida na inicialização,
* autenticação por header `X-API-Key`,
* definição de rotas públicas isentas de chave,
* política de CORS restritiva para webhooks autorizados.

### Observabilidade e robustez

A aplicação possui mecanismos globais para estabilidade e diagnóstico, incluindo:

* middleware de captura de exceções não tratadas,
* resposta padronizada de erro `500` contendo `traceId`,
* normalização de paths com remoção de barras duplicadas,
* logs estruturados contendo método, rota, IP remoto e identificador de rastreio.

## Arquitetura e organização

A solução foi projetada para manter separação clara entre infraestrutura global da API e integrações específicas. O ponto central da aplicação é o arquivo `Program.cs`, responsável por inicializar o host ASP.NET Core, registrar middlewares globais e habilitar controllers compartilhados e específicos de cada rota.

Cada integração é organizada dentro da estrutura `Rotas/<NomeDaRota>`, contendo controllers próprios, configuração dedicada e contratos OpenAPI independentes.

Essa abordagem permite evolução isolada de cada integração sem impactar outras rotas hospedadas no mesmo serviço.

## Stack e recursos técnicos

### Plataforma e framework

* .NET `net10.0`
* ASP.NET Core Web API
* modelo de hosting baseado em minimal hosting (`Program.cs`)
* publicação de endpoints via `MapControllers()`

### Documentação e contratos

* `Microsoft.AspNetCore.OpenApi` para definição e exposição de contratos OpenAPI por integração

### Processamento de documentos

* `UglyToad.PdfPig` para extração textual de PDFs na rota de documentos
* `PdfSharpCore` para mesclagem de PDFs na rota `PdfMerge`

### Publicação

* estrutura preparada para publicação self-contained
* artefatos organizados para implantação na pasta `Producao/`

### Publicação em IIS (recomendado)

Para reduzir impacto em outros sites do servidor, prefira reciclar/parar apenas o **App Pool** da API em vez de executar `iisreset` global.

Exemplo (PowerShell em modo Administrador):

```powershell
cd C:\AplicacoesOnline
Import-Module WebAdministration
# Descubra o nome real do App Pool, se necessário:
# Get-ChildItem IIS:\AppPools | Select-Object -ExpandProperty Name
Stop-WebAppPool -Name "AplicacoesOnline"
powershell -ExecutionPolicy Bypass -File .\scripts\publicar-producao.ps1 -IisAppPoolName "AplicacoesOnline"
Start-WebAppPool -Name "AplicacoesOnline"
```

Use `iisreset` apenas quando for realmente necessário reiniciar todo o IIS do servidor.

### Diagnóstico rápido de erro 500 no IIS

Se o `500` persistir após publicar, execute:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\diagnostico-iis.ps1 -SitePath "C:\AplicacoesOnline\Producao" -AppPoolName "AplicacoesOnline"
```

O script valida existência do App Pool, acessibilidade/estrutura XML do `web.config`, referência ao `AspNetCoreModuleV2`, presença do módulo global no IIS, existência de `Security:ApiKey` (env/INI), ACL NTFS e eventos recentes do Application log relacionados ao IIS (incluindo detalhe de ANCM IDs 1007/1018).

#### Como executar salvando log em arquivo

Por padrão, o output aparece no console da sessão PowerShell. Para guardar tudo em arquivo no servidor:

```powershell
cd C:\AplicacoesOnline
powershell -ExecutionPolicy Bypass -File .\scripts\diagnostico-iis.ps1 -SitePath "C:\AplicacoesOnline\Producao" -AppPoolName "AplicacoesOnline" *> .\logs\diagnostico-iis.log
```

Se a pasta `logs` ainda não existir:

```powershell
New-Item -ItemType Directory -Path .\logs -Force | Out-Null
```

Depois visualize o log com:

```powershell
Get-Content .\logs\diagnostico-iis.log -Tail 200
```

#### Onde ver os eventos que o script consulta

Além do arquivo de log acima, os eventos analisados também ficam no Windows Event Viewer:

* `Event Viewer` → `Windows Logs` → `Application`
* Provedores principais:
  * `IIS AspNetCore Module V2` (ANCM, incluindo IDs `1007` e `1018`)
  * `.NET Runtime`

## Arquitetura de configuração

A aplicação utiliza configuração em camadas para permitir reaproveitamento da base comum sem comprometer o isolamento das integrações.

Em alto nível, a configuração segue esta ordem:

1. configuração global via `Configuracoes/ChaveAPI.ini`,
2. variáveis de ambiente (prioridade prática para segredos),
3. configuração específica da integração em `Rotas/<Rota>/Config/appsettings*.json`.

Esse modelo permite que cada integração evolua com suas próprias definições sem interferir na configuração global da API.

## Estrutura de diretórios

A organização principal do projeto é a seguinte:

```
Fonte/                                  Código-fonte principal da aplicação
Fonte/Program.cs                        Bootstrap e middlewares globais
Fonte/Controllers/                      Endpoints globais
Fonte/Rotas/<Rota>/Controllers/         Endpoints específicos da integração
Fonte/Rotas/<Rota>/Config/              Configuração exclusiva da integração
Fonte/Rotas/<Rota>/OpenApi/             Contrato OpenAPI da integração

Producao/                               Artefatos de publicação
Producao/appsettings*.json              Configuração de runtime
Producao/Configuracoes/                 Configurações operacionais
Producao/Rotas/                         Configurações e contratos por integração
Producao/web.config                     Regras de hospedagem e endurecimento IIS
```

## Controle de acesso

### API Key obrigatória

A aplicação exige uma chave válida configurada em:

```
Security:ApiKey
```

Caso a chave não esteja definida, a inicialização da API é bloqueada com registro de log crítico.

### Header de autenticação

Requisições privadas devem enviar o header:

```
X-API-Key: <chave>
```

### Rotas públicas

As seguintes rotas podem ser acessadas sem autenticação:

* `/`
* `/health`
* `/logo`
* `/favicon.ico`
* `/openapi/*`

Também é permitida sonda pública com `GET`, `HEAD` e `OPTIONS` para:

* `/api/piperun-crosseling/webhook/oportunidade`

### Política de CORS

A política `WebhookCors` permite requisições apenas de domínios HTTPS associados a:

* `pipe.run`
* `piperun.com.br`

Incluindo subdomínios, com permissão para os métodos:

* `GET`
* `POST`
* `OPTIONS`

## Endpoints principais

### Endpoints globais

```
GET /
GET /logo
GET /favicon.ico
GET /health
GET /health/routes
```

### Rota ChatGPTDocumentos

```
GET /api/documents/status
GET /api/documents/files
GET /api/documents/search
GET /api/documents/file-content
GET /api/documents/file-text
```

### Rota PiperunCrosseling

```
GET  /api/piperun-crosseling/webhook/oportunidade
POST /api/piperun-crosseling/webhook/oportunidade
```

### Rota ShortLinks

```
POST /api/short-links
GET  /r/{token}
GET  /s/{token}
```

### Rota PdfMerge

```
POST /merge-pdf
```

### Webhook Meta WhatsApp

```
GET  /webhooks/meta/whatsapp
POST /webhooks/meta/whatsapp
POST /webhooks/meta/whatsapp/interactions
POST /api/meta/whatsapp/interactions/register
GET  /whatsapp/viewer
GET  /api/meta/whatsapp/viewer/conversations
GET  /api/meta/whatsapp/viewer/conversations/{phone}
POST /api/meta/whatsapp/viewer/send-text
POST /api/meta/whatsapp/viewer/close-by-phone
POST /api/meta/whatsapp/flows/preferences/submit
```

Regras de autenticação:

* `GET /webhooks/meta/whatsapp` usa o fluxo oficial de verificação da Meta (`hub.verify_token` + `hub.challenge`).
* `POST /webhooks/meta/whatsapp` **não usa** `X-API-Key` nem `Authorization` da API global.
* O `POST` exige assinatura da Meta no header `X-Hub-Signature-256` (`sha256=<hmac_hex>`), validada com `MetaWhatsAppWebhook:AppSecret`.

Regras da rota `ShortLinks`:

* `POST /api/short-links` exige autenticação por `X-API-Key` ou `Authorization: Bearer`.
* `GET /r/{token}` e `GET /s/{token}` são públicos para consumo externo.

Configuração mínima (sem variáveis de ambiente):

1. Defina `MetaWhatsAppWebhook:VerifyToken` em `Fonte/Rotas/MetaWhatsApp/Config/appsettings.json` (usado no `GET` de verificação).
2. Defina `MetaWhatsAppWebhook:AppSecret` em `Fonte/Rotas/MetaWhatsApp/Config/appsettings.json` (usado no `POST` para validar HMAC).
3. Defina a autenticação dos webhooks internos encaminhados ao n8n em `Fonte/Rotas/MetaWhatsApp/Config/appsettings.json` (seção `N8nWebhookSecurity`, recomendado `HeaderAuth` com `X-API-Key`).

> Os valores sensíveis devem vir de variáveis de ambiente no deploy; mantenha placeholders no repositório.

## Implantação e operação

### Modelo de publicação

O fluxo de publicação gera artefatos na pasta `Producao/`, incluindo runtime e arquivos necessários para execução no servidor. Esse modelo permite implantação sem necessidade de instalar o runtime .NET previamente.

### Itens de produção

A pasta `Producao/` inclui:

* binários publicados da aplicação,
* arquivos `appsettings*.json`,
* configurações operacionais,
* rotas com suas respectivas configurações e contratos,
* `web.config` com regras de hospedagem para IIS.

Entre os controles de produção implementados estão:

* redirecionamento automático HTTP → HTTPS,
* proteção de pastas sensíveis,
* organização dos artefatos de runtime e implantação.

## Configuração recomendada

Para ambientes de produção, recomenda-se definir a chave de API por variável de ambiente:

```
Security__ApiKey
```

Boas práticas adicionais:

* não manter chaves reais no repositório,
* utilizar variáveis de ambiente para segredos,
* evitar credenciais em arquivos de configuração sempre que possível.

## Checklist operacional

Antes de publicar ou validar a aplicação em produção, recomenda-se verificar:

1. `Security__ApiKey` configurada corretamente,
2. `Documents:Roots` definido para a rota de documentos,
3. HTTPS ativo no host público,
4. resposta correta em `/health`,
5. listagem correta em `/health/routes`,
6. bloqueio com `401` em chamadas sem chave,
7. sucesso nas chamadas autenticadas,
8. presença de `traceId` em cenários de erro.

## Boas práticas de evolução

Para facilitar a expansão da solução, este README pode evoluir com os seguintes complementos:

* diagrama do pipeline HTTP da aplicação,
* fluxo de autenticação e autorização por rota,
* padrão para criação de novas integrações em `Rotas/<NovaRota>`,
* versionamento de contratos OpenAPI por integração,
* testes automatizados de integração,
* monitoramento com métricas e alertas.

## Observações finais

Este repositório funciona como host central de integrações online da IBR. Alterações em middlewares globais, autenticação, configuração base ou pipeline de publicação podem impactar simultaneamente todas as rotas hospedadas. Mudanças estruturais devem sempre considerar efeitos sobre segurança, compatibilidade entre integrações e operação em produção.
