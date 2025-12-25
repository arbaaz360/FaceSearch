import { useState } from 'react'
import { seedDirectory } from '../services/api'

function Indexing() {
  const [directoryPath, setDirectoryPath] = useState('')
  const [albumId, setAlbumId] = useState('')
  const [includeVideos, setIncludeVideos] = useState(false)
  const [loading, setLoading] = useState(false)
  const [result, setResult] = useState(null)

  const handleSeed = async () => {
    if (!directoryPath || !albumId) {
      alert('Please provide both directory path and album ID')
      return
    }

    setLoading(true)
    setResult(null)
    try {
      const data = await seedDirectory(directoryPath, albumId, includeVideos)
      setResult(data)
    } catch (error) {
      console.error('Seeding failed:', error)
      alert('Seeding failed: ' + (error.response?.data?.message || error.message))
    } finally {
      setLoading(false)
    }
  }

  return (
    <div>
      <div className="page-header">
        <h1>Indexing</h1>
        <p>Scan directories and add images to the indexing queue</p>
      </div>

      <div className="card">
        <h2 style={{ marginBottom: '16px', fontSize: '18px' }}>Seed Directory</h2>
        <div style={{ display: 'flex', flexDirection: 'column', gap: '16px' }}>
          <div>
            <label style={{ display: 'block', marginBottom: '4px' }}>Directory Path</label>
            <input
              className="input"
              value={directoryPath}
              onChange={(e) => setDirectoryPath(e.target.value)}
              placeholder="C:/path/to/images"
            />
          </div>
          <div>
            <label style={{ display: 'block', marginBottom: '4px' }}>Album ID</label>
            <input
              className="input"
              value={albumId}
              onChange={(e) => setAlbumId(e.target.value)}
              placeholder="my-album-id"
            />
          </div>
          <div>
            <label style={{ display: 'flex', alignItems: 'center', gap: '8px', cursor: 'pointer' }}>
              <input
                type="checkbox"
                checked={includeVideos}
                onChange={(e) => setIncludeVideos(e.target.checked)}
              />
              Include Videos
            </label>
          </div>
          <button
            className="btn btn-primary"
            onClick={handleSeed}
            disabled={loading || !directoryPath || !albumId}
          >
            {loading ? 'Scanning...' : 'Seed Directory'}
          </button>
        </div>

        {result && (
          <div style={{ marginTop: '24px', padding: '16px', background: 'var(--bg)', borderRadius: '8px' }}>
            <h3 style={{ marginBottom: '12px' }}>Results</h3>
            <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
              <div>
                <span className="text-muted">Scanned: </span>
                <strong>{result.scanned || 0}</strong>
              </div>
              <div>
                <span className="text-muted">Matched: </span>
                <strong>{result.matched || 0}</strong>
              </div>
              <div>
                <span className="text-muted">Upserted: </span>
                <strong>{result.upserts || 0}</strong>
              </div>
              <div>
                <span className="text-muted">Succeeded: </span>
                <strong style={{ color: 'var(--success)' }}>{result.succeeded || 0}</strong>
              </div>
            </div>
          </div>
        )}
      </div>
    </div>
  )
}

export default Indexing

