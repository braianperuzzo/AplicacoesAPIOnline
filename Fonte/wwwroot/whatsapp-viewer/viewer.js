let selectedPhone = null;
let lastEventFingerprints = [];
let listRefreshHandle = null;
let timelineEvents = [];
let pollingFallbackHandle = null;
let viewerStreamCursor = null;
let viewerLastEventId = 0;
let streamEventSource = null;
let streamReconnectHandle = null;
let streamReconnectAttempt = 0;
let pollingFallbackInFlight = false;
let pollingFallbackBackoffMs = 0;
let appendQueue = [];
let appendInProgress = false;
let activeTimelineGroup = 'all';
let nextHistoryCursor = null;
const conversationPageSize = 80;
const streamPageSize = 120;
const streamMaxPagesPerPoll = 3;
const pollingBaseIntervalMs = 4000;
const pollingErrorInitialBackoffMs = 2000;
const pollingErrorMaxBackoffMs = 30000;
const sseInitialBackoffMs = 1000;
const sseMaxBackoffMs = 20000;
const conversationsRefreshIntervalMs = 45000;
const filterDebounceMs = 450;
let unreadByPhone = new Map();
let lastConversations = [];
const presetStoragePrefix = 'whatsappViewerPreset:';
const savedQueryStoragePrefix = 'whatsappViewerSavedQuery:';
const profileOverrideStoragePrefix = 'whatsappViewerProfileOverride:';
const pinStoragePrefix = 'whatsappViewerPinned:';
const muteStoragePrefix = 'whatsappViewerMuted:';
let metricsWindowMinutes = 60;
const presetPayloadVersion = 1;
let conversationSort = { field: 'last_event_at', direction: 'desc' };
let quickSortMode = 'default';
let viewerMode = 'compact';
let filtersDebounceHandle = null;
let conversationPaginationByFilter = new Map();
let activeConversationFilterKey = '';
let loadingMoreConversations = false;
let conversationFocusIndex = -1;
let viewerRole = 'operator';
let closeReasonCatalog = [];
let filteredTimelineEvents = [];
let timelineSearchMatches = [];
let timelineActiveMatchIndex = -1;
let timelineSearchServerCursor = null;
const conversationRecentCache = new Map();
const conversationRecentCacheMaxEntries = 6;
const timelinePrefetchByPhone = new Map();
const timelineCacheTtlMs = 60000;
const timelineVirtualConfig = {
  rowHeight: 120,
  overscan: 8
};
let timelineVirtualRenderSignature = '';
let selectedTimelineFingerprint = '';
const timelineSearchServerPageSize = 200;
const timelineServerSearchThreshold = 1000;
const timelineSearchFilters = {
  text: '',
  status: '',
  interactionId: '',
  from: '',
  to: '',
  useServer: false
};

const manualDraftStoragePrefix = 'whatsappViewerDraft:';
const manualMaxChars = 1000;
const quickSnippets = [
  'Olá! Recebemos sua mensagem e vamos seguir com o atendimento agora.',
  'Perfeito, obrigado pelo retorno. Vou validar internamente e já te respondo.',
  'Consegue me confirmar CPF/CNPJ para avançarmos com segurança?',
  'Para continuidade, preciso da confirmação dos dados obrigatórios.',
  'Registro concluído. Se precisar, estou à disposição.'
];
const outsidePolicyKeywords = ['senha', 'token', 'cartão', 'cartao', 'cpf', 'cnpj', 'pix'];
const kpiFilterConfig = {
  tma: { status: 'AGUARDANDO_RESPOSTA', onlySlaBreached: false, hasActiveInteraction: true, quickSortMode: 'oldest_waiting' },
  first_response: { status: 'AGUARDANDO_RESPOSTA', onlySlaBreached: false, hasActiveInteraction: true, quickSortMode: 'oldest_waiting' },
  send_failure_rate: { status: 'ERRO_ENVIO', onlySlaBreached: false, hasActiveInteraction: false, quickSortMode: 'recent_failure' },
  backlog: { status: 'AGUARDANDO_RESPOSTA', onlySlaBreached: false, hasActiveInteraction: true, quickSortMode: 'critical' },
  sla_risk: { status: 'AGUARDANDO_RESPOSTA', onlySlaBreached: false, hasActiveInteraction: true, quickSortMode: 'oldest_waiting', localFilter: 'sla_risk' }
};
let latestMetricsSnapshot = null;
let activeLocalConversationFilter = '';
const slaRiskThresholdMinutes = 10;

function presetStorageKey(operator, user) {
  return `${presetStoragePrefix}${operator}:${user || 'session'}`;
}

function profileOverrideKey(phone) {
  return `${profileOverrideStoragePrefix}${String(phone || '').trim()}`;
}

function getProfileOverride(phone) {
  const key = String(phone || '').trim();
  if (!key) return null;
  try {
    const raw = localStorage.getItem(profileOverrideKey(key));
    if (!raw) return null;
    const parsed = JSON.parse(raw);
    if (!parsed || typeof parsed !== 'object') return null;
    return {
      nome: firstNonEmpty(parsed.nome) || '',
      empresa: firstNonEmpty(parsed.empresa) || '',
      numero: firstNonEmpty(parsed.numero) || ''
    };
  } catch {
    return null;
  }
}

function saveProfileOverride(phone, payload) {
  const key = String(phone || '').trim();
  if (!key) return;
  localStorage.setItem(profileOverrideKey(key), JSON.stringify({
    nome: firstNonEmpty(payload?.nome) || '',
    empresa: firstNonEmpty(payload?.empresa) || '',
    numero: firstNonEmpty(payload?.numero) || ''
  }));
}

function clearProfileOverride(phone) {
  const key = String(phone || '').trim();
  if (!key) return;
  localStorage.removeItem(profileOverrideKey(key));
}

