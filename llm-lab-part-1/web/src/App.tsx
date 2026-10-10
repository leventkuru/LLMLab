import { useCallback, useEffect, useRef, useState } from 'react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import remarkMath from 'remark-math'
import rehypeKatex from 'rehype-katex'
import 'katex/dist/katex.min.css'
import Documents from './Documents'
import {
  deleteConversation,
  getConversation,
  listConversations,
  streamChat,
  type ConversationSummary,
  type SearchHit,
} from './api'
import './App.css'

type Message = {
  role: 'user' | 'assistant'
  content: string
  /** Modelin iç akıl yürütmesi (yalnızca think açıkken dolar). */
  thinking?: string
  /** Düşünmenin kaç saniye sürdüğü; ilk cevap tokeni gelince hesaplanır. */
  thinkSecs?: number
  /** Cevapta kullanılan belge parçaları. undefined = belge araması yapılmadı; [] = bulunamadı. */
  sources?: SearchHit[]
  /** Ajanın bu turda çağırdığı araçlar; position = cevap metninin kaçıncı karakterinde çağrıldığı. */
  tools?: ToolCall[]
}

type ToolCall = { name: string; args?: string; position: number }

type Part = { kind: 'text'; text: string } | { kind: 'tools'; calls: ToolCall[] }

/** Cevabı araç çağrılarının konumlarından bölüp "metin → [araç satırı] → metin" sırasına dizer. */
function splitParts(m: Message): Part[] {
  const parts: Part[] = []
  const content = m.content
  let cursor = 0
  // Aynı konumdaki çağrılar tek satırda gruplanır.
  const groups = new Map<number, ToolCall[]>()
  for (const t of m.tools ?? []) {
    const pos = Math.min(Math.max(t.position, 0), content.length)
    groups.set(pos, [...(groups.get(pos) ?? []), t])
  }
  for (const pos of [...groups.keys()].sort((a, b) => a - b)) {
    const before = content.slice(cursor, pos)
    if (before.trim()) parts.push({ kind: 'text', text: before })
    parts.push({ kind: 'tools', calls: groups.get(pos)! })
    cursor = pos
  }
  const rest = content.slice(cursor)
  if (rest.trim()) parts.push({ kind: 'text', text: rest })
  return parts
}

/** "Used 2 tools ›" gibi tıklanınca açılan araç satırı: ajanın metinler arasında ne yaptığını gösterir. */
function ToolRow({ calls, live }: { calls: ToolCall[]; live: boolean }) {
  const allSearch = calls.every((c) => c.name === 'search_documents')
  const label = allSearch
    ? `🔍 Belgelerde ${live ? 'aranıyor' : 'arandı'}${calls.length > 1 ? ` (${calls.length} kez)` : ''}`
    : `🛠 ${calls.length} araç kullandı`
  return (
    <details className="tool-row">
      <summary>{label}</summary>
      <ul>
        {calls.map((c, i) => (
          <li key={i}>
            <code>{c.name}</code>
            {c.args ? <span>({c.args})</span> : null}
          </li>
        ))}
      </ul>
    </details>
  )
}

/** Kullanıcı en alta yakınsa true: o zaman yeni içerikle birlikte aşağı kaydırırız. */
const nearBottom = (el: HTMLElement) => el.scrollHeight - el.scrollTop - el.clientHeight < 32

/** Modeller formülü bazen \( \) ve \[ \] ile yazar; remark-math $ ve $$ bekler. */
const normalizeMath = (s: string) =>
  s.replace(/\\\[([\s\S]*?)\\\]/g, '$$$$$1$$$$').replace(/\\\(([\s\S]*?)\\\)/g, '$$$1$$')

function Markdown({ children }: { children: string }) {
  return (
    <div className="markdown">
      <ReactMarkdown remarkPlugins={[remarkGfm, remarkMath]} rehypePlugins={[rehypeKatex]}>
        {normalizeMath(children)}
      </ReactMarkdown>
    </div>
  )
}

/** Cevabın dayandığı belge parçaları. Numaralar, cevaptaki [1], [2] atıflarıyla aynı sıradadır. */
function Sources({ sources }: { sources: SearchHit[] }) {
  if (sources.length === 0) {
    return <div className="no-sources">Belgelerde ilgili kaynak bulunamadı</div>
  }
  return (
    <details className="sources">
      <summary>Kaynaklar · {sources.length}</summary>
      <ol>
        {sources.map((s, i) => (
          <li key={s.chunkId}>
            <div className="source-head">
              <span>
                [{i + 1}] {s.fileName} · parça {s.ordinal + 1}
              </span>
              <span className="score" title="Cosine benzerliği (1 = aynı)">
                {s.score.toFixed(3)}
              </span>
            </div>
            <p>{s.text}</p>
          </li>
        ))}
      </ol>
    </details>
  )
}

