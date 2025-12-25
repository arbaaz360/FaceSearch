import { useState, useEffect } from 'react'
import { getReviews, getMergeCandidates, updateReviewStatus, mergeAlbums } from '../services/api'
import { Link, useNavigate } from 'react-router-dom'
import Pagination from '../components/Pagination'

function Reviews() {
  const [reviews, setReviews] = useState([])
  const [mergeCandidates, setMergeCandidates] = useState([])
  const [loading, setLoading] = useState(true)
  const [filter, setFilter] = useState('all') // 'all', 'AlbumMerge', 'AggregatorAlbum'
  const [processing, setProcessing] = useState({})
  const [skip, setSkip] = useState(0)
  const take = 20
  const navigate = useNavigate()

  const [allReviews, setAllReviews] = useState([])

  useEffect(() => {
    loadData()
  }, [filter])

  useEffect(() => {
    // Apply client-side pagination when skip changes
    const paginated = allReviews.slice(skip, skip + take)
    setReviews(paginated)
  }, [skip, allReviews])

  const loadData = async () => {
    setLoading(true)
    setSkip(0) // Reset to first page when filter changes
    try {
      const [reviewsData, candidatesData] = await Promise.all([
        getReviews(filter === 'all' ? null : filter),
        getMergeCandidates(),
      ])
      const reviews = reviewsData.reviews || []
      setAllReviews(reviews)
      setReviews(reviews.slice(0, take)) // Initial page
      setMergeCandidates(candidatesData.candidates || [])
    } catch (error) {
      console.error('Failed to load reviews:', error)
      alert('Failed to load reviews')
    } finally {
      setLoading(false)
    }
  }

  if (loading) {
    return <div className="loading">Loading reviews...</div>
  }

  return (
    <div>
      <div className="page-header">
        <div>
          <h1>Reviews</h1>
          <p>Album merge candidates and aggregator reviews</p>
        </div>
        <div style={{ display: 'flex', gap: '8px', alignItems: 'center' }}>
          <select
            className="input"
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            style={{ width: 'auto' }}
          >
            <option value="all">All Reviews</option>
            <option value="AlbumMerge">Album Merges</option>
            <option value="AggregatorAlbum">Aggregators</option>
          </select>
          <button className="btn btn-secondary" onClick={loadData}>
            Refresh
          </button>
        </div>
      </div>

      {/* Merge Candidates Section */}
      {mergeCandidates.length > 0 && (
        <div style={{ marginBottom: '32px' }}>
          <h2 style={{ marginBottom: '16px' }}>
            Merge Candidates ({mergeCandidates.length})
          </h2>
          <div className="grid grid-cols-2">
            {mergeCandidates.map((candidate) => (
              <div key={candidate.albumId} className="card" style={{ border: '2px solid #ff9800' }}>
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'start', marginBottom: '12px' }}>
                  <div>
                    <h3 style={{ margin: 0, marginBottom: '4px' }}>
                      <Link to={`/albums/${candidate.albumId}`} style={{ color: '#2196F3', textDecoration: 'none' }}>
                        {candidate.albumId}
                      </Link>
                    </h3>
                    {candidate.displayName && (
                      <div className="text-muted text-sm">{candidate.displayName}</div>
                    )}
                  </div>
                  <span className="badge" style={{ backgroundColor: '#ff9800' }}>
                    Merge Candidate
                  </span>
                </div>
                <div style={{ marginBottom: '8px' }}>
                  <div className="text-muted text-sm">
                    <strong>Duplicate Album:</strong>{' '}
                    <Link to={`/albums/${candidate.duplicateAlbumId}`} style={{ color: '#2196F3' }}>
                      {candidate.duplicateAlbumId}
                    </Link>
                  </div>
                  <div className="text-muted text-sm" style={{ marginTop: '4px' }}>
                    <strong>Images:</strong> {candidate.imageCount} | <strong>Faces:</strong> {candidate.faceImageCount}
                  </div>
                </div>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Review Records Section */}
      <div>
        <h2 style={{ marginBottom: '16px' }}>
          Review Records ({reviews.length})
        </h2>
        {reviews.length === 0 ? (
          <div className="card" style={{ textAlign: 'center', padding: '40px' }}>
            <p className="text-muted">No review records found.</p>
          </div>
        ) : (
          <div className="grid grid-cols-1">
            {reviews.map((review) => (
              <div
                key={review.id}
                className="card"
                style={{
                  border: review.status === 'pending' ? '2px solid #ff9800' : '1px solid #ddd',
                }}
              >
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'start', marginBottom: '12px' }}>
                  <div>
                    <h3 style={{ margin: 0, marginBottom: '4px' }}>
                      {review.type === 'AlbumMerge' ? '🔄 Album Merge' : '📊 Aggregator Album'}
                    </h3>
                    <div className="text-muted text-sm">ID: {review.id}</div>
                  </div>
                  <span
                    className="badge"
                    style={{
                      backgroundColor:
                        review.status === 'pending'
                          ? '#ff9800'
                          : review.status === 'approved'
                          ? '#4caf50'
                          : '#f44336',
                    }}
                  >
                    {review.status}
                  </span>
                </div>

                {review.type === 'AlbumMerge' && (
                  <div style={{ marginBottom: '12px' }}>
                    <div style={{ marginBottom: '8px' }}>
                      <strong>Source Album:</strong>{' '}
                      <Link to={`/albums/${review.albumId}`} style={{ color: '#2196F3' }}>
                        {review.albumId}
                      </Link>
                    </div>
                    {review.albumB && (
                      <div style={{ marginBottom: '8px' }}>
                        <strong>Target Album:</strong>{' '}
                        <Link to={`/albums/${review.albumB}`} style={{ color: '#2196F3' }}>
                          {review.albumB}
                        </Link>
                      </div>
                    )}
                    {review.similarity !== null && review.similarity !== undefined && (
                      <div className="text-muted text-sm" style={{ marginTop: '4px' }}>
                        <strong>Similarity:</strong> {(review.similarity * 100).toFixed(1)}%
                      </div>
                    )}
                    {review.ratio !== null && review.ratio !== undefined && (
                      <div className="text-muted text-sm">
                        <strong>Dominance Ratio:</strong> {(review.ratio * 100).toFixed(1)}%
                      </div>
                    )}
                  </div>
                )}

                {review.notes && (
                  <div style={{ marginBottom: '8px', padding: '8px', backgroundColor: '#f5f5f5', borderRadius: '4px' }}>
                    <div className="text-muted text-sm">{review.notes}</div>
                  </div>
                )}

                <div className="text-muted text-sm" style={{ marginTop: '8px', marginBottom: '12px' }}>
                  Created: {new Date(review.createdAt).toLocaleString()}
                </div>

                {review.status === 'pending' && (
                  <div style={{ display: 'flex', gap: '8px', marginTop: '12px' }}>
                    {review.type === 'AlbumMerge' && review.albumId && review.albumB && (
                      <button
                        className="btn btn-primary"
                        disabled={processing[review.id]}
                        onClick={async () => {
                          if (!confirm(`Merge album "${review.albumId}" into "${review.albumB}"?\n\nThis will move all images, clusters, and vectors from the source to the target album.`)) {
                            return
                          }
                          setProcessing({ ...processing, [review.id]: true })
                          try {
                            await mergeAlbums(review.albumId, review.albumB)
                            await updateReviewStatus(review.id, 'approved')
                            alert('Albums merged successfully!')
                            loadData()
                            navigate(`/albums/${review.albumB}`)
                          } catch (error) {
                            console.error('Merge failed:', error)
                            alert(`Merge failed: ${error.response?.data?.message || error.message}`)
                          } finally {
                            setProcessing({ ...processing, [review.id]: false })
                          }
                        }}
                      >
                        {processing[review.id] ? 'Merging...' : 'Merge Albums'}
                      </button>
                    )}
                    <button
                      className="btn btn-secondary"
                      disabled={processing[review.id]}
                      onClick={async () => {
                        if (!confirm(`Approve this ${review.type} review?`)) return
                        setProcessing({ ...processing, [review.id]: true })
                        try {
                          await updateReviewStatus(review.id, 'approved')
                          loadData()
                        } catch (error) {
                          console.error('Failed to approve:', error)
                          alert('Failed to approve review')
                        } finally {
                          setProcessing({ ...processing, [review.id]: false })
                        }
                      }}
                    >
                      Approve
                    </button>
                    <button
                      className="btn btn-danger"
                      disabled={processing[review.id]}
                      onClick={async () => {
                        if (!confirm(`Reject this ${review.type} review?`)) return
                        setProcessing({ ...processing, [review.id]: true })
                        try {
                          await updateReviewStatus(review.id, 'rejected')
                          loadData()
                        } catch (error) {
                          console.error('Failed to reject:', error)
                          alert('Failed to reject review')
                        } finally {
                          setProcessing({ ...processing, [review.id]: false })
                        }
                      }}
                    >
                      Reject
                    </button>
                  </div>
                )}
              </div>
            ))}
          </div>
        )}
        
        {allReviews.length > take && (
          <Pagination 
            skip={skip} 
            take={take} 
            total={allReviews.length} 
            onPageChange={setSkip} 
          />
        )}
      </div>
    </div>
  )
}

export default Reviews

