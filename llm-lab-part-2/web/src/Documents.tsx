import { useCallback, useEffect, useRef, useState } from 'react'
import {
  deleteDocument,
  listDocuments,
  searchDocuments,
  uploadDocument,
  type DocumentInfo,
  type SearchHit,
} from './api'

const ACCEPT = '.txt,.md,.pdf'

const formatSize = (bytes: number) =>
  bytes < 1024 ? `${bytes} B` : bytes < 1024 * 1024 ? `${(bytes / 1024).toFixed(1)} KB` : `${(bytes / 1024 / 1024).toFixed(1)} MB`

export default function Documents() {
  const [docs, setDocs] = useState<DocumentInfo[]>([])
  const [uploading, setUploading] = useState<string | null>(null) // yüklenen dosyanın adı
  const [dragOver, setDragOver] = useState(false)
  const [query, setQuery] = useState('')
  const [hits, setHits] = useState<SearchHit[] | null>(null) // null = henüz aranmadı
  const [searching, setSearching] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const fileInput = useRef<HTMLInputElement>(null)

  const refresh = useCallback(async () => {
    try {
      setDocs(await listDocuments())
    } catch (e) {
      setError((e as Error).message)
    }
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  // Dosyaları sırayla yükle: her biri parçalanıp gömüldüğü için birkaç saniye sürebilir.
  async function upload(files: FileList | File[]) {
    setError(null)
    for (const file of Array.from(files)) {
      setUploading(file.name)
      try {
        await uploadDocument(file)
        await refresh()
      } catch (e) {
        setError(`${file.name}: ${(e as Error).message}`)
        break
      }
    }
    setUploading(null)
    if (fileInput.current) fileInput.current.value = '' // aynı dosyayı yeniden seçebilmek için
  }

  async function remove(d: DocumentInfo) {
    if (!window.confirm(`"${d.fileName}" ve ${d.chunkCount} parçası silinsin mi?`)) return
    try {
      await deleteDocument(d.id)
      setHits(null) // eski sonuçlar silinen belgeye ait olabilir
      await refresh()
    } catch (e) {
      setError((e as Error).message)
    }
  }

  async function search() {
    const q = query.trim()
    if (!q || searching) return
    setError(null)
    setSearching(true)
    try {
      setHits(await searchDocuments(q, 5))
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setSearching(false)
    }
  }

  return (
    <main className="docs">
      <div className="docs-inner">
        <section>
          <h2>Belgeler</h2>
          <div
            className={`dropzone${dragOver ? ' over' : ''}`}
            onDragOver={(e) => {
              e.preventDefault()
              setDragOver(true)
            }}
            onDragLeave={() => setDragOver(false)}
            onDrop={(e) => {
              e.preventDefault()
              setDragOver(false)
              if (!uploading) void upload(e.dataTransfer.files)
            }}
          >
            <input
              ref={fileInput}
              type="file"
              accept={ACCEPT}
              multiple
              hidden
              onChange={(e) => e.target.files && void upload(e.target.files)}
            />
            {uploading ? (
              <p>
                <strong>{uploading}</strong> parçalanıyor ve gömülüyor…
              </p>
            ) : (
              <p>
                Dosyayı buraya bırakın ya da{' '}
                <button type="button" className="link" onClick={() => fileInput.current?.click()}>
                  seçin
                </button>
                <br />
                <span className="hint">.txt, .md, .pdf · en fazla 20 MB</span>
              </p>
            )}
          </div>

          <ul className="doc-list">
            {docs.map((d) => (
              <li key={d.id}>
                <div className="doc-name" title={d.fileName}>
                  {d.fileName}
                </div>
                <div className="doc-meta">
                  {formatSize(d.sizeBytes)} · {d.chunkCount} parça
                </div>
                <button className="conv-del shown" aria-label={`"${d.fileName}" belgesini sil`} onClick={() => void remove(d)}>
                  ✕
                </button>
              </li>
            ))}
            {docs.length === 0 && !uploading && <li className="no-convs">Henüz belge yok.</li>}
          </ul>
        </section>

        <section>
          <h2>Vektör arama</h2>
          <form
            className="search-form"
            onSubmit={(e) => {
              e.preventDefault()
              void search()
            }}
          >
            <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Bir şey sorun veya anlatın…" />
            <button type="submit" disabled={!query.trim() || searching || docs.length === 0}>
              {searching ? 'Aranıyor…' : 'Ara'}
            </button>
          </form>

          {hits?.length === 0 && <p className="hint">Sonuç yok.</p>}
          <ol className="hits">
            {hits?.map((h) => (
              <li key={h.chunkId}>
                <div className="hit-head">
                  <span className="hit-source">
                    {h.fileName} · parça {h.ordinal + 1}
                  </span>
                  <span className="score" title="Cosine benzerliği (1 = aynı)">
                    {h.score.toFixed(3)}
                  </span>
                </div>
                <div className="score-bar">
                  <div style={{ width: `${Math.max(0, Math.min(1, h.score)) * 100}%` }} />
                </div>
                <p className="hit-text">{h.text}</p>
              </li>
            ))}
          </ol>
        </section>

        {error && <div className="error">{error}</div>}
      </div>
    </main>
  )
}
