import { useState, useEffect } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { getAlbums, mergeAlbums } from '../services/api'

function Albums() {
  const [albums, setAlbums] = useState([])
  const [loading, setLoading] = useState(true)
  const [skip, setSkip] = useState(0)
  const [total, setTotal] = useState(0)
  const [merging, setMerging] = useState({})
  const navigate = useNavigate()
  const take = 20

  useEffect(() => {
    loadAlbums()
  }, [skip])

  const loadAlbums = async () => {
    setLoading(true)
    try {
      const data = await getAlbums(skip, take)
      setAlbums(data.items || [])
      setTotal(data.total || 0)
    } catch (error) {
      console.error('Failed to load albums:', error)
    } finally {
      setLoading(false)
    }
  }

  if (loading && albums.length === 0) {
    return <div className="loading">Loading albums...</div>
  }

  return (
    <div>
      <div className="page-header">
        <h1>Albums</h1>
        <p>Manage and view all albums ({total} total)</p>
      </div>

      <div className="grid grid-cols-4">
        {albums.map((album) => (
          <div
            key={album.albumId}
            className="card"
          >
            <Link
              to={`/albums/${album.albumId}`}
              style={{ textDecoration: 'none', color: 'inherit' }}
            >
              <div style={{ marginBottom: '12px' }}>
                {album.previewBase64 ? (
                  <img
                    src={album.previewBase64}
                    alt={album.displayName || album.albumId}
                    style={{
                      width: '100%',
                      height: '200px',
                      objectFit: 'cover',
                      borderRadius: '8px',
                      marginBottom: '12px',
                    }}
                  />
                ) : (
                  <div
                    style={{
                      width: '100%',
                      height: '200px',
                      background: 'var(--bg)',
                      borderRadius: '8px',
                      display: 'flex',
                      alignItems: 'center',
                      justifyContent: 'center',
                      color: 'var(--muted)',
                      marginBottom: '12px',
                    }}
                  >
                    No preview
                  </div>
                )}
              </div>
              <div>
                <h3 style={{ marginBottom: '4px', fontSize: '16px' }}>
                  {album.displayName || album.albumId}
                </h3>
                {album.instagramHandle && (
                  <p className="text-muted" style={{ marginBottom: '8px' }}>
                    {album.instagramHandle}
                  </p>
                )}
                <div style={{ display: 'flex', gap: '12px', fontSize: '13px', color: 'var(--muted)' }}>
                  <span>{album.imageCount} images</span>
                  <span>{album.faceImageCount} faces</span>
                </div>
                <div style={{ display: 'flex', gap: '4px', marginTop: '8px', flexWrap: 'wrap' }}>
                  {album.mergeCandidate && (
                    <span style={{ padding: '4px 8px', background: '#ff9800', borderRadius: '4px', fontSize: '11px', color: 'white', fontWeight: 'bold' }}>
                      🔄 Merge Candidate
                    </span>
                  )}
                  {album.suspiciousAggregator && (
                    <span style={{ padding: '4px 8px', background: '#7f1d1d', borderRadius: '4px', fontSize: '11px', color: 'white' }}>
                      ⚠️ Suspicious
                    </span>
                  )}
                </div>
              </div>
            </Link>
            {album.mergeCandidate && album.duplicateAlbumId && (
              <div style={{ marginTop: '12px', paddingTop: '12px', borderTop: '1px solid var(--bg)' }}>
                <div className="text-muted text-sm" style={{ marginBottom: '8px' }}>
                  Duplicate of: <Link to={`/albums/${album.duplicateAlbumId}`} style={{ color: '#2196F3' }}>{album.duplicateAlbumId}</Link>
                </div>
                <button
                  className="btn btn-primary"
                  style={{ width: '100%' }}
                  disabled={merging[album.albumId]}
                  onClick={async (e) => {
                    e.preventDefault()
                    e.stopPropagation()
                    if (!confirm(`Merge album "${album.albumId}" into "${album.duplicateAlbumId}"?\n\nThis will move all images, clusters, and vectors from the source to the target album.`)) {
                      return
                    }
                    setMerging({ ...merging, [album.albumId]: true })
                    try {
                      await mergeAlbums(album.albumId, album.duplicateAlbumId)
                      alert('Albums merged successfully!')
                      loadAlbums()
                      navigate(`/albums/${album.duplicateAlbumId}`)
                    } catch (error) {
                      console.error('Merge failed:', error)
                      alert(`Merge failed: ${error.response?.data?.message || error.message}`)
                    } finally {
                      setMerging({ ...merging, [album.albumId]: false })
                    }
                  }}
                >
                  {merging[album.albumId] ? 'Merging...' : 'Merge Albums'}
                </button>
              </div>
            )}
          </div>
        ))}
      </div>

      {albums.length === 0 && !loading && (
        <div className="card" style={{ textAlign: 'center', padding: '40px' }}>
          <p className="text-muted">No albums found. Start by indexing some images.</p>
        </div>
      )}

      {total > take && (
        <div style={{ display: 'flex', gap: '12px', justifyContent: 'center', marginTop: '24px' }}>
          <button
            className="btn btn-secondary"
            onClick={() => setSkip(Math.max(0, skip - take))}
            disabled={skip === 0}
          >
            Previous
          </button>
          <span style={{ display: 'flex', alignItems: 'center', color: 'var(--muted)' }}>
            {skip + 1}-{Math.min(skip + take, total)} of {total}
          </span>
          <button
            className="btn btn-secondary"
            onClick={() => setSkip(skip + take)}
            disabled={skip + take >= total}
          >
            Next
          </button>
        </div>
      )}
    </div>
  )
}

export default Albums

