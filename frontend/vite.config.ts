import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
export default defineConfig({
  plugins: [vue()],
  server: { proxy: { '/api': 'http://localhost:5218', '/health': 'http://localhost:5218' } },
  build: { outDir: '../src/PulletCryptoRadar.Host/wwwroot', emptyOutDir: true },
})