function escapeHtml(value) {
  return String(value || '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

function escapeRegExp(value) {
  return String(value || '').replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function highlightText(value, term) {
  const text = fixMojibake(String(value || ''));
  const needle = String(term || '').trim();
  if (!needle) return escapeHtml(text);
  const matcher = new RegExp(`(${escapeRegExp(needle)})`, 'ig');
  return escapeHtml(text).replace(matcher, '<mark class="timeline-hit">$1</mark>');
}

function parseDateInput(value) {
  if (!value) return null;
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return null;
  return parsed.getTime();
}

function fixMojibake(value) {
  const text = String(value || '');
  if (!text || !/[ÃÂ�]/.test(text)) return text;
  try {
    const bytes = Uint8Array.from(text, ch => ch.charCodeAt(0) & 0xFF);
    const decoded = new TextDecoder('utf-8', { fatal: false }).decode(bytes);
    const replacementChars = (decoded.match(/�/g) || []).length;
    return replacementChars <= 2 ? decoded : text;
  } catch {
    return text;
  }
}

function formatDatePtBr(value) {
  if (!value) return '-';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return escapeHtml(String(value));
  const dd = String(date.getDate()).padStart(2, '0');
  const mm = String(date.getMonth() + 1).padStart(2, '0');
  const yyyy = String(date.getFullYear());
  const hh = String(date.getHours()).padStart(2, '0');
  const min = String(date.getMinutes()).padStart(2, '0');
  const ss = String(date.getSeconds()).padStart(2, '0');
  return `${dd}/${mm}/${yyyy} ${hh}:${min}:${ss}`;
}

function formatConversationTime(value) {
  if (!value) return '-';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '-';
  const now = new Date();
  if (date.toDateString() === now.toDateString()) {
    return date.toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit' });
  }
  const yesterday = new Date(now);
  yesterday.setDate(now.getDate() - 1);
  if (date.toDateString() === yesterday.toDateString()) return 'Ontem';
  const diffDays = Math.floor((now.getTime() - date.getTime()) / (24 * 60 * 60 * 1000));
  if (diffDays < 7) {
    return date.toLocaleDateString('pt-BR', { weekday: 'short' });
  }
  return date.toLocaleDateString('pt-BR', { day: '2-digit', month: '2-digit' });
}

function initialsFromName(value) {
  const text = firstNonEmpty(value).trim();
  if (!text) return '??';
  const parts = text.split(/\s+/).filter(Boolean);
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
  return `${parts[0][0] || ''}${parts[1][0] || ''}`.toUpperCase();
}

function mapBackendDeliveryStatus(value) {
  const normalized = String(value || '').trim().toLowerCase();
  if (!normalized) return { code: 'pending', label: 'Pendente', tone: 'warning', icon: '🕒' };
  const matchers = [
    { code: 'failed', label: 'Falha', tone: 'error', icon: '⚠', keys: ['falh', 'erro', 'error', 'failed', 'rejected', 'invalid', 'expir', 'cancel', 'undeliver'] },
    { code: 'read', label: 'Lido', tone: 'success', icon: '✓✓', keys: ['lido', 'read', 'visualiz', 'seen'] },
    { code: 'delivered', label: 'Entregue', tone: 'success', icon: '✓✓', keys: ['entreg', 'delivered', 'received'] },
    { code: 'sent', label: 'Enviado', tone: 'success', icon: '✓', keys: ['enviado', 'sent', 'accepted', 'aceito', 'dispatch'] },
    { code: 'pending', label: 'Pendente', tone: 'warning', icon: '🕒', keys: ['pendente', 'pending', 'queued', 'process', 'aguard'] }
  ];
  const mapped = matchers.find(item => item.keys.some(key => normalized.includes(key)));
  if (mapped) return mapped;
  return { code: 'pending', label: toNaturalLabel(value), tone: 'warning', icon: '🕒' };
}

function buildStatusTransitionTooltip(evt, status) {
  const fields = evt && evt.fields ? evt.fields : {};
  const pick = (...keys) => firstNonEmpty(...keys.map(key => fields[key]));
  const transitions = [];
  const transitionConfig = [
    { label: 'Pendente', keys: ['pending_at', 'queued_at', 'created_at', 'scheduled_at'] },
    { label: 'Enviado', keys: ['sent_at', 'dispatch_at', 'submitted_at', 'accepted_at'] },
    { label: 'Entregue', keys: ['delivered_at', 'delivery_at'] },
    { label: 'Lido', keys: ['read_at', 'seen_at', 'opened_at'] },
    { label: 'Falha', keys: ['failed_at', 'error_at'] }
  ];
  transitionConfig.forEach(item => {
    const value = pick(...item.keys);
    if (value) transitions.push(`${item.label}: ${formatDatePtBr(value)}`);
  });
  if (transitions.length === 0 && evt?.timestamp) {
    transitions.push(`Última atualização: ${formatDatePtBr(evt.timestamp)}`);
  }
  const failureReason = pick('error_message', 'error_details', 'error', 'erro', 'failure_reason', 'status_error_message', 'status_error_code');
  if (status.code === 'failed' && failureReason) {
    transitions.push(`Causa: ${failureReason}`);
  }
  return transitions.join('\n');
}

function conversationAlertFlags(item, finalStatus, localUnread) {
  const alerts = [];
  if ((localUnread?.inbound || 0) > 0 || item.isUnread) {
    alerts.push({ code: 'new_inbound', label: 'Nova resposta', tone: 'success' });
  }
  if ((item.waitingMinutes || 0) >= 15) {
    alerts.push({ code: 'waiting', label: `Sem resposta ${item.waitingMinutes}m`, tone: 'warning' });
  }
  if (item.isSlaBreached) {
    alerts.push({ code: 'sla', label: 'SLA estourado', tone: 'error' });
  }
  if (finalStatus?.tone === 'error') {
    alerts.push({ code: 'delivery_error', label: 'Falha de envio', tone: 'error' });
  }
  return alerts;
}

function pinKey(phone) {
  return `${pinStoragePrefix}${String(phone || '').trim()}`;
}

function muteKey(phone) {
  return `${muteStoragePrefix}${String(phone || '').trim()}`;
}

function isPinnedConversation(phone) {
  return localStorage.getItem(pinKey(phone)) === '1';
}

function isMutedConversation(phone) {
  return localStorage.getItem(muteKey(phone)) === '1';
}

function togglePinnedConversation(phone) {
  if (isPinnedConversation(phone)) localStorage.removeItem(pinKey(phone));
  else localStorage.setItem(pinKey(phone), '1');
}

function toggleMutedConversation(phone) {
  if (isMutedConversation(phone)) localStorage.removeItem(muteKey(phone));
  else localStorage.setItem(muteKey(phone), '1');
}

function traduzirCategoria(value) {
  const normalized = String(value || '').trim().toLowerCase();
  const mapa = {
    system: 'sistema',
    sent: 'enviado',
    customer: 'cliente',
    meta: 'meta',
    error: 'erro',
    outbound: 'saída',
    inbound: 'entrada'
  };
  return mapa[normalized] || normalized || 'sistema';
}

function classifyEventGroup(evt) {
  if (isInternalAction(evt)) return 'internal';
  if (isOutbound(evt)) return 'outbound';
  return 'inbound';
}

function typeLabelByGroup(group) {
  if (group === 'internal') return 'Interno';
  if (group === 'outbound') return 'Outbound';
  return 'Inbound';
}

function stableSortTimeline(events) {
  return (events || [])
    .map((evt, index) => ({ evt, index }))
    .sort((a, b) => {
      const ta = new Date(a.evt?.timestamp || 0).getTime();
      const tb = new Date(b.evt?.timestamp || 0).getTime();
      if (ta !== tb) return ta - tb;
      return a.index - b.index;
    })
    .map(item => item.evt);
}

function normalizeEventPayload(evt) {
  if (!evt || typeof evt !== 'object') return null;
  const fields = evt.fields && typeof evt.fields === 'object' ? evt.fields : {};
  const eventName = firstNonEmpty(evt.eventName, evt.event_type, fields.event_type, evt.status);
  const direction = firstNonEmpty(evt.direction, fields.direction, fields.direcao, fields.message_direction) || 'system';
  const status = firstNonEmpty(evt.status, fields.status, fields.status_final, fields.final_status, eventName);
  const author = firstNonEmpty(evt.author, fields.author);
  const timestamp = firstNonEmpty(evt.timestamp);
  const messageId = firstNonEmpty(evt.message_id, evt.metaMessageId, evt.meta_message_id, fields.meta_message_id, fields.wamid);
  const correlationId = firstNonEmpty(evt.correlation_id, evt.interactionId, evt.interaction_id, fields.interaction_id, fields.canonical_correlation_key);
  return {
    ...evt,
    eventName,
    event_type: eventName,
    direction,
    status,
    author,
    timestamp,
    message_id: messageId,
    correlation_id: correlationId,
    interactionId: correlationId,
    metaMessageId: messageId,
    fields
  };
}

function normalizeEventsPayload(events) {
  return stableSortTimeline((events || []).map(normalizeEventPayload).filter(Boolean));
}

function inferFinalStatus(value) {
  return mapBackendDeliveryStatus(value);
}

function eventFinalStatus(evt) {
  const fields = evt && evt.fields ? evt.fields : {};
  const raw = firstNonEmpty(
    fields.status_final,
    fields.final_status,
    fields.delivery_status,
    fields.interaction_status,
    fields.status,
    fields.estado,
    evt.status,
    evt.eventStatus
  );
  const mapped = raw ? inferFinalStatus(raw) : (isOutbound(evt) ? inferFinalStatus('pending') : inferFinalStatus('sent'));
  return {
    ...mapped,
    tooltip: buildStatusTransitionTooltip(evt, mapped)
  };
}

function normalizedFieldMap(evt) {
  const map = {};
  const fields = evt && evt.fields && typeof evt.fields === 'object' ? evt.fields : {};
  Object.entries(fields).forEach(([key, value]) => {
    map[String(key || '').toLowerCase()] = value;
  });
  return map;
}

function firstFieldValue(evt, ...keys) {
  const fields = normalizedFieldMap(evt);
  for (const key of keys) {
    const value = fields[String(key || '').toLowerCase()];
    const normalized = firstNonEmpty(value);
    if (normalized) return normalized;
  }
  return '';
}

function summarizePayload(evt) {
  return {
    event: firstNonEmpty(evt?.eventName, '-'),
    direcao: firstNonEmpty(evt?.direction, '-'),
    status: firstNonEmpty(evt?.status, '-'),
    autor: firstNonEmpty(evt?.author, '-'),
    timestamp: formatDatePtBr(evt?.timestamp)
  };
}

function trackingIds(evt) {
  return {
    interaction_id: firstNonEmpty(evt?.interactionId, evt?.correlation_id, firstFieldValue(evt, 'interaction_id', 'canonical_correlation_key')),
    message_id: firstNonEmpty(evt?.message_id, evt?.metaMessageId, firstFieldValue(evt, 'meta_message_id', 'wamid', 'message_id')),
    trace_id: firstFieldValue(evt, 'trace_id', 'traceid'),
    execution_id: firstFieldValue(evt, 'execution_id'),
    provider_message_id: firstFieldValue(evt, 'provider_message_id', 'context_message_id', 'id_mensagem')
  };
}

function parseNumber(...values) {
  for (const value of values) {
    const n = Number(value);
    if (!Number.isNaN(n) && Number.isFinite(n)) return n;
  }
  return null;
}

function extractDiagnostics(evt) {
  const latencyMs = parseNumber(
    firstFieldValue(evt, 'latency_ms', 'provider_latency_ms', 'elapsed_ms', 'duration_ms', 'latencia_ms')
  );
  const attempts = parseNumber(
    firstFieldValue(evt, 'attempt', 'attempts', 'retry_count', 'send_attempt', 'tentativas', 'tentativa')
  );
  return {
    latencyLabel: latencyMs === null ? '-' : `${Math.round(latencyMs)} ms`,
    attemptsLabel: attempts === null ? '-' : String(Math.round(attempts))
  };
}

function redactSensitiveData(value, keyHint = '') {
  if (value === null || value === undefined) return value;
  if (Array.isArray(value)) return value.map(item => redactSensitiveData(item, keyHint));
  if (typeof value === 'object') {
    const output = {};
    Object.entries(value).forEach(([key, nested]) => {
      const normalizedKey = String(key || '').toLowerCase();
      if (/(token|secret|password|senha|authorization|api[-_]?key)/i.test(normalizedKey)) {
        output[key] = '[redacted]';
      } else {
        output[key] = redactSensitiveData(nested, normalizedKey);
      }
    });
    return output;
  }
  const text = String(value);
  if (/(phone|telefone|wa_id|document|cpf|cnpj|email)/i.test(keyHint)) {
    return '[redacted]';
  }
  return text
    .replace(/Bearer\s+[A-Za-z0-9\-._~+/]+=*/gi, 'Bearer [redacted]')
    .replace(/[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}/gi, '[redacted-email]')
    .replace(/\+?\d{10,15}/g, '[redacted-phone]');
}

function sanitizedProviderResponse(evt) {
  const raw = firstNonEmpty(
    firstFieldValue(evt, 'provider_response', 'response_body', 'response_json', 'resposta_provedor', 'resposta_raw'),
    evt?.mainText
  );
  if (!raw) return '{}';
  const parsed = tryParseJson(raw);
  if (parsed) {
    return JSON.stringify(redactSensitiveData(parsed), null, 2);
  }
  return JSON.stringify({ texto: redactSensitiveData(raw) }, null, 2);
}

function buildStatusHistory(evt) {
  const fields = normalizedFieldMap(evt);
  const transitions = [];
  const add = (label, ...keys) => {
    for (const key of keys) {
      const value = firstNonEmpty(fields[key]);
      if (value) {
        transitions.push({ label, timestamp: formatDatePtBr(value) });
        return;
      }
    }
  };
  add('Pendente', 'pending_at', 'queued_at', 'created_at', 'scheduled_at');
  add('Enviado', 'sent_at', 'dispatch_at', 'submitted_at', 'accepted_at');
  add('Entregue', 'delivered_at', 'delivery_at');
  add('Lido', 'read_at', 'seen_at', 'opened_at');
  add('Falha', 'failed_at', 'error_at');
  if (transitions.length === 0) {
    transitions.push({ label: firstNonEmpty(evt?.status, evt?.eventName, 'Evento'), timestamp: formatDatePtBr(evt?.timestamp) });
  }
  return transitions;
}

function conversationGroup(item) {
  const source = firstNonEmpty(item?.lastEventName, item?.status, item?.lastDirection).toLowerCase();
  if (source.includes('interacao') || source.includes('internal') || source.includes('intern')) return 'internal';
  if (source.includes('outbound') || source.includes('envi')) return 'outbound';
  if (source.includes('inbound') || source.includes('receb') || source.includes('respon')) return 'inbound';
  return 'inbound';
}

function conversationFinalStatus(item) {
  const rawStatus = firstNonEmpty(item?.statusFinal, item?.finalStatus, item?.status, item?.lastEventName);
  return inferFinalStatus(rawStatus || 'sem status');
}

function renderTimelineEvent(evt) {
  const technical = isTechnicalOnlyEvent(evt);
  const isError = isErrorEvent(evt);
  const summaryText = firstNonEmpty(
    extractConversationText(evt),
    technical ? buildTechnicalCompactSummary(evt) : cleanLogLikeText(enrichTechnicalText(evt)),
    formatEventNameNatural(evt)
  );
  const text = highlightText(summaryText, timelineSearchFilters.text);
  const eventName = formatEventNameNatural(evt);
  const group = classifyEventGroup(evt);
  const directionClass = isError
    ? 'event-error'
    : group === 'outbound'
    ? 'message-outbound'
    : group === 'internal'
      ? 'message-internal'
      : 'message-inbound';
  const status = eventFinalStatus(evt);
  const statusTooltip = escapeHtml(status.tooltip || status.label || '');
  const contentText = firstNonEmpty(extractConversationText(evt));
  const canResend = status.code === 'failed' && !!contentText;
  const resendPayload = encodeURIComponent(contentText);
  const statusLabel = escapeHtml(status.label || 'Atualização');
  const technicalPayload = escapeHtml(JSON.stringify({
    event: eventName || evt.status || '-',
    timestamp: evt.timestamp || '-',
    interaction_id: firstNonEmpty(evt.correlation_id, evt.interactionId, evt.fields?.interaction_id),
    message_id: firstNonEmpty(evt.message_id, evt.metaMessageId, evt.fields?.meta_message_id),
    category: evt.category || '-',
    direction: evt.direction || '-'
  }, null, 2));
  const compactTimestamp = escapeHtml(formatEventClock(evt.timestamp));
  const eventFingerprint = fingerprint(evt);
  const selectedClass = selectedTimelineFingerprint === eventFingerprint ? 'event-selected' : '';
  return `<div class="event message-only ${directionClass} ${technical ? 'event-technical' : ''} timeline-match ${selectedClass}" data-match-id="${escapeHtml(eventFingerprint)}" data-event-fingerprint="${escapeHtml(eventFingerprint)}">
            <div class="text">${text}</div>
            <div class="event-meta-line">
              <span class="event-time">${compactTimestamp}</span>
              ${renderDeliveryIcon(status, statusTooltip)}
              ${technical ? `<span class="event-tech-min">${statusLabel} • ${escapeHtml(formatDatePtBr(evt.timestamp))}</span>` : ''}
            </div>
            <div class="event-note">
              <span class="group-chip ${group}">${typeLabelByGroup(group)}</span>
              <span class="status-chip ${status.tone}" title="${statusTooltip}">${escapeHtml(status.label)}</span>
              <span>${escapeHtml(eventName)} • ${escapeHtml(formatDatePtBr(evt.timestamp))}</span>
              ${canResend ? `<button type="button" class="mini-action timeline-resend-action" data-action="resend" data-phone="${escapeHtml(firstNonEmpty(evt.phone, selectedPhone))}" data-text="${escapeHtml(resendPayload)}" title="Reenviar mensagem com falha">Reenviar</button>` : ''}
              ${technical ? `<details class="event-tech"><summary>Detalhes técnicos</summary><pre>${technicalPayload}</pre></details>` : ''}
            </div>
          </div>`;
}

function buildTechnicalCompactSummary(evt) {
  const status = eventFinalStatus(evt);
  return `${status.label} • ${formatDatePtBr(evt?.timestamp)}`;
}

function renderDeliveryIcon(status, tooltip) {
  const code = status?.code || 'pending';
  const title = escapeHtml(tooltip || status?.label || '');
  if (code === 'sent') return `<span class="delivery-icon delivery-icon-sent" title="${title}"><span class="tick">✓</span></span>`;
  if (code === 'delivered') return `<span class="delivery-icon delivery-icon-delivered" title="${title}"><span class="tick">✓✓</span></span>`;
  if (code === 'read') return `<span class="delivery-icon delivery-icon-read" title="${title}"><span class="tick">✓✓</span></span>`;
  if (code === 'failed') return `<span class="delivery-icon delivery-icon-failed" title="${title}">!</span>`;
  return `<span class="delivery-icon delivery-icon-pending" title="${title}">🕒</span>`;
}

function formatEventClock(value) {
  if (!value) return '--:--';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '--:--';
  return date.toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit' });
}

function dayLabelForSeparator(value) {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  const now = new Date();
  if (date.toDateString() === now.toDateString()) return 'Hoje';
  const yesterday = new Date(now);
  yesterday.setDate(now.getDate() - 1);
  if (date.toDateString() === yesterday.toDateString()) return 'Ontem';
  return date.toLocaleDateString('pt-BR', { day: '2-digit', month: '2-digit', year: 'numeric' });
}

function firstNonEmpty(...values) {
  for (const value of values) {
    const normalized = fixMojibake(String(value || '')).trim();
    if (normalized) return normalized;
  }

  return '';
}

function isOutbound(evt) {
  return (evt.direction || '').toLowerCase() === 'outbound' || (evt.category || '').toLowerCase() === 'sent';
}

function isInbound(evt) {
  return (evt.direction || '').toLowerCase() === 'inbound' || (evt.category || '').toLowerCase() === 'customer';
}

function extractConversationText(evt) {
  const fields = evt && evt.fields ? evt.fields : {};
  const flowResponse = extractFlowResponseSummary(evt);
  const direct = firstNonEmpty(
    fields.text,
    fields.texto,
    fields.resposta_raw,
    fields.message_text,
    fields.body,
    fields.mensagem,
    fields.reply_text
  );
  if (flowResponse && direct.toUpperCase() === 'FLOW') {
    return `FLUXO: ${flowResponse}`;
  }
  if (direct) return direct;
  if (flowResponse) return `FLUXO: ${flowResponse}`;
  return firstNonEmpty(cleanLogLikeText(evt.mainText || ''));
}

function tryParseJson(value) {
  const text = String(value || '').trim();
  if (!text || (text[0] !== '{' && text[0] !== '[')) return null;
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}

function findFlowResponseJson(node) {
  if (!node || typeof node !== 'object') return null;
  if (!Array.isArray(node) && node.interactive && node.interactive.nfm_reply && node.interactive.nfm_reply.response_json) {
    return node.interactive.nfm_reply.response_json;
  }
  if (!Array.isArray(node) && node.response_json) {
    return node.response_json;
  }

  const values = Array.isArray(node) ? node : Object.values(node);
  for (const value of values) {
    const found = findFlowResponseJson(value);
    if (found) return found;
  }

  return null;
}

function toFlowSummary(value) {
  if (!value) return '';

  if (typeof value === 'string') {
    const parsed = tryParseJson(value);
    if (parsed) return toFlowSummary(parsed);
    return value;
  }

  if (typeof value !== 'object') return String(value);

  const entries = Object.entries(value)
    .filter(([, v]) => v !== null && v !== undefined && String(v).trim() !== '')
    .map(([k, v]) => `${toNaturalLabel(k)}: ${typeof v === 'object' ? JSON.stringify(v) : String(v)}`);

  return entries.join(' | ');
}

function extractFlowResponseSummary(evt) {
  const fields = evt && evt.fields ? evt.fields : {};
  const directFlowFields = [fields.flow_response_json, fields.response_json, fields.nfm_reply_response_json];
  for (const raw of directFlowFields) {
    const parsed = typeof raw === 'object' ? raw : tryParseJson(raw);
    if (!parsed) continue;
    const summary = toFlowSummary(parsed);
    if (summary) return summary;
  }

  const payloadFields = [fields.raw_payload_json, fields.normalized_payload_json];
  for (const payload of payloadFields) {
    const parsedPayload = tryParseJson(payload);
    if (!parsedPayload) continue;
    const responseJson = findFlowResponseJson(parsedPayload);
    if (!responseJson) continue;
    const summary = toFlowSummary(responseJson);
    if (summary) return summary;
  }

  return '';
}

function toNaturalLabel(value) {
  const normalized = String(value || '')
    .toLowerCase()
    .replaceAll('_', ' ')
    .replace(/\s+/g, ' ')
    .trim();
  return normalized ? normalized.charAt(0).toUpperCase() + normalized.slice(1) : 'Evento';
}

function formatEventNameNatural(evt) {
  const eventName = String(evt && evt.eventName ? evt.eventName : '').toUpperCase();
  const fields = evt && evt.fields ? evt.fields : {};
  if (eventName === 'INTERACAO_CRIADA') {
    const actor = firstNonEmpty(fields.usuario, fields.user, fields.operador, fields.origem, 'sistema');
    return `Interação criada por ${actor}`;
  }
  if (eventName === 'INTERACAO_ENCERRADA_MANUALMENTE') {
    const actor = firstNonEmpty(fields.usuario, fields.user, fields.operador, fields.origem, 'operador');
    return `Interação encerrada manualmente por ${actor}`;
  }
  if (eventName === 'INTERACAO_FECHADA') return 'Interação fechada';
  if (eventName.startsWith('INTERACAO_')) return toNaturalLabel(eventName.replace('INTERACAO_', 'Interação '));
  if (eventName === 'CLIENTE_RESPONDEU') return 'Cliente respondeu';
  if (eventName === 'WEBHOOK_META_RECEBIDO') return 'Mensagem recebida do cliente';
  if (eventName === 'ENVIO_ACEITO_META') return 'Envio aceito pela Meta';
  if (eventName === 'MIDIA_RECEBIDA') return 'Mídia recebida';
  return toNaturalLabel(eventName || 'EVENTO');
}

function cleanLogLikeText(value) {
  const text = String(value || '');
  if (!text) return '';
  return text
    .split('\n')
    .map(line => line.trim())
    .filter(line => line.length > 0)
    .filter(line => !line.startsWith('interaction_id=') && !line.startsWith('meta_message_id='))
    .join('\n');
}

function enrichTechnicalText(evt) {
  const base = evt && evt.mainText ? String(evt.mainText) : '-';
  if (!evt || String(evt.eventName || '').toUpperCase() !== 'INTERACAO_CRIADA') {
    return base;
  }

  const fields = evt.fields || {};
  const flow = firstNonEmpty(fields.flow_name, fields.flow, fields.flow_id, fields.pipeline, fields.workflow);
  const template = firstNonEmpty(fields.template_name, fields.template, fields.template_id, fields.message_template);
  const extras = [];
  if (flow) extras.push(`Fluxo: ${flow}`);
  if (template) extras.push(`Modelo: ${template}`);
  return extras.length > 0 ? `${base}\n${extras.join(' | ')}` : base;
}

function isInternalAction(evt) {
  if (!evt) return false;
  const eventName = String(evt.eventName || '').toUpperCase();
  const category = String(evt.category || '').toLowerCase();
  if (eventName.startsWith('INTERACAO_')) return true;
  return category === 'system' && !isInbound(evt) && !isOutbound(evt);
}

function eventMatchesGroup(evt, group) {
  if (!evt) return false;
  if (group === 'outbound') return isOutbound(evt);
  if (group === 'inbound') return isInbound(evt);
  if (group === 'internal') return isInternalAction(evt);
  return true;
}

function isTechnicalOnlyEvent(evt) {
  if (!evt) return false;
  const eventName = String(evt.eventName || '').toUpperCase();
  const technicalEventNames = new Set([
    'EXECUTION_CONTEXT_REGISTRADO',
    'META_STATUS_AUDITADO_SEM_DISPARO',
    'ENVIO_ACEITO_META',
    'DOCUMENTOS_ENVIADOS',
    'MIDIA_RECEBIDA',
    'CORRELACAO_NAO_RESOLVIDA'
  ]);
  if (technicalEventNames.has(eventName)) {
    return true;
  }

  const technicalHints = [
    'atualização de envio recebida no endpoint de status',
    'contexto de execução registrado para consulta por execution_id',
    'template não enviado: etapa atual não está mapeada para disparo',
    'template nao enviado: etapa atual nao esta mapeada para disparo',
    'mensagem com mídia/documento detectada no webhook da meta',
    'mensagem com midia/documento detectada no webhook da meta',
    'webhook recebido, porém sem correlação para interação ativa',
    'webhook recebido, porem sem correlacao para interacao ativa'
  ];
  const normalizedText = fixMojibake(String(enrichTechnicalText(evt) || '')).toLowerCase();
  return technicalHints.some(hint => normalizedText.includes(hint));
}

function isErrorEvent(evt) {
  const status = eventFinalStatus(evt);
  if (status.code === 'failed') return true;
  const eventName = String(evt?.eventName || '').toLowerCase();
  const category = String(evt?.category || '').toLowerCase();
  const raw = `${eventName} ${category} ${firstNonEmpty(evt?.status, evt?.mainText)}`.toLowerCase();
  return /erro|error|falh|failed|rejeit|invalid/.test(raw);
}

function isMainEvent(evt, group = 'all') {
  if (!evt) return false;
  if (!eventMatchesGroup(evt, group)) {
    return false;
  }
  if (isTechnicalOnlyEvent(evt)) {
    return false;
  }

  const text = extractConversationText(evt);
  if (group === 'internal') return isInternalAction(evt);
  if (group === 'outbound') return isOutbound(evt) && !!text;
  if (group === 'inbound') return isInbound(evt) && !!text;
  if (isInternalAction(evt)) return true;
  return (isInbound(evt) || isOutbound(evt)) && !!text;
}

function fingerprint(evt) {
  return `${evt.timestamp || '-'}|${evt.eventName || '-'}|${extractConversationText(evt) || evt.mainText || '-'}`;
}

function notifyEvent(evt) {
  const container = document.getElementById('notifications');
  if (!container) return;
  if (isMutedConversation(evt?.phone)) return;
  const text = escapeHtml(fixMojibake(extractConversationText(evt)));
  const klass = isInbound(evt) ? 'inbound' : 'outbound';
  const title = isInbound(evt) ? 'Novo recebido' : 'Novo envio';
  const phone = String(evt?.phone || '').trim();
  const node = document.createElement('div');
  node.className = `notif ${klass}`;
  if (phone) node.dataset.phone = phone;
  node.innerHTML = `<div class="title">${title}</div><div class="body">${text}</div><div class="meta">Clique para fechar</div>`;
  node.onclick = () => node.remove();
  container.prepend(node);
}

function dismissNotificationsForPhone(phone) {
  const key = String(phone || '').trim();
  if (!key) return;
  document.querySelectorAll('#notifications .notif').forEach(node => {
    if (String(node.dataset.phone || '').trim() === key) {
      node.remove();
    }
  });
}

function ensureUnreadCounter(phone) {
  const key = String(phone || '').trim();
  if (!key) return null;
  if (!unreadByPhone.has(key)) {
    unreadByPhone.set(key, { inbound: 0, outbound: 0 });
  }
  return unreadByPhone.get(key);
}

function incrementUnread(phone, evt) {
  const key = String(phone || '').trim();
  const counter = ensureUnreadCounter(phone);
  if (!counter || !isMainEvent(evt, 'all')) return;
  if (isInbound(evt)) counter.inbound += 1;
  else if (isOutbound(evt)) counter.outbound += 1;
  updateUnreadTotal();
  updateConversationRowBadges(key);
}

function clearUnread(phone) {
  const key = String(phone || '').trim();
  if (!key) return;
  unreadByPhone.set(key, { inbound: 0, outbound: 0 });
  updateUnreadTotal();
  updateConversationRowBadges(key);
}

function unreadTotal() {
  let total = 0;
  for (const counter of unreadByPhone.values()) {
    total += (counter.inbound || 0) + (counter.outbound || 0);
  }
  return total;
}

function updateUnreadTotal() {
  const root = document.getElementById('unreadTotal');
  if (!root) return;
  root.textContent = `Não lidas novas: ${unreadTotal()}`;
}

function updateConversationRowBadges(phone) {
  const key = String(phone || '').trim();
  if (!key) return;
  const escapedPhone = (typeof CSS !== 'undefined' && typeof CSS.escape === 'function')
    ? CSS.escape(key)
    : key.replaceAll('"', '\\"');
  const row = document.querySelector(`#conversations .row[data-phone="${escapedPhone}"]`);
  if (!row) return;
  const badgesRoot = row.querySelector('.unread-badges');
  if (!badgesRoot) return;
  const localUnread = ensureUnreadCounter(key) || { inbound: 0, outbound: 0 };
  const inbound = localUnread.inbound || 0;
  const outbound = localUnread.outbound || 0;
  const inboundBadge = inbound > 0 ? `<span class="unread-badge inbound" title="Novas recebidas">${inbound}</span>` : '';
  const outboundBadge = outbound > 0 ? `<span class="unread-badge outbound" title="Novas enviadas">${outbound}</span>` : '';
  const legacyBadge = row.dataset.legacyUnread === 'true'
    ? '<span class="unread-badge inbound" title="Conversa com pendência de resposta">•</span>'
    : '';
  badgesRoot.innerHTML = `${legacyBadge}${inboundBadge}${outboundBadge}`;
}

function markConversationAsActive(phone) {
  const key = String(phone || '').trim();
  document.querySelectorAll('#conversations .row').forEach(row => {
    row.classList.toggle('active', String(row.dataset.phone || '').trim() === key);
  });
}

function resolveConversationDisplay(phone) {
  const profileOverride = getProfileOverride(phone);
  const conversation = (lastConversations || []).find(item => item.phone === phone);
  return {
    name: firstNonEmpty(profileOverride?.nome, conversation?.displayName, phone),
    phone: firstNonEmpty(profileOverride?.numero, conversation?.phone, phone),
    company: firstNonEmpty(profileOverride?.empresa, conversation?.company, ''),
    avatar: initialsFromName(firstNonEmpty(profileOverride?.nome, conversation?.displayName, phone))
  };
}

function isTypingEvent(evt) {
  const haystack = `${firstNonEmpty(evt?.eventName, evt?.status, evt?.mainText, evt?.fields?.status, evt?.fields?.event_type)} ${firstNonEmpty(evt?.fields?.text, evt?.fields?.body)}`.toLowerCase();
  return haystack.includes('digitando') || haystack.includes('typing');
}

function renderConversationHeader(phone) {
  const wrapper = document.getElementById('conversationHeader');
  const avatar = document.getElementById('conversationAvatar');
  const title = document.getElementById('title');
  const context = document.getElementById('conversationContext');
  const typing = document.getElementById('typingIndicator');
  const statusPill = document.getElementById('conversationStatusPill');
  const queuePill = document.getElementById('conversationQueuePill');
  const lastActivity = document.getElementById('conversationLastActivity');
  if (!wrapper || !avatar || !title || !context || !typing || !statusPill || !queuePill || !lastActivity) return;
  if (!phone) {
    wrapper.style.display = 'none';
    title.textContent = 'Selecione uma conversa';
    context.textContent = 'Selecione uma conversa para ver contexto.';
    typing.style.display = 'none';
    return;
  }

  const display = resolveConversationDisplay(phone);
  const latest = (timelineEvents || []).slice().reverse()[0];
  const latestTimestamp = latest?.timestamp ? formatDatePtBr(latest.timestamp) : '-';
  const finalStatus = latest ? eventFinalStatus(latest) : { label: 'Sem status', tone: 'neutral' };
  const unread = unreadByPhone.get(phone) || { inbound: 0, outbound: 0 };
  const queueText = unread.inbound > 0 ? `Fila: ${unread.inbound} pendente(s)` : 'Fila: normal';
  const contextText = [display.phone, display.company].filter(Boolean).join(' • ');
  avatar.textContent = display.avatar;
  title.textContent = display.name;
  context.textContent = `${contextText || display.phone} • Contexto operacional ativo`;
  const pillTone = ['success', 'warning', 'error'].includes(finalStatus.tone) ? finalStatus.tone : 'neutral';
  statusPill.className = `header-pill ${pillTone}`;
  statusPill.textContent = finalStatus.label || 'Sem status';
  queuePill.className = `header-pill ${unread.inbound > 0 ? 'warning' : 'success'}`;
  queuePill.textContent = queueText;
  lastActivity.textContent = `Última atividade: ${latestTimestamp}`;
  wrapper.style.display = 'flex';

  const isTyping = !!latest && isTypingEvent(latest) && (Date.now() - new Date(latest.timestamp).getTime()) <= 15000;
  typing.style.display = isTyping ? 'inline-flex' : 'none';
}

function renderProfile(events, phone) {
  const root = document.getElementById('profile');
  if (!root) return;
  const items = events || [];
  const findLatest = (keys) => {
    for (let i = items.length - 1; i >= 0; i--) {
      const fields = items[i].fields || {};
      for (const key of keys) {
        const value = firstNonEmpty(fields[key]);
        if (value) return value;
      }
    }
    return '-';
  };

  const nome = findLatest(['NM_PESSOA', 'nm_pessoa', 'contato_nome', 'contact_name', 'name', 'cliente', 'customer_name']);
  const empresa = findLatest(['NM_EMPRESA', 'nm_empresa', 'empresa', 'company', 'nm_fantasia', 'razao_social']);
  const numero = firstNonEmpty(phone, findLatest(['canonical_phone', 'telefone', 'customer_phone'])) || '-';
  const override = getProfileOverride(phone);
  const displayNome = firstNonEmpty(override?.nome, nome) || '-';
  const displayEmpresa = firstNonEmpty(override?.empresa, empresa) || '-';
  const displayNumero = firstNonEmpty(override?.numero, numero) || '-';
  const hasOverride = !!override;
  const numeroBase = firstNonEmpty(phone, numero) || '';

  root.style.display = 'block';
  root.innerHTML = `<h3 style="margin:0 0 8px 0;">Perfil do contato</h3>
    <div class="profile-grid">
      <div class="k">Nome</div><div>${escapeHtml(displayNome)}</div>
      <div class="k">Empresa</div><div>${escapeHtml(displayEmpresa)}</div>
      <div class="k">Número</div><div>${escapeHtml(displayNumero)}</div>
    </div>
    <details style="margin-top:10px;">
      <summary style="cursor:pointer; color:#7a4a21; font-weight:600;">Editar dados (somente nesta visualização)</summary>
      <div class="profile-edit-grid">
        <label>Nome
          <input id="profileEditNome" type="text" value="${escapeHtml(displayNome === '-' ? '' : displayNome)}" />
        </label>
        <label>Empresa
          <input id="profileEditEmpresa" type="text" value="${escapeHtml(displayEmpresa === '-' ? '' : displayEmpresa)}" />
        </label>
        <label>Número
          <input id="profileEditNumero" type="text" value="${escapeHtml(displayNumero === '-' ? numeroBase : displayNumero)}" />
        </label>
      </div>
      <div class="profile-edit-actions">
        <button type="button" onclick="saveProfileEdits()">Salvar visualização</button>
        <button type="button" onclick="resetProfileEdits()" ${hasOverride ? '' : 'disabled'}>Remover edição</button>
      </div>
    </details>`;
}

function saveProfileEdits() {
  const key = String(selectedPhone || '').trim();
  if (!key) return;
  const payload = {
    nome: (document.getElementById('profileEditNome')?.value || '').trim(),
    empresa: (document.getElementById('profileEditEmpresa')?.value || '').trim(),
    numero: (document.getElementById('profileEditNumero')?.value || '').trim()
  };
  saveProfileOverride(key, payload);
  renderProfile(timelineEvents, key);
  renderOperationalSummary(timelineEvents, key);
  renderConversationHeader(key);
  loadConversations().catch(() => {});
}

function resetProfileEdits() {
  const key = String(selectedPhone || '').trim();
  if (!key) return;
  clearProfileOverride(key);
  renderProfile(timelineEvents, key);
  renderOperationalSummary(timelineEvents, key);
  renderConversationHeader(key);
  loadConversations().catch(() => {});
}

function ensureOperationalSummaryContainer() {
  let summary = document.getElementById('operationalSummary');
  if (summary) return summary;
  const metricsRoot = document.getElementById('metricsCards');
  if (!metricsRoot || !metricsRoot.parentElement) return null;
  summary = document.createElement('div');
  summary.id = 'operationalSummary';
  summary.className = 'operational-summary';
  metricsRoot.insertAdjacentElement('afterend', summary);
  return summary;
}

function latestEventByDirection(events, direction) {
  const source = (events || []).slice().reverse();
  for (const evt of source) {
    if (direction === 'inbound' && isInbound(evt)) return evt;
    if (direction === 'outbound' && isOutbound(evt)) return evt;
    if (direction === 'internal' && isInternalAction(evt)) return evt;
  }
  return null;
}

function decisionHint(conversation, latestInbound) {
  if (!conversation) return 'Selecione uma conversa para recomendações.';
  if (conversation.isSlaBreached) return 'Prioridade alta: responder cliente e registrar ação interna.';
  if ((conversation.waitingMinutes || 0) >= 15) return 'Atenção: pendência acima de 15 minutos sem resposta.';
  if (conversation.conversationStatus && String(conversation.conversationStatus).toLowerCase().includes('erro')) {
    return 'Revisar envio e reprocessar mensagem com falha.';
  }
  if (latestInbound) return 'Cliente respondeu: avaliar contexto e enviar retorno objetivo.';
  return 'Monitorar interação e manter fluxo operacional.';
}

function renderOperationalSummary(events, phone) {
  const root = ensureOperationalSummaryContainer();
  if (!root) return;
  if (!phone) {
    root.style.display = 'none';
    root.innerHTML = '';
    return;
  }
  const conversation = (lastConversations || []).find(item => String(item.phone || '') === String(phone || ''));
  const inbound = latestEventByDirection(events, 'inbound');
  const outbound = latestEventByDirection(events, 'outbound');
  const internal = latestEventByDirection(events, 'internal');
  const summaryHint = decisionHint(conversation, inbound);
  root.style.display = 'block';
  root.innerHTML = `<div class="operational-summary-head">Resumo operacional</div>
    <div class="operational-summary-grid">
      <div><span>Última resposta</span><strong>${escapeHtml(inbound ? formatDatePtBr(inbound.timestamp) : '-')}</strong></div>
      <div><span>Último envio</span><strong>${escapeHtml(outbound ? formatDatePtBr(outbound.timestamp) : '-')}</strong></div>
      <div><span>Última ação interna</span><strong>${escapeHtml(internal ? formatDatePtBr(internal.timestamp) : '-')}</strong></div>
      <div><span>Status/SLA</span><strong>${escapeHtml(conversation?.isSlaBreached ? 'SLA estourado' : 'Dentro do SLA')}</strong></div>
    </div>
    <div class="operational-summary-hint">${escapeHtml(summaryHint)}</div>`;
}

function eventMatchesTimelineFilters(evt) {
  const textNeedle = timelineSearchFilters.text.trim().toLowerCase();
  const statusNeedle = timelineSearchFilters.status.trim().toLowerCase();
  const interactionNeedle = timelineSearchFilters.interactionId.trim().toLowerCase();
  const fromTs = parseDateInput(timelineSearchFilters.from);
  const toTs = parseDateInput(timelineSearchFilters.to);
  const evtTs = new Date(evt.timestamp || 0).getTime();

  if (fromTs && evtTs < fromTs) return false;
  if (toTs && evtTs > toTs) return false;

  if (statusNeedle) {
    const status = firstNonEmpty(evt.fields?.status, evt.fields?.event_type, evt.status, evt.eventName).toLowerCase();
    if (!status.includes(statusNeedle)) return false;
  }

  if (interactionNeedle) {
    const interactionId = firstNonEmpty(evt.interactionId, evt.fields?.interaction_id).toLowerCase();
    if (!interactionId.includes(interactionNeedle)) return false;
  }

  if (textNeedle) {
    const haystack = [
      extractConversationText(evt),
      evt.mainText,
      evt.eventName,
      firstNonEmpty(evt.fields?.status, evt.fields?.event_type)
    ].join(' ').toLowerCase();
    if (!haystack.includes(textNeedle)) return false;
  }

  return true;
}

function applyTimelineLocalFilters() {
  filteredTimelineEvents = (timelineEvents || []).filter(eventMatchesTimelineFilters);
}

function updateTimelineSearchCounter() {
  const label = document.getElementById('timelineSearchCounter');
  if (!label) return;
  const suffix = timelineSearchServerCursor ? ' (com mais páginas)' : '';
  label.textContent = `${filteredTimelineEvents.length} resultados${suffix}`;
}

function syncTimelineMatchElements() {
  timelineSearchMatches = Array.from(document.querySelectorAll('#timeline .timeline-match'));
  if (timelineSearchMatches.length === 0) {
    timelineActiveMatchIndex = -1;
    return;
  }
  if (timelineActiveMatchIndex < 0) timelineActiveMatchIndex = 0;
  if (timelineActiveMatchIndex >= timelineSearchMatches.length) timelineActiveMatchIndex = timelineSearchMatches.length - 1;
  timelineSearchMatches.forEach((el, idx) => el.classList.toggle('timeline-match-active', idx === timelineActiveMatchIndex));
}

async function applyTimelineSearch(resetServerCursor = true) {
  if (resetServerCursor) {
    timelineSearchServerCursor = null;
  }

  const shouldUseServer = timelineSearchFilters.useServer && selectedPhone && timelineEvents.length >= timelineServerSearchThreshold;
  if (shouldUseServer) {
    const query = new URLSearchParams({ page_size: String(timelineSearchServerPageSize) });
    if (timelineSearchFilters.text) query.set('text', timelineSearchFilters.text);
    if (timelineSearchFilters.status) query.set('status', timelineSearchFilters.status);
    if (timelineSearchFilters.interactionId) query.set('interaction_id', timelineSearchFilters.interactionId);
    if (timelineSearchFilters.from) query.set('from', new Date(timelineSearchFilters.from).toISOString());
    if (timelineSearchFilters.to) query.set('to', new Date(timelineSearchFilters.to).toISOString());
    if (timelineSearchServerCursor) query.set('cursor', timelineSearchServerCursor);
    const payload = await apiGet(`/api/meta/whatsapp/viewer/conversations/${encodeURIComponent(selectedPhone)}/search?${query.toString()}`);
    filteredTimelineEvents = normalizeEventsPayload(payload?.events);
    timelineSearchServerCursor = payload?.next_cursor || null;
  } else {
    applyTimelineLocalFilters();
  }

  const moreBtn = document.getElementById('timelineServerMoreBtn');
  if (moreBtn) {
    moreBtn.style.display = timelineSearchServerCursor ? 'inline-block' : 'none';
  }
  timelineActiveMatchIndex = -1;
  refreshTimelineView();
}

async function loadMoreTimelineSearchResults() {
  if (!selectedPhone || !timelineSearchServerCursor) return;
  const query = new URLSearchParams({ page_size: String(timelineSearchServerPageSize), cursor: timelineSearchServerCursor });
  if (timelineSearchFilters.text) query.set('text', timelineSearchFilters.text);
  if (timelineSearchFilters.status) query.set('status', timelineSearchFilters.status);
  if (timelineSearchFilters.interactionId) query.set('interaction_id', timelineSearchFilters.interactionId);
  if (timelineSearchFilters.from) query.set('from', new Date(timelineSearchFilters.from).toISOString());
  if (timelineSearchFilters.to) query.set('to', new Date(timelineSearchFilters.to).toISOString());
  const payload = await apiGet(`/api/meta/whatsapp/viewer/conversations/${encodeURIComponent(selectedPhone)}/search?${query.toString()}`);
  const older = normalizeEventsPayload(payload?.events);
  filteredTimelineEvents = stableSortTimeline(filteredTimelineEvents.concat(older));
  timelineSearchServerCursor = payload?.next_cursor || null;
  refreshTimelineView();
}

function activeTimelineSource() {
  const source = filteredTimelineEvents.length > 0 || hasActiveTimelineFilters()
    ? filteredTimelineEvents
    : timelineEvents;
  return stableSortTimeline((source || []).filter(evt => eventMatchesGroup(evt, activeTimelineGroup))
    .map(evt => normalizeEventPayload(evt))
    .filter(Boolean));
}

function bindTimelineVirtualScroll(root) {
  if (!root || root.dataset.virtualBound === '1') return;
  root.dataset.virtualBound = '1';
}

function renderTimelineWindow(root, events, stickBottom) {
  const safeEvents = Array.isArray(events) ? events : [];
  const total = safeEvents.length;
  const firstFp = total > 0 ? fingerprint(safeEvents[0]) : '-';
  const lastFp = total > 0 ? fingerprint(safeEvents[total - 1]) : '-';
  const signature = `${total}:${firstFp}:${lastFp}`;

  if (signature !== timelineVirtualRenderSignature) {
    let previousDayLabel = '';
    let previousTimestamp = null;
    const windowHtml = safeEvents
      .map((evt, idx) => {
        const currentDayLabel = dayLabelForSeparator(evt.timestamp);
        const chunks = [];
        if (currentDayLabel && currentDayLabel !== previousDayLabel) {
          chunks.push(`<div class="timeline-date-separator"><span>${escapeHtml(currentDayLabel)}</span></div>`);
          previousDayLabel = currentDayLabel;
        }
        const eventTimestamp = new Date(evt.timestamp).getTime();
        const hasGap = previousTimestamp && !Number.isNaN(eventTimestamp) && (eventTimestamp - previousTimestamp) > (1000 * 60 * 35);
        if (hasGap) {
          chunks.push('<div class="timeline-gap-separator"><span>⏱️ pausa de conversa</span></div>');
        }
        const eventHtml = renderTimelineEvent(evt);
        const isNewCluster = idx === 0 || hasGap;
        chunks.push(isNewCluster
          ? eventHtml.replace('class="event ', 'class="event event-start-cluster ')
          : eventHtml);
        previousTimestamp = eventTimestamp;
        return chunks.join('');
      })
      .join('');
    root.innerHTML = windowHtml;
    timelineVirtualRenderSignature = signature;
  }

  if (stickBottom) {
    root.scrollTop = root.scrollHeight;
  }
}

function refreshTimelineView(options = {}) {
  const root = document.getElementById('timeline');
  if (!root) return;
  bindTimelineVirtualScroll(root);
  const source = activeTimelineSource();
  if (source.length === 0) {
    root.innerHTML = `<div class="empty-state">
      <h3>Nenhuma mensagem nessa conversa</h3>
      <p>Quando houver tráfego, as mensagens vão aparecer aqui em tempo real.</p>
    </div>`;
    syncTimelineMatchElements();
    updateTimelineSearchCounter();
    return;
  }
  renderTimelineWindow(root, source, options.stickBottom !== false);
  syncTimelineMatchElements();
  updateTimelineSearchCounter();
}

function setSelectedTimelineEvent(evt) {
  selectedTimelineFingerprint = evt ? fingerprint(evt) : '';
  document.querySelectorAll('#timeline .event[data-event-fingerprint]').forEach(node => {
    node.classList.toggle('event-selected', node.dataset.eventFingerprint === selectedTimelineFingerprint);
  });
  renderDebugPanel(evt);
}

function findTimelineEventByFingerprint(fp) {
  const source = (filteredTimelineEvents.length > 0 || hasActiveTimelineFilters()) ? filteredTimelineEvents : timelineEvents;
  return (source || []).find(evt => fingerprint(evt) === fp) || null;
}

function copyText(value, successMessage) {
  const payload = String(value || '');
  if (!payload) {
    alert('Nada para copiar.');
    return;
  }
  const fallback = () => {
    const temp = document.createElement('textarea');
    temp.value = payload;
    document.body.appendChild(temp);
    temp.select();
    document.execCommand('copy');
    temp.remove();
    alert(successMessage);
  };
  if (navigator?.clipboard?.writeText) {
    navigator.clipboard.writeText(payload).then(() => alert(successMessage)).catch(() => fallback());
  } else {
    fallback();
  }
}

function renderDebugPanel(evt) {
  const panel = document.getElementById('eventDebugPanel');
  if (!panel) return;
  if (!evt) {
    panel.innerHTML = '<div class="debug-empty">Selecione um evento para ver payload, rastreio e depuração.</div>';
    return;
  }
  const payload = summarizePayload(evt);
  const ids = trackingIds(evt);
  const diagnostics = extractDiagnostics(evt);
  const statusHistory = buildStatusHistory(evt);
  const sanitizedJson = sanitizedProviderResponse(evt);
  const anchorId = firstNonEmpty(ids.interaction_id, ids.message_id, ids.trace_id);
  panel.innerHTML = `
    <div class="debug-header">
      <strong>Debug do evento</strong>
      <div class="debug-actions">
        <button type="button" class="mini-action" data-debug-action="copy-id">copiar ID</button>
        <button type="button" class="mini-action" data-debug-action="copy-json">copiar JSON sanitizado</button>
        <button type="button" class="mini-action" data-debug-action="open-correlates">abrir correlatos</button>
      </div>
    </div>
    <div class="debug-section">
      <h4>Payload resumido</h4>
      <div class="debug-grid">
        <div>Evento</div><div>${escapeHtml(payload.event)}</div>
        <div>Direção</div><div>${escapeHtml(payload.direcao)}</div>
        <div>Status</div><div>${escapeHtml(payload.status)}</div>
        <div>Autor</div><div>${escapeHtml(payload.autor)}</div>
        <div>Timestamp</div><div>${escapeHtml(payload.timestamp)}</div>
      </div>
    </div>
    <div class="debug-section">
      <h4>IDs de rastreio</h4>
      <div class="debug-grid">
        <div>interaction_id</div><div>${escapeHtml(ids.interaction_id || '-')}</div>
        <div>message_id</div><div>${escapeHtml(ids.message_id || '-')}</div>
        <div>trace_id</div><div>${escapeHtml(ids.trace_id || '-')}</div>
        <div>execution_id</div><div>${escapeHtml(ids.execution_id || '-')}</div>
      </div>
    </div>
    <div class="debug-section">
      <h4>Latência e tentativas</h4>
      <div class="debug-grid">
        <div>Latência</div><div>${escapeHtml(diagnostics.latencyLabel)}</div>
        <div>Tentativas</div><div>${escapeHtml(diagnostics.attemptsLabel)}</div>
      </div>
    </div>
    <div class="debug-section">
      <h4>Histórico de status</h4>
      <ul class="debug-status-history">${statusHistory.map(item => `<li><strong>${escapeHtml(item.label)}</strong> • ${escapeHtml(item.timestamp)}</li>`).join('')}</ul>
    </div>
    <div class="debug-section">
      <h4>Resposta sanitizada do provedor</h4>
      <pre class="debug-json">${escapeHtml(sanitizedJson)}</pre>
    </div>`;

  panel.querySelector('[data-debug-action="copy-id"]')?.addEventListener('click', () => {
    copyText(anchorId || '', 'ID copiado.');
  });
  panel.querySelector('[data-debug-action="copy-json"]')?.addEventListener('click', () => {
    copyText(sanitizedJson, 'JSON sanitizado copiado.');
  });
  panel.querySelector('[data-debug-action="open-correlates"]')?.addEventListener('click', () => {
    if (!anchorId) {
      alert('Nenhum ID correlacionável disponível neste evento.');
      return;
    }
    const interactionInput = document.getElementById('timelineFilterInteractionId');
    if (interactionInput) interactionInput.value = anchorId;
    timelineSearchFilters.interactionId = anchorId;
    applyTimelineSearch(true).catch(() => {});
  });
}

function hasActiveTimelineFilters() {
  return !!(timelineSearchFilters.text || timelineSearchFilters.status || timelineSearchFilters.interactionId || timelineSearchFilters.from || timelineSearchFilters.to);
}

function jumpTimelineMatch(direction) {
  if (!timelineSearchMatches.length) return;
  timelineActiveMatchIndex = (timelineActiveMatchIndex + direction + timelineSearchMatches.length) % timelineSearchMatches.length;
  timelineSearchMatches.forEach((el, idx) => el.classList.toggle('timeline-match-active', idx === timelineActiveMatchIndex));
  timelineSearchMatches[timelineActiveMatchIndex].scrollIntoView({ behavior: 'smooth', block: 'center' });
}

function updateLoadMoreButton() {
  const button = document.getElementById('loadMoreHistoryBtn');
  if (!button) return;
  button.style.display = nextHistoryCursor ? 'inline-block' : 'none';
}

function setTimelineGroup(group) {
  activeTimelineGroup = group || 'all';
  document.querySelectorAll('#timelineGroupTabs .timeline-tab').forEach(tab => {
    tab.classList.toggle('active', tab.dataset.group === activeTimelineGroup);
  });
  refreshTimelineView();
}

function appendEventsIncrementally(events, notifyNew = true) {
  if (!events || events.length === 0) return;
  appendQueue.push({ events: normalizeEventsPayload(events), notifyNew: notifyNew !== false });
  if (appendInProgress) return;

  appendInProgress = true;
  try {
    while (appendQueue.length > 0) {
      const batch = appendQueue.shift();
      const known = new Set(lastEventFingerprints);
      const incoming = (batch?.events || [])
        .filter(evt => evt && evt.phone === selectedPhone)
        .filter(evt => !known.has(fingerprint(evt)));
      if (incoming.length === 0) continue;

      incoming.forEach(evt => {
        timelineEvents.push(evt);
        const fp = fingerprint(evt);
        lastEventFingerprints.push(fp);
        known.add(fp);
        if (batch?.notifyNew && isMainEvent(evt, 'all')) {
          notifyEvent(evt);
        }
      });
    }

    timelineEvents = stableSortTimeline(timelineEvents);
    applyTimelineLocalFilters();
    refreshTimelineView();
    renderProfile(timelineEvents, selectedPhone);
    renderOperationalSummary(timelineEvents, selectedPhone);
    renderConversationHeader(selectedPhone);
  } finally {
    appendInProgress = false;
  }
}

function headers() {
  const customHeaders = { 'Content-Type': 'application/json' };
  const operatorName = (document.getElementById('operatorName')?.value || '').trim();
  if (operatorName) {
    customHeaders['X-Viewer-Operator'] = operatorName;
  }
  return customHeaders;
}

function isReadOnlyRole() {
  return String(viewerRole || '').toLowerCase() === 'read_only';
}

function applyPermissionUiState() {
  const mutationButtons = ['sendManualBtn', 'closeInteractionBtn', 'savePresetBtn'];
  mutationButtons.forEach(id => {
    const button = document.getElementById(id);
    if (!button) return;
    button.disabled = isReadOnlyRole();
    button.style.display = isReadOnlyRole() ? 'none' : '';
  });

  const reasonSelect = document.getElementById('closeReasonCode');
  if (reasonSelect) {
    reasonSelect.disabled = isReadOnlyRole();
    reasonSelect.style.display = isReadOnlyRole() ? 'none' : '';
  }
}

function setCloseReasonCatalog(reasons) {
  closeReasonCatalog = Array.isArray(reasons) ? reasons : [];
  const select = document.getElementById('closeReasonCode');
  if (!select) return;

  const options = ['<option value="">Motivo do encerramento...</option>']
    .concat(closeReasonCatalog.map(item => `<option value="${escapeHtml(item.code)}">${escapeHtml(item.label)}</option>`));
  select.innerHTML = options.join('');
}

async function loadViewerConfig() {
  try {
    const res = await fetch('/api/meta/whatsapp/viewer/config');
    if (!res.ok) return;
    const data = await res.json();
    viewerRole = data?.role || viewerRole;
    setCloseReasonCatalog(data?.close_reasons);
    applyPermissionUiState();
    const logo = document.getElementById('ibrLogo');
    const logoDataUri = typeof data?.logoDataUri === 'string' ? data.logoDataUri.trim() : '';
    if (logo && logoDataUri) {
      logo.src = logoDataUri;
      logo.style.display = '';
    }
  } catch {
    // no-op
  }
}

async function apiGet(url) {
  const res = await fetch(url, { headers: headers() });
  if (res.status === 401) {
    lockSession();
    throw new Error('SESSION_EXPIRED');
  }
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return await res.json();
}

async function apiPost(url, body) {
  const res = await fetch(url, {
    method: 'POST',
    headers: headers(),
    body: JSON.stringify(body || {})
  });
  if (res.status === 401) {
    lockSession();
    throw new Error('SESSION_EXPIRED');
  }
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return await res.json();
}

function lockSession() {
  stopLiveUpdates();
  stopConversationListAutoRefresh();
  document.getElementById('lock').style.display = 'flex';
}

function stopConversationListAutoRefresh() {
  if (listRefreshHandle) {
    clearInterval(listRefreshHandle);
    listRefreshHandle = null;
  }
}

function startConversationListAutoRefresh() {
  stopConversationListAutoRefresh();
}

async function unlock() {
  const lockError = document.getElementById('lockError');
  lockError.textContent = '';
  const username = document.getElementById('username').value.trim();
  const key = document.getElementById('apiKey').value.trim();
  if (!key) {
    lockError.textContent = 'Informe a senha.';
    return;
  }

  try {
    const res = await fetch('/api/meta/whatsapp/viewer/session', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-API-Key': key
      },
      body: JSON.stringify({ username, password: key })
    });

    if (!res.ok) {
      throw new Error('INVALID_PASSWORD');
    }

    const payload = await res.json();
    viewerRole = payload?.role || viewerRole;
    applyPermissionUiState();
    await loadViewerConfig().catch(() => {});
    document.getElementById('apiKey').value = '';
    document.getElementById('username').value = '';
    document.getElementById('lock').style.display = 'none';
    await loadConversations();
    startLiveUpdates();
    startConversationListAutoRefresh();
  } catch {
    lockError.textContent = 'Senha inválida.';
  }
}

async function endSession() {
  try {
    await fetch('/api/meta/whatsapp/viewer/session', {
      method: 'DELETE',
      headers: headers()
    });
  } finally {
    selectedPhone = null;
    viewerStreamCursor = null;
    timelineEvents = [];
    filteredTimelineEvents = [];
    timelineSearchServerCursor = null;
    timelineVirtualRenderSignature = '';
    lastEventFingerprints = [];
    nextHistoryCursor = null;
    conversationRecentCache.clear();
    timelinePrefetchByPhone.clear();
    conversationPaginationByFilter = new Map();
    activeConversationFilterKey = '';
    document.getElementById('conversations').innerHTML = '';
    document.getElementById('timeline').innerHTML = '';
    document.getElementById('metricsCards').innerHTML = '';
    latestMetricsSnapshot = null;
    activeLocalConversationFilter = '';
    document.getElementById('title').textContent = 'Selecione uma conversa';
    renderConversationHeader(null);
    renderOperationalSummary([], null);
    updateLoadMoreButton();
    lockSession();
  }
}

function currentConversationFilters() {
  const search = document.getElementById('search').value || '';
  const limit = document.getElementById('limit').value || '50';
  const status = document.getElementById('filterStatus').value || '';
  const eventType = document.getElementById('filterEventType').value || '';
  const operator = document.getElementById('filterOperator').value || '';
  const templateCampaign = document.getElementById('filterTemplateCampaign').value || '';
  const errorCode = document.getElementById('filterErrorCode').value || '';
  const from = document.getElementById('filterFrom').value || '';
  const to = document.getElementById('filterTo').value || '';
  const onlyUnread = document.getElementById('onlyUnread').checked ? 'true' : 'false';
  const onlySlaBreached = document.getElementById('onlySlaBreached').checked ? 'true' : 'false';
  const hasActiveInteraction = document.getElementById('hasActiveInteraction').checked ? 'true' : 'false';
  return { search, limit, status, eventType, operator, templateCampaign, errorCode, from, to, onlyUnread, onlySlaBreached, hasActiveInteraction };
}

function conversationFilterKey(filters) {
  return JSON.stringify({
    search: filters.search,
    limit: filters.limit,
    status: filters.status,
    eventType: filters.eventType,
    operator: filters.operator,
    templateCampaign: filters.templateCampaign,
    errorCode: filters.errorCode,
    from: filters.from,
    to: filters.to,
    onlyUnread: filters.onlyUnread,
    onlySlaBreached: filters.onlySlaBreached,
    hasActiveInteraction: filters.hasActiveInteraction,
    sortField: conversationSort?.field || 'last_event_at',
    sortDirection: conversationSort?.direction || 'desc'
  });
}

function ensureConversationState(filterKey) {
  if (!conversationPaginationByFilter.has(filterKey)) {
    conversationPaginationByFilter.set(filterKey, {
      items: [],
      nextCursor: null,
      hasMore: true
    });
  }

  return conversationPaginationByFilter.get(filterKey);
}

function conversationQueryFromFilters(filters, cursor) {
  const query = new URLSearchParams({
    search: filters.search,
    limit: filters.limit,
    status: filters.status,
    event_type: filters.eventType,
    operator: filters.operator,
    template_campaign: filters.templateCampaign,
    error_code: filters.errorCode,
    from: filters.from ? new Date(filters.from).toISOString() : '',
    to: filters.to ? new Date(filters.to).toISOString() : '',
    only_unread: filters.onlyUnread,
    only_sla_breached: filters.onlySlaBreached,
    has_active_interaction: filters.hasActiveInteraction
  });

  if (cursor) {
    query.set('cursor', cursor);
  }

  return query;
}

function renderConversationsList(items) {
  const safeItems = (items || []).slice();
  let visibleItems = safeItems;
  if (activeLocalConversationFilter === 'sla_risk') {
    visibleItems = safeItems.filter(isSlaRiskConversation);
  }
  const ordered = applyConversationSort(visibleItems);
  lastConversations = safeItems;
  renderKpiCards(safeItems);
  syncKpiSelection();
  const root = document.getElementById('conversations');
  if (!root) return;
  root.innerHTML = '';

  if (ordered.length === 0) {
    root.innerHTML = `<div class="empty-state">
      <h3>Nenhuma conversa encontrada</h3>
      <p>Tente buscar por número ou nome para localizar mensagens.</p>
    </div>`;
    conversationFocusIndex = -1;
    updateConversationLoadMoreButton();
    return;
  }

  ordered.forEach(item => {
    const profileOverride = getProfileOverride(item.phone);
    const displayName = firstNonEmpty(profileOverride?.nome, item.displayName, item.phone, '-');
    const localUnread = ensureUnreadCounter(item.phone) || { inbound: 0, outbound: 0 };
    const hasInboundUnread = (localUnread.inbound || 0) > 0;
    const hasOutboundUnread = (localUnread.outbound || 0) > 0;
    const unreadBadges = `${hasInboundUnread ? `<span class="unread-badge inbound" title="Novas recebidas">${localUnread.inbound}</span>` : ''}${hasOutboundUnread ? `<span class="unread-badge outbound" title="Novas enviadas">${localUnread.outbound}</span>` : ''}`;
    const legacyUnread = item.isUnread ? '<span class="unread-badge inbound" title="Conversa com pendência de resposta">•</span>' : '';
    const group = conversationGroup(item);
    const finalStatus = conversationFinalStatus(item);
    const subtitle = `${compactConversationStatus(item)} • ${formatDatePtBr(item.lastEventAt)}`;
    const previewText = summarizeConversationPreview(item);
    const displayPhone = firstNonEmpty(item.phone, '-');
    const compactTime = formatConversationTime(item.lastEventAt);
    const priority = conversationPriority(item);
    const avatarInitials = initialsFromName(displayName);
    const el = document.createElement('div');
    el.className = `row row-group-${group} row-priority-${priority.level}` + (item.phone === selectedPhone ? ' active' : '');
    el.dataset.phone = String(item.phone || '');
    el.dataset.legacyUnread = item.isUnread ? 'true' : 'false';
    el.onclick = () => selectConversation(item.phone);
    el.innerHTML = `<div class="row-main">
                      <div class="row-head">
                        <div class="row-title">
                          <span class="row-avatar" title="Contato">${escapeHtml(avatarInitials)}</span>
                          <strong class="truncate-1" title="${escapeHtml(displayName)}">${escapeHtml(displayName)}</strong>
                          <span class="group-chip ${group}" title="Tipo da conversa">${typeLabelByGroup(group)}</span>
                        </div>
                        <div class="row-meta-right">
                          <span class="row-time" title="${formatDatePtBr(item.lastEventAt)}">${escapeHtml(compactTime)}</span>
                          <div class="unread-badges">${legacyUnread}${unreadBadges}</div>
                        </div>
                      </div>
                      <div class="row-preview truncate-1" title="${escapeHtml(previewText)}">${escapeHtml(previewText)}</div>
                      <div class="row-phone truncate-1" title="${escapeHtml(displayPhone)}">${escapeHtml(displayPhone)}</div>
                      <div class='meta truncate-2' title="${escapeHtml(subtitle)}">${escapeHtml(subtitle)}</div>
                      <div class="row-status-line">
                        <span class="delivery-icon delivery-icon-${finalStatus.code || 'pending'}" title="Entrega">${finalStatus.icon || '🕒'}</span>
                        <span class="status-chip ${finalStatus.tone}" title="Status final">${escapeHtml(finalStatus.label)}</span>
                        <span class="priority-chip ${priority.level}" title="Prioridade para backlog" style="display:none;">${priority.icon} ${escapeHtml(priority.label)}</span>
                      </div>
                    </div>`;
    root.appendChild(el);
  });

  const selectedIndex = ordered.findIndex(item => item.phone === selectedPhone);
  conversationFocusIndex = selectedIndex >= 0 ? selectedIndex : (ordered.length > 0 ? 0 : -1);

  updateConversationLoadMoreButton();
}

function compactConversationStatus(item) {
  const status = conversationFinalStatus(item);
  return status?.label || 'Sem status';
}

function summarizeConversationPreview(item) {
  const eventName = String(firstNonEmpty(item?.lastEventName, '')).toUpperCase();
  const rawPreview = firstNonEmpty(item?.lastMessagePreview, item?.lastEventName, 'Sem mensagem');
  if (eventName.includes('CLIENTE') || eventName.includes('WEBHOOK_META_RECEBIDO')) return rawPreview;
  if (eventName.includes('FLOW')) return `Flow: ${rawPreview}`;
  if (eventName.includes('ENVIO') || eventName.includes('SENT')) return `Envio: ${rawPreview}`;
  if (/STATUS|AUDIT|ENDPOINT|META_STATUS/.test(eventName)) return 'Atualização técnica';
  if (/ERRO|FALHA|FAILED/.test(eventName)) return 'Falha no envio';
  return rawPreview;
}

function updateConversationLoadMoreButton() {
  const button = document.getElementById('loadMoreConversationsBtn');
  if (!button) return;
  const state = conversationPaginationByFilter.get(activeConversationFilterKey);
  button.style.display = state && state.hasMore ? 'block' : 'none';
  button.disabled = loadingMoreConversations;
}

async function loadConversations(options = {}) {
  const reset = options.reset !== false;
  if (loadingMoreConversations) return;
  loadingMoreConversations = true;
  document.body.classList.add('conversations-loading');
  if (reset) {
    const root = document.getElementById('conversations');
    if (root) {
      root.innerHTML = `<div class="skeleton-list">
        <div class="skeleton-card"></div>
        <div class="skeleton-card"></div>
        <div class="skeleton-card"></div>
      </div>`;
    }
  }
  const filters = currentConversationFilters();
  const filterKey = conversationFilterKey(filters);
  activeConversationFilterKey = filterKey;
  syncUrlWithFilters();
  const state = ensureConversationState(filterKey);
  if (reset) {
    state.items = [];
    state.nextCursor = null;
    state.hasMore = true;
  }

  const query = conversationQueryFromFilters(filters, state.nextCursor);
  renderActiveFilterChips();

  try {
    const data = await apiGet(`/api/meta/whatsapp/viewer/conversations?${query.toString()}`);
    const incoming = Array.isArray(data.conversations) ? data.conversations : [];
    if (reset) {
      state.items = incoming.slice();
    } else {
      const existingPhones = new Set(state.items.map(item => item.phone));
      for (const item of incoming) {
        if (!existingPhones.has(item.phone)) {
          state.items.push(item);
        }
      }
    }

    state.nextCursor = data.next_cursor || null;
    state.hasMore = !!state.nextCursor;
    renderConversationsList(state.items);
    updateConversationLoadMoreButton();
  } finally {
    loadingMoreConversations = false;
    document.body.classList.remove('conversations-loading');
    updateConversationLoadMoreButton();
  }

  const ordered = lastConversations;
  if (!selectedPhone && ordered.length > 0) {
    await selectConversation(ordered[0].phone);
    return;
  }

}

async function loadMoreConversations() {
  const state = conversationPaginationByFilter.get(activeConversationFilterKey);
  if (!state || !state.hasMore || loadingMoreConversations) return;
  await loadConversations({ reset: false });
}

function bindConversationInfiniteScroll() {
  const leftColumn = document.querySelector('.left');
  if (!leftColumn) return;
  leftColumn.addEventListener('scroll', () => {
    if (loadingMoreConversations) return;
    const threshold = 120;
    const atBottom = leftColumn.scrollTop + leftColumn.clientHeight >= leftColumn.scrollHeight - threshold;
    if (!atBottom) return;
    loadMoreConversations().catch(() => {});
  });
}

function currentFilterState() {
  return {
    version: presetPayloadVersion,
    operator: (document.getElementById('operatorName').value || '').trim(),
    status: document.getElementById('filterStatus').value || '',
    eventType: document.getElementById('filterEventType').value || '',
    operatorFilter: document.getElementById('filterOperator').value || '',
    templateCampaign: document.getElementById('filterTemplateCampaign').value || '',
    errorCode: document.getElementById('filterErrorCode').value || '',
    from: document.getElementById('filterFrom').value || '',
    to: document.getElementById('filterTo').value || '',
    onlyUnread: document.getElementById('onlyUnread').checked,
    onlySlaBreached: document.getElementById('onlySlaBreached').checked,
    hasActiveInteraction: document.getElementById('hasActiveInteraction').checked,
    sorting: {
      field: conversationSort.field || 'last_event_at',
      direction: conversationSort.direction || 'desc'
    },
    quickSortMode,
    activeTab: activeTimelineGroup || 'all',
    viewerMode
  };
}

function conversationPriority(item) {
  const score = Number(item?.priorityScore || 0);
  const tier = String(item?.priorityTier || '').trim().toUpperCase();
  const waiting = Math.max(0, Number(item?.waitingMinutes || 0));
  if (tier === 'P1' || score >= 75) return { level: 'high', icon: '🔴', label: `P1 • ${waiting} min` };
  if (tier === 'P2' || score >= 45) return { level: 'medium', icon: '🟠', label: `P2 • ${waiting} min` };
  return { level: 'low', icon: '🟢', label: `P3 • ${waiting} min` };
}

function matchesFailureStatus(item) {
  const status = firstNonEmpty(item?.conversationStatus, item?.status).toUpperCase();
  return ['FALHA', 'ERRO', 'FAILED', 'RECUSADO'].some(key => status.includes(key));
}

function isSlaRiskConversation(item) {
  return !!item?.hasActiveInteraction
    && !item?.isSlaBreached
    && Number(item?.waitingMinutes || 0) >= slaRiskThresholdMinutes;
}

function kpiTone(value, goals) {
  const numeric = Number(value || 0);
  if (numeric <= goals.goodMax) return 'good';
  if (numeric <= goals.warnMax) return 'warn';
  return 'bad';
}

function setKpiCardState(cardKey, value, tone, formatter = String) {
  const valueNode = document.getElementById(cardKey);
  if (valueNode) valueNode.textContent = formatter(value);
  const card = valueNode?.closest('.kpi-card');
  if (!card) return;
  card.classList.remove('good', 'warn', 'bad');
  card.classList.add(tone);
}

function renderKpiCards(items) {
  const safeItems = Array.isArray(items) ? items : [];
  const backlog = safeItems.filter(item => item?.hasActiveInteraction).length;
  const slaRisk = safeItems.filter(isSlaRiskConversation).length;
  const tma = Number(latestMetricsSnapshot?.averageResponseTimeSeconds || 0);
  const firstResponse = Number(latestMetricsSnapshot?.averageFirstResponseTimeSeconds || 0);
  const failureRatePercent = Number(latestMetricsSnapshot?.sendFailureRate || 0) * 100;

  setKpiCardState('kpiTma', tma, kpiTone(tma, { goodMax: 300, warnMax: 900 }), value => formatDuration(value));
  setKpiCardState('kpiFirstResponse', firstResponse, kpiTone(firstResponse, { goodMax: 120, warnMax: 300 }), value => formatDuration(value));
  setKpiCardState('kpiSendFailureRate', failureRatePercent, kpiTone(failureRatePercent, { goodMax: 2, warnMax: 5 }), value => `${value.toFixed(1)}%`);
  setKpiCardState('kpiBacklog', backlog, kpiTone(backlog, { goodMax: 5, warnMax: 15 }));
  setKpiCardState('kpiSlaRisk', slaRisk, kpiTone(slaRisk, { goodMax: 3, warnMax: 8 }));
}

function syncKpiSelection() {
  const status = String(document.getElementById('filterStatus')?.value || '').trim().toUpperCase();
  const onlySlaBreached = !!document.getElementById('onlySlaBreached')?.checked;
  const hasActiveInteraction = !!document.getElementById('hasActiveInteraction')?.checked;
  document.querySelectorAll('#kpiFilters .kpi-card').forEach(card => card.classList.remove('active'));
  const matchedKey = Object.entries(kpiFilterConfig).find(([, config]) => {
    const configStatus = String(config.status || '').trim().toUpperCase();
    return status === configStatus
      && onlySlaBreached === !!config.onlySlaBreached
      && hasActiveInteraction === !!config.hasActiveInteraction
      && activeLocalConversationFilter === (config.localFilter || '');
  })?.[0];
  if (!matchedKey) return;
  const activeCard = document.querySelector(`#kpiFilters .kpi-card[data-kpi="${matchedKey}"]`);
  if (activeCard) activeCard.classList.add('active');
}

async function applyKpiFilter(kpiKey) {
  const config = kpiFilterConfig[kpiKey];
  if (!config) return;
  document.getElementById('filterStatus').value = config.status || '';
  document.getElementById('onlySlaBreached').checked = !!config.onlySlaBreached;
  document.getElementById('hasActiveInteraction').checked = !!config.hasActiveInteraction;
  activeLocalConversationFilter = config.localFilter || '';
  setQuickSortMode(config.quickSortMode || 'default', false);
  await loadConversations();
}

function setMetricsWindow(minutes) {
  const normalized = [15, 60, 1440].includes(Number(minutes)) ? Number(minutes) : 60;
  metricsWindowMinutes = normalized;
  document.querySelectorAll('#metricsWindowSwitcher [data-window-minutes]').forEach(button => {
    button.classList.toggle('active', Number(button.dataset.windowMinutes) === normalized);
  });
}

function renderActiveFilterChips() {
  const state = currentFilterState();
  const chips = [];
  if (state.status) chips.push(`status: ${state.status}`);
  if (state.eventType) chips.push(`evento: ${state.eventType}`);
  if (state.operatorFilter) chips.push(`operador: ${state.operatorFilter}`);
  if (state.templateCampaign) chips.push(`template/campanha: ${state.templateCampaign}`);
  if (state.errorCode) chips.push(`erro: ${state.errorCode}`);
  if (state.from) chips.push(`de: ${formatDatePtBr(state.from)}`);
  if (state.to) chips.push(`até: ${formatDatePtBr(state.to)}`);
  if (state.onlyUnread) chips.push('somente não lidas');
  if (state.onlySlaBreached) chips.push('SLA estourado');
  if (state.hasActiveInteraction) chips.push('interação ativa');
  if (activeLocalConversationFilter === 'sla_risk') chips.push('SLA em risco');
  if (state.quickSortMode === 'critical') chips.push('ordenação: críticas');
  if (state.quickSortMode === 'oldest_waiting') chips.push('ordenação: sem resposta há mais tempo');
  if (state.quickSortMode === 'recent_failure') chips.push('ordenação: falhas recentes');
  chips.push(`janela métricas: ${metricsWindowMinutes >= 60 ? (metricsWindowMinutes === 1440 ? '24h' : '1h') : '15m'}`);

  const root = document.getElementById('activeFilterChips');
  root.innerHTML = chips.map(chip => `<span class="chip">${escapeHtml(chip)}</span>`).join('');
}

function setViewerMode(mode) {
  const normalized = String(mode || '').toLowerCase();
  viewerMode = (normalized === 'comfortable' || normalized === 'detailed') ? 'comfortable' : 'compact';
  document.body.classList.toggle('mode-compact', viewerMode === 'compact');
  document.body.classList.toggle('mode-comfortable', viewerMode === 'comfortable');
  document.querySelectorAll('.view-mode-switch button[data-mode]').forEach(button => {
    button.classList.toggle('active', button.dataset.mode === viewerMode);
  });
}

function buildViewerModeControls() {
  const operatorInput = document.getElementById('operatorName');
  if (!operatorInput || document.getElementById('viewerModeSwitch')) return;
  const wrapper = document.createElement('div');
  wrapper.id = 'viewerModeSwitch';
  wrapper.className = 'view-mode-switch';
  wrapper.innerHTML = `
    <span class="meta">densidade:</span>
    <button type="button" data-mode="compact">Compacta</button>
    <button type="button" data-mode="comfortable">Confortável</button>`;
  wrapper.querySelectorAll('button[data-mode]').forEach(button => {
    button.addEventListener('click', () => setViewerMode(button.dataset.mode));
  });
  operatorInput.parentElement?.appendChild(wrapper);
  setViewerMode(viewerMode);
}

function formatPercent(value) {
  const number = Number(value || 0) * 100;
  return `${number.toFixed(1)}%`;
}

function formatDuration(seconds) {
  const totalSeconds = Math.max(0, Math.round(Number(seconds || 0)));
  const minutes = Math.floor(totalSeconds / 60);
  const remainingSeconds = totalSeconds % 60;
  if (minutes <= 0) return `${remainingSeconds}s`;
  if (minutes < 60) return `${minutes}m ${remainingSeconds}s`;
  const hours = Math.floor(minutes / 60);
  const remMinutes = minutes % 60;
  return `${hours}h ${remMinutes}m`;
}

function renderMetricsCards(metrics) {
  const root = document.getElementById('metricsCards');
  if (!root) return;
  if (!metrics) {
    root.innerHTML = '';
    return;
  }

  const latestBucket = Array.isArray(metrics.buckets) && metrics.buckets.length > 0
    ? metrics.buckets[metrics.buckets.length - 1]
    : null;
  const bucketLabel = latestBucket
    ? `${formatDatePtBr(latestBucket.windowStart)} até ${formatDatePtBr(latestBucket.windowEnd)}`
    : 'sem dados na janela';

  const cards = [
    { title: 'Total de envios', value: String(metrics.totalSent || 0), tone: 'sent' },
    { title: 'Total de respostas', value: String(metrics.totalResponses || 0), tone: 'responses' },
    { title: 'Ações internas', value: String(metrics.totalInternalActions || 0), tone: 'internal' },
    { title: 'Taxa de falha de envio', value: formatPercent(metrics.sendFailureRate), tone: 'warning' },
    { title: 'Tempo médio envio→resposta', value: formatDuration(metrics.averageResponseTimeSeconds), tone: 'neutral' },
    { title: 'Tempo até 1ª resposta', value: formatDuration(metrics.averageFirstResponseTimeSeconds), tone: 'neutral' },
    { title: 'Resolução por interação', value: formatPercent(metrics.interactionResolutionRate), tone: 'responses' }
  ];

  const failureTop = Array.isArray(metrics.failureRateByTemplateChannel) ? metrics.failureRateByTemplateChannel.slice(0, 3) : [];
  const backlog = Array.isArray(metrics.backlogByAge) ? metrics.backlogByAge : [];
  const hourlyTrend = buildTrendValues(metrics, 'hour');
  const dailyTrend = buildTrendValues(metrics, 'day');

  root.innerHTML = cards.map(card => `
    <div class="metric-card ${card.tone}">
      <div class="metric-title">${escapeHtml(card.title)}</div>
      <div class="metric-value">${escapeHtml(card.value)}</div>
      <div class="metric-subtitle">Última janela: ${escapeHtml(bucketLabel)}</div>
    </div>`).join('') + `
    <div class="metric-card neutral metric-card-wide">
      <div class="metric-title">Tendência (últimas horas)</div>
      ${renderSparkline(hourlyTrend)}
    </div>
    <div class="metric-card neutral metric-card-wide">
      <div class="metric-title">Tendência (últimos dias)</div>
      ${renderSparkline(dailyTrend)}
    </div>
    <div class="metric-card warning metric-card-wide">
      <div class="metric-title">% falha por template/canal</div>
      <div class="metric-subtitle">${renderFailurePreview(failureTop)}</div>
    </div>
    <div class="metric-card internal metric-card-wide">
      <div class="metric-title">Backlog por idade</div>
      <div class="metric-subtitle">${renderBacklogPreview(backlog)}</div>
    </div>`;
}

function buildTrendValues(metrics, mode) {
  const buckets = Array.isArray(metrics?.buckets) ? metrics.buckets : [];
  if (buckets.length === 0) return [];
  if (mode === 'hour') {
    return buckets.slice(-24).map(item => Number(item.totalResponses || 0));
  }

  const grouped = new Map();
  buckets.forEach(item => {
    const key = String(item.windowStart || '').slice(0, 10);
    grouped.set(key, (grouped.get(key) || 0) + Number(item.totalResponses || 0));
  });
  return Array.from(grouped.values()).slice(-7);
}

function renderSparkline(values) {
  if (!Array.isArray(values) || values.length === 0) {
    return '<div class="metric-subtitle">Sem dados suficientes para tendência.</div>';
  }

  const max = Math.max(...values, 1);
  const points = values.map((value, index) => {
    const x = values.length === 1 ? 0 : (index / (values.length - 1)) * 100;
    const y = 100 - ((Number(value) || 0) / max) * 100;
    return `${x.toFixed(2)},${y.toFixed(2)}`;
  }).join(' ');
  return `<svg class="metric-sparkline" viewBox="0 0 100 100" preserveAspectRatio="none"><polyline points="${points}" /></svg>`;
}

function renderFailurePreview(items) {
  if (!Array.isArray(items) || items.length === 0) return 'Sem envios com template/canal no período.';
  return items.map(item => {
    const template = firstNonEmpty(item.template, 'desconhecido');
    const channel = firstNonEmpty(item.channel, 'desconhecido');
    return `${template} (${channel}): ${formatPercent(item.failureRate)}`;
  }).join(' · ');
}

function renderBacklogPreview(items) {
  if (!Array.isArray(items) || items.length === 0) return 'Sem backlog pendente.';
  return items.map(item => `${firstNonEmpty(item.label, '-')}: ${Number(item.count || 0)}`).join(' · ');
}

async function loadMetrics() {
  const status = document.getElementById('filterStatus').value || '';
  const eventType = document.getElementById('filterEventType').value || '';
  const operator = document.getElementById('filterOperator').value || '';
  const templateCampaign = document.getElementById('filterTemplateCampaign').value || '';
  const errorCode = document.getElementById('filterErrorCode').value || '';
  const from = document.getElementById('filterFrom').value || '';
  const to = document.getElementById('filterTo').value || '';
  const query = new URLSearchParams({
    status,
    event_type: eventType,
    operator,
    template_campaign: templateCampaign,
    error_code: errorCode,
    phone: selectedPhone || '',
    from: from ? new Date(from).toISOString() : '',
    to: to ? new Date(to).toISOString() : '',
    window_minutes: String(metricsWindowMinutes)
  });
  const data = await apiGet(`/api/meta/whatsapp/viewer/metrics?${query.toString()}`);
  latestMetricsSnapshot = data.metrics || null;
  renderMetricsCards(latestMetricsSnapshot);
  renderKpiCards(lastConversations || []);
}

async function saveViewPreset() {
  if (isReadOnlyRole()) {
    alert('Perfil somente leitura não pode salvar visão.');
    return;
  }
  const operator = (document.getElementById('operatorName').value || '').trim();
  const user = (document.getElementById('presetUser')?.value || '').trim();
  if (!operator) {
    alert('Informe o operador para salvar a visão.');
    return;
  }

  const state = currentFilterState();
  const payload = {
    version: presetPayloadVersion,
    operator,
    user: user || undefined,
    filters: {
      status: state.status || '',
      event_type: state.eventType || '',
      operator: state.operatorFilter || '',
      template_campaign: state.templateCampaign || '',
      error_code: state.errorCode || '',
      from: state.from || '',
      to: state.to || '',
      only_unread: !!state.onlyUnread,
      only_sla_breached: !!state.onlySlaBreached,
      has_active_interaction: !!state.hasActiveInteraction
    },
    sorting: {
      field: state.sorting.field || 'last_event_at',
      direction: state.sorting.direction || 'desc'
    },
    quick_sort_mode: state.quickSortMode || 'default',
    active_tab: state.activeTab || 'all',
    viewer_mode: state.viewerMode || 'compact'
  };

  localStorage.setItem(presetStorageKey(operator, user), JSON.stringify(state));
  try {
    await apiPost('/api/meta/whatsapp/viewer/presets', payload);
    alert(`Visão salva para ${operator} (API).`);
  } catch {
    alert(`Sem conexão com API. Visão salva localmente para ${operator}.`);
  }
}

function normalizePreset(rawPreset) {
  if (!rawPreset || typeof rawPreset !== 'object') return null;

  const filters = rawPreset.filters && typeof rawPreset.filters === 'object'
    ? rawPreset.filters
    : rawPreset;
  const sorting = rawPreset.sorting && typeof rawPreset.sorting === 'object'
    ? rawPreset.sorting
    : {};
  const activeTab = String(rawPreset.active_tab || rawPreset.activeTab || 'all').toLowerCase();
  const field = String(sorting.field || 'last_event_at').toLowerCase();
  const direction = String(sorting.direction || 'desc').toLowerCase();
  const quickMode = String(rawPreset.quick_sort_mode || rawPreset.quickSortMode || 'default').toLowerCase();
  const allowedTabs = new Set(['all', 'internal', 'outbound', 'inbound']);
  const allowedFields = new Set(['last_event_at', 'display_name', 'phone']);
  const allowedDirections = new Set(['asc', 'desc']);
  const allowedQuickModes = new Set(['default', 'critical', 'oldest_waiting', 'recent_failure']);

  if (!allowedTabs.has(activeTab)) return null;
  if (!allowedFields.has(field) || !allowedDirections.has(direction)) return null;
  if (!allowedQuickModes.has(quickMode)) return null;

  return {
    status: String(filters.status || ''),
    eventType: String(filters.event_type || filters.eventType || ''),
    operatorFilter: String(filters.operator || filters.operatorFilter || ''),
    templateCampaign: String(filters.template_campaign || filters.templateCampaign || ''),
    errorCode: String(filters.error_code || filters.errorCode || ''),
    from: String(filters.from || ''),
    to: String(filters.to || ''),
    onlyUnread: !!(filters.only_unread ?? filters.onlyUnread),
    onlySlaBreached: !!(filters.only_sla_breached ?? filters.onlySlaBreached),
    hasActiveInteraction: !!(filters.has_active_interaction ?? filters.hasActiveInteraction),
    sorting: { field, direction },
    quickSortMode: quickMode,
    activeTab,
    viewerMode: ['comfortable', 'detailed'].includes(String(rawPreset.viewer_mode || rawPreset.viewerMode || 'compact').toLowerCase()) ? 'comfortable' : 'compact'
  };
}

function applyPresetState(preset) {
  document.getElementById('filterStatus').value = preset.status || '';
  document.getElementById('filterEventType').value = preset.eventType || '';
  document.getElementById('filterOperator').value = preset.operatorFilter || '';
  document.getElementById('filterTemplateCampaign').value = preset.templateCampaign || '';
  document.getElementById('filterErrorCode').value = preset.errorCode || '';
  document.getElementById('filterFrom').value = preset.from || '';
  document.getElementById('filterTo').value = preset.to || '';
  document.getElementById('onlyUnread').checked = !!preset.onlyUnread;
  document.getElementById('onlySlaBreached').checked = !!preset.onlySlaBreached;
  document.getElementById('hasActiveInteraction').checked = !!preset.hasActiveInteraction;
  conversationSort = {
    field: preset.sorting?.field || 'last_event_at',
    direction: preset.sorting?.direction || 'desc'
  };
  setQuickSortMode(preset.quickSortMode || 'default', false);
  setTimelineGroup(preset.activeTab || 'all');
  setViewerMode(preset.viewerMode || 'compact');
}

async function applyViewPreset() {
  const operator = (document.getElementById('operatorName').value || '').trim();
  const user = (document.getElementById('presetUser')?.value || '').trim();
  if (!operator) {
    alert('Informe o operador para carregar a visão.');
    return;
  }

  try {
    const presetQuery = new URLSearchParams({ operator });
    if (user) presetQuery.set('user', user);
    const response = await apiGet(`/api/meta/whatsapp/viewer/presets?${presetQuery.toString()}`);
    const preset = normalizePreset(response && response.preset ? response.preset : null);
    if (!preset) {
      throw new Error('preset_invalid');
    }
    applyPresetState(preset);
    localStorage.setItem(presetStorageKey(operator, user), JSON.stringify({
      ...preset,
      version: presetPayloadVersion,
      operator,
      user
    }));
    await loadConversations();
    return;
  } catch {
    const raw = localStorage.getItem(presetStorageKey(operator, user))
      || localStorage.getItem(`${presetStoragePrefix}${operator}`);
    if (!raw) {
      alert('Nenhuma visão salva para este operador.');
      return;
    }

    try {
      const preset = normalizePreset(JSON.parse(raw));
      if (!preset) {
        alert('Preset inválido/incompatível.');
        return;
      }
      applyPresetState(preset);
      await loadConversations();
    } catch {
      alert('Falha ao ler preset local.');
    }
  }
}

function compareByField(a, b, field) {
  if (field === 'display_name') {
    return String(a.displayName || a.phone || '').localeCompare(String(b.displayName || b.phone || ''), 'pt-BR');
  }
  if (field === 'phone') {
    return String(a.phone || '').localeCompare(String(b.phone || ''), 'pt-BR');
  }
  const da = new Date(a.lastEventAt || 0).getTime();
  const db = new Date(b.lastEventAt || 0).getTime();
  return da - db;
}

function applyConversationSort(items) {
  const field = String(conversationSort?.field || 'last_event_at').toLowerCase();
  const direction = String(conversationSort?.direction || 'desc').toLowerCase();
  const multiplier = direction === 'asc' ? 1 : -1;
  return items.sort((a, b) => {
    const pinDiff = Number(isPinnedConversation(b.phone)) - Number(isPinnedConversation(a.phone));
    if (pinDiff !== 0) return pinDiff;
    if (quickSortMode === 'critical') {
      const priorityDiff = Number(b?.priorityScore || 0) - Number(a?.priorityScore || 0);
      if (priorityDiff !== 0) return priorityDiff;
    } else if (quickSortMode === 'oldest_waiting') {
      const waitingDiff = Number(b?.waitingMinutes || 0) - Number(a?.waitingMinutes || 0);
      if (waitingDiff !== 0) return waitingDiff;
    } else if (quickSortMode === 'recent_failure') {
      const failureDiff = Number(!!b?.hasRecentFailure) - Number(!!a?.hasRecentFailure);
      if (failureDiff !== 0) return failureDiff;
    }
    return compareByField(a, b, field) * multiplier;
  });
}

function setQuickSortMode(mode, reload = true) {
  const normalized = new Set(['default', 'critical', 'oldest_waiting', 'recent_failure']).has(mode) ? mode : 'default';
  quickSortMode = normalized;
  document.querySelectorAll('[data-sort-mode]').forEach(button => {
    button.classList.toggle('active', button.dataset.sortMode === quickSortMode);
  });
  if (reload) {
    loadConversations().catch(() => {});
  } else {
    renderConversationsList(lastConversations || []);
    renderActiveFilterChips();
  }
}

function scheduleConversationReload() {
  if (filtersDebounceHandle) {
    clearTimeout(filtersDebounceHandle);
  }
  filtersDebounceHandle = setTimeout(() => {
    filtersDebounceHandle = null;
    syncUrlWithFilters();
    loadConversations().catch(() => {});
  }, filterDebounceMs);
}

function bindFilterListeners() {
  const debouncedInputIds = ['search', 'filterStatus', 'filterEventType', 'filterOperator', 'filterTemplateCampaign', 'filterErrorCode'];
  debouncedInputIds.forEach(id => {
    const input = document.getElementById(id);
    if (!input) return;
    input.addEventListener('input', scheduleConversationReload);
  });

  const debouncedChangeIds = ['limit', 'filterFrom', 'filterTo', 'onlyUnread', 'onlySlaBreached', 'hasActiveInteraction'];
  debouncedChangeIds.forEach(id => {
    const input = document.getElementById(id);
    if (!input) return;
    input.addEventListener('change', scheduleConversationReload);
  });
}

function buildShareUrlFromCurrentFilters() {
  const state = currentFilterState();
  const params = new URLSearchParams();
  const maybeSet = (key, value) => {
    const text = String(value || '').trim();
    if (text) params.set(key, text);
  };
  maybeSet('search', document.getElementById('search')?.value);
  maybeSet('limit', document.getElementById('limit')?.value);
  maybeSet('status', state.status);
  maybeSet('event_type', state.eventType);
  maybeSet('operator', state.operatorFilter);
  maybeSet('template_campaign', state.templateCampaign);
  maybeSet('error_code', state.errorCode);
  maybeSet('from', state.from);
  maybeSet('to', state.to);
  if (state.onlyUnread) params.set('only_unread', '1');
  if (state.onlySlaBreached) params.set('only_sla_breached', '1');
  if (state.hasActiveInteraction) params.set('has_active_interaction', '1');
  maybeSet('quick_sort_mode', state.quickSortMode);
  maybeSet('active_tab', state.activeTab);
  maybeSet('viewer_mode', state.viewerMode);
  return `${window.location.origin}${window.location.pathname}${params.toString() ? `?${params.toString()}` : ''}`;
}

function syncUrlWithFilters() {
  window.history.replaceState({}, '', buildShareUrlFromCurrentFilters());
}

async function shareCurrentFilters() {
  const url = buildShareUrlFromCurrentFilters();
  try {
    await navigator.clipboard.writeText(url);
    alert('URL dos filtros copiada.');
  } catch {
    window.prompt('Copie a URL dos filtros:', url);
  }
}

function applyFiltersFromUrl() {
  const params = new URLSearchParams(window.location.search || '');
  const setValue = (id, key, fallback = '') => {
    const node = document.getElementById(id);
    if (!node) return;
    node.value = params.get(key) || fallback;
  };
  setValue('search', 'search');
  setValue('limit', 'limit', '50');
  setValue('filterStatus', 'status');
  setValue('filterEventType', 'event_type');
  setValue('filterOperator', 'operator');
  setValue('filterTemplateCampaign', 'template_campaign');
  setValue('filterErrorCode', 'error_code');
  setValue('filterFrom', 'from');
  setValue('filterTo', 'to');
  document.getElementById('onlyUnread').checked = params.get('only_unread') === '1';
  document.getElementById('onlySlaBreached').checked = params.get('only_sla_breached') === '1';
  document.getElementById('hasActiveInteraction').checked = params.get('has_active_interaction') === '1';
  setQuickSortMode(params.get('quick_sort_mode') || 'default', false);
  setTimelineGroup(params.get('active_tab') || 'all');
  setViewerMode(params.get('viewer_mode') || viewerMode);
}

function savedQueriesStorageKey() {
  return `${savedQueryStoragePrefix}${(document.getElementById('operatorName')?.value || 'global').trim().toLowerCase() || 'global'}`;
}

function listSavedQueries() {
  try {
    const parsed = JSON.parse(localStorage.getItem(savedQueriesStorageKey()) || '[]');
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

function refreshSavedQueriesSelect() {
  const select = document.getElementById('savedQuerySelect');
  if (!select) return;
  const items = listSavedQueries();
  select.innerHTML = '<option value="">consultas salvas...</option>'
    + items.map(item => `<option value="${escapeHtml(item.name)}">${escapeHtml(item.name)}</option>`).join('');
}

function saveQuickQuery() {
  const queryName = String(window.prompt('Nome da consulta salva:') || '').trim();
  if (!queryName) return;
  const all = listSavedQueries().filter(item => item.name !== queryName);
  all.unshift({ name: queryName, state: currentFilterState() });
  localStorage.setItem(savedQueriesStorageKey(), JSON.stringify(all.slice(0, 20)));
  refreshSavedQueriesSelect();
}

async function loadSavedQuery() {
  const selectedName = String(document.getElementById('savedQuerySelect')?.value || '').trim();
  if (!selectedName) return;
  const query = listSavedQueries().find(item => item.name === selectedName);
  if (!query || !query.state) return;
  applyPresetState(query.state);
  syncUrlWithFilters();
  await loadConversations();
}

function removeSavedQuery() {
  const selectedName = String(document.getElementById('savedQuerySelect')?.value || '').trim();
  if (!selectedName) return;
  const all = listSavedQueries().filter(item => item.name !== selectedName);
  localStorage.setItem(savedQueriesStorageKey(), JSON.stringify(all));
  refreshSavedQueriesSelect();
}

async function resetAllFilters() {
  document.getElementById('search').value = '';
  document.getElementById('limit').value = '50';
  applyPresetState({
    status: '',
    eventType: '',
    operatorFilter: '',
    templateCampaign: '',
    errorCode: '',
    from: '',
    to: '',
    onlyUnread: false,
    onlySlaBreached: false,
    hasActiveInteraction: false,
    sorting: { field: 'last_event_at', direction: 'desc' },
    quickSortMode: 'default',
    activeTab: 'all',
    viewerMode: 'compact'
  });
  syncUrlWithFilters();
  await loadConversations();
}

function bindTimelineFilterListeners() {
  let handle = null;
  const schedule = () => {
    if (handle) clearTimeout(handle);
    handle = setTimeout(() => {
      handle = null;
      timelineSearchFilters.text = (document.getElementById('timelineFilterText')?.value || '').trim();
      timelineSearchFilters.status = (document.getElementById('timelineFilterStatus')?.value || '').trim();
      timelineSearchFilters.interactionId = (document.getElementById('timelineFilterInteractionId')?.value || '').trim();
      timelineSearchFilters.from = (document.getElementById('timelineFilterFrom')?.value || '').trim();
      timelineSearchFilters.to = (document.getElementById('timelineFilterTo')?.value || '').trim();
      timelineSearchFilters.useServer = !!document.getElementById('timelineServerSearch')?.checked;
      applyTimelineSearch(true).catch(() => {});
    }, 220);
  };

  ['timelineFilterText', 'timelineFilterStatus', 'timelineFilterInteractionId', 'timelineFilterFrom', 'timelineFilterTo', 'timelineServerSearch']
    .forEach(id => {
      const element = document.getElementById(id);
      if (!element) return;
      element.addEventListener(id === 'timelineServerSearch' ? 'change' : 'input', schedule);
    });
}

function moveConversationFocus(delta) {
  const rows = Array.from(document.querySelectorAll('#conversations .row'));
  if (rows.length === 0) return;
  if (conversationFocusIndex < 0) conversationFocusIndex = 0;
  conversationFocusIndex = (conversationFocusIndex + delta + rows.length) % rows.length;
  rows.forEach((row, idx) => row.classList.toggle('row-focused', idx === conversationFocusIndex));
  rows[conversationFocusIndex]?.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
}

function openFocusedConversation() {
  const rows = Array.from(document.querySelectorAll('#conversations .row'));
  if (rows.length === 0) return;
  if (conversationFocusIndex < 0) conversationFocusIndex = 0;
  const row = rows[conversationFocusIndex];
  const phone = String(row?.dataset?.phone || '').trim();
  if (!phone) return;
  selectConversation(phone).catch(() => {});
}

function touchConversationCache(phone, payload) {
  const key = String(phone || '').trim();
  if (!key || !payload) return;
  conversationRecentCache.set(key, { ...payload, cachedAt: Date.now() });
  const keys = Array.from(conversationRecentCache.keys());
  if (keys.length > conversationRecentCacheMaxEntries) {
    conversationRecentCache.delete(keys[0]);
  }
}

function readConversationCache(phone) {
  const key = String(phone || '').trim();
  if (!key || !conversationRecentCache.has(key)) return null;
  const cached = conversationRecentCache.get(key);
  if (!cached || (Date.now() - (cached.cachedAt || 0)) > timelineCacheTtlMs) {
    conversationRecentCache.delete(key);
    return null;
  }
  conversationRecentCache.delete(key);
  conversationRecentCache.set(key, cached);
  return cached;
}

async function prefetchConversationPage(phone, cursor) {
  const key = String(phone || '').trim();
  if (!key || !cursor) return;
  if (timelinePrefetchByPhone.has(key)) return;
  const job = (async () => {
    const query = new URLSearchParams({ page_size: String(conversationPageSize), cursor: String(cursor) });
    const data = await apiGet(`/api/meta/whatsapp/viewer/conversations/${encodeURIComponent(key)}?${query.toString()}`);
    return {
      cursor,
      events: normalizeEventsPayload(data?.events),
      nextCursor: data?.next_cursor || null
    };
  })();

  timelinePrefetchByPhone.set(key, job);
  try {
    await job;
  } catch {
    // noop
  } finally {
    timelinePrefetchByPhone.delete(key);
  }
}

function bindKeyboardShortcuts() {
  document.addEventListener('keydown', event => {
    const target = event.target;
    const isEditable = target && (
      target.tagName === 'INPUT'
      || target.tagName === 'TEXTAREA'
      || target.tagName === 'SELECT'
      || target.isContentEditable
    );

    if (event.key === '/' && !isEditable) {
      event.preventDefault();
      document.getElementById('search')?.focus();
      return;
    }

    if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') {
      if (document.activeElement === document.getElementById('manualText')) {
        event.preventDefault();
        sendManual().catch(() => {});
      }
      return;
    }

    if (isEditable) return;

    if (event.key === 'j' || event.key === 'J') {
      event.preventDefault();
      moveConversationFocus(1);
      return;
    }
    if (event.key === 'k' || event.key === 'K') {
      event.preventDefault();
      moveConversationFocus(-1);
      return;
    }
    if (event.key === 'Enter') {
      event.preventDefault();
      openFocusedConversation();
    }
  });
}

async function selectConversation(phone) {
  selectedPhone = phone;
  clearUnread(phone);
  dismissNotificationsForPhone(phone);
  markConversationAsActive(phone);
  lastEventFingerprints = [];
  timelineEvents = [];
  filteredTimelineEvents = [];
  timelineSearchServerCursor = null;
  selectedTimelineFingerprint = '';
  nextHistoryCursor = null;
  timelineVirtualRenderSignature = '';
  viewerStreamCursor = null;
  viewerLastEventId = 0;
  renderConversationHeader(phone);
  const activeConversation = (lastConversations || []).find(item => String(item.phone || '') === String(phone || ''));
  const titleLabel = firstNonEmpty(activeConversation?.displayName, phone, '-');
  document.getElementById('title').textContent = titleLabel;
  restoreManualDraft();
  const cached = readConversationCache(phone);
  if (cached) {
    timelineEvents = normalizeEventsPayload(cached.events || []);
    nextHistoryCursor = cached.nextCursor || null;
    lastEventFingerprints = timelineEvents.map(fingerprint);
    applyTimelineLocalFilters();
    refreshTimelineView({ stickBottom: true });
    renderProfile(timelineEvents, selectedPhone);
    renderOperationalSummary(timelineEvents, selectedPhone);
  }
  try {
    await loadConversationHistoryPage({ reset: true, notifyNew: false });
    setConnectionState('live');
  } catch {
    setConnectionState('offline');
    showNotification('Não foi possível carregar essa conversa agora. Tente novamente.', 'outbound');
  }
}

async function refreshSelectedConversation(notifyNew = true) {
  await loadConversationHistoryPage({ reset: true, notifyNew });
}

async function loadConversationHistoryPage(options = {}) {
  if (!selectedPhone) return;
  const reset = !!options.reset;
  const notifyNew = options.notifyNew !== false;
  const requestedCursor = reset ? null : nextHistoryCursor;
  if (!reset && !requestedCursor) return;
  if (reset) {
    const timelineRoot = document.getElementById('timeline');
    if (timelineRoot) {
      timelineRoot.innerHTML = `<div class="skeleton-list">
        <div class="skeleton-card"></div>
        <div class="skeleton-card"></div>
      </div>`;
    }
  }

  let data = null;
  if (!reset && requestedCursor) {
    const prefetchPromise = timelinePrefetchByPhone.get(selectedPhone);
    const prefetched = prefetchPromise ? await prefetchPromise.catch(() => null) : null;
    if (prefetched && prefetched.cursor === requestedCursor) {
      data = { events: prefetched.events || [], next_cursor: prefetched.nextCursor || null };
    }
  }

  if (!data) {
    const query = new URLSearchParams({ page_size: String(conversationPageSize) });
    if (requestedCursor) query.set('cursor', requestedCursor);
    data = await apiGet(`/api/meta/whatsapp/viewer/conversations/${encodeURIComponent(selectedPhone)}?${query.toString()}`);
  }

  const events = normalizeEventsPayload(data.events);
  if (reset) {
    timelineEvents = events.slice();
    applyTimelineLocalFilters();
    refreshTimelineView({ stickBottom: true });
    renderProfile(timelineEvents, selectedPhone);
    renderOperationalSummary(timelineEvents, selectedPhone);
    renderConversationHeader(selectedPhone);

    const nextFingerprints = timelineEvents.map(fingerprint);
    if (notifyNew && lastEventFingerprints.length > 0) {
      const known = new Set(lastEventFingerprints);
      const fresh = timelineEvents.filter(evt => !known.has(fingerprint(evt)));
      fresh.filter(evt => isMainEvent(evt, 'all')).forEach(notifyEvent);
    }
    lastEventFingerprints = nextFingerprints;
  } else {
    const known = new Set(lastEventFingerprints);
    const incomingOlder = events.filter(evt => !known.has(fingerprint(evt)));
    if (incomingOlder.length > 0) {
      timelineEvents = stableSortTimeline(incomingOlder.concat(timelineEvents));
      lastEventFingerprints = timelineEvents.map(fingerprint);
      await applyTimelineSearch(true);
      renderProfile(timelineEvents, selectedPhone);
      renderOperationalSummary(timelineEvents, selectedPhone);
      renderConversationHeader(selectedPhone);
    }
  }

  nextHistoryCursor = data.next_cursor || null;
  touchConversationCache(selectedPhone, { events: timelineEvents.slice(), nextCursor: nextHistoryCursor });
  if (nextHistoryCursor) {
    prefetchConversationPage(selectedPhone, nextHistoryCursor).catch(() => {});
  }
  updateLoadMoreButton();
}

async function loadMoreHistory() {
  if (!selectedPhone || !nextHistoryCursor) return;
  await loadConversationHistoryPage({ reset: false, notifyNew: false });
}

async function fullResync() {
  await performFullResync('manual');
  stopLiveUpdates();
  startLiveUpdates();
}

async function exportConversation(format) {
  if (!selectedPhone) {
    alert('Selecione uma conversa.');
    return;
  }

  const normalizedFormat = (format || '').toLowerCase();
  if (normalizedFormat !== 'json' && normalizedFormat !== 'csv') {
    alert('Formato inválido.');
    return;
  }

  setExportUiState(true, 'Gerando arquivo...');
  try {
    const query = new URLSearchParams({ format: normalizedFormat });
    if (timelineSearchFilters.from) query.set('from_event_time', timelineSearchFilters.from);
    if (timelineSearchFilters.to) query.set('to', timelineSearchFilters.to);

    const createRes = await fetch(`/api/meta/whatsapp/viewer/conversations/${encodeURIComponent(selectedPhone)}/export-jobs?${query.toString()}`, {
      method: 'POST',
      headers: headers()
    });
    if (createRes.status === 401) {
      lockSession();
      alert('Sessão expirada.');
      return;
    }
    const createPayload = await createRes.json().catch(() => ({}));
    if (!createRes.ok) {
      alert(createPayload.detail || `Falha ao iniciar exportação (${createRes.status}).`);
      return;
    }

    const jobId = createPayload.job_id;
    if (!jobId) {
      alert('Falha ao iniciar exportação: job inválido.');
      return;
    }

    const maxPollAttempts = 60;
    for (let attempt = 0; attempt < maxPollAttempts; attempt += 1) {
      if (attempt > 0) {
        setExportUiState(true, `Gerando arquivo... (${attempt * 2}s)`);
        await delay(2000);
      }

      const statusRes = await fetch(`/api/meta/whatsapp/viewer/conversations/${encodeURIComponent(selectedPhone)}/export-jobs/${encodeURIComponent(jobId)}`, {
        headers: headers()
      });
      if (statusRes.status === 401) {
        lockSession();
        alert('Sessão expirada.');
        return;
      }

      const statusPayload = await statusRes.json().catch(() => ({}));
      if (!statusRes.ok) {
        alert(statusPayload.detail || `Falha ao consultar status (${statusRes.status}).`);
        return;
      }

      if (statusPayload.status === 'failed') {
        alert(statusPayload.detail || 'Falha ao gerar arquivo de exportação.');
        return;
      }

      if (statusPayload.status === 'completed' && statusPayload.download_url) {
        window.location.href = statusPayload.download_url;
        setExportUiState(false, '');
        return;
      }
    }

    alert('Exportação ainda em processamento. Tente novamente em instantes.');
  } finally {
    setExportUiState(false, '');
  }
}

function setExportUiState(loading, message) {
  const text = document.getElementById('exportStatusText');
  const jsonBtn = document.getElementById('exportJsonBtn');
  const csvBtn = document.getElementById('exportCsvBtn');
  if (text) text.textContent = message || '';
  if (jsonBtn) jsonBtn.disabled = !!loading;
  if (csvBtn) csvBtn.disabled = !!loading;
}

function delay(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

function setConnectionState(state, detail = '') {
  const label = document.getElementById('connectionStateLabel');
  const root = document.getElementById('connectionState');
  if (!label || !root) return;
  const normalized = String(state || 'offline').toLowerCase();
  root.dataset.state = normalized;
  if (normalized === 'live') {
    label.textContent = 'Ao vivo';
    return;
  }
  if (normalized === 'reconnecting') {
    label.textContent = 'Reconectando';
    return;
  }
  label.textContent = 'Offline';
}

async function performFullResync(reason = 'gap') {
  viewerStreamCursor = null;
  viewerLastEventId = 0;
  streamReconnectAttempt = 0;
  setConnectionState('reconnecting');
  if (selectedPhone) {
    await refreshSelectedConversation(false).catch(() => {});
  }
  await loadConversations().catch(() => {});
}

function stopLiveUpdates() {
  if (streamReconnectHandle) {
    clearTimeout(streamReconnectHandle);
    streamReconnectHandle = null;
  }
  if (streamEventSource) {
    streamEventSource.close();
    streamEventSource = null;
  }
  if (pollingFallbackHandle) {
    clearTimeout(pollingFallbackHandle);
    pollingFallbackHandle = null;
  }
  pollingFallbackInFlight = false;
  pollingFallbackBackoffMs = 0;
  setConnectionState('offline');
}

function startPollingFallback() {
  if (pollingFallbackHandle || pollingFallbackInFlight) return;
  const scheduleNext = () => {
    const delay = Math.max(pollingBaseIntervalMs, pollingFallbackBackoffMs || 0);
    pollingFallbackHandle = setTimeout(() => {
      pollingFallbackHandle = null;
      pollOnce().catch(() => {});
    }, delay);
  };

  const pollOnce = async () => {
    if (pollingFallbackInFlight) {
      scheduleNext();
      return;
    }
    if (document.hidden) {
      scheduleNext();
      return;
    }

    pollingFallbackInFlight = true;
    let shouldContinueImmediately = false;
    try {
      let receivedEvents = [];
      let hasMore = false;
      for (let page = 0; page < streamMaxPagesPerPoll; page += 1) {
        const query = new URLSearchParams({
          page_size: String(streamPageSize)
        });
        if (viewerStreamCursor) query.set('cursor', viewerStreamCursor);
        const payload = await apiGet(`/api/meta/whatsapp/viewer/stream?${query.toString()}`);
        viewerStreamCursor = payload.cursor || viewerStreamCursor;
        const pageEvents = normalizeEventsPayload(payload.events);
        if (pageEvents.length > 0) {
          receivedEvents = receivedEvents.concat(pageEvents);
        }
        hasMore = !!payload.has_more;
        if (!hasMore) break;
      }

      pollingFallbackBackoffMs = 0;
      setConnectionState('live', 'fallback');
      if (receivedEvents.length > 0) {
        receivedEvents.forEach(evt => incrementUnread(evt.phone, evt));
        receivedEvents
          .filter(evt => isMainEvent(evt, 'all'))
          .forEach(evt => notifyEvent(evt));
        if (selectedPhone) clearUnread(selectedPhone);
        const selectedEvents = selectedPhone
          ? receivedEvents.filter(evt => String(evt?.phone || '').trim() === String(selectedPhone || '').trim())
          : [];
        if (selectedEvents.length > 0) {
          appendEventsIncrementally(selectedEvents, false);
        }
        loadConversations().catch(() => {});
      }

      shouldContinueImmediately = hasMore;
    } catch {
      pollingFallbackBackoffMs = pollingFallbackBackoffMs <= 0
        ? pollingErrorInitialBackoffMs
        : Math.min(pollingFallbackBackoffMs * 2, pollingErrorMaxBackoffMs);
      setConnectionState('reconnecting', 'fallback');
      refreshSelectedConversation(true).catch(() => {});
    } finally {
      pollingFallbackInFlight = false;
      if (shouldContinueImmediately) {
        pollingFallbackHandle = setTimeout(() => {
          pollingFallbackHandle = null;
          pollOnce().catch(() => {});
        }, 0);
      } else {
        scheduleNext();
      }
    }
  };

  pollOnce().catch(() => {});
}

function processStreamPayload(payload) {
  if (!payload || typeof payload !== 'object') return;
  if (payload.cursor) viewerStreamCursor = payload.cursor;
  if (payload.last_event_id) viewerLastEventId = Number(payload.last_event_id) || viewerLastEventId;

  if (payload.gap_detected || payload.reset_detected) {
    performFullResync('gap').catch(() => {});
    return;
  }

  const receivedEvents = normalizeEventsPayload(payload.events);
  if (receivedEvents.length === 0) return;
  receivedEvents.forEach(evt => incrementUnread(evt.phone, evt));
  receivedEvents
    .filter(evt => isMainEvent(evt, 'all'))
    .forEach(evt => notifyEvent(evt));
  if (selectedPhone) clearUnread(selectedPhone);
  const selectedEvents = selectedPhone
    ? receivedEvents.filter(evt => String(evt?.phone || '').trim() === String(selectedPhone || '').trim())
    : [];
  if (selectedEvents.length > 0) {
    appendEventsIncrementally(selectedEvents, false);
  }
  loadConversations().catch(() => {});
}

function scheduleSseReconnect() {
  if (streamReconnectHandle) return;
  const delayMs = Math.min(sseInitialBackoffMs * (2 ** streamReconnectAttempt), sseMaxBackoffMs);
  setConnectionState('reconnecting');
  streamReconnectHandle = setTimeout(() => {
    streamReconnectHandle = null;
    streamReconnectAttempt += 1;
    connectLiveStream();
  }, delayMs);
}

function connectLiveStream() {
  if (typeof EventSource === 'undefined') {
    startPollingFallback();
    return;
  }

  if (streamEventSource) {
    streamEventSource.close();
  }
  const query = new URLSearchParams({ page_size: String(streamPageSize) });
  if (viewerStreamCursor) query.set('cursor', viewerStreamCursor);
  if (viewerLastEventId > 0) query.set('last_event_id', String(viewerLastEventId));

  const source = new EventSource(`/api/meta/whatsapp/viewer/stream/sse?${query.toString()}`);
  streamEventSource = source;
  source.onopen = () => {
    streamReconnectAttempt = 0;
    setConnectionState('live');
  };
  source.onerror = () => {
    if (streamEventSource) {
      streamEventSource.close();
      streamEventSource = null;
    }
    scheduleSseReconnect();
  };
  source.addEventListener('delta', event => {
    const serverEventId = Number(event?.lastEventId || 0);
    if (serverEventId > 0) {
      viewerLastEventId = Math.max(viewerLastEventId, serverEventId);
    }
    try {
      const payload = JSON.parse(event.data || '{}');
      processStreamPayload(payload);
    } catch {
      setConnectionState('reconnecting', 'payload inválido');
      scheduleSseReconnect();
    }
  });
}

function startLiveUpdates() {
  setConnectionState('reconnecting', 'iniciando');
  connectLiveStream();
}

document.addEventListener('visibilitychange', () => {
  if (!document.hidden) {
    if (selectedPhone) {
      refreshSelectedConversation(true).catch(() => {});
    }
    loadConversations().catch(() => {});
  }
});


function draftStorageKey(phone) {
  return `${manualDraftStoragePrefix}${String(phone || '').trim()}`;
}

function setSendFeedback(kind, message) {
  const root = document.getElementById('sendFeedback');
  if (!root) return;
  root.className = 'send-feedback';
  if (!message) {
    root.textContent = '';
    return;
  }
  if (kind) root.classList.add(kind);
  root.textContent = message;
}

function updateManualCounter() {
  const input = document.getElementById('manualText');
  const counter = document.getElementById('manualCharCounter');
  if (!input || !counter) return;
  const len = (input.value || '').length;
  counter.textContent = `${len}/${manualMaxChars}`;
  counter.classList.toggle('warning', len >= manualMaxChars * 0.85 && len < manualMaxChars);
  counter.classList.toggle('error', len >= manualMaxChars);
}

function persistManualDraft() {
  if (!selectedPhone) return;
  const input = document.getElementById('manualText');
  if (!input) return;
  const key = draftStorageKey(selectedPhone);
  const value = String(input.value || '');
  if (value.trim()) localStorage.setItem(key, value);
  else localStorage.removeItem(key);
}

function restoreManualDraft() {
  const input = document.getElementById('manualText');
  if (!input) return;
  if (!selectedPhone) {
    input.value = '';
    updateManualCounter();
    return;
  }
  input.value = localStorage.getItem(draftStorageKey(selectedPhone)) || '';
  updateManualCounter();
}

function clearManualDraft(silent = false) {
  const input = document.getElementById('manualText');
  if (selectedPhone) localStorage.removeItem(draftStorageKey(selectedPhone));
  if (input) input.value = '';
  updateManualCounter();
  if (!silent) setSendFeedback('', 'Rascunho limpo.');
}

function isOutsideSendPolicy(text) {
  const content = String(text || '').trim().toLowerCase();
  if (!content) return { blocked: false, reason: '' };
  if (content.length > manualMaxChars) {
    return { blocked: true, reason: `Mensagem excede ${manualMaxChars} caracteres.` };
  }
  if (outsidePolicyKeywords.some(keyword => content.includes(keyword))) {
    return { blocked: true, reason: 'Texto contém dados sensíveis (ex.: CPF/senha/token).' };
  }
  return { blocked: false, reason: '' };
}

function requiresSensitiveConfirmation(text) {
  const content = String(text || '').toLowerCase();
  return /(cancelar|encerrar|excluir|bloquear|urgente|juridico|jurídico)/.test(content);
}

function ensureManualRequiredFields(text) {
  if (!selectedPhone) return 'Selecione uma conversa.';
  if (!text || !text.trim()) return 'Digite uma mensagem antes de enviar.';
  return '';
}

function initializeQuickSnippets() {
  const select = document.getElementById('quickSnippetSelect');
  if (!select) return;
  select.innerHTML = ['<option value="">Snippets rápidos...</option>']
    .concat(quickSnippets.map((snippet, idx) => `<option value="${idx}">${escapeHtml(snippet.slice(0, 70))}</option>`))
    .join('');
}

function insertSelectedSnippet() {
  const select = document.getElementById('quickSnippetSelect');
  const input = document.getElementById('manualText');
  if (!select || !input) return;
  const index = Number(select.value);
  if (!Number.isInteger(index) || !quickSnippets[index]) {
    setSendFeedback('error', 'Selecione um snippet válido.');
    return;
  }
  const prefix = input.value && !input.value.endsWith('\n') ? '\n' : '';
  input.value = `${input.value || ''}${prefix}${quickSnippets[index]}`.trimStart();
  updateManualCounter();
  persistManualDraft();
  input.focus();
  setSendFeedback('', 'Snippet inserido no rascunho.');
}

function bindComposerEvents() {
  const input = document.getElementById('manualText');
  if (!input) return;
  input.addEventListener('input', () => {
    updateManualCounter();
    persistManualDraft();
    setSendFeedback('', '');
  });
  input.addEventListener('keydown', event => {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      sendManual().catch(() => {});
    }
  });
}

async function sendManual() {
  if (isReadOnlyRole()) { alert('Perfil somente leitura não pode enviar mensagens.'); return; }
  const text = document.getElementById('manualText')?.value || '';
  const requiredValidation = ensureManualRequiredFields(text);
  if (requiredValidation) {
    setSendFeedback('error', requiredValidation);
    alert(requiredValidation);
    return;
  }

  const policy = isOutsideSendPolicy(text);
  if (policy.blocked) {
    const warning = `Envio fora de política: ${policy.reason}`;
    setSendFeedback('error', warning);
    alert(warning);
    return;
  }

  if (requiresSensitiveConfirmation(text)) {
    const confirmation = window.prompt('Envio sensível detectado. Digite ENVIAR para confirmar.');
    if (confirmation !== 'ENVIAR') {
      setSendFeedback('error', 'Envio cancelado: confirmação sensível inválida.');
      return;
    }
  }

  const sendButton = document.getElementById('sendManualBtn');
  if (sendButton) sendButton.disabled = true;
  setSendFeedback('loading', 'Enviando mensagem...');

  try {
    const result = await sendTextToPhone(selectedPhone, text.trim());
    if (result?.success) {
      setSendFeedback('success', 'Mensagem enviada com sucesso.');
      clearManualDraft(true);
      await selectConversation(selectedPhone);
      return;
    }

    const reason = result?.error || result?.detail || 'erro desconhecido';
    setSendFeedback('error', `Falha no envio: ${reason}`);
    alert(`Falha no envio: ${reason}`);
  } catch (error) {
    const reason = error?.message || 'erro de rede';
    setSendFeedback('error', `Erro ao enviar: ${reason}`);
    alert(`Erro ao enviar: ${reason}`);
  } finally {
    if (sendButton && !isReadOnlyRole()) sendButton.disabled = false;
  }
}

async function sendTextToPhone(phone, text) {
  const res = await fetch('/api/meta/whatsapp/viewer/send-text', {
    method: 'POST',
    headers: headers(),
    body: JSON.stringify({ phone, text })
  });
  return res.json();
}

async function resendFailedMessage(phone, rawText) {
  if (isReadOnlyRole()) { alert('Perfil somente leitura não pode reenviar mensagens.'); return; }
  const targetPhone = firstNonEmpty(phone, selectedPhone);
  if (!targetPhone) { alert('Telefone da conversa não identificado.'); return; }
  const text = firstNonEmpty(rawText).trim();
  if (!text) { alert('Mensagem original não encontrada para reenvio.'); return; }
  const result = await sendTextToPhone(targetPhone, text);
  alert(result.success ? 'Mensagem reenviada.' : ('Falha no reenvio: ' + (result.error || 'erro')));
  if (selectedPhone === targetPhone) await selectConversation(targetPhone);
}

function bindTimelineActions() {
  const timeline = document.getElementById('timeline');
  if (!timeline) return;
  timeline.addEventListener('click', event => {
    const button = event.target && typeof event.target.closest === 'function'
      ? event.target.closest('.timeline-resend-action')
      : null;
    if (!button) return;
    event.preventDefault();
    event.stopPropagation();
    const rawText = decodeURIComponent(button.dataset.text || '');
    resendFailedMessage(button.dataset.phone || selectedPhone, rawText).catch(() => {
      alert('Não foi possível reenviar a mensagem com falha.');
    });
    return;
  });
  timeline.addEventListener('click', event => {
    const eventNode = event.target && typeof event.target.closest === 'function'
      ? event.target.closest('.event[data-event-fingerprint]')
      : null;
    if (!eventNode) return;
    const evt = findTimelineEventByFingerprint(eventNode.dataset.eventFingerprint || '');
    if (!evt) return;
    setSelectedTimelineEvent(evt);
  });
}

async function closePhone() {
  if (isReadOnlyRole()) { alert('Perfil somente leitura não pode encerrar interações.'); return; }
  if (!selectedPhone) { alert('Selecione uma conversa.'); return; }
  const reasonCode = (document.getElementById('closeReasonCode')?.value || '').trim();
  if (!reasonCode) {
    alert('Selecione um motivo padronizado para encerrar.');
    return;
  }
  const confirmation = window.prompt('Confirma o encerramento? Digite CONFIRMAR para prosseguir.');
  if (confirmation !== 'CONFIRMAR') {
    alert('Encerramento cancelado. Confirmação inválida.');
    return;
  }
  const res = await fetch('/api/meta/whatsapp/viewer/close-by-phone', {
    method: 'POST',
    headers: headers(),
    body: JSON.stringify({ phone: selectedPhone, reason_code: reasonCode, confirmation })
  });
  const data = await res.json();
  alert(`Interações encerradas: ${data.closed || 0}`);
  await selectConversation(selectedPhone);
}

document.addEventListener('DOMContentLoaded', () => {
  buildViewerModeControls();
  setViewerMode(viewerMode);
  setQuickSortMode('default', false);
  applyFiltersFromUrl();
  refreshSavedQueriesSelect();
  document.getElementById('operatorName')?.addEventListener('change', refreshSavedQueriesSelect);
  bindFilterListeners();
  bindTimelineFilterListeners();
  bindKeyboardShortcuts();
  bindConversationInfiniteScroll();
  bindTimelineActions();
  renderDebugPanel(null);
  initializeQuickSnippets();
  bindComposerEvents();
  updateManualCounter();
  updateUnreadTotal();
  updateTimelineSearchCounter();
  setMetricsWindow(metricsWindowMinutes);
  renderConversationHeader(null);
  applyPermissionUiState();
});
