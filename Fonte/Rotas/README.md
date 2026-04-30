# Padrão de organização das rotas (Fonte)

Esta pasta concentra APIs específicas por software.

## Estrutura obrigatória por rota

```text
Fonte/Rotas/<NomeDaRota>/
├─ README.md
├─ Controllers/
├─ Config/
└─ OpenApi/
```

## Responsabilidades

- `README.md`: documentação funcional e operacional da rota.
- `Controllers/`: endpoints exclusivos da rota.
- `Config/`: parâmetros exclusivos da rota (não compartilhados).
- `OpenApi/`: contratos OpenAPI da rota.

## Regras para evitar sobreposição

1. Não reutilizar nome de rota.
2. Não colocar controllers de rota em `Fonte/Controllers/`.
3. Não colocar config de rota em `Fonte/appsettings*.json`.
4. Não compartilhar OpenAPI entre rotas com domínios diferentes.
5. Toda rota nova deve ter README próprio.
