import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const repoRoot = path.resolve(__dirname, '..', '..');
const webUiRoot = path.join(repoRoot, 'src', 'YomicDesktop', 'WebUI');
const distRoot = path.join(webUiRoot, 'dist');

const mockData = await import(pathToFileURL(path.join(distRoot, 'mock-data.js')).href);
const sourceIcons = await import(pathToFileURL(path.join(distRoot, 'utils', 'source-icons.js')).href);
const bridgeMod = await import(pathToFileURL(path.join(distRoot, 'bridge.js')).href);
const i18nMod = await import(pathToFileURL(path.join(distRoot, 'i18n.js')).href);

describe('WebUI XSS & Input Sanitizers', () => {
  it('escapeXml encodes HTML/XML special characters and handles nullish values', () => {
    assert.equal(mockData.escapeXml(null), '');
    assert.equal(mockData.escapeXml(undefined), '');
    assert.equal(
      mockData.escapeXml(`<img src="x" onerror='alert(1)&'>`),
      '&lt;img src=&quot;x&quot; onerror=&apos;alert(1)&amp;&apos;&gt;'
    );
  });

  it('sanitizeImageSrc allows safe image schemes and rejects javascript/file/html data URIs', () => {
    assert.equal(mockData.sanitizeImageSrc('https://cdn.example.com/a.jpg?x=1&y=2'), 'https://cdn.example.com/a.jpg?x=1&amp;y=2');
    assert.equal(mockData.sanitizeImageSrc('http://yomic.local/img.png'), 'http://yomic.local/img.png');
    assert.equal(mockData.sanitizeImageSrc('data:image/png;base64,AAAA'), 'data:image/png;base64,AAAA');
    assert.equal(mockData.sanitizeImageSrc('blob:https://app.yomic/1234'), 'blob:https://app.yomic/1234');
    assert.equal(mockData.sanitizeImageSrc('./assets/cover.png'), './assets/cover.png');
    assert.equal(mockData.sanitizeImageSrc('app-logo.png'), 'app-logo.png');

    assert.equal(mockData.sanitizeImageSrc('javascript:alert(1)'), '');
    assert.equal(mockData.sanitizeImageSrc('  JAVASCRIPT:alert(1) '), '');
    assert.equal(mockData.sanitizeImageSrc('data:text/html,<script>alert(1)</script>'), '');
    assert.equal(mockData.sanitizeImageSrc('file:///C:/Windows/win.ini'), '');
    assert.equal(mockData.sanitizeImageSrc('vbscript:msgbox(1)'), '');
    assert.equal(mockData.sanitizeImageSrc(''), '');
  });

  it('sanitizeCssColor allows valid hex/rgb(a) and rejects CSS injection payloads', () => {
    assert.equal(mockData.sanitizeCssColor('#0F172A'), '#0F172A');
    assert.equal(mockData.sanitizeCssColor('rgb(255, 128, 0)'), 'rgb(255, 128, 0)');
    assert.equal(mockData.sanitizeCssColor('rgba(10, 20, 30, 0.5)'), 'rgba(10, 20, 30, 0.5)');

    assert.equal(mockData.sanitizeCssColor('#fff; background:url(javascript:alert(1))', '#111111'), '#111111');
    assert.equal(mockData.sanitizeCssColor('expression(alert(1))', '#222222'), '#222222');
    assert.equal(mockData.sanitizeCssColor(null, '#333333'), '#333333');
  });

  it('buildMangaCoverSvg escapes title/source and sanitizes palette colors', () => {
    const svgUri = mockData.buildMangaCoverSvg({
      id: 'm1',
      title: '<script>alert(1)</script>',
      sourceName: 'Bad"Source',
      coverPalette: ['red;evil', '#123456', 'url(x)']
    });
    assert.ok(svgUri.startsWith('data:image/svg+xml;utf8,'));
    const decoded = decodeURIComponent(svgUri.slice('data:image/svg+xml;utf8,'.length));
    assert.ok(!decoded.includes('<script>'));
    assert.ok(!decoded.includes('red;evil'));
    assert.ok(!decoded.includes('url(x)'));
    assert.ok(decoded.includes('&lt;script&gt;'));
  });

  it('getMangaCoverHtmlSrc rejects javascript: coverUrl and falls back to SVG', () => {
    const safeRes = sourceIcons.getMangaCoverHtmlSrc.call({}, {
      id: 'm1',
      title: 'Test',
      coverUrl: 'https://cdn.example.com/c.webp'
    });
    assert.equal(safeRes, 'https://cdn.example.com/c.webp');

    const unsafeRes = sourceIcons.getMangaCoverHtmlSrc.call({}, {
      id: 'm2',
      title: 'Evil',
      coverUrl: 'javascript:alert(1)'
    });
    assert.ok(unsafeRes.startsWith('data:image/svg+xml;utf8,'));
  });
});

