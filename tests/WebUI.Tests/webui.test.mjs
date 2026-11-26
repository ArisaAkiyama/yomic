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
const { SOLAR_PATHS, getIconSvg } = await import(pathToFileURL(path.join(distRoot, 'icons.js')).href);
const { buildReaderImgStyle, computePrefetchPageIndices } = await import(pathToFileURL(path.join(distRoot, 'views', 'reader.js')).href);
const { upsertMangaItem, bindBackendEvents } = await import(pathToFileURL(path.join(distRoot, 'services', 'backend-sync.js')).href);
const { isExplicitManga } = await import(pathToFileURL(path.join(distRoot, 'views', 'library.js')).href);

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

  it('reader switches in index.html have role=switch, tabindex=0, aria-label, and data-i18n-aria', () => {
    const html = fs.readFileSync(path.join(webUiRoot, 'index.html'), 'utf8');
    for (const id of ['toggleReaderCrop', 'toggleReaderSharpen', 'toggleReaderOpt']) {
      const match = html.match(new RegExp(`<div\\s+id="${id}"[^>]*>`));
      assert.ok(match, `Missing reader switch #${id}`);
      assert.ok(match[0].includes('role="switch"'), `#${id} missing role="switch"`);
      assert.ok(match[0].includes('tabindex="0"'), `#${id} missing tabindex="0"`);
      assert.ok(match[0].includes('aria-label="'), `#${id} missing aria-label`);
      assert.ok(match[0].includes('data-i18n-aria="'), `#${id} missing data-i18n-aria`);
    }
  });

  it('downloads.ts optimistic rollback matches dl-opt- prefix produced by manga-detail.ts', () => {
    const detailTs = fs.readFileSync(path.join(webUiRoot, 'ts', 'views', 'manga-detail.ts'), 'utf8');
    const downloadsTs = fs.readFileSync(path.join(webUiRoot, 'ts', 'views', 'downloads.ts'), 'utf8');
    assert.ok(detailTs.includes('dl-opt-'), 'Expected manga-detail.ts to create optimistic dl-opt- rows');
    assert.ok(downloadsTs.includes("startsWith('dl-opt-')"), 'downloads.ts catch rollback must match dl-opt- optimistic rows');
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
        // Walk to end of statement (semicolon at parenDepth 0 AND braceDepth 0), skipping strings and comments
        let parenDepth = 0;
        let braceDepth = 0;
        let idx = m.index;
        let stmtEnd = text.length;
        for (; idx < text.length; idx++) {
          const ch = text[idx];
          const next = text[idx + 1];
          // Skip // line comments
          if (ch === '/' && next === '/') {
            idx += 2;
            while (idx < text.length && text[idx] !== '\n') idx++;
            continue;
          }
          // Skip /* block comments */
          if (ch === '/' && next === '*') {
            idx += 2;
            while (idx + 1 < text.length && !(text[idx] === '*' && text[idx + 1] === '/')) idx++;
            idx++;
            continue;
          }
          // Skip quoted string literals ('...' or "...")
          if (ch === "'" || ch === '"') {
            const quote = ch;
            idx++;
            while (idx < text.length) {
              if (text[idx] === '\\') { idx += 2; continue; }
              if (text[idx] === quote) break;
              idx++;
            }
            continue;
          }
          // Skip template literals (`...`), balancing nested ${...} expressions
          if (ch === '`') {
            idx++;
            while (idx < text.length) {
              if (text[idx] === '\\') { idx += 2; continue; }
              if (text[idx] === '$' && text[idx + 1] === '{') {
                idx += 2;
                let exprDepth = 1;
                while (idx < text.length && exprDepth > 0) {
                  if (text[idx] === '\\') { idx += 2; continue; }
                  if (text[idx] === '{') exprDepth++;
                  else if (text[idx] === '}') exprDepth--;
                  if (exprDepth > 0) idx++;
                }
                idx++;
                continue;
              }
              if (text[idx] === '`') break;
              idx++;
            }
            continue;
          }
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

  it('every literal icon key passed to getIconSvg() or showToast() exists in SOLAR_PATHS', () => {
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

    const missing = [];
    for (const file of tsFiles) {
      const text = fs.readFileSync(file, 'utf8');
      for (const m of text.matchAll(/getIconSvg\(\s*['"]([a-zA-Z0-9_-]+)['"]/g)) {
        const key = m[1];
        if (!SOLAR_PATHS[key]) {
          missing.push(`${path.relative(tsRoot, file)}: getIconSvg('${key}')`);
        }
      }
      for (const m of text.matchAll(/showToast\([^;\n]+,\s*['"]([a-zA-Z0-9_-]+)['"]\s*\)/g)) {
        const key = m[1];
        if (!SOLAR_PATHS[key]) {
          missing.push(`${path.relative(tsRoot, file)}: showToast(..., '${key}')`);
        }
      }
    }

    assert.deepEqual(missing, [], `Missing icon keys in SOLAR_PATHS: ${missing.join(', ')}`);
    assert.ok(getIconSvg('warning').includes('<path'), 'warning icon must render non-empty SVG path');
  });

  it('buildReaderImgStyle scales images in fit-width, fit-height, original, and dual modes & computePrefetchPageIndices stays in bounds', () => {
    const fw100 = buildReaderImgStyle('fit-width', 'webtoon', 100);
    const fw150 = buildReaderImgStyle('fit-width', 'webtoon', 150);
    assert.ok(fw100.includes('width:740px') && fw100.includes('max-width:100%'));
    assert.ok(fw150.includes('width:1110px') && fw150.includes('max-width:none'), 'fit-width zoom > 100% must unclamp max-width so zooming works on narrow windows');

    const fh100 = buildReaderImgStyle('fit-height', 'single', 100);
    const fh150 = buildReaderImgStyle('fit-height', 'single', 150);
    assert.ok(fh100.includes('1.000') && fh150.includes('1.500') && fh150.includes('max-width:none'));

    const orig80 = buildReaderImgStyle('original', 'webtoon', 80);
    const orig140 = buildReaderImgStyle('original', 'webtoon', 140);
    assert.ok(orig80.includes('zoom:0.800') && orig140.includes('zoom:1.400'));

    const dual120 = buildReaderImgStyle('fit-width', 'dual', 120);
    assert.ok(dual120.includes('1.200') && dual120.includes('max-width:624px'));

    assert.deepEqual(computePrefetchPageIndices(1, 3, 5), [1, 2]);
    assert.deepEqual(computePrefetchPageIndices(3, 10, 3), [3, 4, 5, 1]);
    assert.deepEqual(computePrefetchPageIndices(1, 0, 5), []);
  });

  it('TRANSLATIONS id and en maintain 100% key and placeholder parity & interpolate params', () => {
    const idDict = i18nMod.TRANSLATIONS.id;
    const enDict = i18nMod.TRANSLATIONS.en;
    const idKeys = Object.keys(idDict);
    const enKeys = Object.keys(enDict);

    const missingInEn = idKeys.filter((k) => !(k in enDict));
    const missingInId = enKeys.filter((k) => !(k in idDict));
    assert.deepEqual(missingInEn, [], `Keys missing in EN_DICT: ${missingInEn.join(', ')}`);
    assert.deepEqual(missingInId, [], `Keys missing in ID_DICT: ${missingInId.join(', ')}`);
    assert.ok(idKeys.length >= 200, `Expected >= 200 translation keys, got ${idKeys.length}`);

    const emptyValues = [];
    const placeholderMismatches = [];
    const extractPlaceholders = (s) => [...new Set([...String(s).matchAll(/\{([a-zA-Z0-9_]+)\}/g)].map((m) => m[1]))].sort();

    for (const k of idKeys) {
      if (!idDict[k] || !enDict[k]) {
        emptyValues.push(k);
        continue;
      }
      const idVars = extractPlaceholders(idDict[k]);
      const enVars = extractPlaceholders(enDict[k]);
      if (JSON.stringify(idVars) !== JSON.stringify(enVars)) {
        placeholderMismatches.push(`${k}: id=[${idVars.join(',')}] vs en=[${enVars.join(',')}]`);
      }
    }

    assert.deepEqual(emptyValues, [], `Empty translation strings: ${emptyValues.join(', ')}`);
    assert.deepEqual(placeholderMismatches, [], `Placeholder mismatches between id and en:\n${placeholderMismatches.join('\n')}`);

    assert.equal(
      i18nMod.translate('en', 'settings.appearance.languageChanged', { lang: 'English' }),
      'Interface language switched to English.'
    );
  });

  it('upsertMangaItem merges src->db ID transitions and preserves chapters, categories, and MAL tracking', () => {
    const mockApp = {
      mangaList: [
        {
          id: 'src-10-abc',
          sourceId: '10',
          url: '/manga/solo',
          title: 'Solo',
          inLibrary: true,
          categoryIds: ['cat-fav'],
          chapters: [{ id: 'ch-1', number: 1, title: 'Ch 1' }],
          malTracked: true,
          malId: 121496,
          malTitle: 'Solo Leveling',
          malStatus: 'reading',
          malScore: 9
        }
      ],
      activeDetailMangaId: 'src-10-abc',
      readerMangaId: 'src-10-abc'
    };

    const merged = upsertMangaItem.call(mockApp, {
      id: 'db-42',
      sourceId: 10,
      url: '/manga/solo',
      title: 'Solo Leveling',
      inLibrary: true,
      categoryIds: [],
      chapters: [],
      malTracked: false,
      malId: 0
    });

    assert.equal(mockApp.mangaList.length, 1);
    assert.equal(merged.id, 'db-42');
    assert.equal(mockApp.activeDetailMangaId, 'db-42');
    assert.equal(mockApp.readerMangaId, 'db-42');
    assert.deepEqual(merged.categoryIds, ['cat-fav']);
    assert.equal(merged.chapters.length, 1);
    assert.equal(merged.malTracked, true);
    assert.equal(merged.malId, 121496);
    assert.equal(merged.malScore, 9);

    // Inserting a brand-new item without chapters initializes chapters to []
    const added = upsertMangaItem.call(mockApp, {
      id: 'db-99',
      sourceId: 10,
      url: '/manga/other',
      title: 'Other'
    });
    assert.equal(mockApp.mangaList.length, 2);
    assert.deepEqual(added.chapters, []);
  });

  it('buildComicPageSvg escapes XML payloads and DEFAULT_APP_SETTINGS / DEFAULT_HOTKEYS satisfy invariants', () => {
    const svgUri = mockData.buildComicPageSvg('<script>alert("x")</script>', 12, 3, 20);
    assert.ok(svgUri.startsWith('data:image/svg+xml;utf8,'));
    const decoded = decodeURIComponent(svgUri.slice('data:image/svg+xml;utf8,'.length));
    assert.ok(!decoded.includes('<script>'));
    assert.ok(decoded.includes('&lt;script&gt;'));
    assert.ok(decoded.includes('Bab 12'));
    assert.ok(decoded.includes('3 / 20'));

    assert.equal(mockData.DEFAULT_APP_SETTINGS.appLanguage, 'id');
    assert.equal(mockData.DEFAULT_APP_SETTINGS.defaultReaderMode, 'webtoon');
    assert.equal(mockData.DEFAULT_APP_SETTINGS.compactSidebar, false);
    assert.ok(Object.keys(mockData.DEFAULT_HOTKEYS).length === mockData.HOTKEY_DEFINITIONS.length);
  });

  it('every C# PushEvent("event:...") emitted in IPC has a registered listener in backend-sync.ts', () => {
    const ipcRoot = path.join(repoRoot, 'src', 'YomicDesktop', 'IPC');
    const csFiles = [];
    const walkCs = (dir) => {
      for (const ent of fs.readdirSync(dir, { withFileTypes: true })) {
        const full = path.join(dir, ent.name);
        if (ent.isDirectory()) walkCs(full);
        else if (ent.name.endsWith('.cs')) csFiles.push(full);
      }
    };
    walkCs(ipcRoot);

    const emitted = new Set();
    for (const file of csFiles) {
      const text = fs.readFileSync(file, 'utf8');
      for (const m of text.matchAll(/PushEvent\(\s*"(event:[a-z0-9-]+)"/g)) {
        emitted.add(m[1]);
      }
    }
    assert.ok(emitted.size >= 5, `Expected >= 5 PushEvent names, found ${emitted.size}`);

    const backendSyncTs = fs.readFileSync(path.join(webUiRoot, 'ts', 'services', 'backend-sync.ts'), 'utf8');
    const missingListeners = [...emitted].filter((ev) => !backendSyncTs.includes(`'${ev}'`) && !backendSyncTs.includes(`"${ev}"`));
    assert.deepEqual(missingListeners, [], `Unregistered C# PushEvent listeners in backend-sync.ts: ${missingListeners.join(', ')}`);
  });

  it('isExplicitManga matches numeric sourceId, getMangaCoverHtmlSrc rejects attribute breakout, and chapter tooltips match DEFAULT_HOTKEYS', () => {
    const mockApp = {
      extensions: [{ id: 'doujindesu', sourceId: 987654321, name: 'DoujinDesu', isNsfw: true }]
    };
    assert.equal(
      isExplicitManga.call(mockApp, { id: 'db-1', sourceId: '987654321', sourceName: 'Renamed Alias', genres: [] }),
      true
    );

    const breakout = sourceIcons.getMangaCoverHtmlSrc.call({}, {
      id: 'm3',
      title: 'Safe',
      coverUrl: 'https://cdn.example.com/a.jpg" onerror="alert(1)'
    });
    assert.ok(breakout.startsWith('data:image/svg+xml;utf8,'));

    assert.ok(i18nMod.translate('id', 'reader.prevChapterTitle').includes('([)'));
    assert.ok(i18nMod.translate('en', 'reader.nextChapterTitle').includes('(])'));
  });

  it('bindBackendEvents handles event:extensions-updated and updates extensions in-place', () => {
    const prevDoc = globalThis.document;
    globalThis.document = { getElementById: () => ({ id: 'viewContainer' }) };
    try {
      const handlers = new Map();
      let countersUpdated = 0;
      let persisted = 0;
      let renderedExtensions = 0;
      const mockApp = {
        bridge: {
          on(eventName, fn) {
            handlers.set(eventName, fn);
          }
        },
        extensions: [{ id: 'old-ext', sourceId: 1, name: 'Old', version: '1.0.0', hasUpdate: true }],
        downloads: [],
        mangaList: [],
        currentTab: 'extensions',
        activeDetailMangaId: null,
        activeBrowseSourceId: null,
        syncProgress: { active: false, current: 0, total: 1 },
        isManualBrowseRefreshing: false,
        browseVerificationProgress: { active: false, current: 0, total: 0 },
        downloadingExtProgress: new Map(),
        announcements: [],
        updateSidebarCounters() { countersUpdated++; },
        persistLocalCache() { persisted++; },
        renderExtensionsView() { renderedExtensions++; },
        renderBrowseView() {},
        renderLibraryView() {},
        renderUpdatesView() {},
        renderDownloadsView() {},
        applyBackendAnnouncements() {},
        showToast() {},
        l(id) { return id; }
      };

      bindBackendEvents.call(mockApp);
      assert.ok(handlers.has('event:extensions-updated'), 'Expected event:extensions-updated listener');

      handlers.get('event:extensions-updated')([
        { id: 'old-ext', sourceId: 1, name: 'Old', version: '1.1.0', hasUpdate: false }
      ]);

      assert.equal(mockApp.extensions.length, 1);
      assert.equal(mockApp.extensions[0].version, '1.1.0');
      assert.equal(mockApp.extensions[0].hasUpdate, false);
      assert.equal(countersUpdated, 1);
      assert.equal(persisted, 1);
      assert.equal(renderedExtensions, 1);
    } finally {
      globalThis.document = prevDoc;
    }
  });

  it('app.ts deep-clones DEFAULT_APP_SETTINGS.customHotkeys so mutating settings never corrupts defaults', () => {
    const appTs = fs.readFileSync(path.join(webUiRoot, 'ts', 'app.ts'), 'utf8');
    assert.ok(
      appTs.includes('customHotkeys: { ...DEFAULT_APP_SETTINGS.customHotkeys }'),
      'app.ts must deep-clone customHotkeys on initialization'
    );
    const cloned = { ...mockData.DEFAULT_APP_SETTINGS, customHotkeys: { ...mockData.DEFAULT_APP_SETTINGS.customHotkeys } };
    cloned.customHotkeys.readerNextPage = 'x';
    assert.equal(mockData.DEFAULT_APP_SETTINGS.customHotkeys.readerNextPage, undefined);
  });

  it('Batch 24 CSS & views enforce safe-center zoom, hover-unblur across all views, Chapter 0 reload, and multi-complete download counts', () => {
    const readerCss = fs.readFileSync(path.join(webUiRoot, 'styles', 'modules', '05-reader.css'), 'utf8');
    const refineCss = fs.readFileSync(path.join(webUiRoot, 'styles', 'modules', '07-refinements-updater.css'), 'utf8');
    const modalsCss = fs.readFileSync(path.join(webUiRoot, 'styles', 'modules', '06-modals-dark-stats.css'), 'utf8');
    assert.ok(readerCss.includes('align-items: safe center;'), '05-reader.css must use align-items: safe center');
    assert.ok(refineCss.includes('justify-content: safe center;'), '07-refinements-updater.css must use justify-content: safe center');
    assert.ok(modalsCss.includes('.v1-cover-img.is-blurred:hover'), '06-modals-dark-stats.css must unblur detail/list/update covers on hover');

    for (const rel of ['library.ts', 'browse.ts', 'manga-detail.ts', 'updates-history.ts', 'downloads.ts']) {
      const src = fs.readFileSync(path.join(webUiRoot, 'ts', 'views', rel), 'utf8');
      assert.ok(src.includes('blurExplicitCovers') && src.includes('is-blurred'), `${rel} must apply is-blurred when blurExplicitCovers is enabled`);
    }

    const readerTs = fs.readFileSync(path.join(webUiRoot, 'ts', 'views', 'reader.ts'), 'utf8');
    assert.ok(readerTs.includes('this.readerChapterNumber < 0'), 'reader.ts must allow refreshing Chapter 0');
    assert.ok(readerTs.includes("lower.startsWith('data:') || lower.startsWith('blob:')"), 'reader.ts must skip data:/blob: URIs in refreshReaderCanvasImages');

    const settingsTs = fs.readFileSync(path.join(webUiRoot, 'ts', 'views', 'settings.ts'), 'utf8');
    assert.ok(settingsTs.includes('this.tutorial.refreshLocale();'), 'settings.ts must refresh tutorial locale on language change');
    assert.ok(settingsTs.includes('this.toggleSidebarCollapse(false, false);'), 'settings.ts must reset sidebar collapse on factory reset');

    // Multi-complete downloadCount sync for unloaded chapters
    const prevDoc = globalThis.document;
    globalThis.document = { getElementById: () => ({ id: 'viewContainer' }), documentElement: { lang: 'en' } };
    try {
      const handlers = new Map();
      const mockApp = {
        bridge: { on(ev, fn) { handlers.set(ev, fn); } },
        extensions: [],
        downloads: [],
        mangaList: [{ id: 'db-99', chapters: [], downloadedCount: 0 }],
        currentTab: 'library',
        activeDetailMangaId: null,
        activeBrowseSourceId: null,
        syncProgress: { active: false, current: 0, total: 1 },
        isManualBrowseRefreshing: false,
        browseVerificationProgress: { active: false, current: 0, total: 0 },
        downloadingExtProgress: new Map(),
        announcements: [],
        updateSidebarCounters() {},
        persistLocalCache() {},
        renderExtensionsView() {},
        renderBrowseView() {},
        renderLibraryView() {},
        renderUpdatesView() {},
        renderDownloadsView() {},
        applyBackendAnnouncements() {},
        showToast() {},
        l(id) { return id; }
      };
      bindBackendEvents.call(mockApp);
      handlers.get('event:downloads-updated')([
        { id: 'd1', mangaId: 'db-99', chapterUrl: '/c1', chapterNumber: 1, status: 'completed', progressPercent: 100 },
        { id: 'd2', mangaId: 'db-99', chapterUrl: '/c2', chapterNumber: 2, status: 'completed', progressPercent: 100 }
      ]);
      assert.equal(mockApp.mangaList[0].downloadedCount, 2, 'Expected downloadedCount to reflect all completed chapters even when chapters[] is unloaded');
    } finally {
      globalThis.document = prevDoc;
    }
  });

  it('Batch 27 CSS & views fix Dual-Page zoom safe-center/300%, bare thumbnail blur clip-path, and Chapter 0 downloads', () => {
    const refineCss = fs.readFileSync(path.join(webUiRoot, 'styles', 'modules', '07-refinements-updater.css'), 'utf8');
    const modalsCss = fs.readFileSync(path.join(webUiRoot, 'styles', 'modules', '06-modals-dark-stats.css'), 'utf8');
    const readerTs = fs.readFileSync(path.join(webUiRoot, 'ts', 'views', 'reader.ts'), 'utf8');
    const downloadsTs = fs.readFileSync(path.join(webUiRoot, 'ts', 'views', 'downloads.ts'), 'utf8');

    assert.ok(refineCss.includes('justify-content: safe center !important;'), 'Dual-Page container must use safe center');
    assert.ok(refineCss.includes('width: max-content;'), 'Dual-Page container must allow horizontal expansion when zoomed > 100%');
    assert.ok(modalsCss.includes('clip-path: inset(0 round 5px);'), 'Bare thumbnail blur must clip cleanly inside 5px radius');
    assert.ok(readerTs.includes('Math.min(3.0,'), 'getDualPageImgStyle must allow zooming up to 300%');
    assert.ok(downloadsTs.includes('dl.chapterNumber >= 0'), 'downloads.ts must preserve Chapter 0');
  });
});
