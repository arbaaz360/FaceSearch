import { useState, useEffect } from 'react'
import {
  fastSearchFace,
  fastIndexFolder,
  fastIndexVideos,
  fastStatus,
  fastBulkCheck,
  fastUpsertWatchFolder,
  fastDeleteWatchFolder
} from '../services/fastApi'
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
  const [checkFiles, setCheckFiles] = useState([])
  const [checkThreshold, setCheckThreshold] = useState(0.6)
  const [checkResult, setCheckResult] = useState(null)
  const [checkError, setCheckError] = useState(null)
  const [checkLoading, setCheckLoading] = useState(false)
  const [checkDragging, setCheckDragging] = useState(false)
  const [watchIntervalSeconds, setWatchIntervalSeconds] = useState(60)
  const [videoFolderPath, setVideoFolderPath] = useState('')
  const [videoIncludeSubdirs, setVideoIncludeSubdirs] = useState(true)
  const [videoSampleEverySeconds, setVideoSampleEverySeconds] = useState(10)
  const [videoKeyframesOnly, setVideoKeyframesOnly] = useState(true)
  const [videoMaxFacesPerVideo, setVideoMaxFacesPerVideo] = useState(50)
  const [videoMaxFacesPerFrame, setVideoMaxFacesPerFrame] = useState(10)
  const [videoMaxFrameWidth, setVideoMaxFrameWidth] = useState(0)
  const [videoMaxSimilarityToExisting, setVideoMaxSimilarityToExisting] = useState(0.95)
  const [videoMinFaceWidthPx, setVideoMinFaceWidthPx] = useState(40)
  const [videoMinBlurVariance, setVideoMinBlurVariance] = useState(40)
  const [videoOutputDirectory, setVideoOutputDirectory] = useState('')
  const [videoMsg, setVideoMsg] = useState('')

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

  const onCheckFileSelect = (e) => {
    const files = Array.from(e.target.files || [])
    setCheckFiles(files)
    setCheckResult(null)
    setCheckError(null)
  }

  const onCheckDrop = (e) => {
    e.preventDefault()
    e.stopPropagation()
    setCheckDragging(false)
    const files = Array.from(e.dataTransfer?.files || [])
    if (files.length === 0) return
    setCheckFiles(files)
    setCheckResult(null)
    setCheckError(null)
  }

  const onCheckDragOver = (e) => {
    e.preventDefault()
    e.stopPropagation()
    setCheckDragging(true)
  }

  const onCheckDragLeave = (e) => {
    e.preventDefault()
    e.stopPropagation()
    setCheckDragging(false)
  }

  const onRunBulkCheck = async () => {
    if (checkFiles.length === 0) {
      setCheckError('Select files or a folder to check.')
      return
    }
    setCheckLoading(true)
    setCheckError(null)
    setCheckResult(null)
    try {
      const data = await fastBulkCheck(checkFiles, Number(checkThreshold) || 0.6)
      setCheckResult(data)
    } catch (err) {
      setCheckError(err?.response?.data ?? err.message ?? 'Bulk check failed')
    } finally {
      setCheckLoading(false)
    }
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

  const onMonitorFolder = async () => {
    setIndexMsg('')
    if (!folderPath.trim()) {
      setIndexMsg('Enter a folder path.')
      return
    }
    const interval = Number(watchIntervalSeconds)
    const safeInterval = Number.isFinite(interval) ? Math.max(10, Math.min(Math.round(interval), 86400)) : 60

    try {
      await fastUpsertWatchFolder({
        folderPath: folderPath.trim(),
        includeSubdirectories: includeSubdirs,
        note: note.trim() || null,
        intervalSeconds: safeInterval,
        overwriteExisting: overwrite,
        checkNote,
        enabled: true
      })
      setIndexMsg(`Monitoring enabled (every ${safeInterval}s). New files will be indexed automatically.`)
    } catch (err) {
      setIndexMsg(err?.response?.data ?? err.message ?? 'Failed to enable monitoring')
    }
  }

  const onRemoveWatch = async (id) => {
    if (!id) return
    setIndexMsg('')
    try {
      await fastDeleteWatchFolder(id)
      setIndexMsg('Monitoring removed.')
    } catch (err) {
      setIndexMsg(err?.response?.data ?? err.message ?? 'Failed to remove monitoring')
    }
  }

  const onVideoIndex = async () => {
    setVideoMsg('')
    if (!videoFolderPath.trim()) {
      setVideoMsg('Enter a video folder path.')
      return
    }

    const sampleEvery = Number(videoSampleEverySeconds)
    const safeSampleEvery = Number.isFinite(sampleEvery) ? Math.max(1, Math.min(Math.round(sampleEvery), 3600)) : 10

    const maxFaces = Number(videoMaxFacesPerVideo)
    const safeMaxFaces = Number.isFinite(maxFaces) ? Math.max(1, Math.min(Math.round(maxFaces), 10000)) : 50

    const maxFacesFrame = Number(videoMaxFacesPerFrame)
    const safeMaxFacesFrame = Number.isFinite(maxFacesFrame) ? Math.max(1, Math.min(Math.round(maxFacesFrame), 50)) : 10

    const frameWidth = Number(videoMaxFrameWidth)
    const safeFrameWidth = Number.isFinite(frameWidth) ? (frameWidth > 0 ? Math.max(160, Math.min(Math.round(frameWidth), 4096)) : 0) : 0

    const maxSim = Number(videoMaxSimilarityToExisting)
    const safeMaxSim = Number.isFinite(maxSim) ? Math.max(0, Math.min(maxSim, 1)) : 0.95

    const minFacePx = Number(videoMinFaceWidthPx)
    const safeMinFacePx = Number.isFinite(minFacePx) ? Math.max(20, Math.min(Math.round(minFacePx), 2000)) : 40

    const minBlur = Number(videoMinBlurVariance)
    const safeMinBlur = Number.isFinite(minBlur) ? Math.max(0, minBlur) : 40

    try {
      await fastIndexVideos({
        folderPath: videoFolderPath.trim(),
        includeSubdirectories: videoIncludeSubdirs,
        note: null,
        sampleEverySeconds: safeSampleEvery,
        keyframesOnly: videoKeyframesOnly,
        maxFacesPerVideo: safeMaxFaces,
        maxFacesPerFrame: safeMaxFacesFrame,
        maxFrameWidth: safeFrameWidth,
        minFaceWidthPx: safeMinFacePx,
        minBlurVariance: safeMinBlur,
        maxSimilarityToExisting: safeMaxSim,
        outputDirectory: videoOutputDirectory.trim() || null,
        saveCrops: true
      })
      setVideoMsg('Video indexing job queued.')
    } catch (err) {
      setVideoMsg(err?.response?.data ?? err.message ?? 'Video indexing failed')
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
        <h3>Check known faces in selected files/folders</h3>
        <p className="muted">Drop files or select a folder; counts how many have a match at or above the threshold.</p>
        <div className="fast-form">
          <label className="file-picker">
            <span>Select files or folder</span>
            <input type="file" multiple webkitdirectory="true" onChange={onCheckFileSelect} />
          </label>
          <div
            className={`drop-zone ${checkDragging ? 'dragging' : ''}`}
            onDragOver={onCheckDragOver}
            onDragEnter={onCheckDragOver}
            onDragLeave={onCheckDragLeave}
            onDrop={onCheckDrop}
          >
            Drop files/folder here
          </div>
          <label className="topk">
            Threshold:
            <input
              type="number"
              step="0.01"
              min="0"
              max="1"
              value={checkThreshold}
              onChange={(e) => setCheckThreshold(e.target.value)}
              title="Score threshold for a match"
            />
          </label>
          <button onClick={onRunBulkCheck} disabled={checkLoading}>
            {checkLoading ? 'Checking...' : 'Check known'}
          </button>
        </div>
        <div className="muted">
          Selected files: {checkFiles.length}{' '}
          {checkFiles.length > 0 && `(e.g., ${checkFiles[0].name}${checkFiles.length > 1 ? ', ...' : ''})`}
        </div>
        {checkError && <div className="error">{checkError}</div>}
        {checkResult && (
          <div className="muted">
            Processed: {checkResult.processed} | Matched: {checkResult.matched} | Unmatched:{' '}
            {Math.max(0, (checkResult.processed || 0) - (checkResult.matched || 0))} | Threshold: {checkResult.threshold}{' '}
            | Time: {checkResult.elapsedMs} ms
            {checkResult.errors?.length ? ` | Errors: ${checkResult.errors.length}` : ''}
          </div>
        )}
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
          <label className="topk">
            Monitor every (sec):
            <input
              type="number"
              min="10"
              step="10"
              value={watchIntervalSeconds}
              onChange={(e) => setWatchIntervalSeconds(e.target.value)}
              title="Periodic scan interval for new files"
            />
          </label>
          <button className="secondary" onClick={onMonitorFolder}>
            Monitor folder
          </button>
        </div>
        {indexMsg && <div className="muted">{indexMsg}</div>}

        {status?.watchFolders?.length > 0 && (
          <div className="watch-list">
            <div className="watch-title-row">
              <div className="watch-title-text">Monitored folders</div>
              <div className="muted">{status.watchFolders.length}</div>
            </div>
            <div className="watch-items">
              {status.watchFolders.map((w) => {
                const label = (w.note || '').trim() || (w.folderPath || '').split(/[\\/]/).pop() || '(folder)'
                return (
                  <div className="watch-item" key={w.id}>
                    <div className="watch-main" title={w.folderPath || ''}>
                      <div className="watch-name">{label}</div>
                      <div className="watch-meta muted">
                        <span className="watch-path">{w.folderPath}</span>
                        {typeof w.intervalSeconds === 'number' ? ` | every ${w.intervalSeconds}s` : ''}
                        {typeof w.includeSubdirectories === 'boolean' ? ` | ${w.includeSubdirectories ? 'subfolders' : 'no subfolders'}` : ''}
                      </div>
                    </div>
                    <button className="danger" onClick={() => onRemoveWatch(w.id)}>
                      Remove
                    </button>
                  </div>
                )
              })}
            </div>
          </div>
        )}

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
                    {p.updatedAt ? ` | ${new Date(p.updatedAt).toLocaleTimeString()}` : ''}
                    {p.note ? ` | ${p.note}` : ''}
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

      <div className="fast-card">
        <h3>Index video folder (good faces only)</h3>
        <p className="muted">
          Samples frames periodically, keeps only faces that are large and sharp enough, saves face crops, and indexes them for fast search.
        </p>
        <div className="fast-form">
          <input
            className="path-input"
            type="text"
            placeholder="X:\\path\\to\\videos"
            value={videoFolderPath}
            onChange={(e) => setVideoFolderPath(e.target.value)}
          />
          <label className="checkbox">
            <input
              type="checkbox"
              checked={videoIncludeSubdirs}
              onChange={(e) => setVideoIncludeSubdirs(e.target.checked)}
            />
            Include subfolders
          </label>
          <label className="checkbox">
            <input
              type="checkbox"
              checked={videoKeyframesOnly}
              onChange={(e) => setVideoKeyframesOnly(e.target.checked)}
            />
            Keyframes only
          </label>
          <label className="topk">
            Every (sec):
            <input
              type="number"
              min="1"
              step="1"
              value={videoSampleEverySeconds}
              onChange={(e) => setVideoSampleEverySeconds(e.target.value)}
              title="Sampling interval in seconds"
            />
          </label>
          <label className="topk">
            Max faces/video:
            <input
              type="number"
              min="1"
              step="1"
              value={videoMaxFacesPerVideo}
              onChange={(e) => setVideoMaxFacesPerVideo(e.target.value)}
              title="Stop early after this many good faces per video"
            />
          </label>
          <label className="topk">
            Max faces/frame:
            <input
              type="number"
              min="1"
              step="1"
              value={videoMaxFacesPerFrame}
              onChange={(e) => setVideoMaxFacesPerFrame(e.target.value)}
              title="How many faces to consider per frame"
            />
          </label>
          <label className="topk">
            Frame width (0=original):
            <input
              type="number"
              min="0"
              step="160"
              value={videoMaxFrameWidth}
              onChange={(e) => setVideoMaxFrameWidth(e.target.value)}
              title="Resize frames to this max width before face detection (0 disables resizing)"
            />
          </label>
          <label className="topk">
            Min face (px):
            <input
              type="number"
              min="20"
              step="10"
              value={videoMinFaceWidthPx}
              onChange={(e) => setVideoMinFaceWidthPx(e.target.value)}
              title="Minimum face bounding box width/height in pixels"
            />
          </label>
          <label className="topk">
            Sharpness:
            <input
              type="number"
              min="0"
              step="10"
              value={videoMinBlurVariance}
              onChange={(e) => setVideoMinBlurVariance(e.target.value)}
              title="Higher means stricter; filters blurry faces"
            />
          </label>
          <label className="topk">
            Dedup ≥
            <input
              type="number"
              min="0"
              max="1"
              step="0.01"
              value={videoMaxSimilarityToExisting}
              onChange={(e) => setVideoMaxSimilarityToExisting(e.target.value)}
              title="Treat faces with cosine similarity >= this value as duplicates (0 disables dedup)"
            />
          </label>
          <input
            className="note-input"
            type="text"
            placeholder="Output folder (optional)"
            value={videoOutputDirectory}
            onChange={(e) => setVideoOutputDirectory(e.target.value)}
          />
          <button onClick={onVideoIndex}>Queue video index</button>
        </div>
        <div className="muted">
          Output: {videoOutputDirectory.trim() || '(default) .fast-video-faces next to .fast-jobs'}
        </div>
        {videoMsg && <div className="muted">{videoMsg}</div>}
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
