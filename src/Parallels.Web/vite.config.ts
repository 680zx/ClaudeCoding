import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The SPA talks to Parallels.Api over HTTP only (spec 3.1). In dev that means a
// proxy so the browser sees a same-origin /api and no CORS preflight is involved.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: process.env.PARALLELS_API_URL ?? 'http://127.0.0.1:5080',
        changeOrigin: true,
      },
    },
  },
});
