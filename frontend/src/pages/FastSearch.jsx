import { useState, useEffect } from 'react'
import { fastSearchFace, fastIndexFolder, fastStatus } from '../services/fastApi'
import './FastSearch.css'

function FastSearch() {
  const [file, setFile] = useState(null)
  const [topK, setTopK] = useState(10)
  const [loading, setLoading] = useState(false)
  const [results, setResults] = useState([])
  const [error, setError] = useState(null)
  const [folderPath, setFolderPath] = useState('')
  const [includeSubdirs, setIncludeSubdirs] = useState(true)
  const [note, setNote] = useState('')
  const [overwrite, setOverwrite] = useState(false)
  const [checkNote, setCheckNote] = useState(true)
  const [indexMsg, setIndexMsg] = useState('')
  const [status, setStatus] = useState(null)
  const [statusError, setStatusError] = useState(null)
  const [dragging, setDragging] = useState(false)

  const onFileChange = (e) => {
    setFile(e.target.files?.[0] || null)
    setResults([])
    setError(null)
  }

  const normalizeTopK = (value) => {
    const num = Number(value)
    if (!Number.isFinite(num) || num <= 0) return 10
    return Math.min(Math.max(Math.round(num), 1), 200)
  }

  const runSearch = async (blob) => {
    const targetFile = blob || file
    if (!targetFile) {
      setError('Pick an image with a face.')
      return
    }
    const limit = normalizeTopK(topK)
    setTopK(limit)
    setLoading(true)
    setError(null)
    try {
      const data = await fastSearchFace(targetFile, limit)
      setResults((data.results || []).slice(0, limit))
    } catch (err) {
      setError(err?.response?.data?.message || err.message || 'Search failed')
    } finally {
      setLoading(false)
    }
  }

  const onSearch = async () => {
    await runSearch()
  }

  const onDrop = async (e) => {
    e.preventDefault()
    e.stopPropagation()
    setDragging(false)
    const dropped = e.dataTransfer?.files?.[0]
    if (!dropped) return
    setFile(dropped)
    await runSearch(dropped)
  }

  const onDragOver = (e) => {
    e.preventDefault()
    e.stopPropagation()
    setDragging(true)
  }

  const onDragLeave = (e) => {
    e.preventDefault()
    e.stopPropagation()
    setDragging(false)
  }

  const onIndex = async () => {
    setIndexMsg('')
    if (!folderPath.trim()) {
      setIndexMsg('Enter a folder path.')
      return
    }
    try {
      await fastIndexFolder({
        folderPath: folderPath.trim(),
        includeSubdirectories: includeSubdirs,
        note: note.trim() || null,
        overwriteExisting: overwrite,
        checkNote
      })
      setIndexMsg('Index job queued.')
    } catch (err) {
      setIndexMsg(err?.response?.data ?? err.message ?? 'Indexing failed')
    }
  }

  useEffect(() => {
    let mounted = true
    const loadStatus = async () => {
      try {
        const data = await fastStatus()
        if (mounted) {
          setStatus(data)
          setStatusError(null)
        }
      } catch (err) {
        if (mounted) setStatusError(err?.message || 'Failed to load status')
      }
    }
    loadStatus()
    const id = setInterval(loadStatus, 2000)
    return () => {
      mounted = false
      clearInterval(id)
    }
  }, [])

  return (
    <div className="fast-search">
      <div className="fast-card">
        <h2>Fast Face Search (Mongo-backed metadata)</h2>
        <p className="muted">
          Uses the fast collection (no clustering) with metadata served from Mongo and payload-free Qdrant.
        </p>
        <div className="fast-form">
          <label className="file-picker">
            <span>Select face image</span>
            <input type="file" accept="image/*" onChange={onFileChange} />
          </label>
          <div
            className={`drop-zone ${dragging ? 'dragging' : ''}`}
            onDragOver={onDragOver}
            onDragEnter={onDragOver}
            onDragLeave={onDragLeave}
            onDrop={onDrop}
          >
            Drag & drop to search
          </div>
          <label className="topk">
            Top K (max results):
            <input
              type="number"
              min="1"
              max="200"
              value={topK}
              onChange={(e) => setTopK(e.target.value)}
              title="How many results to return (default 10, max 200)"
            />
          </label>
          <button onClick={onSearch} disabled={loading}>
            {loading ? 'Searching…' : 'Search'}
          </button>
        </div>
        {error && <div className="error">{error}</div>}
      </div>

      <div className="fast-card">
        <h3>Queue folder for fast indexing</h3>
        {status && (
          <div className="status-line">
            <span>Queued: {status.jobs?.queued ?? 0}</span>
            <span>Done: {status.jobs?.done ?? 0}</span>
            <span>Failed: {status.jobs?.failed ?? 0}</span>
            <span>Points: {status.collection?.points ?? 'n/a'}</span>
          </div>
        )}
        {statusError && <div className="error">{statusError}</div>}
        <div className="fast-form">
          <input
            className="path-input"
            type="text"
            placeholder="C:\\path\\to\\images"
            value={folderPath}
            onChange={(e) => setFolderPath(e.target.value)}
          />
          <label className="checkbox">
            <input
              type="checkbox"
              checked={includeSubdirs}
              onChange={(e) => setIncludeSubdirs(e.target.checked)}
            />
            Include subfolders
          </label>
          <label className="checkbox">
            <input
              type="checkbox"
              checked={overwrite}
              onChange={(e) => setOverwrite(e.target.checked)}
            />
            Overwrite existing
          </label>
          <label className="checkbox">
            <input
              type="checkbox"
              checked={checkNote}
              onChange={(e) => setCheckNote(e.target.checked)}
            />
            Update note if changed
          </label>
          <input
            className="note-input"
            type="text"
            placeholder="Optional note"
            value={note}
            onChange={(e) => setNote(e.target.value)}
          />
          <button onClick={onIndex}>Queue index</button>
        </div>
        {indexMsg && <div className="muted">{indexMsg}</div>}

        {status?.progress?.length > 0 && (
          <div className="progress-list">
            {status.progress.map((p, idx) => {
              const filesTotal = p.filesTotal ?? 0
              const filesProcessed = p.filesProcessed ?? 0
              const facesIndexed = p.facesIndexed ?? 0
              const skipped = p.filesSkippedExisting ?? 0
              const noteUpdated = p.filesNoteUpdated ?? 0
              const percent = filesTotal > 0 ? Math.min(100, Math.round((filesProcessed / filesTotal) * 100)) : null
              return (
                <div className="progress-card" key={p.jobId || p.folder || idx}>
                  <div className="progress-title" title={p.folder || ''}>
                    {(p.folder || '').split(/[\\/]/).pop() || '(job)'}
                  </div>
                  <div className="progress-meta">
                    <span>Files: {filesProcessed}{filesTotal ? ` / ${filesTotal}` : ''}</span>
                    <span>Faces: {facesIndexed}</span>
                    <span>Skipped: {skipped}</span>
                    <span>Note updates: {noteUpdated}</span>
                  </div>
                  <div className="progress-bar">
                    <div className="progress-bar-fill" style={{ width: `${percent ?? 0}%` }} />
                  </div>
                  <div className="progress-sub muted">
                    {(p.state || 'running').toUpperCase()}
                    {p.updatedAt ? ` • ${new Date(p.updatedAt).toLocaleTimeString()}` : ''}
                    {p.note ? ` • ${p.note}` : ''}
                  </div>
                  {p.folder && (
                    <div className="progress-folder muted" title={p.folder}>
                      {p.folder}
                    </div>
                  )}
                </div>
              )
            })}
          </div>
        )}
      </div>

      <div className="fast-results">
        {results.length === 0 && !loading && <div className="muted">No results yet.</div>}
        {results.map((r, idx) => (
          <div className="fast-result" key={r.id || idx}>
            <div className="score">Score: {r.score?.toFixed(4)}</div>
            {r.path ? (
              <img
                className="thumb"
                src={`/fastapi/fast/thumbnail?path=${encodeURIComponent(r.path)}`}
                alt={r.path}
                onError={(e) => {
                  e.target.style.display = 'none'
                  const ph = e.target.nextSibling
                  if (ph) ph.style.display = 'flex'
                }}
              />
            ) : (
              <div className="thumb-placeholder">No preview (local file)</div>
            )}
            <div className="thumb-placeholder" style={{ display: 'none' }}>No preview (local file)</div>
            <div className="path-title">{r.path ? r.path.split(/[/\\\\]/).pop() : '(no path)'}</div>
            <div className="path">{r.path || '(no path payload)'}</div>
            {r.note && <div className="note">Note: {r.note}</div>}
          </div>
        ))}
      </div>
    </div>
  )
}

export default FastSearch
