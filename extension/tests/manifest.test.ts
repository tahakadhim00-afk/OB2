import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

const manifest = JSON.parse(readFileSync(join(__dirname, '..', 'manifest.json'), 'utf8'));

/** Chrome's rule: SHA-256 of the public key, first 16 bytes, each hex digit mapped 0-f → a-p. */
function extensionIdFromKey(base64Key: string): string {
  const hex = createHash('sha256').update(Buffer.from(base64Key, 'base64')).digest('hex').slice(0, 32);
  return [...hex].map((c) => String.fromCharCode('a'.charCodeAt(0) + parseInt(c, 16))).join('');
}

describe('manifest (§8.1, §8.2)', () => {
  it('pins the extension id that host-manifest.json allows (decision D-51)', () => {
    expect(extensionIdFromKey(manifest.key)).toBe('mgmmgplfngnhcencbhpfagnjcnenflpm');
  });

  it('asks for exactly the permissions in §8.1, plus webRequest (D-69) and scripting (D-70)', () => {
    expect([...manifest.permissions].sort()).toEqual(['contextMenus', 'cookies', 'downloads', 'nativeMessaging', 'scripting', 'storage', 'webRequest']);
    expect(manifest.host_permissions).toEqual(['<all_urls>']);
  });

  it('is MV3 with a strict CSP and no remote code (§11.2)', () => {
    expect(manifest.manifest_version).toBe(3);
    expect(manifest.content_security_policy.extension_pages).toBe("script-src 'self'; object-src 'none'");
    expect(JSON.stringify(manifest)).not.toMatch(/https?:\/\/(?!\*)/);
  });

  it('matches the version in package.json', () => {
    const pkg = JSON.parse(readFileSync(join(__dirname, '..', 'package.json'), 'utf8'));
    expect(manifest.version).toBe(pkg.version);
  });
});
