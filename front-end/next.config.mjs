/** @type {import('next').NextConfig} */
const nextConfig = {
  output: 'export',
  trailingSlash: true,
  reactStrictMode: true,
  images: { unoptimized: true },
  transpilePackages: [
    'jattac.libs.web.zest-button',
    'jattac.libs.web.zest-textbox',
    'jattac.libs.web.zest-sidekick-menu',
    'jattac.libs.web.zest-responsive-layout',
    'jattac.libs.web.responsive-table',
    'jattac.libs.web.overflow-menu',
  ],
};

export default nextConfig;
