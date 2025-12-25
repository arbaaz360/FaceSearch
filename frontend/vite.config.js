import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 3000,
    proxy: {
      '/api': {
        target: 'http://localhost:5240',
        changeOrigin: true
      },
      '/_diagnostics': {
        target: 'http://localhost:5240',
        changeOrigin: true
      }
    }
  },
  build: {
    outDir: '../FaceSearch/wwwroot',
    emptyOutDir: true
  }
})

