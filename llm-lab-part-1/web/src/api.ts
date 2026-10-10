// Geliştirmede (npm run dev) API ayrı bir porttadır. Üretim derlemesi (konteyner) aynı kökenden "/api"
// çağırır; nginx bunu API'ye yönlendirir, böylece CORS gerekmez ve imaj ortamdan bağımsız kalır.
const API_URL = import.meta.env.VITE_API_URL ?? (import.meta.env.DEV ? 'http://localhost:5080' : '/api')

export type ConversationSummary = { id: string; title: string; updatedAt: string }

export type StoredMessage = {
  role: 'user' | 'assistant'
  content: string
  thinking: string | null
  thinkSecs: number | null
  /** null = belge araması yapılmadı; [] = arandı ama yeterince benzer parça bulunamadı. */
  sources: SearchHit[] | null
  /** Ajanın çağırdığı araçlar (yalnızca ajan tabanlı API; eski API alanı hiç göndermez). */
  toolCalls?: { name: string; arguments: string | null; position: number }[] | null
}

export type ConversationDetail = { id: string; title: string; messages: StoredMessage[] }

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API_URL}${path}`, init)
  if (!res.ok) {
    // Sunucu doğrulama hatalarını { "error": "..." } olarak döner; varsa kullanıcıya onu göster.
    const body = (await res.json().catch(() => null)) as { error?: string } | null
    throw new Error(body?.error ?? `API hatası: ${res.status}`)
  }
  return res.status === 204 ? (undefined as T) : ((await res.json()) as T)
}

export type DocumentInfo = {
  id: string
  fileName: string
  sizeBytes: number
  chunkCount: number
  createdAt: string
}

export type SearchHit = {
  chunkId: number
  documentId: string
  fileName: string
  ordinal: number
  text: string
  /** Cosine benzerliği: 1'e yakın = çok benzer. */
  score: number
}

export const listDocuments = () => request<DocumentInfo[]>('/documents')
export const deleteDocument = (id: string) => request<void>(`/documents/${id}`, { method: 'DELETE' })

export function uploadDocument(file: File): Promise<DocumentInfo> {
  const form = new FormData()
  form.append('file', file) // Content-Type'ı tarayıcı sınır (boundary) ile kendisi ekler
  return request<DocumentInfo>('/documents', { method: 'POST', body: form })
}

export const searchDocuments = (query: string, topK = 5) =>
  request<SearchHit[]>('/search', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ query, topK }),
  })

export const listConversations = () => request<ConversationSummary[]>('/conversations')
export const getConversation = (id: string) => request<ConversationDetail>(`/conversations/${id}`)
export const deleteConversation = (id: string) => request<void>(`/conversations/${id}`, { method: 'DELETE' })

export type StreamHandlers = {
  /** Sunucunun bildirdiği sohbet kimliği (yeni sohbette ilk olay). */
  onConversation: (id: string) => void
  /** Ajan bir araç çağırdı (yalnızca ajan tabanlı API; eski API bu olayı göndermez). args: "query=aurora" gibi. */
  onTool: (name: string, args?: string) => void
  /** Cevapta kullanılan belge parçaları (yalnızca belge araması açıkken; boş liste = bulunamadı). */
  onSources: (sources: SearchHit[]) => void
  /** Modelin iç akıl yürütmesinden gelen parça (yalnızca think açıkken). */
  onThinking: (text: string) => void
  /** Asıl cevaptan gelen parça. */
  onToken: (text: string) => void
}

/**
 * /chat/stream'e POST atar ve gelen olayları ilgili işleyiciye yollar.
 * conversationId boşsa sunucu yeni sohbet açar ve kimliğini onConversation ile bildirir.
 * EventSource yalnızca GET destekler; POST gövdesi için fetch + ReadableStream kullanılır.
 * Sunucu her olayı "data: {"type":"...","text":"..."}\n\n" biçiminde yollar.
 */
export async function streamChat(
  conversationId: string | null,
  message: string,
  options: { think: boolean; useDocuments: boolean },
  handlers: StreamHandlers,
  signal: AbortSignal,
): Promise<void> {
  const res = await fetch(`${API_URL}/chat/stream`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ message, conversationId, ...options }),
    signal,
  })
  if (!res.ok || !res.body) throw new Error(`API hatası: ${res.status}`)

  const reader = res.body.pipeThrough(new TextDecoderStream()).getReader()
  let buffer = ''
  for (;;) {
    const { value, done } = await reader.read()
    if (done) break
    buffer += value
    // Bir olay "\n\n" ile biter; yarım kalan son parça buffer'da bekler.
    const events = buffer.split('\n\n')
    buffer = events.pop() ?? ''
    for (const ev of events) {
      if (!ev.startsWith('data: ')) continue
      const { type, text, name, sources } = JSON.parse(ev.slice(6)) as {
        type: string
        text?: string
        name?: string
        sources?: SearchHit[]
      }
      if (type === 'conversation') handlers.onConversation(text ?? '')
      else if (type === 'tool') handlers.onTool(name ?? 'araç', text)
      else if (type === 'sources') handlers.onSources(sources ?? [])
      else if (type === 'thinking') handlers.onThinking(text ?? '')
      else if (type === 'token') handlers.onToken(text ?? '')
    }
  }
}
