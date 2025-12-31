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
  maxFacesPerVideo = 50,
  maxFacesPerFrame = 3,
  maxFrameWidth = 640,
  minFaceWidthPx = 90,
  minFaceAreaRatio = 0.02,
  minBlurVariance = 80,
  facePadding = 0.25,
  outputDirectory = null,
  saveCrops = true
}) => {
  const { data } = await fastApi.post('/fast/index-videos', {
    folderPath,
    includeSubdirectories,
    note,
    sampleEverySeconds,
    keyframesOnly,
    maxFacesPerVideo,
    maxFacesPerFrame,
    maxFrameWidth,
    minFaceWidthPx,
    minFaceAreaRatio,
    minBlurVariance,
    facePadding,
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

export default fastApi