describe('HostBridge IPC Lifecycle', () => {
  function setupMockWindow(withWebView = true) {
    const listeners = [];
    const posted = [];
    globalThis.window = {
      setTimeout: globalThis.setTimeout.bind(globalThis),
      clearTimeout: globalThis.clearTimeout.bind(globalThis),
      chrome: withWebView
        ? {
            webview: {
              postMessage(raw) {
                posted.push(JSON.parse(raw));
              },
              addEventListener(type, fn) {
                if (type === 'message') listeners.push(fn);
              }
            }
          }
        : undefined
    };
    return {
      posted,
      emit(data) {
        for (const fn of listeners) fn({ data });
      }
    };
  }

  it('resolves invoke() when host replies with matching requestId', async () => {
    const env = setupMockWindow(true);
    const bridge = new bridgeMod.HostBridge();
    assert.equal(bridge.isWebViewHost, true);

    const p = bridge.invoke('test:ping', { hello: 1 }, 1000);
    assert.equal(env.posted.length, 1);
    const reqId = env.posted[0].requestId;
    env.emit({ type: 'response', requestId: reqId, ok: true, payload: { pong: 42 } });

    const res = await p;
    assert.deepEqual(res, { pong: 42 });
  });

  it('rejects invoke() when host returns ok=false', async () => {
    const env = setupMockWindow(true);
    const bridge = new bridgeMod.HostBridge();

    const p = bridge.invoke('test:fail', {}, 1000);
    const reqId = env.posted[0].requestId;
    env.emit({ type: 'response', requestId: reqId, ok: false, error: 'Database locked' });

    await assert.rejects(p, /Database locked/);
  });

  it('rejects invoke() on timeout and when WebView2 host is unavailable', async () => {
    setupMockWindow(true);
    const bridge = new bridgeMod.HostBridge();
    await assert.rejects(bridge.invoke('test:slow', {}, 25), /melampaui batas waktu/);

    setupMockWindow(false);
    const noHostBridge = new bridgeMod.HostBridge();
    assert.equal(noHostBridge.isWebViewHost, false);
    await assert.rejects(noHostBridge.invoke('test:ping'), /tidak tersedia/);
  });

  it('dispatches push events and isolates handler exceptions', () => {
    const env = setupMockWindow(true);
    const bridge = new bridgeMod.HostBridge();
    const received = [];

    bridge.on('event:downloads-updated', () => {
      throw new Error('broken listener');
    });
    bridge.on('event:downloads-updated', (payload) => {
      received.push(payload);
    });

    env.emit(JSON.stringify({ type: 'event:downloads-updated', payload: [{ id: 'dl-1' }] }));
    assert.deepEqual(received, [[{ id: 'dl-1' }]]);
  });
});

