import { useRef, useState } from 'react'
import { streamDialog, type AgentInfo } from './api'

type Turn = { index: number; agentId: string; name: string; text: string; tools: string[] }

/**
 * İki ajanın birbiriyle konuşması: iki ajan seçilir, bir konu verilir; ajanlar sırayla (en fazla 12 tur) konuşur.
 * Diyalog kaydedilmez. Koordinatör seçilemez (alt ajanlarını kendisi yönetir).
 */
export default function Dialog({ agents }: { agents: AgentInfo[] }) {
  const candidates = agents.filter((a) => a.enabled && !a.canDelegate)
  const [agentA, setAgentA] = useState('')
  const [agentB, setAgentB] = useState('')
  const [topic, setTopic] = useState('')
  const [turns, setTurns] = useState(6)
  const [useDocs, setUseDocs] = useState(false)
  const [transcript, setTranscript] = useState<Turn[]>([])
  const [busy, setBusy] = useState(false)
  const [done, setDone] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const abortRef = useRef<AbortController | null>(null)

  // Varsayılan seçim: listedeki ilk iki ajan.
  const a = agentA || candidates[0]?.id || ''
  const b = agentB || candidates.find((c) => c.id !== a)?.id || ''

  async function start() {
    if (busy || !topic.trim() || !a || !b) return
    setError(null)
    setDone(false)
    setTranscript([])
    setBusy(true)
    const controller = new AbortController()
    abortRef.current = controller
    try {
      await streamDialog(
        { agentA: a, agentB: b, topic: topic.trim(), turns, useDocuments: useDocs },
        {
          onTurn: (index, agentId, name) => setTranscript((t) => [...t, { index, agentId, name, text: '', tools: [] }]),
          onToken: (index, text) =>
            setTranscript((all) => all.map((t) => (t.index === index ? { ...t, text: t.text + text } : t))),
          onTool: (index, name) =>
            setTranscript((all) => all.map((t) => (t.index === index ? { ...t, tools: [...t.tools, name] } : t))),
          onDone: () => setDone(true),
        },
        controller.signal,
      )
    } catch (e) {
      if ((e as Error).name !== 'AbortError') setError((e as Error).message)
    } finally {
      setBusy(false)
      abortRef.current = null
    }
  }

  return (
    <main className="docs">
      <div className="docs-inner">
        <section>
          <h2>İki ajan konuşuyor</h2>
          <p className="hint">
            Ajan A konuyu başlatır; her ajan diğerinin cevabını "kullanıcı mesajı" olarak alır. Her ajan kendi oturumunu, rolünü
            ve izinli araçlarını kullanır. Cevaplar kısa tutulur (en fazla 3 cümle).
          </p>

          <div className="field row">
            <label className="toggle">
              Ajan A
              <select value={a} onChange={(e) => setAgentA(e.target.value)} disabled={busy}>
                {candidates.map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.displayName}
                  </option>
                ))}
              </select>
            </label>
            <label className="toggle">
              Ajan B
              <select value={b} onChange={(e) => setAgentB(e.target.value)} disabled={busy}>
                {candidates.map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.displayName}
                  </option>
                ))}
              </select>
            </label>
            <label className="toggle">
              Tur
              <select value={turns} onChange={(e) => setTurns(Number(e.target.value))} disabled={busy}>
                {[2, 4, 6, 8, 10, 12].map((n) => (
                  <option key={n} value={n}>
                    {n}
                  </option>
                ))}
              </select>
            </label>
            <label className="toggle">
              <input type="checkbox" checked={useDocs} onChange={(e) => setUseDocs(e.target.checked)} disabled={busy} />
              Belgeler
            </label>
          </div>

          <div className="search-form">
            <input
              value={topic}
              onChange={(e) => setTopic(e.target.value)}
              placeholder="Konu ya da açılış isteği…"
              disabled={busy}
              onKeyDown={(e) => e.key === 'Enter' && void start()}
            />
            {busy ? (
              <button type="button" onClick={() => abortRef.current?.abort()}>
                Durdur
              </button>
            ) : (
              <button type="button" onClick={() => void start()} disabled={!topic.trim() || !a || !b || a === b}>
                Başlat
              </button>
            )}
          </div>
          {a === b && a !== '' && <p className="hint">İki farklı ajan seçin.</p>}
          {candidates.length < 2 && <p className="hint">Diyalog için en az iki etkin (koordinatör olmayan) ajan gerekir.</p>}
        </section>

        <section>
          <div className="dialog">
            {transcript.map((t) => (
              <div key={t.index} className={`dialog-turn ${t.index % 2 === 0 ? 'left' : 'right'}`}>
                <div className="dialog-name">
                  {t.name} <span className="hint">· tur {t.index + 1}</span>
                </div>
                {t.tools.length > 0 && <div className="hint">araçlar: {t.tools.join(', ')}</div>}
                <div className="bubble">{t.text || '…'}</div>
              </div>
            ))}
            {done && <p className="hint">Diyalog bitti.</p>}
          </div>
        </section>

        {error && <div className="error">{error}</div>}
      </div>
    </main>
  )
}
