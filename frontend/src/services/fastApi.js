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

export const fastStatus = async () => {
  const { data } = await fastApi.get('/fast/status')
  return data
}

export default fastApi
