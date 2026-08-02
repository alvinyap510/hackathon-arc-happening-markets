import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    proxy: {
      "/v1": {
        target: process.env.VITE_BACKEND_ORIGIN ?? "http://localhost:8080",
        changeOrigin: true,
      },
      "/ws": {
        target: process.env.VITE_BACKEND_ORIGIN ?? "http://localhost:8080",
        changeOrigin: true,
        ws: true,
      },
    },
  },
});
