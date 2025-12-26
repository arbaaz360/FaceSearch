import { useState, useEffect } from 'react'
import { seedDirectory, seedInstagram, getInstagramStatus, resetInstagramIngestion, resetSingleInstagramAccount, factoryReset, resetErrors, getProcessingStats, getUsernamesWithoutPosts, fetchPosts, getFetchStatus } from '../services/api'
import Pagination from '../components/Pagination'

function Indexing() {
  const [directoryPath, setDirectoryPath] = useState('')
  const [albumId, setAlbumId] = useState('')
  const [includeVideos, setIncludeVideos] = useState(false)
  const [seedSubdirectoriesAsAlbums, setSeedSubdirectoriesAsAlbums] = useState(false)
  const [loading, setLoading] = useState(false)
  const [result, setResult] = useState(null)

  // Instagram ingestion state
  const [activeTab, setActiveTab] = useState('directory') // 'directory', 'instagram', or 'post-fetch'
  const [instagramTargetUsername, setInstagramTargetUsername] = useState('')
  const [instagramFollowingUsername, setInstagramFollowingUsername] = useState('')
  const [instagramIncludeVideos, setInstagramIncludeVideos] = useState(false)
  const [instagramLoading, setInstagramLoading] = useState(false)
  const [instagramResult, setInstagramResult] = useState(null)
  const [instagramStatuses, setInstagramStatuses] = useState([])
  const [statusLoading, setStatusLoading] = useState(false)
  const [resetting, setResetting] = useState(false)
  const [resettingErrors, setResettingErrors] = useState(false)
  const [resettingUsername, setResettingUsername] = useState(null) // Track which username is being reset
  const [statusSkip, setStatusSkip] = useState(0)
  const statusTake = 20
  const [processingStats, setProcessingStats] = useState(null)
  const [loadingStats, setLoadingStats] = useState(false)

  // Post Fetch state
  const [usernamesWithoutPosts, setUsernamesWithoutPosts] = useState([])
  const [loadingUsernames, setLoadingUsernames] = useState(false)
  const [selectedUsernames, setSelectedUsernames] = useState(new Set())
  const [fetchingPosts, setFetchingPosts] = useState(false)
  const [fetchId, setFetchId] = useState(null)
  const [fetchStatus, setFetchStatus] = useState(null)
  const [postFetchTargetUsername, setPostFetchTargetUsername] = useState('')

  const handleSeed = async () => {
    if (!directoryPath) {
      alert('Please provide directory path')
      return
    }
    if (!seedSubdirectoriesAsAlbums && !albumId) {
      alert('Please provide album ID (or enable "Seed Subdirectories as Albums")')
      return
    }

    setLoading(true)
    setResult(null)
    try {
      const data = await seedDirectory(directoryPath, albumId, includeVideos, seedSubdirectoriesAsAlbums)
      setResult(data)
    } catch (error) {
      console.error('Seeding failed:', error)
      alert('Seeding failed: ' + (error.response?.data?.message || error.message))
    } finally {
      setLoading(false)
    }
  }

  const handleInstagramSeed = async () => {
    setInstagramLoading(true)
    setInstagramResult(null)
    try {
      const request = {
        targetUsername: instagramTargetUsername || null,
        followingUsername: instagramFollowingUsername || null,
        includeVideos: instagramIncludeVideos,
      }
      const data = await seedInstagram(request)
      setInstagramResult(data)
      // Refresh status after ingestion
      await loadInstagramStatus()
    } catch (error) {
      console.error('Instagram seeding failed:', error)
      alert('Instagram seeding failed: ' + (error.response?.data?.message || error.message))
    } finally {
      setInstagramLoading(false)
    }
  }

  const loadInstagramStatus = async () => {
    setStatusLoading(true)
    try {
      const data = await getInstagramStatus(
        instagramTargetUsername || null,
        instagramFollowingUsername || null
      )
      setInstagramStatuses(data)
    } catch (error) {
      console.error('Failed to load Instagram status:', error)
    } finally {
      setStatusLoading(false)
    }
  }

  const handleReset = async (deleteImages = false) => {
    if (!confirm(`Are you sure you want to reset ingestion status${deleteImages ? ' and delete all created images' : ''}?`)) {
      return
    }

    setResetting(true)
    try {
      const result = await resetInstagramIngestion(
        instagramTargetUsername || null,
        instagramFollowingUsername || null,
        deleteImages
      )
      const deletedParts = []
      if (deleteImages) {
        if (result.imagesDeleted > 0) deletedParts.push(`${result.imagesDeleted} images`)
        if (result.albumsDeleted > 0) deletedParts.push(`${result.albumsDeleted} albums`)
        if (result.clustersDeleted > 0) deletedParts.push(`${result.clustersDeleted} clusters`)
      }
      const deletedText = deletedParts.length > 0 ? `, ${deletedParts.join(', ')} deleted` : ''
      alert(`Reset complete: ${result.accountsReset} accounts reset${deletedText}`)
      await loadInstagramStatus()
    } catch (error) {
      console.error('Reset failed:', error)
      alert('Reset failed: ' + (error.response?.data?.error || error.message))
    } finally {
      setResetting(false)
    }
  }

  const loadProcessingStats = async () => {
    setLoadingStats(true)
    try {
      const stats = await getProcessingStats()
      setProcessingStats(stats)
    } catch (error) {
      console.error('Failed to load processing stats:', error)
    } finally {
      setLoadingStats(false)
    }
  }

  useEffect(() => {
    if (activeTab === 'instagram') {
      loadInstagramStatus()
      loadProcessingStats()
    } else if (activeTab === 'post-fetch') {
      // Auto-load usernames when switching to post-fetch tab
      const loadUsernames = async () => {
        setLoadingUsernames(true)
        try {
          const data = await getUsernamesWithoutPosts()
          setUsernamesWithoutPosts(data || [])
        } catch (error) {
          console.error('Failed to load usernames:', error)
        } finally {
          setLoadingUsernames(false)
        }
      }
      loadUsernames()
    }
  }, [activeTab, instagramTargetUsername])

  useEffect(() => {
    // Refresh stats every 10 seconds when on Instagram tab
    if (activeTab === 'instagram') {
      const interval = setInterval(loadProcessingStats, 10000)
      return () => clearInterval(interval)
    }
  }, [activeTab])

  return (
    <div>
      <div className="page-header">
        <h1>Indexing</h1>
        <p>Scan directories or ingest Instagram data into the indexing queue</p>
      </div>

      {/* Tab Navigation */}
      <div style={{ display: 'flex', gap: '8px', marginBottom: '24px', borderBottom: '2px solid var(--border)' }}>
        <button
          className={`btn ${activeTab === 'directory' ? 'btn-primary' : 'btn-secondary'}`}
          onClick={() => setActiveTab('directory')}
          style={{ borderRadius: '8px 8px 0 0', marginBottom: '-2px' }}
        >
          Directory Indexing
        </button>
        <button
          className={`btn ${activeTab === 'instagram' ? 'btn-primary' : 'btn-secondary'}`}
          onClick={() => setActiveTab('instagram')}
          style={{ borderRadius: '8px 8px 0 0', marginBottom: '-2px' }}
        >
          Instagram Ingestion
        </button>
        <button
          className={`btn ${activeTab === 'post-fetch' ? 'btn-primary' : 'btn-secondary'}`}
          onClick={() => setActiveTab('post-fetch')}
          style={{ borderRadius: '8px 8px 0 0', marginBottom: '-2px' }}
        >
          Post Fetch
        </button>
      </div>

      {activeTab === 'directory' && (
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
            <label style={{ display: 'block', marginBottom: '4px' }}>
              Album ID <span className="text-muted">(ignored if "Seed Subdirectories as Albums" is enabled)</span>
            </label>
            <input
              className="input"
              value={albumId}
              onChange={(e) => setAlbumId(e.target.value)}
              placeholder="my-album-id"
              disabled={seedSubdirectoriesAsAlbums}
            />
          </div>
          <div>
            <label style={{ display: 'flex', alignItems: 'center', gap: '8px', cursor: 'pointer' }}>
              <input
                type="checkbox"
                checked={seedSubdirectoriesAsAlbums}
                onChange={(e) => {
                  setSeedSubdirectoriesAsAlbums(e.target.checked)
                  if (e.target.checked) {
                    setAlbumId('') // Clear albumId when this mode is enabled
                  }
                }}
              />
              <span>
                <strong>Seed Subdirectories as Albums</strong>
                <span className="text-muted" style={{ display: 'block', fontSize: '12px', marginTop: '2px' }}>
                  Each subdirectory will become a separate album (subdirectory name = albumId)
                </span>
              </span>
            </label>
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
            disabled={loading || !directoryPath || (!seedSubdirectoriesAsAlbums && !albumId)}
          >
            {loading ? 'Scanning...' : seedSubdirectoriesAsAlbums ? 'Seed Subdirectories' : 'Seed Directory'}
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
      )}

      {activeTab === 'instagram' && (
        <>
          <div className="card">
            <h2 style={{ marginBottom: '16px', fontSize: '18px' }}>Ingest Instagram Data</h2>
            <div style={{ display: 'flex', flexDirection: 'column', gap: '16px' }}>
              <div>
                <label style={{ display: 'block', marginBottom: '4px' }}>
                  Target Username (Source Account) <span className="text-muted">(Optional)</span>
                </label>
                <input
                  className="input"
                  value={instagramTargetUsername}
                  onChange={(e) => setInstagramTargetUsername(e.target.value)}
                  placeholder="viralbhayani (leave empty for all accounts)"
                />
                <small className="text-muted">Filter by source account that fetched the followings</small>
              </div>
              <div>
                <label style={{ display: 'block', marginBottom: '4px' }}>
                  Following Username (Test Single Account) <span className="text-muted">(Optional)</span>
                </label>
                <input
                  className="input"
                  value={instagramFollowingUsername}
                  onChange={(e) => setInstagramFollowingUsername(e.target.value)}
                  placeholder="acharyavinodkumar (leave empty for all accounts)"
                />
                <small className="text-muted">Test with a single account before processing all</small>
              </div>
              <div>
                <label style={{ display: 'flex', alignItems: 'center', gap: '8px', cursor: 'pointer' }}>
                  <input
                    type="checkbox"
                    checked={instagramIncludeVideos}
                    onChange={(e) => setInstagramIncludeVideos(e.target.checked)}
                  />
                  Include Videos
                </label>
              </div>
              <button
                className="btn btn-primary"
                onClick={handleInstagramSeed}
                disabled={instagramLoading}
              >
                {instagramLoading ? 'Ingesting...' : instagramFollowingUsername ? 'Test Single Account' : 'Ingest All Accounts'}
              </button>
            </div>

            {instagramResult && (
              <div style={{ marginTop: '24px', padding: '16px', background: 'var(--bg)', borderRadius: '8px' }}>
                <h3 style={{ marginBottom: '12px' }}>Ingestion Results</h3>
                <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
                  <div>
                    <span className="text-muted">Accounts Processed: </span>
                    <strong>{instagramResult.accountsProcessed || 0}</strong>
                  </div>
                  <div>
                    <span className="text-muted">Posts Scanned: </span>
                    <strong>{instagramResult.postsScanned || 0}</strong>
                  </div>
                  <div>
                    <span className="text-muted">Posts Matched: </span>
                    <strong>{instagramResult.postsMatched || 0}</strong>
                  </div>
                  <div>
                    <span className="text-muted">Posts Upserted: </span>
                    <strong>{instagramResult.postsUpserted || 0}</strong>
                  </div>
                  <div>
                    <span className="text-muted">Posts Succeeded: </span>
                    <strong style={{ color: 'var(--success)' }}>{instagramResult.postsSucceeded || 0}</strong>
                  </div>
                  {instagramResult.errors && instagramResult.errors.length > 0 && (
                    <div style={{ marginTop: '12px' }}>
                      <span className="text-muted">Errors: </span>
                      <div style={{ maxHeight: '200px', overflow: 'auto', background: 'var(--danger-bg)', padding: '8px', borderRadius: '4px', marginTop: '4px' }}>
                        {instagramResult.errors.map((err, i) => (
                          <div key={i} style={{ fontSize: '12px', color: 'var(--danger)' }}>{err}</div>
                        ))}
                      </div>
                    </div>
                  )}
                </div>
              </div>
            )}
          </div>

          <div className="card" style={{ marginTop: '24px' }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '16px' }}>
              <h2 style={{ fontSize: '18px', margin: 0 }}>Account Status</h2>
              <div style={{ display: 'flex', gap: '8px' }}>
                <button
                  className="btn btn-secondary"
                  onClick={loadInstagramStatus}
                  disabled={statusLoading}
                >
                  {statusLoading ? 'Loading...' : 'Refresh'}
                </button>
                <button
                  className="btn btn-secondary"
                  onClick={() => handleReset(false)}
                  disabled={resetting}
                  style={{ backgroundColor: 'var(--warning)', color: '#fff', fontWeight: 'bold' }}
                >
                  {resetting ? 'Resetting...' : 'Reset Status'}
                </button>
                <button
                  className="btn btn-secondary"
                  onClick={() => handleReset(true)}
                  disabled={resetting}
                  style={{ backgroundColor: 'var(--danger)', color: 'white' }}
                >
                  {resetting ? 'Resetting...' : 'Reset & Delete Images'}
                </button>
              </div>
            </div>
            <div style={{ marginTop: '16px', padding: '12px', background: 'var(--bg)', borderRadius: '8px', border: '1px solid var(--border)' }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '12px' }}>
                <div>
                  <h3 style={{ fontSize: '16px', margin: '0 0 4px 0' }}>🔧 Processing Diagnostics</h3>
                  <p style={{ margin: 0, fontSize: '13px', color: 'var(--text-muted)' }}>
                    Reset failed images back to pending status so the worker can retry processing them.
                  </p>
                </div>
                <button
                  className="btn btn-secondary"
                  onClick={async () => {
                    if (!confirm('Reset all error images back to pending status? This will allow the worker to retry processing failed images.')) {
                      return
                    }
                    setResettingErrors(true)
                    try {
                      const result = await resetErrors()
                      alert(`Reset ${result.resetCount || 0} error image(s) back to pending. The worker will now retry processing them.`)
                      await loadInstagramStatus()
                    } catch (error) {
                      console.error('Reset errors failed:', error)
                      alert('Reset errors failed: ' + (error.response?.data?.message || error.message))
                    } finally {
                      setResettingErrors(false)
                    }
                  }}
                  disabled={resettingErrors}
                    style={{ backgroundColor: 'var(--warning)', color: '#fff', fontWeight: 'bold' }}
                  >
                    {resettingErrors ? 'Resetting...' : '🔄 Reset All Errors'}
                  </button>
              </div>
              {processingStats && (
                <div style={{ marginTop: '12px', padding: '12px', background: 'var(--bg)', borderRadius: '6px', border: '1px solid var(--border)' }}>
                  <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(120px, 1fr))', gap: '12px', marginBottom: processingStats.errorImages > 0 ? '12px' : '0' }}>
                    <div>
                      <div style={{ fontSize: '11px', color: 'var(--text-muted)', marginBottom: '4px' }}>Total</div>
                      <div style={{ fontSize: '20px', fontWeight: 'bold' }}>{processingStats.totalImages || 0}</div>
                    </div>
                    <div>
                      <div style={{ fontSize: '11px', color: 'var(--text-muted)', marginBottom: '4px' }}>Pending</div>
                      <div style={{ fontSize: '20px', fontWeight: 'bold', color: processingStats.pendingImages > 0 ? 'var(--warning)' : 'var(--success)' }}>
                        {processingStats.pendingImages || 0}
                      </div>
                    </div>
                    <div>
                      <div style={{ fontSize: '11px', color: 'var(--text-muted)', marginBottom: '4px' }}>Done</div>
                      <div style={{ fontSize: '20px', fontWeight: 'bold', color: 'var(--success)' }}>{processingStats.doneImages || 0}</div>
                    </div>
                    <div>
                      <div style={{ fontSize: '11px', color: 'var(--text-muted)', marginBottom: '4px' }}>Errors</div>
                      <div style={{ fontSize: '20px', fontWeight: 'bold', color: processingStats.errorImages > 0 ? 'var(--danger)' : 'inherit' }}>
                        {processingStats.errorImages || 0}
                      </div>
                    </div>
                    <div>
                      <div style={{ fontSize: '11px', color: 'var(--text-muted)', marginBottom: '4px' }}>Progress</div>
                      <div style={{ fontSize: '20px', fontWeight: 'bold' }}>{processingStats.progressPercent || 0}%</div>
                    </div>
                  </div>
                  <div style={{ marginTop: '12px', padding: '8px', background: processingStats.workerActive ? 'rgba(76, 175, 80, 0.1)' : 'rgba(255, 152, 0, 0.1)', borderRadius: '4px', border: `1px solid ${processingStats.workerActive ? 'var(--success)' : 'var(--warning)'}` }}>
                    <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                      <div>
                        <div style={{ fontSize: '12px', fontWeight: 'bold', color: processingStats.workerActive ? 'var(--success)' : 'var(--warning)' }}>
                          {processingStats.workerActive ? '✓ Worker Active' : '⚠ Worker Inactive'}
                        </div>
                        {processingStats.oldestPendingAgeMinutes != null && processingStats.pendingImages > 0 && (
                          <div style={{ fontSize: '11px', color: 'var(--text-muted)', marginTop: '4px' }}>
                            Oldest pending: {Math.round(processingStats.oldestPendingAgeMinutes)} minutes ago
                          </div>
                        )}
                      </div>
                    </div>
                  </div>
                  {processingStats.errorImages > 0 && processingStats.topErrors && processingStats.topErrors.length > 0 && (
                    <div style={{ marginTop: '12px', paddingTop: '12px', borderTop: '1px solid var(--border)' }}>
                      <div style={{ fontSize: '12px', color: 'var(--text-muted)', marginBottom: '8px', fontWeight: 'bold' }}>Top Errors:</div>
                      <div style={{ display: 'flex', flexDirection: 'column', gap: '4px' }}>
                        {processingStats.topErrors.slice(0, 5).map((err, i) => (
                          <div key={i} style={{ fontSize: '11px', color: 'var(--text-muted)', fontFamily: 'monospace', wordBreak: 'break-word' }}>
                            <span style={{ color: 'var(--danger)', fontWeight: 'bold' }}>{err.count}x</span> {err.message.length > 100 ? err.message.substring(0, 100) + '...' : err.message}
                          </div>
                        ))}
                      </div>
                    </div>
                  )}
                </div>
              )}
              <p style={{ margin: '12px 0 0 0', fontSize: '12px', color: 'var(--text-muted)', fontStyle: 'italic' }}>
                💡 Stats auto-refresh every 10 seconds. {processingStats && !processingStats.workerActive && processingStats.pendingImages > 0 && '⚠️ Worker appears inactive - check if the worker service is running!'}
              </p>
            </div>
            <div style={{ marginTop: '16px', padding: '12px', background: 'var(--bg)', borderRadius: '8px', border: '2px solid var(--danger)' }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                <div>
                  <h3 style={{ fontSize: '16px', margin: '0 0 4px 0', color: 'var(--danger)' }}>⚠️ Factory Reset</h3>
                  <p style={{ margin: 0, fontSize: '13px', color: 'var(--text-muted)' }}>
                    WARNING: This will delete ALL data (Qdrant collections, MongoDB documents, albums, images, clusters) - not just Instagram data!
                  </p>
                </div>
                <button
                  className="btn btn-secondary"
                  onClick={async () => {
                    if (!confirm('⚠️ DANGER: This will delete ALL data in the system (Qdrant + MongoDB). This cannot be undone!\n\nAre you absolutely sure?')) {
                      return
                    }
                    if (!confirm('This is your LAST WARNING. All data will be permanently deleted. Continue?')) {
                      return
                    }
                    try {
                      const result = await factoryReset()
                      alert(`Factory reset complete! Deleted ${result.qdrantCollectionsDeleted?.length || 0} Qdrant collections and cleared ${Object.keys(result.mongoCollectionsCleared || {}).length} MongoDB collections.`)
                      await loadInstagramStatus()
                    } catch (error) {
                      console.error('Factory reset failed:', error)
                      alert('Factory reset failed: ' + (error.response?.data?.message || error.message))
                    }
                  }}
                  style={{ backgroundColor: 'var(--danger)', color: 'white', fontWeight: 'bold' }}
                >
                  🗑️ Factory Reset
                </button>
              </div>
            </div>
            <div style={{ marginTop: '16px', padding: '12px', background: 'var(--bg)', borderRadius: '8px', border: '1px solid var(--border)' }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '12px' }}>
                <div>
                  <h3 style={{ fontSize: '16px', margin: '0 0 4px 0' }}>🔧 Processing Diagnostics</h3>
                  <p style={{ margin: 0, fontSize: '13px', color: 'var(--text-muted)' }}>
                    Monitor processing status and reset failed images back to pending.
                  </p>
                </div>
                <div style={{ display: 'flex', gap: '8px', alignItems: 'center' }}>
                  <button
                    className="btn btn-secondary"
                    onClick={loadProcessingStats}
                    disabled={loadingStats}
                    style={{ fontSize: '12px', padding: '6px 12px' }}
                  >
                    {loadingStats ? 'Loading...' : '🔄 Refresh'}
                  </button>
                  <button
                    className="btn btn-secondary"
                    onClick={async () => {
                      if (!confirm('Reset all error images back to pending status? This will allow the worker to retry processing failed images.')) {
                        return
                      }
                      setResettingErrors(true)
                      try {
                        const result = await resetErrors()
                        alert(`Reset ${result.resetCount || 0} error image(s) back to pending. The worker will now retry processing them.`)
                        await loadInstagramStatus()
                        await loadProcessingStats()
                      } catch (error) {
                        console.error('Reset errors failed:', error)
                        alert('Reset errors failed: ' + (error.response?.data?.message || error.message))
                      } finally {
                        setResettingErrors(false)
                      }
                    }}
                    disabled={resettingErrors}
                    style={{ backgroundColor: 'var(--warning)', color: '#fff', fontWeight: 'bold', fontSize: '12px', padding: '6px 12px' }}
                  >
                    {resettingErrors ? 'Resetting...' : '🔄 Reset All Errors'}
                  </button>
                </div>
              </div>
              {processingStats && (
                <div style={{ marginTop: '12px', padding: '12px', background: 'var(--bg)', borderRadius: '6px', border: '1px solid var(--border)' }}>
                  <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(120px, 1fr))', gap: '12px', marginBottom: processingStats.errorImages > 0 ? '12px' : '0' }}>
                    <div>
                      <div style={{ fontSize: '11px', color: 'var(--text-muted)', marginBottom: '4px' }}>Total</div>
                      <div style={{ fontSize: '20px', fontWeight: 'bold' }}>{processingStats.totalImages || 0}</div>
                    </div>
                    <div>
                      <div style={{ fontSize: '11px', color: 'var(--text-muted)', marginBottom: '4px' }}>Pending</div>
                      <div style={{ fontSize: '20px', fontWeight: 'bold', color: processingStats.pendingImages > 0 ? 'var(--warning)' : 'var(--success)' }}>
                        {processingStats.pendingImages || 0}
                      </div>
                    </div>
                    <div>
                      <div style={{ fontSize: '11px', color: 'var(--text-muted)', marginBottom: '4px' }}>Done</div>
                      <div style={{ fontSize: '20px', fontWeight: 'bold', color: 'var(--success)' }}>{processingStats.doneImages || 0}</div>
                    </div>
                    <div>
                      <div style={{ fontSize: '11px', color: 'var(--text-muted)', marginBottom: '4px' }}>Errors</div>
                      <div style={{ fontSize: '20px', fontWeight: 'bold', color: processingStats.errorImages > 0 ? 'var(--danger)' : 'inherit' }}>
                        {processingStats.errorImages || 0}
                      </div>
                    </div>
                    <div>
                      <div style={{ fontSize: '11px', color: 'var(--text-muted)', marginBottom: '4px' }}>Progress</div>
                      <div style={{ fontSize: '20px', fontWeight: 'bold' }}>{processingStats.progressPercent || 0}%</div>
                    </div>
                  </div>
                  {processingStats.errorImages > 0 && processingStats.topErrors && processingStats.topErrors.length > 0 && (
                    <div style={{ marginTop: '12px', paddingTop: '12px', borderTop: '1px solid var(--border)' }}>
                      <div style={{ fontSize: '12px', color: 'var(--text-muted)', marginBottom: '8px', fontWeight: 'bold' }}>Top Errors:</div>
                      <div style={{ display: 'flex', flexDirection: 'column', gap: '4px' }}>
                        {processingStats.topErrors.slice(0, 5).map((err, i) => (
                          <div key={i} style={{ fontSize: '11px', color: 'var(--text-muted)', fontFamily: 'monospace', wordBreak: 'break-word' }}>
                            <span style={{ color: 'var(--danger)', fontWeight: 'bold' }}>{err.count}x</span> {err.message.length > 100 ? err.message.substring(0, 100) + '...' : err.message}
                          </div>
                        ))}
                      </div>
                    </div>
                  )}
                </div>
              )}
              <p style={{ margin: '12px 0 0 0', fontSize: '12px', color: 'var(--text-muted)', fontStyle: 'italic' }}>
                💡 Stats auto-refresh every 10 seconds. If stuck at 91 albums, check if there are pending images waiting to be processed.
              </p>
            </div>
            {instagramStatuses.length === 0 ? (
              <p className="text-muted">No accounts found. Start ingestion to see status.</p>
            ) : (
              <>
                <div style={{ overflowX: 'auto' }}>
                  <table style={{ width: '100%', borderCollapse: 'collapse' }}>
                    <thead>
                      <tr style={{ borderBottom: '2px solid var(--border)' }}>
                        <th style={{ padding: '8px', textAlign: 'left' }}>Username</th>
                        <th style={{ padding: '8px', textAlign: 'left' }}>Source</th>
                        <th style={{ padding: '8px', textAlign: 'right' }}>Posts</th>
                        <th style={{ padding: '8px', textAlign: 'right' }}>Images Created</th>
                        <th style={{ padding: '8px', textAlign: 'center' }}>Status</th>
                        <th style={{ padding: '8px', textAlign: 'left' }}>Pending Reason</th>
                        <th style={{ padding: '8px', textAlign: 'left' }}>Ingested At</th>
                        <th style={{ padding: '8px', textAlign: 'center', width: '120px' }}>Actions</th>
                      </tr>
                    </thead>
                    <tbody>
                      {instagramStatuses.slice(statusSkip, statusSkip + statusTake).map((status, i) => (
                        <tr key={i} style={{ borderBottom: '1px solid var(--border)' }}>
                          <td style={{ padding: '8px' }}>{status.username}</td>
                          <td style={{ padding: '8px', color: 'var(--text-muted)' }}>{status.targetUsername || '-'}</td>
                          <td style={{ padding: '8px', textAlign: 'right' }}>{status.postCount}</td>
                          <td style={{ padding: '8px', textAlign: 'right' }}>{status.imagesCreated}</td>
                          <td style={{ padding: '8px', textAlign: 'center' }}>
                            {status.isIngested ? (
                              <span style={{ color: 'var(--success)', fontWeight: 'bold' }}>✓ Ingested</span>
                            ) : (
                              <span style={{ color: 'var(--warning)', fontWeight: 'bold' }}>Pending</span>
                            )}
                          </td>
                          <td style={{ padding: '8px', fontSize: '12px', color: status.pendingReason ? 'var(--warning)' : 'var(--text-muted)', maxWidth: '300px', wordBreak: 'break-word' }}>
                            {status.pendingReason || (status.isIngested ? '-' : 'Processing...')}
                          </td>
                          <td style={{ padding: '8px', fontSize: '12px', color: 'var(--text-muted)' }}>
                            {status.ingestedAt ? new Date(status.ingestedAt).toLocaleString() : '-'}
                          </td>
                          <td style={{ padding: '8px', textAlign: 'center' }}>
                            <button
                              className="btn btn-secondary"
                              onClick={async () => {
                                if (!confirm(`Reset ingestion status for "${status.username}"?${status.imagesCreated > 0 ? '\n\nThis will reset the ingestion flag. Check "Delete Images" to also remove all created images, albums, and clusters.' : ''}`)) {
                                  return
                                }
                                const deleteImages = status.imagesCreated > 0 && confirm(`Also delete ${status.imagesCreated} image(s), album, and clusters for "${status.username}"?`)
                                setResettingUsername(status.username)
                                try {
                                  const result = await resetSingleInstagramAccount(status.username, deleteImages)
                                  let message = `Reset complete for "${status.username}"`
                                  if (deleteImages) {
                                    message += `: ${result.imagesDeleted || 0} images, ${result.albumsDeleted || 0} albums, ${result.clustersDeleted || 0} clusters deleted`
                                  } else {
                                    message += `: ${result.accountsReset || 0} account(s) reset`
                                  }
                                  alert(message)
                                  await loadInstagramStatus()
                                  await loadProcessingStats()
                                } catch (error) {
                                  console.error('Reset failed:', error)
                                  alert('Reset failed: ' + (error.response?.data?.error || error.message))
                                } finally {
                                  setResettingUsername(null)
                                }
                              }}
                              disabled={resettingUsername === status.username}
                              style={{ 
                                fontSize: '11px', 
                                padding: '4px 8px',
                                backgroundColor: 'var(--warning)',
                                color: '#fff',
                                fontWeight: 'bold',
                                border: 'none',
                                borderRadius: '4px',
                                cursor: resettingUsername === status.username ? 'not-allowed' : 'pointer',
                                opacity: resettingUsername === status.username ? 0.6 : 1
                              }}
                            >
                              {resettingUsername === status.username ? 'Resetting...' : '🔄 Reset'}
                            </button>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
                {instagramStatuses.length > statusTake && (
                  <Pagination 
                    skip={statusSkip} 
                    take={statusTake} 
                    total={instagramStatuses.length} 
                    onPageChange={setStatusSkip} 
                  />
                )}
              </>
            )}
          </div>
        </>
      )}

      {activeTab === 'post-fetch' && (
        <>
          <div className="card">
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '16px' }}>
              <h2 style={{ fontSize: '18px', margin: 0 }}>Usernames Without Posts</h2>
              <div style={{ display: 'flex', gap: '8px' }}>
                <button
                  className="btn btn-secondary"
                  onClick={async () => {
                    setLoadingUsernames(true)
                    try {
                      const data = await getUsernamesWithoutPosts()
                      setUsernamesWithoutPosts(data || [])
                    } catch (error) {
                      console.error('Failed to load usernames:', error)
                      alert('Failed to load usernames: ' + (error.response?.data?.error || error.message))
                    } finally {
                      setLoadingUsernames(false)
                    }
                  }}
                  disabled={loadingUsernames}
                >
                  {loadingUsernames ? 'Loading...' : '🔄 Refresh'}
                </button>
              </div>
            </div>
            <div style={{ marginBottom: '16px' }}>
              <label style={{ display: 'block', marginBottom: '4px' }}>
                Target Username (Optional)
              </label>
              <input
                className="input"
                value={postFetchTargetUsername}
                onChange={(e) => setPostFetchTargetUsername(e.target.value)}
                placeholder="viralbhayani (optional, for tracking)"
                style={{ width: '300px' }}
              />
            </div>
            {usernamesWithoutPosts.length === 0 ? (
              <p className="text-muted">No usernames found. Click Refresh to load.</p>
            ) : (
              <>
                <div style={{ marginBottom: '12px', display: 'flex', gap: '8px', alignItems: 'center' }}>
                  <button
                    className="btn btn-secondary"
                    onClick={() => {
                      if (selectedUsernames.size === usernamesWithoutPosts.length) {
                        setSelectedUsernames(new Set())
                      } else {
                        setSelectedUsernames(new Set(usernamesWithoutPosts.map(u => u.username)))
                      }
                    }}
                    style={{ fontSize: '12px', padding: '4px 8px' }}
                  >
                    {selectedUsernames.size === usernamesWithoutPosts.length ? 'Deselect All' : 'Select All'}
                  </button>
                  <span className="text-muted" style={{ fontSize: '12px' }}>
                    {selectedUsernames.size} of {usernamesWithoutPosts.length} selected
                  </span>
                  <button
                    className="btn btn-primary"
                    onClick={async () => {
                      if (selectedUsernames.size === 0) {
                        alert('Please select at least one username')
                        return
                      }
                      if (!confirm(`Fetch posts for ${selectedUsernames.size} username(s)? This will make API calls with 2 second delays between requests.`)) {
                        return
                      }
                      setFetchingPosts(true)
                      try {
                        const result = await fetchPosts(Array.from(selectedUsernames), postFetchTargetUsername || null)
                        setFetchId(result.fetchId)
                        alert(`Post fetch initiated! Fetch ID: ${result.fetchId}\n\nThis will run in the background. Check the status below.`)
                        // Start polling for status
                        const pollInterval = setInterval(async () => {
                          try {
                            const status = await getFetchStatus(result.fetchId)
                            setFetchStatus(status)
                            if (status.status === 'completed' || status.status === 'cancelled') {
                              clearInterval(pollInterval)
                              setFetchingPosts(false)
                              // Refresh the list
                              const data = await getUsernamesWithoutPosts()
                              setUsernamesWithoutPosts(data || [])
                            }
                          } catch (error) {
                            console.error('Failed to get fetch status:', error)
                          }
                        }, 2000) // Poll every 2 seconds
                        // Cleanup after 10 minutes
                        setTimeout(() => clearInterval(pollInterval), 600000)
                      } catch (error) {
                        console.error('Failed to fetch posts:', error)
                        alert('Failed to fetch posts: ' + (error.response?.data?.error || error.message))
                        setFetchingPosts(false)
                      }
                    }}
                    disabled={fetchingPosts || selectedUsernames.size === 0}
                  >
                    {fetchingPosts ? 'Fetching...' : `📥 Fetch Posts (${selectedUsernames.size})`}
                  </button>
                </div>
                <div style={{ overflowX: 'auto' }}>
                  <table style={{ width: '100%', borderCollapse: 'collapse' }}>
                    <thead>
                      <tr style={{ borderBottom: '2px solid var(--border)' }}>
                        <th style={{ padding: '8px', textAlign: 'left', width: '40px' }}>
                          <input
                            type="checkbox"
                            checked={selectedUsernames.size === usernamesWithoutPosts.length && usernamesWithoutPosts.length > 0}
                            onChange={(e) => {
                              if (e.target.checked) {
                                setSelectedUsernames(new Set(usernamesWithoutPosts.map(u => u.username)))
                              } else {
                                setSelectedUsernames(new Set())
                              }
                            }}
                          />
                        </th>
                        <th style={{ padding: '8px', textAlign: 'left' }}>Username</th>
                        <th style={{ padding: '8px', textAlign: 'left' }}>Source</th>
                        <th style={{ padding: '8px', textAlign: 'right' }}>Posts (Followings)</th>
                        <th style={{ padding: '8px', textAlign: 'right' }}>Posts (Posts Collection)</th>
                        <th style={{ padding: '8px', textAlign: 'left' }}>Reason</th>
                        <th style={{ padding: '8px', textAlign: 'center', width: '120px' }}>Actions</th>
                      </tr>
                    </thead>
                    <tbody>
                      {usernamesWithoutPosts.map((item, i) => (
                        <tr key={i} style={{ borderBottom: '1px solid var(--border)' }}>
                          <td style={{ padding: '8px', textAlign: 'center' }}>
                            <input
                              type="checkbox"
                              checked={selectedUsernames.has(item.username)}
                              onChange={(e) => {
                                const newSelected = new Set(selectedUsernames)
                                if (e.target.checked) {
                                  newSelected.add(item.username)
                                } else {
                                  newSelected.delete(item.username)
                                }
                                setSelectedUsernames(newSelected)
                              }}
                            />
                          </td>
                          <td style={{ padding: '8px' }}>{item.username}</td>
                          <td style={{ padding: '8px', color: 'var(--text-muted)' }}>{item.targetUsername || '-'}</td>
                          <td style={{ padding: '8px', textAlign: 'right' }}>{item.postsInFollowingsCollection}</td>
                          <td style={{ padding: '8px', textAlign: 'right' }}>{item.postsInPostsCollection}</td>
                          <td style={{ padding: '8px', fontSize: '12px', color: 'var(--warning)' }}>{item.reason}</td>
                          <td style={{ padding: '8px', textAlign: 'center' }}>
                            <button
                              className="btn btn-secondary"
                              onClick={async () => {
                                if (!confirm(`Fetch posts for "${item.username}"?`)) {
                                  return
                                }
                                setFetchingPosts(true)
                                try {
                                  const result = await fetchPosts([item.username], postFetchTargetUsername || null)
                                  setFetchId(result.fetchId)
                                  alert(`Post fetch initiated for "${item.username}"! Fetch ID: ${result.fetchId}\n\nCheck status below.`)
                                  // Start polling
                                  const pollInterval = setInterval(async () => {
                                    try {
                                      const status = await getFetchStatus(result.fetchId)
                                      setFetchStatus(status)
                                      if (status.status === 'completed' || status.status === 'cancelled') {
                                        clearInterval(pollInterval)
                                        setFetchingPosts(false)
                                        const data = await getUsernamesWithoutPosts()
                                        setUsernamesWithoutPosts(data || [])
                                      }
                                    } catch (error) {
                                      console.error('Failed to get fetch status:', error)
                                    }
                                  }, 2000)
                                  setTimeout(() => clearInterval(pollInterval), 600000)
                                } catch (error) {
                                  console.error('Failed to fetch posts:', error)
                                  alert('Failed to fetch posts: ' + (error.response?.data?.error || error.message))
                                  setFetchingPosts(false)
                                }
                              }}
                              disabled={fetchingPosts}
                              style={{ 
                                fontSize: '11px', 
                                padding: '4px 8px',
                                backgroundColor: 'var(--primary)',
                                color: '#fff',
                                fontWeight: 'bold',
                                border: 'none',
                                borderRadius: '4px',
                                cursor: fetchingPosts ? 'not-allowed' : 'pointer'
                              }}
                            >
                              📥 Fetch
                            </button>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </>
            )}
          </div>

          {fetchId && fetchStatus && (
            <div className="card" style={{ marginTop: '24px' }}>
              <h3 style={{ fontSize: '16px', marginBottom: '12px' }}>Fetch Status: {fetchStatus.status}</h3>
              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(120px, 1fr))', gap: '12px', marginBottom: '16px' }}>
                <div>
                  <div style={{ fontSize: '11px', color: 'var(--text-muted)', marginBottom: '4px' }}>Total</div>
                  <div style={{ fontSize: '20px', fontWeight: 'bold' }}>{fetchStatus.total}</div>
                </div>
                <div>
                  <div style={{ fontSize: '11px', color: 'var(--text-muted)', marginBottom: '4px' }}>Processed</div>
                  <div style={{ fontSize: '20px', fontWeight: 'bold' }}>{fetchStatus.processed}</div>
                </div>
                <div>
                  <div style={{ fontSize: '11px', color: 'var(--text-muted)', marginBottom: '4px' }}>Success</div>
                  <div style={{ fontSize: '20px', fontWeight: 'bold', color: 'var(--success)' }}>{fetchStatus.success}</div>
                </div>
                <div>
                  <div style={{ fontSize: '11px', color: 'var(--text-muted)', marginBottom: '4px' }}>Failed</div>
                  <div style={{ fontSize: '20px', fontWeight: 'bold', color: fetchStatus.failed > 0 ? 'var(--danger)' : 'inherit' }}>{fetchStatus.failed}</div>
                </div>
              </div>
              {fetchStatus.results && fetchStatus.results.length > 0 && (
                <div style={{ marginTop: '16px' }}>
                  <h4 style={{ fontSize: '14px', marginBottom: '8px' }}>Results:</h4>
                  <div style={{ maxHeight: '300px', overflowY: 'auto' }}>
                    <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: '12px' }}>
                      <thead>
                        <tr style={{ borderBottom: '1px solid var(--border)' }}>
                          <th style={{ padding: '4px', textAlign: 'left' }}>Username</th>
                          <th style={{ padding: '4px', textAlign: 'center' }}>Status</th>
                          <th style={{ padding: '4px', textAlign: 'right' }}>Posts Found</th>
                          <th style={{ padding: '4px', textAlign: 'left' }}>Error</th>
                        </tr>
                      </thead>
                      <tbody>
                        {fetchStatus.results.map((result, i) => (
                          <tr key={i} style={{ borderBottom: '1px solid var(--border)' }}>
                            <td style={{ padding: '4px' }}>{result.username}</td>
                            <td style={{ padding: '4px', textAlign: 'center' }}>
                              {result.success ? (
                                <span style={{ color: 'var(--success)', fontWeight: 'bold' }}>✓ Success</span>
                              ) : (
                                <span style={{ color: 'var(--danger)', fontWeight: 'bold' }}>✗ Failed</span>
                              )}
                            </td>
                            <td style={{ padding: '4px', textAlign: 'right' }}>{result.postsFound ?? '-'}</td>
                            <td style={{ padding: '4px', fontSize: '11px', color: 'var(--text-muted)' }}>
                              {result.errorMessage ? (result.errorMessage.length > 50 ? result.errorMessage.substring(0, 50) + '...' : result.errorMessage) : '-'}
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </div>
              )}
            </div>
          )}
        </>
      )}
    </div>
  )
}

export default Indexing

