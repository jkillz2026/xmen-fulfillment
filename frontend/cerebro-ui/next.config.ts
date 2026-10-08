import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  // Static export → `out/` dir, deployable to any static host (Netlify).
  // The dashboard is fully client-side; backend calls are runtime fetch/SignalR.
  output: 'export',
  trailingSlash: true,
};

export default nextConfig;
