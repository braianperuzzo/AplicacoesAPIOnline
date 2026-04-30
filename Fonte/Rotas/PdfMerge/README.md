# Rota PDF Merge

## Endpoint

- `POST /merge-pdf`

## Objetivo

Receber vários PDFs em base64, ordenar por `order`, mesclar em sequência e devolver um único PDF binário para envio por WhatsApp.

## Autenticação

A aplicação aceita:

- `Authorization: Bearer <token>`
- ou `X-API-Key: <token>`

O token deve ser o mesmo valor configurado em `Security:ApiKey`.

## Content-Type

- `application/json`

## Campos principais do body

- `request_id` (opcional)
- `customer` (objeto opcional)
- `output_file_name` (opcional)
- `ignore_invalid_files` (opcional, padrão `false`)
- `files` (obrigatório)

## Regras aplicadas

1. Ordenação sempre por `order` crescente.
2. Validação de base64 e assinatura `%PDF-`.
3. Falha com erro claro quando houver arquivo inválido e `ignore_invalid_files=false`.
4. Se `ignore_invalid_files=true`, arquivos inválidos são ignorados.
5. Resposta de sucesso retorna PDF binário (`application/pdf`).

## Resposta de erro

`400` ou `500` com JSON:

```json
{
  "error": true,
  "message": "descrição clara do erro",
  "request_id": "opcional"
}
```
