import { useState, useEffect } from 'react'
import { getUnresolvedFaces, resolveFace, whoIsThis } from '../services/api'
import Pagination from '../components/Pagination'

function FaceReview() {
  const [faces, setFaces] = useState([])
  const [loading, setLoading] = useState(true)
  const [selectedFace, setSelectedFace] = useState(null)
  const [resolution, setResolution] = useState({ accept: true, albumId: '', displayName: '', instagramHandle: '' })
  const [skip, setSkip] = useState(0)
  const [total, setTotal] = useState(0)
  const take = 20

  useEffect(() => {
    loadFaces()
  }, [skip])

  const loadFaces = async () => {
    setLoading(true)
    try {
      const data = await getUnresolvedFaces(skip, take)
      setFaces(data.faces || [])
      setTotal(data.total || 0)
    } catch (error) {
      console.error('Failed to load faces:', error)
    } finally {
      setLoading(false)
    }
  }

  const handleResolve = async (faceId) => {
    try {
      await resolveFace(faceId, resolution)
      setSelectedFace(null)
      loadFaces()
    } catch (error) {
      console.error('Failed to resolve:', error)
      alert('Failed to resolve face')
    }
  }

  const handleFileUpload = async (e) => {
    const file = e.target.files[0]
    if (!file) return

    try {
      const data = await whoIsThis(file)
      alert(`Found ${data.faces?.length || 0} face(s)`)
      loadFaces()
    } catch (error) {
      console.error('Upload failed:', error)
      alert('Upload failed')
    }
  }

  if (loading && faces.length === 0) {
    return <div className="loading">Loading faces...</div>
  }

  return (
    <div>
      <div className="page-header">
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
          <div>
            <h1>Face Review</h1>
            <p>Review and resolve unresolved faces ({total} pending)</p>
          </div>
          <div style={{ display: 'flex', gap: '8px' }}>
            <label className="btn btn-secondary" style={{ cursor: 'pointer' }}>
              Upload Image
              <input
                type="file"
                accept="image/*"
                onChange={handleFileUpload}
                style={{ display: 'none' }}
              />
            </label>
            <button className="btn btn-secondary" onClick={loadFaces}>
              Refresh
            </button>
          </div>
        </div>
      </div>

      {faces.length === 0 ? (
        <div className="card" style={{ textAlign: 'center', padding: '40px' }}>
          <p className="text-muted">No unresolved faces. All clear!</p>
        </div>
      ) : (
        <div className="grid grid-cols-3">
          {faces.map((face) => (
            <div key={face.faceId} className="card">
              {face.thumbnailBase64 && (
                <img
                  src={face.thumbnailBase64}
                  alt="Face"
                  style={{
                    width: '100%',
                    height: '200px',
                    objectFit: 'cover',
                    borderRadius: '8px',
                    marginBottom: '12px',
                    cursor: 'pointer',
                  }}
                  onClick={() => {
                    setSelectedFace(face)
                    setResolution({
                      accept: true,
                      albumId: face.suggestedAlbumId || '',
                      displayName: '',
                      instagramHandle: '',
                    })
                  }}
                />
              )}
              <div>
                <div className="text-muted text-sm" style={{ marginBottom: '4px' }}>
                  Gender: {face.gender || 'unknown'}
                </div>
                {face.suggestedAlbumId && (
                  <div className="text-muted text-sm">
                    Suggested: {face.suggestedAlbumId} ({(face.suggestedScore * 100).toFixed(1)}%)
                  </div>
                )}
              </div>
            </div>
          ))}
        </div>
      )}

      {selectedFace && (
        <div className="modal-overlay" onClick={() => setSelectedFace(null)}>
          <div className="modal-content" onClick={(e) => e.stopPropagation()}>
            <div className="modal-header">
              <h2>Resolve Face</h2>
              <button className="modal-close" onClick={() => setSelectedFace(null)}>×</button>
            </div>
            {selectedFace.thumbnailBase64 && (
              <img
                src={selectedFace.thumbnailBase64}
                alt="Face"
                style={{ width: '100%', maxHeight: '300px', objectFit: 'cover', borderRadius: '8px', marginBottom: '20px' }}
              />
            )}
            <div style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
              <div>
                <label style={{ display: 'block', marginBottom: '4px' }}>Accept</label>
                <input
                  type="checkbox"
                  checked={resolution.accept}
                  onChange={(e) => setResolution({ ...resolution, accept: e.target.checked })}
                />
              </div>
              <div>
                <label style={{ display: 'block', marginBottom: '4px' }}>Album ID</label>
                <input
                  className="input"
                  value={resolution.albumId}
                  onChange={(e) => setResolution({ ...resolution, albumId: e.target.value })}
                  placeholder="album-id"
                />
              </div>
              <div>
                <label style={{ display: 'block', marginBottom: '4px' }}>Display Name</label>
                <input
                  className="input"
                  value={resolution.displayName}
                  onChange={(e) => setResolution({ ...resolution, displayName: e.target.value })}
                  placeholder="Person's name"
                />
              </div>
              <div>
                <label style={{ display: 'block', marginBottom: '4px' }}>Instagram Handle</label>
                <input
                  className="input"
                  value={resolution.instagramHandle}
                  onChange={(e) => setResolution({ ...resolution, instagramHandle: e.target.value })}
                  placeholder="@username"
                />
              </div>
              <div style={{ display: 'flex', gap: '8px' }}>
                <button className="btn btn-primary" onClick={() => handleResolve(selectedFace.faceId)}>
                  Resolve
                </button>
                <button className="btn btn-secondary" onClick={() => setSelectedFace(null)}>
                  Cancel
                </button>
              </div>
            </div>
          </div>
        </div>
      )}
      
      <Pagination 
        skip={skip} 
        take={take} 
        total={total} 
        onPageChange={setSkip} 
      />
    </div>
  )
}

export default FaceReview

