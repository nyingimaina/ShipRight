import sharp from 'sharp';
import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const frontEndRoot = path.resolve(__dirname, '..');
const repoRoot = path.resolve(frontEndRoot, '..');

const SIZES = [16, 32, 48, 64, 128, 256];

const SVG = `<svg viewBox="0 0 256 256" xmlns="http://www.w3.org/2000/svg">
  <defs>
    <clipPath id="r"><rect width="256" height="256" rx="48"/></clipPath>
  </defs>
  <g clip-path="url(#r)">
    <rect width="256" height="256" fill="#131D30"/>

    <g stroke="#C9A84C" stroke-width="5" stroke-linecap="round">
      <line x1="128" y1="20" x2="128" y2="236"/>
      <line x1="20" y1="128" x2="236" y2="128"/>
      <line transform="rotate(45 128 128)" x1="128" y1="20" x2="128" y2="236"/>
      <line transform="rotate(135 128 128)" x1="128" y1="20" x2="128" y2="236"/>
    </g>

    <g fill="#C9A84C">
      <circle cx="128" cy="20" r="7"/>
      <circle cx="128" cy="236" r="7"/>
      <circle cx="20" cy="128" r="7"/>
      <circle cx="236" cy="128" r="7"/>
      <circle transform="rotate(45 128 128)" cx="128" cy="20" r="7"/>
      <circle transform="rotate(45 128 128)" cx="128" cy="236" r="7"/>
      <circle transform="rotate(135 128 128)" cx="128" cy="20" r="7"/>
      <circle transform="rotate(135 128 128)" cx="128" cy="236" r="7"/>
    </g>

    <circle cx="128" cy="128" r="76" fill="none" stroke="#C9A84C" stroke-width="4"/>

    <circle cx="128" cy="128" r="36" fill="#1A2640" stroke="#C9A84C" stroke-width="5"/>

    <text x="128" y="140" text-anchor="middle"
          font-family="Arial,Helvetica,sans-serif" font-weight="800" font-size="30"
          fill="#C9A84C">SR</text>
  </g>
</svg>`;

async function renderPng(size) {
  const svgBuf = Buffer.from(SVG);
  return sharp(svgBuf)
    .resize(size, size, { kernel: 'lanczos3' })
    .png()
    .toBuffer();
}

function buildIco(pngBuffers, sizes) {
  const HEADER_SIZE = 6;
  const DIR_ENTRY_SIZE = 16;
  const count = sizes.length;
  const header = Buffer.alloc(HEADER_SIZE + count * DIR_ENTRY_SIZE);

  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(count, 4);

  let offset = HEADER_SIZE + count * DIR_ENTRY_SIZE;
  for (let i = 0; i < count; i++) {
    const w = sizes[i] >= 256 ? 0 : sizes[i];
    const h = sizes[i] >= 256 ? 0 : sizes[i];
    const png = pngBuffers[i];
    const entryOffset = HEADER_SIZE + i * DIR_ENTRY_SIZE;
    header.writeUInt8(w, entryOffset);
    header.writeUInt8(h, entryOffset + 1);
    header.writeUInt8(0, entryOffset + 2);
    header.writeUInt8(0, entryOffset + 3);
    header.writeUInt16LE(1, entryOffset + 4);
    header.writeUInt16LE(32, entryOffset + 6);
    header.writeUInt32LE(png.length, entryOffset + 8);
    header.writeUInt32LE(offset, entryOffset + 12);
    offset += png.length;
  }

  return Buffer.concat([header, ...pngBuffers]);
}

async function main() {
  console.log('Rendering icon sizes:', SIZES);
  const pngs = await Promise.all(SIZES.map(renderPng));

  const icoData = buildIco(pngs, SIZES);

  const faviconPath = path.join(frontEndRoot, 'public', 'favicon.ico');
  fs.writeFileSync(faviconPath, icoData);
  console.log(`Wrote ${faviconPath} (${icoData.length} bytes)`);

  const serverIconPath = path.join(repoRoot, 'back-end', 'ShipRight.Server', 'shipright.ico');
  fs.writeFileSync(serverIconPath, icoData);
  console.log(`Wrote ${serverIconPath} (${icoData.length} bytes)`);

  const desktopIconPath = path.join(repoRoot, 'back-end', 'ShipRight.Desktop', 'shipright-desktop.ico');
  fs.writeFileSync(desktopIconPath, icoData);
  console.log(`Wrote ${desktopIconPath} (${icoData.length} bytes)`);
}

main().catch(err => {
  console.error(err);
  process.exit(1);
});
