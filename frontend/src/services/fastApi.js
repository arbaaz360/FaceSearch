import axios from 'axios'

const FAST_API_BASE = '/fastapi'

const fastApi = axios.create({
  baseURL: FAST_API_BASE
})

export const fastSearchFace = async (file, topK = 10) => {
  const formData = new FormData()
  formData.append('file', file)
  const { data } = await fastApi.post('/fast/search', formData, {
    params: { topK },
    headers: { 'Content-Type': 'multipart/form-data' }
  })
  return data
}

export const fastIndexFolder = async ({ folderPath, includeSubdirectories = true, note = null, overwriteExisting = false, checkNote = true }) => {
  const { data } = await fastApi.post('/fast/index-folder', {
    folderPath,
    includeSubdirectories,
    note,
    overwriteExisting,
    checkNote
  })
  return data
}

export const fastIndexVideos = async ({
  folderPath,
  includeSubdirectories = true,
  note = null,
  sampleEverySeconds = 10,
  keyframesOnly = true,
  femaleOnly = true,
  maxFacesPerVideo = 50,
  maxFacesPerFrame = 10,
  maxFrameWidth = 0,
  minFaceWidthPx = 40,
  minFaceAreaRatio = 0,
  minBlurVariance = 40,
  minDetScore = 0.6,
  facePadding = 0.25,
  maxSimilarityToExisting = 0.95,
  outputDirectory = null,
  saveCrops = true
}) => {
  const { data } = await fastApi.post('/fast/index-videos', {
    folderPath,
    includeSubdirectories,
    note,
    sampleEverySeconds,
    keyframesOnly,
    femaleOnly,
    maxFacesPerVideo,
    maxFacesPerFrame,
    maxFrameWidth,
    minFaceWidthPx,
    minFaceAreaRatio,
    minBlurVariance,
    minDetScore,
    facePadding,
    maxSimilarityToExisting,
    outputDirectory,
    saveCrops
  })
  return data
}

export const fastStatus = async () => {
  const { data } = await fastApi.get('/fast/status')
  return data
}

export const fastUpsertWatchFolder = async ({
  id = null,
  folderPath,
  includeSubdirectories = true,
  note = null,
  intervalSeconds = 60,
  overwriteExisting = false,
  checkNote = true,
  enabled = true
}) => {
  const { data } = await fastApi.post('/fast/watch-folders', {
    id,
    folderPath,
    includeSubdirectories,
    note,
    intervalSeconds,
    overwriteExisting,
    checkNote,
    enabled
  })
  return data
}

export const fastDeleteWatchFolder = async (id) => {
  const { data } = await fastApi.delete(`/fast/watch-folders/${encodeURIComponent(id)}`)
  return data
}

export const fastBulkCheck = async (files, threshold) => {
  const formData = new FormData()
  files.forEach((f) => formData.append('files', f))
  const { data } = await fastApi.post('/fast/bulk-check-files', formData, {
    params: { threshold: threshold ?? 0.6 }
  })
  return data
}

export const fastOpenVideo = async (videoPath) => {
  const { data } = await fastApi.post('/fast/open-video', { videoPath })
  return data
}

export default fastApi
