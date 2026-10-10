// Bundles the extension into dist/, which is what Chrome loads ("Load unpacked") and what
// build/publish.ps1 copies into the portable folder as chrome-extension/.
// No remote code: everything is bundled locally (PRD §11.2).
import { build } from 'esbuild';
import { cp, mkdir, rm } from 'node:fs/promises';

await rm('dist', { recursive: true, force: true });
await mkdir('dist/popup', { recursive: true });

await build({
  entryPoints: {
    background: 'src/background.ts',
    content: 'src/content.ts',
    'popup/popup': 'src/popup/popup.ts',
  },
  bundle: true,
  format: 'iife',
  target: 'chrome116',
  outdir: 'dist',
  legalComments: 'none',
  logLevel: 'info',
});

await cp('manifest.json', 'dist/manifest.json');
await cp('src/popup/popup.html', 'dist/popup/popup.html');
await cp('src/popup/popup.css', 'dist/popup/popup.css');
await cp('icons', 'dist/icons', { recursive: true });
