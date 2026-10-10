import { useCallback, useEffect, useState } from 'react'
import {
  deleteAgent,
  listAgents,
  listSubAgents,
  listTools,
  saveAgent,
  setAgentEnabled,
  stopSubAgent,
  type AgentInfo,
  type SaveAgent,
  type SubAgentTask,
  type ToolInfo,
} from './api'

const ID_PATTERN = /^[a-z][a-z0-9-]{1,31}$/

type Draft = SaveAgent & { id: string; isNew: boolean }

const emptyDraft = (): Draft => ({
  id: '',
  isNew: true,
  displayName: '',
  description: '',
  instructions: '',
  allowedTools: [],
  canDelegate: false,
  hosting: 'local',
  enabled: true,
})

const statusLabel: Record<SubAgentTask['status'], string> = {
  running: 'çalışıyor',
  completed: 'tamamlandı',
  failed: 'başarısız',
  stopped: 'durduruldu',
}

/**
 * Ajan kayıt defteri: ajanlar KOD değil VERİDİR. Burada "şunlar çalışsın" anahtarı, rol talimatı, izinli araçlar,
 * devretme yeteneği ve çalışma yeri (yerel / A2A) düzenlenir. Alt bölümde koordinatörün başlattığı görevler izlenir.
 */
export default function Agents({ onChanged }: { onChanged: () => void }) {
  const [agents, setAgents] = useState<AgentInfo[]>([])
  const [tools, setTools] = useState<ToolInfo[]>([])
  const [tasks, setTasks] = useState<SubAgentTask[]>([])
  const [draft, setDraft] = useState<Draft | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    try {
      setAgents(await listAgents())
      onChanged()
    } catch (e) {
      setError((e as Error).message)
    }
  }, [onChanged])

  useEffect(() => {
    void refresh()
    // Araç envanteri MCP sunucuları kapalıyken boş gelebilir; ajan listesi yine çalışır.
    listTools().then(setTools).catch(() => setTools([]))
  }, [refresh])

  // Alt ajan görevleri: koordinatör çalışırken durumu görmek için kısa aralıkla yenilenir.
  useEffect(() => {
    let alive = true
    const load = () => listSubAgents().then((t) => alive && setTasks(t)).catch(() => undefined)
    void load()
    const timer = window.setInterval(load, 3000)
    return () => {
      alive = false
      window.clearInterval(timer)
    }
  }, [])

  async function toggle(a: AgentInfo, enabled: boolean) {
    setError(null)
    try {
      await setAgentEnabled(a.id, enabled)
      await refresh()
    } catch (e) {
      setError((e as Error).message)
    }
  }

  async function remove(a: AgentInfo) {
    if (!window.confirm(`"${a.displayName}" ajanı silinsin mi? (Bu ajanla başlamış sohbetler kalır.)`)) return
    setError(null)
    try {
      await deleteAgent(a.id)
      await refresh()
    } catch (e) {
      setError((e as Error).message)
    }
  }

  function edit(a: AgentInfo) {
    setError(null)
    setDraft({ ...a, isNew: false })
  }

  async function save() {
    if (!draft) return
    if (draft.isNew && !ID_PATTERN.test(draft.id)) {
      setError('Kimlik küçük harfle başlamalı; yalnızca küçük harf, rakam ve "-" içerebilir (2-32 karakter).')
      return
    }
    if (!draft.displayName.trim()) {
      setError('Ad boş olamaz.')
      return
    }
    setSaving(true)
    setError(null)
    try {
      const { id, isNew: _isNew, ...body } = draft
      void _isNew
      await saveAgent(id, body)
      setDraft(null)
      await refresh()
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setSaving(false)
    }
  }

  const toggleTool = (name: string, on: boolean) =>
    setDraft((d) =>
      d ? { ...d, allowedTools: on ? [...new Set([...d.allowedTools, name])] : d.allowedTools.filter((t) => t !== name) } : d,
    )

  // Kayıtlı ama şu an keşfedilemeyen (sunucusu kapalı) araçlar da düzenleyicide görünür kalsın.
  const knownToolNames = tools.map((t) => t.name)
  const orphanTools = (draft?.allowedTools ?? []).filter((t) => !knownToolNames.includes(t))

  return (
    <main className="docs">
      <div className="docs-inner">
        <section>
          <h2>Ajanlar</h2>
          <p className="hint">
            Etkin ajanlar sohbette seçilebilir ve koordinatör tarafından alt ajan olarak çalıştırılabilir. Bir ajanın talimatını
            değiştirmek güvenlik kurallarını (araç sonuçları veridir, kaynak numaraları) devre dışı bırakmaz: onlar sistemden gelir.
          </p>

          <ul className="agent-list">
            {agents.map((a) => (
              <li key={a.id} className={a.enabled ? '' : 'off'}>
                <label className="toggle" title="Çalışsın / çalışmasın">
                  <input type="checkbox" checked={a.enabled} onChange={(e) => void toggle(a, e.target.checked)} />
                </label>
                <div className="agent-main">
                  <div className="agent-name">
                    {a.displayName} <code>{a.id}</code>
                  </div>
                  <div className="hint">{a.description || '—'}</div>
                  <div className="chips">
                    <span className="chip">{a.allowedTools.length === 0 ? 'tüm araçlar' : `${a.allowedTools.length} araç: ${a.allowedTools.join(', ')}`}</span>
                    {a.canDelegate && <span className="chip accent">devreder (koordinatör)</span>}
                    <span className={`chip${a.hosting === 'remote' ? ' accent' : ''}`}>{a.hosting === 'remote' ? 'A2A (uzak)' : 'yerel'}</span>
                    <span className="chip">v{a.version}</span>
                  </div>
                </div>
                <div className="agent-actions">
                  <button type="button" className="ghost" onClick={() => edit(a)}>
                    Düzenle
                  </button>
                  {a.id !== 'assistant' && (
                    <button type="button" className="conv-del shown" aria-label={`"${a.displayName}" ajanını sil`} onClick={() => void remove(a)}>
                      ✕
                    </button>
                  )}
                </div>
              </li>
            ))}
            {agents.length === 0 && <li className="no-convs">Ajan yok (API çalışıyor mu?).</li>}
          </ul>

          {!draft && (
            <button type="button" className="ghost" onClick={() => setDraft(emptyDraft())}>
              + Yeni ajan
            </button>
          )}
        </section>

        {draft && (
          <section className="agent-editor">
            <h2>{draft.isNew ? 'Yeni ajan' : `Düzenle: ${draft.displayName}`}</h2>

            <label className="field">
              <span>Kimlik {draft.isNew ? '' : '(değişmez)'}</span>
              <input
                value={draft.id}
                disabled={!draft.isNew}
                placeholder="örn. muhasebeci"
                onChange={(e) => setDraft({ ...draft, id: e.target.value.trim() })}
              />
            </label>
            <label className="field">
              <span>Ad</span>
              <input value={draft.displayName} onChange={(e) => setDraft({ ...draft, displayName: e.target.value })} />
            </label>
            <label className="field">
              <span>Açıklama (koordinatör ve diğer ajanlar bunu görür)</span>
              <input value={draft.description} onChange={(e) => setDraft({ ...draft, description: e.target.value })} />
            </label>
            <label className="field">
              <span>Rol talimatı</span>
              <textarea
                rows={6}
                value={draft.instructions}
                placeholder="Ajanın rolü ve çalışma biçimi. Güvenlik kuralları sistemden gelir; buraya yazmanız gerekmez."
                onChange={(e) => setDraft({ ...draft, instructions: e.target.value })}
              />
              <span className="hint">{draft.instructions.length}/4000</span>
            </label>

            <div className="field">
              <span>İzinli araçlar (hiçbiri seçilmezse tüm araçlar)</span>
              <div className="tool-grid">
                {tools.map((t) => (
                  <label key={t.name} className="toggle" title={t.description}>
                    <input type="checkbox" checked={draft.allowedTools.includes(t.name)} onChange={(e) => toggleTool(t.name, e.target.checked)} />
                    <code>{t.name}</code> <span className="hint">· {t.server}</span>
                  </label>
                ))}
                {orphanTools.map((name) => (
                  <label key={name} className="toggle" title="Bu araç şu an keşfedilemiyor (sunucusu kapalı olabilir)">
                    <input type="checkbox" checked onChange={(e) => toggleTool(name, e.target.checked)} />
                    <code>{name}</code> <span className="hint">· keşfedilemiyor</span>
                  </label>
                ))}
                {tools.length === 0 && orphanTools.length === 0 && (
                  <span className="hint">Araç listesi alınamadı (MCP sunucuları kapalı olabilir).</span>
                )}
              </div>
            </div>

            <div className="field row">
              <label className="toggle">
                <input type="checkbox" checked={draft.canDelegate} onChange={(e) => setDraft({ ...draft, canDelegate: e.target.checked })} />
                Başka ajanlara iş devredebilir (koordinatör)
              </label>
              <label className="toggle">
                Çalışma yeri
                <select value={draft.hosting} onChange={(e) => setDraft({ ...draft, hosting: e.target.value as 'local' | 'remote' })}>
                  <option value="local">yerel (bu API)</option>
                  <option value="remote">A2A (ayrı servis)</option>
                </select>
              </label>
              <label className="toggle">
                <input type="checkbox" checked={draft.enabled} onChange={(e) => setDraft({ ...draft, enabled: e.target.checked })} />
                Etkin
              </label>
            </div>
            {draft.hosting === 'remote' && (
              <p className="hint">
                A2A ajanı <code>LlmLab.AgentHost</code> servisinde çalışır (port 5201); düşünme akışı ve araç çağrıları arayüzde görünmez. Yeni
                bir ajanın uzak serviste yayınlanması için o servisin yeniden başlatılması gerekir.
              </p>
            )}

            <div className="row">
              <button type="button" onClick={() => void save()} disabled={saving}>
                {saving ? 'Kaydediliyor…' : 'Kaydet'}
              </button>
              <button type="button" className="ghost" onClick={() => setDraft(null)} disabled={saving}>
                Vazgeç
              </button>
            </div>
          </section>
        )}

        <section>
          <h2>Alt ajan görevleri</h2>
          <p className="hint">Koordinatör ajanın arka planda başlattığı görevler (API yeniden başlarsa silinir).</p>
          <ul className="agent-list">
            {tasks.map((t) => (
              <li key={t.taskId}>
                <div className="agent-main">
                  <div className="agent-name">
                    <code>{t.taskId}</code> → {t.agentId} <span className={`chip st-${t.status}`}>{statusLabel[t.status]}</span>
                  </div>
                  <div className="hint">{t.task}</div>
                  {t.toolCalls.length > 0 && <div className="hint">araçlar: {t.toolCalls.join(', ')}</div>}
                  {(t.result || t.error) && <p className="task-result">{t.result ?? t.error}</p>}
                </div>
                <div className="agent-actions">
                  {t.status === 'running' && (
                    <button type="button" className="ghost" onClick={() => void stopSubAgent(t.taskId).then(() => listSubAgents().then(setTasks))}>
                      Durdur
                    </button>
                  )}
                </div>
              </li>
            ))}
            {tasks.length === 0 && <li className="no-convs">Görev yok.</li>}
          </ul>
        </section>

        {error && <div className="error">{error}</div>}
      </div>
    </main>
  )
}
