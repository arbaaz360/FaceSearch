import { useState, useEffect } from 'react'
import {
  getEmbedderStatus,
  getAlbumStatus,
  getAlbumErrors,
  resetErrors,
  factoryReset,
  generateClipEmbeddings,
} from '../services/api'

function Diagnostics() {
  const [embedderStatus, setEmbedderStatus] = useState(null)
  const [albumId, setAlbumId] = useState('')
  const [albumStatus, setAlbumStatus] = useState(null)
  const [albumErrors, setAlbumErrors] = useState(null)
  const [loading, setLoading] = useState(false)
  const [clipAlbumId, setClipAlbumId] = useState('')
  const [clipGenerating, setClipGenerating] = useState(false)
  const [clipResult, setClipResult] = useState(null)

  useEffect(() => {
    loadEmbedderStatus()
  }, [])

  const loadEmbedderStatus = async () => {
    try {
      const data = await getEmbedderStatus()
      setEmbedderStatus(data)
    } catch (error) {
      console.error('Failed to load embedder status:', error)
    }
  }

  const handleGetStatus = async () => {
    if (!albumId) return
    setLoading(true)
    try {
      const [status, errors] = await Promise.all([
        getAlbumStatus(albumId),
        getAlbumErrors(albumId).catch(() => null),
      ])
      setAlbumStatus(status)
      setAlbumErrors(errors)
    } catch (error) {
      console.error('Failed to load status:', error)
      alert('Failed to load album status')
    } finally {
      setLoading(false)
    }
  }

  const handleResetErrors = async () => {
    if (!confirm(`Reset all errors for album "${albumId}"?`)) return
    try {
      await resetErrors(albumId)
      alert('Errors reset successfully')
      handleGetStatus()
    } catch (error) {
      console.error('Failed to reset errors:', error)
      alert('Failed to reset errors')
    }
  }

  const handleFactoryReset = async () => {
    if (!confirm('⚠️ WARNING: This will delete ALL data in Qdrant and MongoDB!\n\nThis cannot be undone. Are you sure?')) {
      return
    }
    if (!confirm('Are you REALLY sure? This will delete everything!')) {
      return
    }
    try {
      const result = await factoryReset()
      alert(`Factory reset completed: ${result.message}`)
    } catch (error) {
      console.error('Factory reset failed:', error)
      alert('Factory reset failed')
    }
  }

  return (
    <div>
      <div className="page-header">
        <h1>Diagnostics</h1>
        <p>System status and diagnostic tools</p>
      </div>

      <div className="grid grid-cols-2" style={{ marginBottom: '24px' }}>
        <div className="card">
          <h2 style={{ marginBottom: '16px', fontSize: '18px' }}>Embedder Status</h2>
          {embedderStatus ? (
            <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
              <div>
                <span className="text-muted">Status: </span>
                <strong style={{ color: 'var(--success)' }}>Online</strong>
              </div>
              {embedderStatus.version && (
                <div>
                  <span className="text-muted">Version: </span>
                  <strong>{embedderStatus.version}</strong>
                </div>
              )}
            </div>
          ) : (
            <p className="text-muted">Loading...</p>
          )}
        </div>

        <div className="card">
          <h2 style={{ marginBottom: '16px', fontSize: '18px' }}>Album Status</h2>
          <div style={{ display: 'flex', gap: '8px', marginBottom: '12px' }}>
            <input
              className="input"
              value={albumId}
              onChange={(e) => setAlbumId(e.target.value)}
              placeholder="Album ID"
              style={{ flex: 1 }}
            />
            <button className="btn btn-primary" onClick={handleGetStatus} disabled={loading || !albumId}>
              Check
            </button>
          </div>
          {albumStatus && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
              <div>
                <span className="text-muted">Total: </span>
                <strong>{albumStatus.totalImages}</strong>
              </div>
              <div>
                <span className="text-muted">Pending: </span>
                <strong>{albumStatus.pendingImages}</strong>
              </div>
              <div>
                <span className="text-muted">Done: </span>
                <strong style={{ color: 'var(--success)' }}>{albumStatus.doneImages}</strong>
              </div>
              <div>
                <span className="text-muted">Errors: </span>
                <strong style={{ color: albumStatus.errorImages > 0 ? 'var(--danger)' : 'inherit' }}>
                  {albumStatus.errorImages}
                </strong>
              </div>
              <div>
                <span className="text-muted">Progress: </span>
                <strong>{albumStatus.progressPercent}%</strong>
              </div>
              {albumStatus.errorImages > 0 && (
                <button className="btn btn-secondary" onClick={handleResetErrors} style={{ marginTop: '8px' }}>
                  Reset Errors
                </button>
              )}
            </div>
          )}
        </div>
      </div>

      {albumErrors && albumErrors.totalErrors > 0 && (
        <div className="card" style={{ marginBottom: '24px' }}>
          <h2 style={{ marginBottom: '16px', fontSize: '18px' }}>Error Groups</h2>
          <div style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
            {albumErrors.errorGroups?.map((group, idx) => (
              <div key={idx} style={{ padding: '12px', background: 'var(--bg)', borderRadius: '8px' }}>
                <div style={{ marginBottom: '8px' }}>
                  <strong>{group.count}x</strong> - {group.errorMessage}
                </div>
                {group.sampleImageIds && group.sampleImageIds.length > 0 && (
                  <div className="text-muted text-sm">
                    Sample IDs: {group.sampleImageIds.slice(0, 3).join(', ')}
                    {group.sampleImageIds.length > 3 && '...'}
                  </div>
                )}
              </div>
            ))}
          </div>
        </div>
      )}

      <div className="card" style={{ marginBottom: '24px' }}>
        <h2 style={{ marginBottom: '16px', fontSize: '18px' }}>Generate CLIP Embeddings</h2>
        <p className="text-muted text-sm" style={{ marginBottom: '12px' }}>
          Generate CLIP embeddings for images that don't have them yet. Leave album ID empty to process all albums.
        </p>
        <div style={{ display: 'flex', gap: '8px', marginBottom: '12px' }}>
          <input
            className="input"
            value={clipAlbumId}
            onChange={(e) => setClipAlbumId(e.target.value)}
            placeholder="Album ID (optional - leave empty for all)"
            style={{ flex: 1 }}
          />
          <button
            className="btn btn-primary"
            onClick={async () => {
              if (!confirm(`Generate CLIP embeddings for ${clipAlbumId ? `album "${clipAlbumId}"` : 'all albums'}?`)) {
                return
              }
              setClipGenerating(true)
              setClipResult(null)
              try {
                const result = await generateClipEmbeddings(clipAlbumId || null, 100)
                setClipResult(result)
                alert(`CLIP generation completed!\nGenerated: ${result.generated}\nSkipped: ${result.skipped}\nErrors: ${result.errors}`)
              } catch (error) {
                console.error('Failed to generate CLIP:', error)
                alert(`Failed to generate CLIP: ${error.response?.data?.message || error.message}`)
              } finally {
                setClipGenerating(false)
              }
            }}
            disabled={clipGenerating}
          >
            {clipGenerating ? 'Generating...' : 'Generate CLIP'}
          </button>
        </div>
        {clipResult && (
          <div style={{ padding: '12px', background: 'var(--bg)', borderRadius: '8px', marginTop: '12px' }}>
            <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
              <div>
                <strong>Total Images:</strong> {clipResult.totalImages}
              </div>
              <div>
                <strong>Generated:</strong> <span style={{ color: 'var(--success)' }}>{clipResult.generated}</span>
              </div>
              <div>
                <strong>Skipped:</strong> {clipResult.skipped} (already have CLIP)
              </div>
              <div>
                <strong>Errors:</strong>{' '}
                <span style={{ color: clipResult.errors > 0 ? 'var(--danger)' : 'inherit' }}>
                  {clipResult.errors}
                </span>
              </div>
              {clipResult.errorMessages && clipResult.errorMessages.length > 0 && (
                <div style={{ marginTop: '8px' }}>
                  <strong>Sample Errors:</strong>
                  <ul style={{ marginTop: '4px', paddingLeft: '20px' }}>
                    {clipResult.errorMessages.slice(0, 5).map((msg, idx) => (
                      <li key={idx} className="text-muted text-sm">{msg}</li>
                    ))}
                  </ul>
                </div>
              )}
            </div>
          </div>
        )}
      </div>

      <div className="card">
        <h2 style={{ marginBottom: '16px', fontSize: '18px', color: 'var(--danger)' }}>Danger Zone</h2>
        <button className="btn btn-danger" onClick={handleFactoryReset}>
          Factory Reset
        </button>
        <p className="text-muted text-sm" style={{ marginTop: '8px' }}>
          This will delete all collections in Qdrant and all documents in MongoDB. This cannot be undone!
        </p>
      </div>
    </div>
  )
}

export default Diagnostics

