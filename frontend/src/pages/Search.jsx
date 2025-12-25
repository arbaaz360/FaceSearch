import { useState } from 'react'
import { searchText, searchImage, searchFace } from '../services/api'

function Search() {
  const [query, setQuery] = useState('')
  const [results, setResults] = useState(null)
  const [loading, setLoading] = useState(false)
  const [searchType, setSearchType] = useState('text')
  const [selectedFile, setSelectedFile] = useState(null)
  const [preview, setPreview] = useState(null)
  const [enlargedImage, setEnlargedImage] = useState(null)

  const handleTextSearch = async () => {
    if (!query.trim()) return
    setLoading(true)
    try {
      const data = await searchText(query)
      setResults(data)
    } catch (error) {
      console.error('Search failed:', error)
      alert('Search failed')
    } finally {
      setLoading(false)
    }
  }

  const handleImageSearch = async () => {
    if (!selectedFile) return
    setLoading(true)
    try {
      const data = await searchImage(selectedFile)
      setResults(data)
    } catch (error) {
      console.error('Search failed:', error)
      alert('Search failed')
    } finally {
      setLoading(false)
    }
  }

  const handleFaceSearch = async () => {
    if (!selectedFile) return
    setLoading(true)
    try {
      const data = await searchFace(selectedFile)
      // Face search returns { results: [...] } instead of { hits: [...] }
      // Convert to same format as other searches for display
      if (data.results) {
        setResults({ hits: data.results.map(r => ({
          score: r.score,
          albumId: r.albumId,
          path: r.absolutePath,
          imageId: r.imageId,
          previewBase64: r.previewUrl, // Face search now includes preview in previewUrl
        })) })
      } else {
        setResults(data)
      }
    } catch (error) {
      console.error('Search failed:', error)
      alert('Search failed: ' + (error.response?.data?.detail || error.message))
    } finally {
      setLoading(false)
    }
  }

  const handleFileChange = (e) => {
    const file = e.target.files[0]
    if (file) {
      setSelectedFile(file)
      const reader = new FileReader()
      reader.onload = (e) => setPreview(e.target.result)
      reader.readAsDataURL(file)
    }
  }

  return (
    <div>
      <div className="page-header">
        <h1>Search</h1>
        <p>Search images by text, image, or face</p>
      </div>

      <div className="card" style={{ marginBottom: '24px' }}>
        <div style={{ display: 'flex', gap: '12px', marginBottom: '16px' }}>
          <button
            className={`btn ${searchType === 'text' ? 'btn-primary' : 'btn-secondary'}`}
            onClick={() => setSearchType('text')}
          >
            Text Search
          </button>
          <button
            className={`btn ${searchType === 'image' ? 'btn-primary' : 'btn-secondary'}`}
            onClick={() => setSearchType('image')}
          >
            Image Search
          </button>
          <button
            className={`btn ${searchType === 'face' ? 'btn-primary' : 'btn-secondary'}`}
            onClick={() => setSearchType('face')}
          >
            Face Search
          </button>
        </div>

        {searchType === 'text' ? (
          <div style={{ display: 'flex', gap: '8px' }}>
            <input
              className="input"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              onKeyPress={(e) => e.key === 'Enter' && handleTextSearch()}
              placeholder="Enter search query..."
            />
            <button className="btn btn-primary" onClick={handleTextSearch} disabled={loading || !query.trim()}>
              Search
            </button>
          </div>
        ) : (
          <div>
            <input
              type="file"
              accept="image/*"
              onChange={handleFileChange}
              style={{ marginBottom: '12px' }}
            />
            {preview && (
              <img
                src={preview}
                alt="Preview"
                style={{
                  maxWidth: '300px',
                  maxHeight: '300px',
                  borderRadius: '8px',
                  marginBottom: '12px',
                  display: 'block',
                }}
              />
            )}
            <button
              className="btn btn-primary"
              onClick={searchType === 'face' ? handleFaceSearch : handleImageSearch}
              disabled={loading || !selectedFile}
            >
              {loading ? 'Searching...' : 'Search'}
            </button>
          </div>
        )}
      </div>

      {loading && <div className="loading">Searching...</div>}

      {results && (
        <div>
          <h2 style={{ marginBottom: '16px' }}>
            Results ({results.hits?.length || results.results?.length || 0})
          </h2>
          {(!results.hits || results.hits.length === 0) && (!results.results || results.results.length === 0) && (
            <div className="card" style={{ textAlign: 'center', padding: '40px' }}>
              <p className="text-muted">No results found. Try:</p>
              <ul style={{ textAlign: 'left', display: 'inline-block', marginTop: '16px' }}>
                <li>Using a clearer face image</li>
                <li>Ensuring the person's face is clearly visible</li>
                <li>Checking if the person has been indexed in the system</li>
                <li>Lowering the minimum score threshold (if applicable)</li>
              </ul>
            </div>
          )}
          <div className="grid grid-cols-3">
            {(results.hits || results.results || []).map((hit, idx) => (
              <div key={idx} className="card">
                {hit.previewBase64 ? (
                  <img
                    src={hit.previewBase64}
                    alt={`Result ${idx + 1}`}
                    style={{
                      width: '100%',
                      height: '200px',
                      objectFit: 'cover',
                      borderRadius: '8px',
                      marginBottom: '12px',
                      cursor: 'pointer',
                    }}
                    onClick={() => setEnlargedImage(hit.previewBase64)}
                  />
                ) : (
                  <div
                    style={{
                      width: '100%',
                      height: '200px',
                      backgroundColor: '#333',
                      borderRadius: '8px',
                      marginBottom: '12px',
                      display: 'flex',
                      alignItems: 'center',
                      justifyContent: 'center',
                      color: '#999',
                    }}
                  >
                    No preview
                  </div>
                )}
                <div>
                  <div style={{ marginBottom: '8px' }}>
                    <strong>Score: </strong>
                    <span>{((hit.score || 0) * 100).toFixed(1)}%</span>
                  </div>
                  {hit.albumId && (
                    <div className="text-muted text-sm">
                      Album: <a href={`/albums/${hit.albumId}`} style={{ color: '#2196F3' }}>{hit.albumId}</a>
                    </div>
                  )}
                  {(hit.path || hit.absolutePath) && (
                    <div className="text-muted text-sm" style={{ marginTop: '4px' }}>
                      <div style={{ wordBreak: 'break-all', fontSize: '10px', opacity: 0.7 }}>
                        {(hit.path || hit.absolutePath).split(/[\\/]/).pop()}
                      </div>
                      <details style={{ marginTop: '4px' }}>
                        <summary style={{ cursor: 'pointer', fontSize: '10px', opacity: 0.6 }}>
                          Show full path
                        </summary>
                        <div style={{ wordBreak: 'break-all', fontSize: '10px', marginTop: '4px', fontFamily: 'monospace' }}>
                          {hit.path || hit.absolutePath}
                        </div>
                      </details>
                    </div>
                  )}
                </div>
              </div>
            ))}
          </div>
        </div>
      )}

      {enlargedImage && (
        <div
          style={{
            position: 'fixed',
            top: 0,
            left: 0,
            right: 0,
            bottom: 0,
            backgroundColor: 'rgba(0, 0, 0, 0.9)',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            zIndex: 1000,
            cursor: 'pointer',
          }}
          onClick={() => setEnlargedImage(null)}
        >
          <img
            src={enlargedImage}
            alt="Enlarged"
            style={{
              maxWidth: '90%',
              maxHeight: '90%',
              objectFit: 'contain',
            }}
          />
          <button
            onClick={(e) => {
              e.stopPropagation()
              setEnlargedImage(null)
            }}
            style={{
              position: 'absolute',
              top: '20px',
              right: '20px',
              background: 'rgba(255, 255, 255, 0.2)',
              border: 'none',
              color: 'white',
              fontSize: '32px',
              width: '50px',
              height: '50px',
              borderRadius: '50%',
              cursor: 'pointer',
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
            }}
          >
            ×
          </button>
        </div>
      )}
    </div>
  )
}

export default Search

