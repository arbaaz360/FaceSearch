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
      },
      '/fastapi': {
        target: 'http://localhost:5251',
        changeOrigin: true,
        rewrite: (path) => path.replace(/^\/fastapi/, '')
      }
    }
  },
  build: {
    outDir: '../FaceSearch/wwwroot',
    emptyOutDir: true
  }
})