/** Düşünce metni: akış sürerken kendi kutusunda en alta kayar (kullanıcı yukarı çıktıysa kaymaz). */
function ThinkingText({ text }: { text: string }) {
  const ref = useRef<HTMLDivElement>(null)
  const stick = useRef(true)

  useEffect(() => {
    const el = ref.current
    if (el && stick.current) el.scrollTop = el.scrollHeight
  }, [text])

  return (
    <div
      ref={ref}
      className="thinking-text"
      onScroll={(e) => {
        stick.current = nearBottom(e.currentTarget)
      }}
    >
      <Markdown>{text}</Markdown>
    </div>
  )
}

function App() {
  const [messages, setMessages] = useState<Message[]>([])
  const [conversations, setConversations] = useState<ConversationSummary[]>([])
  const [activeId, setActiveId] = useState<string | null>(null)
  const [sidebarOpen, setSidebarOpen] = useState(false)
  const [view, setView] = useState<'chat' | 'docs'>('chat')
  const [input, setInput] = useState('')
  const [busy, setBusy] = useState(false)
  const [think, setThink] = useState(true)
  // Belgelerden yararlanma tercihi tarayıcıda hatırlanır (özel pencerede erişim hata verebilir).
  const [useDocs, setUseDocs] = useState(() => {
    try {
      return localStorage.getItem('useDocs') === '1'
    } catch {
      return false
    }
  })
  const [error, setError] = useState<string | null>(null)
  const abortRef = useRef<AbortController | null>(null)
  const mainRef = useRef<HTMLElement>(null)
  const stickRef = useRef(true)

  const refreshList = useCallback(async () => {
    try {
      setConversations(await listConversations())
    } catch (e) {
      setError(`${(e as Error).message}. LlmApi (5080) çalışıyor mu?`)
    }
  }, [])

  useEffect(() => {
    void refreshList()
  }, [refreshList])

  useEffect(() => {
    try {
      localStorage.setItem('useDocs', useDocs ? '1' : '0')
    } catch {
      // yazılamıyorsa tercih yalnızca bu oturumda geçerli olur
    }
  }, [useDocs])

  // Yeni içerik geldikçe, kullanıcı en alttaysa listeyi de en alta kaydır.
  useEffect(() => {
    const el = mainRef.current
    if (el && stickRef.current) el.scrollTop = el.scrollHeight
  }, [messages, error])

  // Son (dolmakta olan) asistan mesajını güncelle.
  function updateLast(fn: (m: Message) => Message) {
    setMessages((all) => [...all.slice(0, -1), fn(all[all.length - 1])])
  }

  function newChat() {
    if (busy) return
    setView('chat')
    setActiveId(null)
    setMessages([])
    setError(null)
    setSidebarOpen(false)
  }

  async function openConversation(id: string) {
    setView('chat')
    if (busy || id === activeId) {
      setSidebarOpen(false)
      return
    }
    try {
      const conv = await getConversation(id)
      stickRef.current = true
      setError(null)
      setActiveId(conv.id)
      setMessages(
        conv.messages.map((m) => ({
          role: m.role,
          content: m.content,
          thinking: m.thinking ?? undefined,
          thinkSecs: m.thinkSecs ?? undefined,
          sources: m.sources ?? undefined,
          tools: m.toolCalls?.map((t) => ({ name: t.name, args: t.arguments ?? undefined, position: t.position })),
        })),
      )
      setSidebarOpen(false)
    } catch (e) {
      setError((e as Error).message)
    }
  }

  async function removeConversation(c: ConversationSummary) {
    if (busy || !window.confirm(`"${c.title}" sohbeti silinsin mi?`)) return
    try {
      await deleteConversation(c.id)
      if (c.id === activeId) newChat()
      await refreshList()
    } catch (e) {
      setError((e as Error).message)
    }
  }

  async function send() {
    const text = input.trim()
    if (!text || busy) return
    setInput('')
    setError(null)
    setBusy(true)
    stickRef.current = true // yeni mesajda her zaman alta in
    // Kullanıcı mesajını ve cevabın dolacağı boş balonu birlikte ekle.
    setMessages((m) => [
      ...m,
      { role: 'user', content: text },
      { role: 'assistant', content: '', thinking: '' },
    ])

    const controller = new AbortController()
    abortRef.current = controller
    let thinkStart: number | null = null
    let thinkSecs: number | undefined
    try {
      await streamChat(
        activeId,
        text,
        { think, useDocuments: useDocs },
        {
          // Yeni sohbette sunucu kimliği ilk olay olarak bildirir; sonraki mesajlar aynı sohbete gider.
          onConversation: setActiveId,
          // Konum: aracın çağrıldığı andaki cevap uzunluğu; araç satırı metnin tam o noktasına konur.
          onTool: (name, args) =>
            updateLast((m) => ({ ...m, tools: [...(m.tools ?? []), { name, args, position: m.content.length }] })),
          onSources: (sources) => updateLast((m) => ({ ...m, sources })),
          onThinking: (t) => {
            thinkStart ??= Date.now()
            updateLast((m) => ({ ...m, thinking: (m.thinking ?? '') + t }))
          },
          onToken: (t) => {
            // İlk cevap tokeni geldiğinde düşünme süresini sabitle.
            if (thinkStart !== null && thinkSecs === undefined) {
              thinkSecs = (Date.now() - thinkStart) / 1000
            }
            updateLast((m) => ({ ...m, content: m.content + t, thinkSecs }))
          },
        },
        controller.signal,
      )
    } catch (e) {
      // Kullanıcı "Durdur"a bastıysa bu bir hata değildir.
      if ((e as Error).name !== 'AbortError') {
        setError(`${(e as Error).message}. LlmApi (5080) ve Ollama çalışıyor mu?`)
      }
    } finally {
      setBusy(false)
      abortRef.current = null
      void refreshList() // başlık ve sıralama güncellensin
    }
  }

  return (
    <div className="app">
      {sidebarOpen && <div className="backdrop" onClick={() => setSidebarOpen(false)} />}

      <aside className={`sidebar${sidebarOpen ? ' open' : ''}`}>
        <button className="new-chat" onClick={newChat} disabled={busy}>
          + Yeni sohbet
        </button>
        <ul>
          {conversations.map((c) => (
            <li key={c.id} className={c.id === activeId ? 'active' : ''}>
              <button
                className="conv-title"
                title={c.title}
                disabled={busy}
                onClick={() => void openConversation(c.id)}
              >
                {c.title}
              </button>
              <button
                className="conv-del"
                aria-label={`"${c.title}" sohbetini sil`}
                disabled={busy}
                onClick={() => void removeConversation(c)}
              >
                ✕
              </button>
            </li>
          ))}
          {conversations.length === 0 && <li className="no-convs">Henüz sohbet yok.</li>}
        </ul>
      </aside>

      <div className="chat">
        <header>
          <button className="menu" aria-label="Sohbet listesi" onClick={() => setSidebarOpen(true)}>
            ☰
          </button>
          <h1>Yerel LLM</h1>
          <nav className="tabs">
            <button className={view === 'chat' ? 'active' : ''} onClick={() => setView('chat')}>
              Sohbet
            </button>
            <button className={view === 'docs' ? 'active' : ''} onClick={() => setView('docs')}>
              Belgeler
            </button>
          </nav>
          <div className="header-right">
            {view === 'chat' && (
              <>
                <label className="toggle" title="Soruyu yüklü belgelerde arayıp cevabı onlara dayandır">
                  <input
                    type="checkbox"
                    checked={useDocs}
                    disabled={busy}
                    onChange={(e) => setUseDocs(e.target.checked)}
                  />
                  Belgeler
                </label>
                <label className="toggle">
                  <input
                    type="checkbox"
                    checked={think}
                    disabled={busy}
                    onChange={(e) => setThink(e.target.checked)}
                  />
                  Düşünme
                </label>
              </>
            )}
            <span className="model">qwen3:8b · Ollama</span>
          </div>
        </header>

        {view === 'docs' && <Documents />}

        {view === 'chat' && (
          <>
        <main
          ref={mainRef}
          onScroll={(e) => {
            stickRef.current = nearBottom(e.currentTarget)
          }}
        >
          <div className="messages">
            {messages.length === 0 && <p className="empty">Bir şey sorarak başlayın.</p>}
            {messages.map((m, i) => {
              const isLast = i === messages.length - 1
              // Düşünme aşaması: akış sürüyor ve henüz cevap tokeni gelmedi.
              const thinkingNow = busy && isLast && m.content === ''
              return (
                <div key={i} className={`msg ${m.role}`}>
                  {m.thinking && (
                    <details className="thinking" open={thinkingNow}>
                      <summary>
                        {thinkingNow
                          ? 'Düşünüyor…'
                          : `Düşünce süreci${m.thinkSecs !== undefined ? ` · ${m.thinkSecs.toFixed(1)} sn` : ''}`}
                      </summary>
                      <ThinkingText text={m.thinking} />
                    </details>
                  )}
                  {m.role === 'assistant' ? (
                    <>
                      {splitParts(m).map((part, k) =>
                        part.kind === 'text' ? (
                          <div key={k} className="bubble">
                            <Markdown>{part.text}</Markdown>
                          </div>
                        ) : (
                          <ToolRow key={k} calls={part.calls} live={busy && isLast && m.content === ''} />
                        ),
                      )}
                      {/* Henüz hiçbir şey gelmediyse (araç da yok, metin de) bekleme balonu */}
                      {!m.content && !m.tools?.length && !m.thinking && busy && isLast && <div className="bubble">…</div>}
                    </>
                  ) : (
                    <div className="bubble">{m.content}</div>
                  )}
                  {m.sources && <Sources sources={m.sources} />}
                </div>
              )
            })}
            {error && <div className="error">{error}</div>}
          </div>
        </main>

        <form
          onSubmit={(e) => {
            e.preventDefault()
            void send()
          }}
        >
          <input
            value={input}
            onChange={(e) => setInput(e.target.value)}
            placeholder="Mesajınız…"
            autoFocus
          />
          {busy ? (
            <button type="button" onClick={() => abortRef.current?.abort()}>
              Durdur
            </button>
          ) : (
            <button type="submit" disabled={!input.trim()}>
              Gönder
            </button>
          )}
        </form>
          </>
        )}
      </div>
    </div>
  )
}

export default App
