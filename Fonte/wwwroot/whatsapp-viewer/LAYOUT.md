# Layout macro — WhatsApp Viewer

## Objetivo
Padronizar a tela em **áreas fixas** para operação diária:

1. **Cabeçalho operacional**: estado de conexão, ações de sessão e não lidas.
2. **Lista de conversas**: filtros + prioridade + fila.
3. **Painel da conversa/timeline**: leitura de contexto e auditoria cronológica.
4. **Painel de detalhes**: KPIs de conversa, composer manual e depuração.

## Grid principal

- Container: `.wrapper`
- Desktop amplo (>= 1440px):
  - `grid-template-columns: minmax(320px, 420px) minmax(540px, 1fr) minmax(320px, 420px)`
  - Áreas: `conversations | timeline | details`
- Desktop padrão / notebook (1100px–1439px):
  - 2 colunas + 2 linhas
  - Coluna 1 recebe `conversations` e `details` em pilha
  - Coluna 2 mantém `timeline` fixa ocupando as 2 linhas
- Tablet e mobile (< 1100px):
  - 1 coluna em ordem operacional: `conversations -> details -> timeline`

## Breakpoints e comportamento responsivo

- `@media (max-width: 1439px)`
  - Reduz a área lateral para 300–360px.
  - Mantém timeline como foco principal contínuo.
- `@media (max-width: 1099px)`
  - Pilha vertical única.
  - Remove bordas laterais e normaliza em separadores horizontais.
  - Limita timeline para `50vh` para manter composer acessível.
- `@media (max-width: 767px)`
  - Header quebra em coluna.
  - Linhas de busca e ações viram blocos verticais.

## Regras de largura por coluna

- **Conversas** (`.panel-conversations`)
  - Min: `320px` (ou `300px` em <= 1439px)
  - Max: `420px`
- **Timeline** (`.panel-timeline`)
  - Min desktop amplo: `540px`
  - Em <= 1439px: flexível `min-width: 0`
- **Detalhes** (`.panel-details`)
  - Min: `320px` (ou `300px` em <= 1439px)
  - Max: `420px`

## Alinhamentos e hierarquia de ação

1. **Status críticos primeiro**
   - Ordem dos cards KPI prioriza: taxa de falha, SLA em risco, backlog.
2. **Conversas urgentes em seguida**
   - Lista mantém filtros e quick-sort críticos próximos do topo.
3. **Composer em posição de ação contínua**
   - Composer fica no painel de detalhes, acima da depuração.

## Cenários de validação da arquitetura

### 1) Triagem operacional
- Aplicar filtro rápido **Críticas**.
- Confirmar visualização simultânea de lista e timeline em desktop.
- Verificar que SLA/falha aparecem antes de métricas secundárias.

### 2) Resposta manual
- Selecionar conversa urgente.
- Escrever no composer, inserir snippet e enviar sem perder a timeline de vista.

### 3) Auditoria de envio
- Abrir aba de envios na timeline.
- Exportar CSV/JSON e cruzar com status chips da conversa.

### 4) Investigação de falha
- Filtrar por `ERRO_ENVIO`.
- Abrir payload no painel de depuração.
- Confirmar rastreio de evento sem trocar de contexto de tela.

## Validação com operadores (velocidade + confiança)

Para validar se os ajustes de bolha/status/cabeçalho melhoraram a operação:

1. **Teste A/B rápido (30 min por operador)**  
   - 15 min com layout antigo (baseline registrado anteriormente).  
   - 15 min com layout atual.
2. **Métricas objetivas**  
   - Tempo para identificar status final de uma mensagem enviada.  
   - Tempo para identificar próxima conversa prioritária na fila.  
   - Número de cliques para chegar ao `interaction_id` durante auditoria.
3. **Métricas subjetivas (escala 1-5)**  
   - “Consigo confiar no estado de entrega sem abrir o payload técnico”.  
   - “Consigo entender rapidamente em qual contexto de contato estou”.
4. **Critério de aceite operacional**  
   - Ganho de pelo menos **15%** no tempo médio de triagem.  
   - Nota média de confiança **>= 4**.