describe('i18n & Static WebUI Integrity', () => {
  it('translates keys in both Indonesian and English without missing values', () => {
    assert.ok(i18nMod.translate('id', 'nav.library').length > 0);
    assert.ok(i18nMod.translate('en', 'nav.library').length > 0);
    assert.notEqual(i18nMod.translate('id', 'nav.library'), i18nMod.translate('en', 'nav.library'));
  });

  it('all index.html data-i18n keys resolve cleanly in both id and en', () => {
    const html = fs.readFileSync(path.join(webUiRoot, 'index.html'), 'utf8');
    const keyMatches = [...html.matchAll(/data-i18n(?:-(?:html|title|aria|placeholder))?="([^"]+)"/g)];
    assert.ok(keyMatches.length > 20, 'Expected data-i18n attributes in index.html');
    for (const m of keyMatches) {
      const key = m[1];
      const idVal = i18nMod.translate('id', key);
      const enVal = i18nMod.translate('en', key);
      assert.ok(idVal && idVal !== key, `Missing ID translation for key: ${key}`);
      assert.ok(enVal && enVal !== key, `Missing EN translation for key: ${key}`);
    }
  });

  it('index.html enforces strict CSP and main.css version-busts all existing CSS modules', () => {
    const html = fs.readFileSync(path.join(webUiRoot, 'index.html'), 'utf8');
    assert.ok(html.includes("script-src 'self'"), 'index.html must enforce script-src self');

    const mainCss = fs.readFileSync(path.join(webUiRoot, 'styles', 'main.css'), 'utf8');
    const imports = [...mainCss.matchAll(/@import\s+url\(['"]([^'"]+)['"]\);/g)];
    assert.equal(imports.length, 7, 'Expected 7 modular stylesheet imports in main.css');
    for (const m of imports) {
      const rawUrl = m[1];
      assert.ok(rawUrl.includes('?v='), `CSS @import missing ?v= cache-buster: ${rawUrl}`);
      const relFile = rawUrl.split('?')[0];
      const fullPath = path.resolve(path.join(webUiRoot, 'styles'), relFile);
      assert.ok(fs.existsSync(fullPath), `Missing imported CSS module: ${fullPath}`);
    }
  });

  it('reader switches in index.html have role=switch, tabindex=0, and aria-label', () => {
    const html = fs.readFileSync(path.join(webUiRoot, 'index.html'), 'utf8');
    for (const id of ['toggleReaderCrop', 'toggleReaderSharpen', 'toggleReaderOpt']) {
      const match = html.match(new RegExp(`<div\\s+id="${id}"[^>]*>`));
      assert.ok(match, `Missing reader switch #${id}`);
      assert.ok(match[0].includes('role="switch"'), `#${id} missing role="switch"`);
      assert.ok(match[0].includes('tabindex="0"'), `#${id} missing tabindex="0"`);
      assert.ok(match[0].includes('aria-label="'), `#${id} missing aria-label`);
    }
  });

  it('zero unguarded void this.bridge.invoke(...) calls without .catch() exist in WebUI/ts', () => {
    const tsRoot = path.join(webUiRoot, 'ts');
    const tsFiles = [];
    const walk = (dir) => {
      for (const ent of fs.readdirSync(dir, { withFileTypes: true })) {
        const full = path.join(dir, ent.name);
        if (ent.isDirectory()) walk(full);
        else if (ent.name.endsWith('.ts')) tsFiles.push(full);
      }
    };
    walk(tsRoot);
    assert.ok(tsFiles.length >= 10, 'Expected TypeScript source files under WebUI/ts');

    const unguarded = [];
    for (const file of tsFiles) {
      const text = fs.readFileSync(file, 'utf8');
      const regex = /void\s+this\.bridge\s*\.\s*invoke\b/g;
      let m;
      while ((m = regex.exec(text)) !== null) {
        // Walk to end of statement (semicolon at parenDepth 0 AND braceDepth 0, skipping strings)
        let parenDepth = 0;
        let braceDepth = 0;
        let idx = m.index;
        let stmtEnd = text.length;
        for (; idx < text.length; idx++) {
          const ch = text[idx];
          if (ch === '(') parenDepth++;
          else if (ch === ')') parenDepth = Math.max(0, parenDepth - 1);
          else if (ch === '{') braceDepth++;
          else if (ch === '}') braceDepth = Math.max(0, braceDepth - 1);
          else if (ch === ';' && parenDepth === 0 && braceDepth === 0) {
            stmtEnd = idx;
            break;
          }
        }
        const stmt = text.slice(m.index, stmtEnd);
        if (!stmt.includes('.catch(')) {
          const lineNum = text.slice(0, m.index).split(/\r?\n/).length;
          unguarded.push(`${path.relative(tsRoot, file)}:${lineNum}`);
        }
      }
    }

    assert.deepEqual(unguarded, [], `Unguarded void this.bridge.invoke() without .catch(): ${unguarded.join(', ')}`);
  });
});
