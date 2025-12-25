import { useState, useEffect } from 'react'
import { useParams, useNavigate, Link } from 'react-router-dom'
import {
  getAlbum,
  getDominantFace,
  getAlbumStatus,
  updateAlbumIdentity,
  setAlbumTags,
  recomputeAlbum,
  mergeAlbums,
  clearSuspiciousFlag,
  getAlbumClusters,
  confirmAggregator,
  markAsJunk,
} from '../services/api'

function AlbumDetail() {
  const { albumId } = useParams()
  const navigate = useNavigate()
  const [album, setAlbum] = useState(null)
  const [status, setStatus] = useState(null)
  const [dominantFace, setDominantFace] = useState(null)
  const [clusters, setClusters] = useState(null)
  const [loading, setLoading] = useState(true)
  const [editing, setEditing] = useState(false)
  const [formData, setFormData] = useState({ displayName: '', instagramHandle: '', tags: [] })
  const [newTag, setNewTag] = useState('')
  const [showImage, setShowImage] = useState(false)
  const [merging, setMerging] = useState(false)

  // Helper function to clean albumId: remove leading/trailing underscores only
  // This preserves underscores that are naturally in usernames (e.g., "user_name")
  const cleanAlbumId = (id) => {
    if (!id) return id
    // Remove leading underscores
    let cleaned = id.replace(/^_+/, '')
    // Remove trailing underscores
    cleaned = cleaned.replace(/_+$/, '')
    return cleaned
  }

  // Get cleaned albumId for display and Instagram link
  const cleanedAlbumId = cleanAlbumId(albumId)

  useEffect(() => {
    loadData()
  }, [albumId])

  const loadData = async () => {
    setLoading(true)
    try {
      const [albumData, statusData, faceData, clustersData] = await Promise.all([
        getAlbum(albumId),
        getAlbumStatus(albumId).catch(() => null),
        getDominantFace(albumId).catch(() => null),
        getAlbumClusters(albumId, 10).catch(() => null),
      ])
      setAlbum(albumData)
      setStatus(statusData)
      setDominantFace(faceData)
      setClusters(clustersData)
      setFormData({
        displayName: albumData.displayName || '',
        instagramHandle: albumData.instagramHandle || '',
        tags: albumData.tags || [],
      })
    } catch (error) {
      console.error('Failed to load album:', error)
    } finally {
      setLoading(false)
    }
  }

  const handleSave = async () => {
    try {
      await updateAlbumIdentity(albumId, {
        displayName: formData.displayName,
        instagramHandle: formData.instagramHandle,
      })
      if (formData.tags.length > 0) {
        await setAlbumTags(albumId, formData.tags)
      }
      setEditing(false)
      loadData()
    } catch (error) {
      console.error('Failed to save:', error)
      alert('Failed to save changes')
    }
  }

  const handleAddTag = () => {
    if (newTag.trim() && !formData.tags.includes(newTag.trim())) {
      setFormData({ ...formData, tags: [...formData.tags, newTag.trim()] })
      setNewTag('')
    }
  }

  const handleRemoveTag = (tag) => {
    setFormData({ ...formData, tags: formData.tags.filter((t) => t !== tag) })
  }

  const handleRecompute = async () => {
    if (confirm('Recompute album dominance and aggregator detection?')) {
      try {
        await recomputeAlbum(albumId)
        loadData()
      } catch (error) {
        console.error('Failed to recompute:', error)
        alert('Failed to recompute album')
      }
    }
  }

  if (loading) {
    return <div className="loading">Loading album...</div>
  }

  if (!album) {
    return <div className="card">Album not found</div>
  }

  return (
    <div>
      <div className="page-header">
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'start' }}>
          <div>
            <h1>{album.displayName || cleanedAlbumId}</h1>
            {album.instagramHandle && <p className="text-muted">{album.instagramHandle}</p>}
          </div>
          <button className="btn btn-secondary" onClick={() => navigate('/albums')}>
            ← Back to Albums
          </button>
        </div>
      </div>

      <div className="grid grid-cols-2" style={{ marginBottom: '24px' }}>
        <div className="card">
          <h2 style={{ marginBottom: '16px', fontSize: '18px' }}>Dominant Face</h2>
          {dominantFace?.previewBase64 ? (
            <div>
              <img
                src={dominantFace.previewBase64}
                alt="Dominant face"
                className="image-preview"
                onClick={() => setShowImage(true)}
              />
              {dominantFace.imagePath && (
                <div style={{ marginTop: '8px', display: 'flex', flexDirection: 'column', gap: '4px' }}>
                  {dominantFace.imagePath.startsWith('http://') || dominantFace.imagePath.startsWith('https://') ? (
                    <a
                      href={dominantFace.imagePath}
                      target="_blank"
                      rel="noopener noreferrer"
                      className="text-muted text-sm"
                      style={{ 
                        textDecoration: 'none',
                        color: 'var(--muted)',
                        wordBreak: 'break-all',
                        maxWidth: '100%',
                        display: 'block'
                      }}
                      title={dominantFace.imagePath}
                    >
                      {dominantFace.imagePath.length > 60 
                        ? `${dominantFace.imagePath.substring(0, 57)}...` 
                        : dominantFace.imagePath}
                    </a>
                  ) : (
                    <p className="text-muted text-sm" title={dominantFace.imagePath}>
                      {dominantFace.imagePath.split(/[\\/]/).pop()}
                    </p>
                  )}
                </div>
              )}
              {/* Instagram profile link */}
              {(() => {
                // Determine Instagram username: 
                // 1. Use instagramHandle if set
                // 2. Otherwise use cleaned albumId (with leading/trailing underscores removed)
                // This works for both __username__ format and plain username format
                const igUsername = album.instagramHandle || cleanedAlbumId;
                
                // Show link if we have a username (always show for Instagram accounts)
                return igUsername ? (
                  <div style={{ marginTop: '8px' }}>
                    <a
                      href={`https://instagram.com/${igUsername}`}
                      target="_blank"
                      rel="noopener noreferrer"
                      style={{
                        display: 'inline-flex',
                        alignItems: 'center',
                        gap: '6px',
                        color: '#E4405F',
                        textDecoration: 'none',
                        fontSize: '14px',
                        fontWeight: 500
                      }}
                    >
                      <span>📷</span>
                      <span>@{igUsername}</span>
                    </a>
                  </div>
                ) : null;
              })()}
            </div>
          ) : (
            <p className="text-muted">No dominant face available</p>
          )}
        </div>

        <div className="card">
          <h2 style={{ marginBottom: '16px', fontSize: '18px' }}>Statistics</h2>
          <div style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
            <div>
              <span className="text-muted">Total Images: </span>
              <strong>{album.imageCount}</strong>
            </div>
            <div>
              <span className="text-muted">Face Images: </span>
              <strong>{album.faceImageCount}</strong>
            </div>
            {album.dominantSubject && (
              <div>
                <span className="text-muted">Dominant Cluster: </span>
                <strong>{album.dominantSubject.imageCount} occurrences</strong>
                <div className="text-muted text-sm" style={{ marginTop: '4px' }}>
                  Ratio: {album.dominantSubject.ratio.toFixed(2)}
                </div>
              </div>
            )}
            {status && (
              <>
                <div>
                  <span className="text-muted">Pending: </span>
                  <strong>{status.pendingImages}</strong>
                </div>
                <div>
                  <span className="text-muted">Done: </span>
                  <strong>{status.doneImages}</strong>
                </div>
                <div>
                  <span className="text-muted">Errors: </span>
                  <strong style={{ color: status.errorImages > 0 ? 'var(--danger)' : 'inherit' }}>
                    {status.errorImages}
                  </strong>
                </div>
                <div>
                  <span className="text-muted">Progress: </span>
                  <strong>{status.progressPercent}%</strong>
                </div>
              </>
            )}
          </div>
        </div>
      </div>

      <div className="card" style={{ marginBottom: '24px' }}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '16px' }}>
          <h2 style={{ fontSize: '18px' }}>Identity</h2>
          {!editing ? (
            <button className="btn btn-secondary" onClick={() => setEditing(true)}>
              Edit
            </button>
          ) : (
            <div style={{ display: 'flex', gap: '8px' }}>
              <button className="btn btn-primary" onClick={handleSave}>
                Save
              </button>
              <button className="btn btn-secondary" onClick={() => { setEditing(false); loadData(); }}>
                Cancel
              </button>
            </div>
          )}
        </div>

        {editing ? (
          <div style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
            <div>
              <label style={{ display: 'block', marginBottom: '4px', fontSize: '14px' }}>Display Name</label>
              <input
                className="input"
                value={formData.displayName}
                onChange={(e) => setFormData({ ...formData, displayName: e.target.value })}
                placeholder="Person's name"
              />
            </div>
            <div>
              <label style={{ display: 'block', marginBottom: '4px', fontSize: '14px' }}>Instagram Handle</label>
              <input
                className="input"
                value={formData.instagramHandle}
                onChange={(e) => setFormData({ ...formData, instagramHandle: e.target.value })}
                placeholder="@username"
              />
            </div>
            <div>
              <label style={{ display: 'block', marginBottom: '4px', fontSize: '14px' }}>Tags</label>
              <div style={{ display: 'flex', gap: '8px', marginBottom: '8px' }}>
                <input
                  className="input"
                  value={newTag}
                  onChange={(e) => setNewTag(e.target.value)}
                  onKeyPress={(e) => e.key === 'Enter' && handleAddTag()}
                  placeholder="Add tag"
                />
                <button className="btn btn-secondary" onClick={handleAddTag}>
                  Add
                </button>
              </div>
              <div style={{ display: 'flex', flexWrap: 'wrap', gap: '6px' }}>
                {formData.tags.map((tag) => (
                  <span
                    key={tag}
                    style={{
                      padding: '4px 10px',
                      background: 'var(--bg)',
                      border: '1px solid var(--border)',
                      borderRadius: '999px',
                      fontSize: '13px',
                      display: 'flex',
                      alignItems: 'center',
                      gap: '6px',
                    }}
                  >
                    {tag}
                    <button
                      onClick={() => handleRemoveTag(tag)}
                      style={{
                        background: 'none',
                        border: 'none',
                        color: 'var(--muted)',
                        cursor: 'pointer',
                        padding: 0,
                        fontSize: '16px',
                        lineHeight: 1,
                      }}
                    >
                      ×
                    </button>
                  </span>
                ))}
              </div>
            </div>
          </div>
        ) : (
          <div>
            <div style={{ marginBottom: '12px' }}>
              <span className="text-muted">Display Name: </span>
              <strong>{album.displayName || 'Not set'}</strong>
            </div>
            <div style={{ marginBottom: '12px' }}>
              <span className="text-muted">Instagram: </span>
              <strong>{album.instagramHandle || 'Not set'}</strong>
            </div>
            {album.tags && album.tags.length > 0 && (
              <div>
                <span className="text-muted">Tags: </span>
                <div style={{ display: 'flex', flexWrap: 'wrap', gap: '6px', marginTop: '8px' }}>
                  {album.tags.map((tag) => (
                    <span
                      key={tag}
                      style={{
                        padding: '4px 10px',
                        background: 'var(--bg)',
                        border: '1px solid var(--border)',
                        borderRadius: '999px',
                        fontSize: '13px',
                      }}
                    >
                      {tag}
                    </span>
                  ))}
                </div>
              </div>
            )}
          </div>
        )}
      </div>

      {album.suspiciousAggregator && (
        <div className="card" style={{ border: '2px solid #7f1d1d', marginBottom: '24px' }}>
          <h2 style={{ fontSize: '18px', marginBottom: '16px', color: '#f44336' }}>
            ⚠️ Suspicious Aggregator
          </h2>
          <div style={{ marginBottom: '12px' }}>
            <p style={{ marginBottom: '8px' }}>
              This album has been flagged as a suspicious aggregator. This typically means the album contains multiple different people (dominant subject ratio is below threshold).
            </p>
            {album.dominantSubject && (
              <div className="text-muted text-sm" style={{ marginBottom: '8px' }}>
                <strong>Dominance Ratio:</strong> {(album.dominantSubject.ratio * 100).toFixed(1)}% (threshold: 50%)
                <br />
                <strong>Dominant Cluster:</strong> {album.dominantSubject.imageCount} images out of {album.faceImageCount} face images
              </div>
            )}
            <p className="text-muted text-sm">
              If this album actually represents a single person, you can clear the suspicious flag. Otherwise, you can confirm it as an aggregator (it will be hidden from the default albums list) or review the images to split them.
            </p>
          </div>
          <div style={{ display: 'flex', gap: '8px', flexWrap: 'wrap' }}>
            <button
              className="btn btn-secondary"
              onClick={async () => {
                if (!confirm('Clear the suspicious aggregator flag for this album?')) {
                  return
                }
                try {
                  await clearSuspiciousFlag(albumId)
                  alert('Suspicious flag cleared successfully!')
                  loadData()
                } catch (error) {
                  console.error('Failed to clear flag:', error)
                  alert(`Failed to clear flag: ${error.response?.data?.message || error.message}`)
                }
              }}
            >
              Clear Suspicious Flag
            </button>
            <button
              className="btn btn-danger"
              onClick={async () => {
                if (!confirm('Confirm this album as an aggregator? It will be hidden from the default albums list (you can show it again with a filter).')) {
                  return
                }
                try {
                  await confirmAggregator(albumId)
                  alert('Album confirmed as aggregator. It will be hidden from the default albums list.')
                  loadData()
                } catch (error) {
                  console.error('Failed to confirm aggregator:', error)
                  alert(`Failed to confirm aggregator: ${error.response?.data?.message || error.message}`)
                }
              }}
            >
              Confirm as Aggregator
            </button>
            <button
              className="btn btn-secondary"
              onClick={() => navigate('/reviews')}
            >
              View Reviews
            </button>
          </div>
        </div>
      )}

      {album && (
        <div className="card" style={{ marginBottom: '24px' }}>
          <h2 style={{ fontSize: '18px', marginBottom: '16px' }}>
            Top Face Clusters
            {clusters && clusters.totalClusters !== undefined && ` (${clusters.totalClusters} total)`}
          </h2>
          {clusters && clusters.clusters && clusters.clusters.length > 0 ? (
            <>
              <p className="text-muted text-sm" style={{ marginBottom: '16px' }}>
                These are the most common face clusters in this album, sorted by number of images. The dominant cluster is highlighted.
              </p>
              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(150px, 1fr))', gap: '16px' }}>
                {clusters.clusters.map((cluster, idx) => (
              <div
                key={cluster.clusterId}
                style={{
                  border: cluster.isDominant ? '2px solid #4caf50' : '1px solid #ddd',
                  borderRadius: '8px',
                  padding: '12px',
                  backgroundColor: cluster.isDominant ? 'rgba(76, 175, 80, 0.1)' : 'transparent',
                  textAlign: 'center',
                }}
              >
                {cluster.previewBase64 ? (
                  <img
                    src={cluster.previewBase64}
                    alt={`Cluster ${idx + 1}`}
                    style={{
                      width: '100%',
                      height: '150px',
                      objectFit: 'cover',
                      borderRadius: '4px',
                      marginBottom: '8px',
                    }}
                  />
                ) : (
                  <div
                    style={{
                      width: '100%',
                      height: '150px',
                      backgroundColor: '#333',
                      borderRadius: '4px',
                      marginBottom: '8px',
                      display: 'flex',
                      alignItems: 'center',
                      justifyContent: 'center',
                      color: '#999',
                    }}
                  >
                    No preview
                  </div>
                )}
                <div style={{ fontSize: '12px', marginBottom: '4px' }}>
                  <strong>#{idx + 1}</strong>
                  {cluster.isDominant && (
                    <span style={{ color: '#4caf50', marginLeft: '4px' }}>★ Dominant</span>
                  )}
                </div>
                <div style={{ fontSize: '11px', color: '#999' }}>
                  <div>{cluster.imageCount} images</div>
                  <div>{cluster.faceCount} faces</div>
                </div>
              </div>
            ))}
              </div>
            </>
          ) : clusters === null ? (
            <p className="text-muted">Loading clusters...</p>
          ) : (
            <p className="text-muted">No clusters found for this album.</p>
          )}
        </div>
      )}

      {album.isSuspectedMergeCandidate && album.existingSuspectedDuplicateAlbumId && (
        <div className="card" style={{ border: '2px solid #ff9800', marginBottom: '24px' }}>
          <h2 style={{ fontSize: '18px', marginBottom: '16px', color: '#ff9800' }}>
            🔄 Merge Candidate
          </h2>
          <div style={{ marginBottom: '12px' }}>
            <p style={{ marginBottom: '8px' }}>
              This album is flagged as a duplicate of:{' '}
              <Link to={`/albums/${album.existingSuspectedDuplicateAlbumId}`} style={{ color: '#2196F3' }}>
                {album.existingSuspectedDuplicateAlbumId}
              </Link>
            </p>
            <p className="text-muted text-sm">
              Merging will move all images, clusters, and vectors from this album into the target album.
            </p>
          </div>
          <button
            className="btn btn-primary"
            disabled={merging}
            onClick={async () => {
              if (!confirm(`Merge album "${albumId}" into "${album.existingSuspectedDuplicateAlbumId}"?\n\nThis will move all images, clusters, and vectors from the source to the target album.`)) {
                return
              }
              setMerging(true)
              try {
                await mergeAlbums(albumId, album.existingSuspectedDuplicateAlbumId)
                alert('Albums merged successfully!')
                navigate(`/albums/${album.existingSuspectedDuplicateAlbumId}`)
              } catch (error) {
                console.error('Merge failed:', error)
                alert(`Merge failed: ${error.response?.data?.message || error.message}`)
              } finally {
                setMerging(false)
              }
            }}
          >
            {merging ? 'Merging...' : 'Merge Albums'}
          </button>
        </div>
      )}

      <div className="card">
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '16px' }}>
          <h2 style={{ fontSize: '18px', marginBottom: '16px' }}>Actions</h2>
          <div style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
            <button
              className="btn btn-secondary"
              onClick={handleRecompute}
            >
              Recompute Album
            </button>
            <button
              className="btn btn-danger"
              onClick={async () => {
                const deleteData = confirm(
                  'Mark this album as junk/blacklisted?\n\n' +
                  'This will:\n' +
                  '- Hide it from all album lists\n' +
                  '- Hide it from search results\n' +
                  '- Prevent it from being re-indexed\n\n' +
                  'Do you also want to DELETE all images and clusters? (Click OK to delete, Cancel to keep data)'
                )
                
                if (!confirm('Are you sure you want to mark this album as junk? This action cannot be easily undone.')) {
                  return
                }
                
                try {
                  const result = await markAsJunk(albumId, deleteData)
                  alert(
                    `Album marked as junk successfully.\n` +
                    (deleteData 
                      ? `Deleted ${result.deletedImages} images and ${result.deletedClusters} clusters.`
                      : 'Data kept (album is hidden but not deleted).')
                  )
                  navigate('/albums') // Navigate away since album is now hidden
                } catch (error) {
                  console.error('Failed to mark as junk:', error)
                  alert(`Failed to mark as junk: ${error.response?.data?.message || error.message}`)
                }
              }}
            >
              🗑️ Mark as Junk / Blacklist
            </button>
          </div>
        </div>
      </div>

      {showImage && dominantFace?.previewBase64 && (
        <div className="image-fullscreen" onClick={() => setShowImage(false)}>
          <img src={dominantFace.previewBase64} alt="Dominant face" />
          <button className="close" onClick={() => setShowImage(false)}>×</button>
        </div>
      )}
    </div>
  )
}

export default AlbumDetail

