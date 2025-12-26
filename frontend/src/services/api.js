import axios from 'axios'

const API_BASE = '/api'
const DIAGNOSTICS_BASE = '/_diagnostics/embedder'

const api = axios.create({
  baseURL: API_BASE,
  headers: {
    'Content-Type': 'application/json',
  },
})

// Albums
export const getAlbums = async (skip = 0, take = 20, includeConfirmedAggregators = false) => {
  const { data } = await api.get('/albums', { params: { skip, take, includeConfirmedAggregators } })
  return data
}

export const getAlbum = async (albumId) => {
  const { data } = await api.get(`/albums/${albumId}`)
  return data
}

export const getDominantFace = async (albumId) => {
  const { data } = await axios.get(`${API_BASE}/albums/${albumId}/dominant-face`)
  return data
}

export const updateAlbumIdentity = async (albumId, identity) => {
  const { data } = await api.post(`/albums/${albumId}/identity`, identity)
  return data
}

export const setAlbumTags = async (albumId, tags) => {
  const { data } = await api.post(`/albums/${albumId}/tags`, { tags })
  return data
}

export const recomputeAlbum = async (albumId) => {
  const { data } = await api.post(`/albums/${albumId}/recompute`)
  return data
}

export const clearSuspiciousFlag = async (albumId) => {
  const { data } = await api.post(`/albums/${albumId}/clear-suspicious`)
  return data
}

export const confirmAggregator = async (albumId) => {
  const { data } = await api.post(`/albums/${albumId}/confirm-aggregator`)
  return data
}

export const markAsJunk = async (albumId, deleteData = false) => {
  const { data } = await api.post(`/albums/${albumId}/mark-junk`, { deleteData })
  return data
}

export const getAlbumClusters = async (albumId, topK = 10) => {
  const { data } = await api.get(`/albums/${albumId}/clusters`, { params: { topK } })
  return data
}

// Search
export const searchText = async (query, topK = 30) => {
  const { data } = await api.post('/search/text', { query, topK })
  return data
}

export const searchImage = async (file, topK = 30) => {
  const formData = new FormData()
  formData.append('file', file)
  const { data } = await api.post('/search/image', formData, {
    params: { topK },
    headers: { 'Content-Type': 'multipart/form-data' },
  })
  return data
}

export const searchFace = async (file, topK = 30) => {
  const formData = new FormData()
  formData.append('file', file)
  const { data } = await api.post('/search/face', formData, {
    params: { topK },
    headers: { 'Content-Type': 'multipart/form-data' },
  })
  return data
}

// Face Review
export const getUnresolvedFaces = async (skip = 0, take = 20) => {
  const { data } = await api.get('/faces/unresolved', { params: { skip, take } })
  return data
}

export const resolveFace = async (faceId, resolution) => {
  const { data } = await api.post(`/faces/${faceId}/resolve`, resolution)
  return data
}

export const whoIsThis = async (file, threshold = 0.72, topK = 5) => {
  const formData = new FormData()
  formData.append('file', file)
  const { data } = await api.post('/faces/who', formData, {
    params: { threshold, topK },
    headers: { 'Content-Type': 'multipart/form-data' },
  })
  return data
}

// Indexing
export const seedDirectory = async (directoryPath, albumId, includeVideos = false, seedSubdirectoriesAsAlbums = false) => {
  const { data } = await api.post('/index/seed-directory', {
    directoryPath,
    albumId: seedSubdirectoriesAsAlbums ? null : albumId,
    includeVideos,
    seedSubdirectoriesAsAlbums,
    recursive: true, // Always recursive for subdirectories mode
  })
  return data
}

// Instagram Ingestion
export const seedInstagram = async (request) => {
  const { data } = await api.post('/instagram/seed', request)
  return data
}

export const getInstagramStatus = async (targetUsername = null, followingUsername = null) => {
  const params = {}
  if (targetUsername) params.targetUsername = targetUsername
  if (followingUsername) params.followingUsername = followingUsername
  const { data } = await api.get('/instagram/status', { params })
  return data
}

export const resetInstagramIngestion = async (targetUsername = null, followingUsername = null, deleteImages = false) => {
  const { data } = await api.post('/instagram/reset', null, {
    params: {
      ...(targetUsername ? { targetUsername } : {}),
      ...(followingUsername ? { followingUsername } : {}),
      deleteImages,
    },
  })
  return data
}

export const resetSingleInstagramAccount = async (username, deleteImages = false) => {
  const { data } = await api.post('/instagram/reset', null, {
    params: {
      followingUsername: username,
      deleteImages,
    },
  })
  return data
}

// Post Fetch
export const getUsernamesWithoutPosts = async () => {
  const { data } = await api.get('/post-fetch/usernames-without-posts')
  return data
}

export const fetchPosts = async (usernames, targetUsername = null) => {
  const { data } = await api.post('/post-fetch/fetch', {
    usernames,
    targetUsername,
  })
  return data
}

export const getFetchStatus = async (fetchId) => {
  const { data } = await api.get(`/post-fetch/status/${fetchId}`)
  return data
}

// Diagnostics
export const getAlbumStatus = async (albumId) => {
  const { data } = await axios.get(`${DIAGNOSTICS_BASE}/album-status/${albumId}`)
  return data
}

export const getAlbumErrors = async (albumId) => {
  const { data } = await axios.get(`${DIAGNOSTICS_BASE}/album-errors/${albumId}`)
  return data
}

export const resetErrors = async (albumId = null) => {
  const { data } = await axios.post(`${DIAGNOSTICS_BASE}/reset-errors`, null, {
    params: albumId ? { albumId } : {},
  })
  return data
}

export const getProcessingStats = async () => {
  const { data } = await axios.get(`${DIAGNOSTICS_BASE}/processing-stats`)
  return data
}

export const factoryReset = async () => {
  const { data } = await axios.post(`${DIAGNOSTICS_BASE}/factory-reset`)
  return data
}

export const getEmbedderStatus = async () => {
  const { data } = await axios.get(`${DIAGNOSTICS_BASE}/status`)
  return data
}

// Reviews
export const getReviews = async (type = null) => {
  const { data } = await axios.get(`${DIAGNOSTICS_BASE}/reviews`, {
    params: type ? { type } : {},
  })
  return data
}

export const getMergeCandidates = async () => {
  const { data } = await axios.get(`${DIAGNOSTICS_BASE}/merge-candidates`)
  return data
}

export const updateReviewStatus = async (reviewId, status) => {
  const { data } = await axios.post(`${DIAGNOSTICS_BASE}/reviews/${reviewId}/status`, { status })
  return data
}

export const mergeAlbums = async (sourceAlbumId, targetAlbumId) => {
  const { data } = await axios.post(`${DIAGNOSTICS_BASE}/albums/merge`, {
    sourceAlbumId,
    targetAlbumId,
  })
  return data
}

export const generateClipEmbeddings = async (albumId = null, batchSize = 100) => {
  const { data } = await axios.post(`${DIAGNOSTICS_BASE}/generate-clip-embeddings`, null, {
    params: {
      ...(albumId ? { albumId } : {}),
      batchSize,
    },
  })
  return data
}

export default api

