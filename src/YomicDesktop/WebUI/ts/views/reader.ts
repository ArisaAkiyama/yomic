import type { YomicApp } from '../app.js';
import { getIconSvg, SOLAR_PATHS, icons } from '../icons.js';
import {
  buildComicPageSvg,
  buildMangaCoverSvg,
  DEFAULT_APP_SETTINGS,
  escapeXml,
  sanitizeImageSrc
} from '../mock-data.js';
import {
  ActivityUpdateItem,
  AppSettingsState,
  ChapterItem,
  DownloadQueueItem,
  ExtensionItem,
  LibraryCategory,
  LibraryFilterMode,
  LibrarySortBy,
  MangaItem,
  NavTabId,
  ReaderMode,
  ReaderPagesResult,
  SettingsSubTab,
  SourceFeedResult
} from '../types.js';

/* =========================================================================
 * 6. READER OVERLAY ENGINE (AUTO DEEP-OCEAN DARK #07111E)
 * ========================================================================= */

/**
 * Pure helper to compute 0-based page indices to prefetch around the current 1-based page.
 * Prefetches up to `lookahead` pages ahead (default 5) plus 1 page behind, deduplicated and bounds-safe.
 */
export function computePrefetchPageIndices(currentPage: number, totalPages: number, lookahead = 5): number[] {
  if (totalPages <= 0) return [];
  const clampedCurrent = Math.max(1, Math.min(totalPages, Math.floor(currentPage)));
  const currentIdx = clampedCurrent - 1;
  const result: number[] = [];
  for (let offset = 1; offset <= lookahead; offset++) {
    const idx = currentIdx + offset;
    if (idx < totalPages) {
      result.push(idx);
    }
  }
  const prevIdx = currentIdx - 1;
  if (prevIdx >= 0) {
    result.push(prevIdx);
  }
  return result;
}

/**
 * Pure helper producing the inline CSS style for a reader comic page image across all fit modes and zoom levels.
 * Ensures zoom controls scale images consistently in fit-width, fit-height, original, and dual modes.
 */
export function buildReaderImgStyle(fitMode: string, readerMode: string, zoomPercent: number): string {
  const clampedZoom = Math.max(25, Math.min(300, Number(zoomPercent) || 100));
  const scale = clampedZoom / 100;
  if (readerMode === 'dual') {
    const maxHeightPct = Math.round(clampedZoom);
    const maxWidthPx = Math.round(520 * scale);
    return `max-height:calc((100vh - 96px) * ${scale.toFixed(3)});max-width:${maxWidthPx}px;width:auto;height:auto;object-fit:contain;display:block;`;
  }
  if (fitMode === 'fit-height') {
    const maxW = scale > 1 ? 'none' : '100%';
    return `height:calc((100vh - 80px) * ${scale.toFixed(3)});width:auto;max-width:${maxW};object-fit:contain;display:block;margin:0 auto;`;
  }
  if (fitMode === 'original') {
    return `width:auto;max-width:none;height:auto;zoom:${scale.toFixed(3)};display:block;margin:0 auto;`;
  }
  const targetWidth = Math.round(740 * scale);
  const maxW = scale > 1 ? 'none' : '100%';
  return `width:${targetWidth}px;max-width:${maxW};height:auto;display:block;margin:0 auto;`;
}

/**
 * O(log N) binary search over cached page wrappers using layout offsets (offsetTop / offsetHeight),
 * eliminating O(N) getBoundingClientRect() forced synchronous layouts during 60-120 FPS Webtoon scroll.
 */
function findActiveWebtoonPageBinarySearch(wrappers: HTMLElement[], midContentY: number): number {
  const n = wrappers.length;
  if (n === 0) return 1;
  let lo = 0;
  let hi = n - 1;
  let ans = 0;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    const el = wrappers[mid];
    const top = el.offsetTop;
    const bottom = top + el.offsetHeight;
    if (midContentY < top) {
      hi = mid - 1;
    } else if (midContentY > bottom) {
      ans = mid;
      lo = mid + 1;
    } else {
      return mid + 1;
    }
  }
  return Math.max(1, Math.min(n, ans + 1));
}

function getDualSpreadPages(currentPage: number, totalPages: number): {
  leftPage: number;
  rightPage: number | null;
  spreadPages: number[];
} {
  const safeTotal = Math.max(1, totalPages);
  const clamped = Math.max(1, Math.min(safeTotal, Math.floor(currentPage)));
  const leftPage = clamped % 2 === 0 ? Math.max(1, clamped - 1) : clamped;
  const rightPage = leftPage + 1 <= safeTotal ? leftPage + 1 : null;
  const spreadPages = rightPage !== null ? [leftPage, rightPage] : [leftPage];
  return { leftPage, rightPage, spreadPages };
}

function getDualPageImgStyle(zoomPercent: number): string {
  const scale = Math.max(0.25, Math.min(3.0, (Number(zoomPercent) || 100) / 100));
  return `max-height:calc((100vh - 28px) * ${scale.toFixed(3)});max-width:calc((50vw - 20px) * ${scale.toFixed(3)});width:auto;height:auto;object-fit:contain;display:block;`;
}

export function prefetchReaderPagesAround(this: YomicApp, currentPage: number): void {
  if (!this.isReaderOpen || !this.readerPages || this.readerPages.length === 0) return;
  const indices = computePrefetchPageIndices(currentPage, this.readerPages.length, 5);
  for (const idx of indices) {
    const url = this.readerPages[idx];
    if (!url || this.readerPrefetchedUrls.has(url)) continue;
    this.readerPrefetchedUrls.add(url);

    const pageNum = idx + 1;
    const domImg = document.getElementById(`reader-page-${pageNum}`) as HTMLImageElement | null;
    if (domImg) {
      if (domImg.loading === 'lazy') {
        domImg.loading = 'eager';
      }
      domImg.setAttribute('fetchpriority', 'high');
      if (domImg.complete && domImg.naturalHeight > 0 && typeof domImg.decode === 'function') {
        domImg.decode().catch(() => {});
      }
    } else {
      const preload = new Image();
      preload.decoding = 'async';
      preload.src = url;
      if (typeof preload.decode === 'function') {
        preload.decode().catch(() => {});
      }
    }
  }
}

export function openReader(this: YomicApp, mangaId: string, chapterNumber: number, forceRefresh = false, manualVerify = false): void {
  // Flush/cancel any pending debounced progress save from the previous chapter BEFORE switching chapter state
  if (this.readerSaveProgressDebounceTimer) {
    clearTimeout(this.readerSaveProgressDebounceTimer);
    this.readerSaveProgressDebounceTimer = null;
    if (this.isReaderOpen && !this.isRestoringReaderScroll) {
      this.saveReaderProgressToBackend();
    }
  }
  this.stopReaderAutoScroll();
  this.saveWorkspaceScroll();
  let manga: MangaItem;
  const found = this.mangaList.find((m) => m.id === mangaId);
  if (found) {
    manga = found;
  } else if (this.mangaList.length > 0 && (!mangaId || mangaId === 'manga-0')) {
    manga = this.mangaList[0];
  } else {
    manga = {
      id: mangaId || 'manga-empty',
      title: mangaId ? this.l('Komik', 'Comic') : this.l('Pilih Komik', 'Select Comic'),
      altTitle: '',
      author: 'Unknown',
      artist: 'Studio',
      status: 'Ongoing',
      type: 'Manga',
      sourceId: '1',
      sourceName: this.l('Sumber Komik', 'Comic Source'),
      lang: 'ID',
      rating: 9.0,
      unreadCount: 0,
      downloadedCount: 0,
      totalChapters: 1,
      lastReadChapter: chapterNumber,
      lastReadPage: 1,
      lastReadTotalPages: 1,
      lastReadTimeAgo: 'Baru saja',
      inLibrary: false,
      isFavorite: false,
      malTracked: false,
      synopsis: '',
      genres: [],
      coverPalette: ['#071426', '#0066CC', '#38BDF8'],
      coverAccentLabel: 'YOMIC',
      chapters: [
        {
          id: `ch-${chapterNumber}`,
          number: chapterNumber,
          title: `Chapter ${chapterNumber}`,
          releaseDate: 'Terbaru',
          isRead: false,
          isBookmarked: false,
          isDownloaded: false,
          scanlator: ''
        }
      ]
    };
    this.mangaList.push(manga);
  }
  this.isReaderOpen = true;
  this.readerMangaId = manga.id;
  this.readerChapterNumber = chapterNumber;
  this.readerPrefetchedUrls.clear();
  this.readerPageWrappers = [];
  this.readerLastCompletedFlushKey = '';

  // Retrieve instant saved position (Yomic v1 pattern)
  let initialSavedPercent = 0;
  let initialSavedPage = 1;
  try {
    const localRaw = localStorage.getItem(`yomic_ch_pos_${manga.id}_${chapterNumber}`);
    if (localRaw) {
      const parsed = JSON.parse(localRaw);
      if (typeof parsed.scrollPercent === 'number' && parsed.scrollPercent > 0) {
        initialSavedPercent = parsed.scrollPercent;
      }
      if (typeof parsed.page === 'number' && parsed.page > 0) {
        initialSavedPage = parsed.page;
      }
    }
  } catch {}

  const chObj = manga.chapters.find((c) => Math.abs(c.number - chapterNumber) < 0.01);
  if (chObj) {
    if (chObj.scrollPercent && chObj.scrollPercent > 0 && initialSavedPercent === 0) {
      initialSavedPercent = chObj.scrollPercent;
    }
    if (chObj.lastPageRead && chObj.lastPageRead > 0 && initialSavedPage === 1) {
      if (chObj.lastPageRead > 1000) {
        initialSavedPercent = chObj.lastPageRead / 1_000_000.0;
        if (manga.lastReadTotalPages && manga.lastReadTotalPages > 1) {
          initialSavedPage = Math.min(manga.lastReadTotalPages, Math.max(1, Math.round(initialSavedPercent * (manga.lastReadTotalPages - 1)) + 1));
        }
      } else {
        initialSavedPage = chObj.lastPageRead;
      }
    }
  } else if (manga.lastReadChapter === chapterNumber && manga.lastReadPage && manga.lastReadPage > 0 && initialSavedPage === 1) {
    initialSavedPage = manga.lastReadPage;
  }

  this.readerCurrentPage = initialSavedPage;
  this.readerScrollPercent = initialSavedPercent;
  this.targetRestorePercent = initialSavedPercent;
  this.readerPages = [];
  this.readerTotalPages = 0;
  manga.lastReadChapter = chapterNumber;
  manga.lastReadPage = initialSavedPage;
  manga.lastReadTimeAgo = 'Baru saja';
  manga.lastViewedEpoch = Date.now();
  this.readerLastSaveEpoch = Date.now();
  this.readerChapterUrl = chObj?.url || '';
  const overlay = document.getElementById('readerOverlay');
  overlay?.classList.add('open');
  overlay?.setAttribute('aria-hidden', 'false');
  overlay?.classList.remove('hud-hidden');
  if (this.readerHudAutoHideTimer) {
    clearTimeout(this.readerHudAutoHideTimer);
  }
  this.readerHudAutoHideTimer = window.setTimeout(() => {
    if (this.isReaderOpen && !this.isReaderFlyoutOpen() && !this.isReaderInteractingWithControls) {
      this.hideReaderHud();
    }
    this.readerHudAutoHideTimer = null;
  }, 3000);
  const titleEl = document.getElementById('readerMangaTitle');
  const subEl = document.getElementById('readerChapterTitle');
  if (titleEl) titleEl.textContent = manga.title;
  if (subEl) {
    subEl.textContent = `Chapter ${chapterNumber}`;
  }
  const bookmarkBtn = document.getElementById('btnReaderBookmarkChapter');
  if (bookmarkBtn) {
    const isBookmarked = Boolean(chObj?.isBookmarked);
    bookmarkBtn.classList.toggle('is-bookmarked', isBookmarked);
    const bookmarkIconSlot = document.getElementById('readerBookmarkIconSlot');
    const bookmarkLabel = document.getElementById('readerBookmarkLabel');
    const labelText = isBookmarked
      ? this.l('Hapus Tanda Chapter', 'Remove Chapter Bookmark')
      : this.l('Tandai Chapter', 'Bookmark Chapter');
    if (bookmarkIconSlot) {
      bookmarkIconSlot.innerHTML = getIconSvg(isBookmarked ? 'bookmark-fill' : 'bookmark-line', { size: 17 });
    } else {
      bookmarkBtn.innerHTML = getIconSvg(isBookmarked ? 'bookmark-fill' : 'bookmark-line', { size: 17 });
    }
    if (bookmarkLabel) {
      bookmarkLabel.textContent = labelText;
    }
    bookmarkBtn.title = labelText;
    bookmarkBtn.setAttribute('aria-label', labelText);
  }

  // Dynamically update HUD button tooltips with effective hotkeys
  const updateTooltip = (id: string, text: string) => {
    const el = document.getElementById(id);
    if (el) el.title = text;
  };
  updateTooltip('btnCloseReader', `${this.l('Kembali / Tutup', 'Back / Close')} (${this.getEffectiveHotkey('readerClose')})`);
  updateTooltip('btnReaderFullscreen', `${this.l('Layar Penuh', 'Fullscreen')} (${this.getEffectiveHotkey('readerToggleFullscreen')})`);
  updateTooltip('btnAutoScroll', `${this.t('reader.autoScroll')} (${this.getEffectiveHotkey('readerToggleAutoScroll')})`);
  updateTooltip('btnZoomIn', `${this.l('Perbesar Zoom', 'Zoom In')} (${this.getEffectiveHotkey('readerZoomIn')})`);
  updateTooltip('btnZoomOut', `${this.l('Perkecil Zoom', 'Zoom Out')} (${this.getEffectiveHotkey('readerZoomOut')})`);
  document.getElementById('readerModeFlyoutMenu')?.classList.remove('open');
  document.getElementById('btnReaderModeFlyout')?.classList.remove('active');
  document.getElementById('readerZoomFlyoutMenu')?.classList.remove('open');
  document.getElementById('btnReaderZoomFlyout')?.classList.remove('active');
  document.getElementById('readerChapterDropdownMenu')?.classList.remove('open');
  document.getElementById('btnReaderChapterDropdown')?.classList.remove('active');
  document.getElementById('readerChapterSheetMenu')?.classList.remove('open');
  document.getElementById('btnReaderChapterListDock')?.classList.remove('active');
  document.getElementById('readerImageFlyoutMenu')?.classList.remove('open');
  document.getElementById('btnReaderImageFlyout')?.classList.remove('active');
  document.getElementById('readerMoreFlyoutMenu')?.classList.remove('open');
  document.getElementById('btnReaderMoreFlyout')?.classList.remove('active');
  this.applyTheme(this.settings.isDarkMode, `${manga.title} (Ch. ${chapterNumber})`);
  const cacheKey = `${manga.id}_${chapterNumber}`;
  const cachedPages = !forceRefresh ? this.chapterPagesCache?.get(cacheKey) : undefined;
  if (cachedPages && cachedPages.length > 0) {
    this.readerLoadSeq++;
    this.readerPages = cachedPages;
    this.readerTotalPages = cachedPages.length;
    this.isReaderLoadingPages = false;
    this.setReaderMode(this.readerMode);
    if (this.targetRestorePercent <= 0 && this.readerCurrentPage <= 1) {
      const scrollArea = document.getElementById('readerCanvasScroll');
      if (scrollArea) scrollArea.scrollTop = 0;
    }
    this.restoreReaderScrollPosition();
    return;
  }
  if (this.bridge.isWebViewHost) {
    const reqSeq = ++this.readerLoadSeq;
    this.isReaderLoadingPages = true;
    this.readerRequiresVerification = false;
    this.setReaderMode(this.readerMode);
    this.updateReaderPageIndicator();
    void this.bridge
      .invoke<ReaderPagesResult>(
        'reader:get-pages',
        {
          mangaId: manga.id,
          mangaUrl: manga.url || '',
          dbId: manga.dbId,
          sourceId: Number(manga.sourceId) || 0,
          sourceName: manga.sourceName || '',
          chapterId: chObj?.dbId,
          chapterNumber,
          chapterUrl: chObj?.url || '',
          forceRefresh,
          manualVerify
        },
        120000
      )
      .then((res) => {
        if (this.readerLoadSeq !== reqSeq || !this.isReaderOpen || this.readerMangaId !== manga.id || this.readerChapterNumber !== chapterNumber) {
          return;
        }
        if (res && Array.isArray(res.pages) && res.pages.length > 0) {
          this.readerRequiresVerification = false;
          this.chapterPagesCache?.set(cacheKey, res.pages);
          this.readerPages = res.pages;
          this.readerTotalPages = res.pages.length;
          const backendPercent = typeof res.scrollPercent === 'number' && res.scrollPercent > 0 ? res.scrollPercent : 0;
          if (backendPercent > 0) {
            this.targetRestorePercent = backendPercent;
            this.readerScrollPercent = backendPercent;
          }
          if (this.targetRestorePercent > 0 && (this.readerMode === 'webtoon' || !res.lastPageRead || res.lastPageRead <= 1)) {
            this.readerCurrentPage = Math.min(
              this.readerTotalPages,
              Math.max(1, Math.round(this.targetRestorePercent * (this.readerTotalPages - 1)) + 1)
            );
          } else if (res.lastPageRead && res.lastPageRead > 0) {
            this.readerCurrentPage = Math.max(1, Math.min(this.readerTotalPages, res.lastPageRead));
          } else {
            this.readerCurrentPage = Math.max(1, Math.min(this.readerTotalPages, initialSavedPage));
          }
        } else {
          this.readerRequiresVerification = Boolean(res?.requiresVerification);
          this.readerPages = [];
          this.readerTotalPages = 0;
          this.readerCurrentPage = 1;
        }
      })
      .catch(() => {
        if (this.readerLoadSeq !== reqSeq || !this.isReaderOpen || this.readerMangaId !== manga.id || this.readerChapterNumber !== chapterNumber) {
          return;
        }
        this.readerPages = [];
        this.readerTotalPages = 0;
        this.readerCurrentPage = 1;
        this.showToast(this.l('Gagal mengambil halaman bab dari sumber.', 'Failed to fetch chapter pages from source.'));
      })
      .finally(() => {
        if (this.readerLoadSeq !== reqSeq || !this.isReaderOpen || this.readerMangaId !== manga.id || this.readerChapterNumber !== chapterNumber) {
          return;
        }
        this.isReaderLoadingPages = false;
        this.renderReaderPages();
        this.updateReaderPageIndicator();
        this.restoreReaderScrollPosition();
      });
    return;
  }
  this.setReaderMode(this.readerMode);
  this.updateReaderPageIndicator();
}

export function retryPageImg(this: YomicApp, pageNumber: number): void {
  const wrap = document.getElementById(`reader-page-wrap-${pageNumber}`);
  const rawUrl = this.readerPages[pageNumber - 1];
  if (!wrap || !rawUrl) return;
  const fitMode = this.settings.readerFitMode || 'fit-width';
  const baseWidth = Math.round((this.readerMode === 'dual' ? 480 : 740) * (this.readerZoomPercent / 100));
  const imgStyle = this.readerMode === 'dual'
    ? getDualPageImgStyle(this.readerZoomPercent)
    : buildReaderImgStyle(fitMode, this.readerMode, this.readerZoomPercent);
  const isHttpLike = /^https?:\/\//i.test(rawUrl);
  const retryCandidate = isHttpLike
    ? (rawUrl.includes('?') ? `${rawUrl}&force=1&t=${Date.now()}` : `${rawUrl}?force=1&t=${Date.now()}`)
    : rawUrl;
  const freshUrl = sanitizeImageSrc(retryCandidate);
  const pageAlt = escapeXml(this.l(`Halaman ${pageNumber}`, `Page ${pageNumber}`));
  wrap.innerHTML = `
    <img id="reader-page-${pageNumber}" class="reader-page-art" style="${imgStyle}" src="${freshUrl}" alt="${pageAlt}" loading="eager" decoding="async" data-page-num="${pageNumber}" />
  `;
  const img = wrap.querySelector('img');
  if (img) {
    img.onerror = () => {
      wrap.innerHTML = `
        <div class="reader-img-error" data-retry-page="${pageNumber}" style="width:${baseWidth}px;min-height:240px;background:var(--reader-surface,#FFFFFF);border:1px dashed var(--reader-border,#D4D4D8);border-radius:8px;display:flex;flex-direction:column;align-items:center;justify-content:center;color:var(--reader-ink-muted,#71717A);gap:8px;cursor:pointer;margin:0 auto;">
          <span style="font-size:14px;color:#EF4444;">⚠️ ${this.l(`Gagal memuat Halaman ${pageNumber}`, `Failed to load Page ${pageNumber}`)}</span>
          <span style="font-size:12px;color:var(--reader-ink,#09090B);font-weight:600;">${this.l('Klik untuk coba lagi', 'Click to retry')}</span>
        </div>
      `;
    };
  }
}

export function saveReaderProgressToBackend(this: YomicApp): void {
  // Never save/complete unloaded or failed chapters (prevents accidental mass mark-as-read & auto-delete)
  if (this.isReaderLoadingPages || this.readerPages.length === 0 || this.readerTotalPages <= 0) {
    return;
  }

  const manga = this.mangaList.find((m) => m.id === this.readerMangaId);
  const chObj = manga?.chapters.find((c) => Math.abs(c.number - this.readerChapterNumber) < 0.01);
  let isCompleted = false;
  if (this.readerMode === 'dual') {
    const { spreadPages } = getDualSpreadPages(this.readerCurrentPage, this.readerTotalPages);
    isCompleted = spreadPages.includes(this.readerTotalPages);
  } else if (this.readerMode === 'webtoon') {
    isCompleted = this.readerScrollPercent >= 0.995 || this.readerCurrentPage >= this.readerTotalPages;
  } else {
    isCompleted = this.readerCurrentPage >= this.readerTotalPages;
  }

  if (manga) {
    manga.lastReadChapter = this.readerChapterNumber;
    manga.lastReadPage = this.readerCurrentPage;
    manga.lastReadTotalPages = this.readerTotalPages;
    manga.lastViewedEpoch = Date.now();
    manga.lastReadTimeAgo = 'Baru saja';
  }

  // Update chapter in-memory state
  const normalizedPagedPercent = this.readerTotalPages > 1 ? (this.readerCurrentPage - 1) / (this.readerTotalPages - 1) : 0;
  if (chObj) {
    if (this.readerMode === 'webtoon' && this.readerScrollPercent > 0) {
      chObj.lastPageRead = Math.max(1001, Math.round(this.readerScrollPercent * 1_000_000));
      chObj.scrollPercent = this.readerScrollPercent;
    } else {
      chObj.lastPageRead = this.readerCurrentPage;
      chObj.scrollPercent = normalizedPagedPercent;
    }
  }

  // Save to localStorage for instant 0ms restoration
  try {
    const posData = {
      page: this.readerCurrentPage,
      scrollPercent: this.readerMode === 'webtoon' ? this.readerScrollPercent : normalizedPagedPercent,
      mode: this.readerMode,
      totalPages: this.readerTotalPages,
      timestamp: Date.now()
    };
    localStorage.setItem(`yomic_ch_pos_${this.readerMangaId}_${this.readerChapterNumber}`, JSON.stringify(posData));
  } catch {}

  if (!this.bridge.isWebViewHost || !manga) return;
  if (isCompleted && chObj && !chObj.isRead) {
    chObj.isRead = true;
    manga.unreadCount = Math.max(0, manga.unreadCount - 1);
  }
  const nowMs = Date.now();
  const elapsedSec = this.readerLastSaveEpoch > 0
    ? Math.min(180, Math.max(0, Math.round((nowMs - this.readerLastSaveEpoch) / 1000)))
    : 0;
  this.readerLastSaveEpoch = nowMs;
  void this.bridge
    .invoke('reader:save-progress', {
      mangaId: manga.id,
      mangaUrl: manga.url || '',
      sourceId: Number(manga.sourceId) || 0,
      dbId: manga.dbId,
      chapterId: chObj?.dbId,
      chapterUrl: chObj?.url || '',
      chapterNumber: this.readerChapterNumber,
      currentPage: this.readerCurrentPage,
      totalPages: this.readerTotalPages,
      scrollPercent: this.readerMode === 'webtoon' ? this.readerScrollPercent : 0,
      mode: this.readerMode,
      completed: isCompleted,
      isRead: isCompleted || (chObj?.isRead ?? false),
      sessionReadSeconds: elapsedSec
    })
    .catch(() => {});
}

export function closeReader(this: YomicApp): void {
  this.readerLoadSeq++;
  if (this.readerSaveProgressDebounceTimer) {
    clearTimeout(this.readerSaveProgressDebounceTimer);
    this.readerSaveProgressDebounceTimer = null;
  }
  this.saveReaderProgressToBackend();
  this.isReaderOpen = false;
  this.isReaderLoadingPages = false;
  this.readerRequiresVerification = false;
  this.stopReaderAutoScroll();
  this.isRestoringReaderScroll = false;
  this.targetRestorePercent = -1;
  this.readerPages = [];
  this.readerTotalPages = 0;
  this.readerPageWrappers = [];
  this.readerPrefetchedUrls.clear();
  this.readerLastCompletedFlushKey = '';
  const pagesContainer = document.getElementById('readerPagesContainer') as (HTMLElement & { _cancelScrollRestore?: () => void }) | null;
  if (pagesContainer) {
    pagesContainer._cancelScrollRestore?.();
    pagesContainer._cancelScrollRestore = undefined;
    pagesContainer.innerHTML = '';
    pagesContainer.style.opacity = '1';
    pagesContainer.style.transition = '';
  }
  if (this.readerHudAutoHideTimer) {
    clearTimeout(this.readerHudAutoHideTimer);
    this.readerHudAutoHideTimer = null;
  }
  this.isReaderInteractingWithControls = false;
  document.getElementById('readerModeFlyoutMenu')?.classList.remove('open');
  document.getElementById('btnReaderModeFlyout')?.classList.remove('active');
  document.getElementById('readerZoomFlyoutMenu')?.classList.remove('open');
  document.getElementById('btnReaderZoomFlyout')?.classList.remove('active');
  document.getElementById('readerChapterDropdownMenu')?.classList.remove('open');
  document.getElementById('btnReaderChapterDropdown')?.classList.remove('active');
  document.getElementById('readerChapterSheetMenu')?.classList.remove('open');
  document.getElementById('btnReaderChapterListDock')?.classList.remove('active');
  document.getElementById('readerImageFlyoutMenu')?.classList.remove('open');
  document.getElementById('btnReaderImageFlyout')?.classList.remove('active');
  document.getElementById('readerMoreFlyoutMenu')?.classList.remove('open');
  document.getElementById('btnReaderMoreFlyout')?.classList.remove('active');
  const overlay = document.getElementById('readerOverlay');
  overlay?.classList.remove('open');
  overlay?.setAttribute('aria-hidden', 'true');
  this.applyTheme(this.settings.isDarkMode);
  this.renderCurrentWorkspace(true);
}

export function setReaderMode(this: YomicApp, mode: ReaderMode): void {
  this.readerMode = mode;
  if (mode !== 'webtoon') {
    this.stopReaderAutoScroll();
  }
  document.querySelectorAll<HTMLButtonElement>('[data-reader-mode]').forEach((btn) => {
    btn.classList.toggle('active', btn.dataset.readerMode === mode);
  });
  const dockLabel = document.getElementById('readerModeDockLabel');
  if (dockLabel) {
    dockLabel.textContent =
      mode === 'webtoon'
        ? this.l('Webtoon', 'Webtoon')
        : mode === 'single'
        ? this.l('Tunggal', 'Single')
        : this.l('Dua Hal.', 'Dual');
  }
  const overlay = document.getElementById('readerOverlay');
  overlay?.classList.toggle('paged-mode', mode !== 'webtoon');
  this.renderReaderPages();
  this.updateReaderPageIndicator();
}

export function renderReaderPages(this: YomicApp): void {
  const pagesContainer = document.getElementById('readerPagesContainer');
  const scrollArea = document.getElementById('readerCanvasScroll');
  if (!pagesContainer || !scrollArea) return;
  const manga = this.mangaList.find((m) => m.id === this.readerMangaId) ?? this.mangaList[0];
  const isRtl = this.settings.readingDirection === 'rtl';
  pagesContainer.className = `reader-pages-container mode-${this.readerMode}${isRtl ? ' dir-rtl' : ''}`;
  const baseWidth = Math.round((this.readerMode === 'dual' ? 480 : 740) * (this.readerZoomPercent / 100));

  // Event delegation on container
  pagesContainer.onclick = (ev) => {
    const target = (ev.target as HTMLElement).closest<HTMLElement>('[data-retry-page]');
    if (target) {
      ev.stopPropagation();
      const pageNum = Number(target.dataset.retryPage);
      if (!Number.isNaN(pageNum)) {
        this.retryPageImg(pageNum);
      }
      return;
    }
    if ((ev.target as HTMLElement).closest('#btnRetryReaderPages')) {
      ev.stopPropagation();
      this.openReader(this.readerMangaId, this.readerChapterNumber, true, false);
      return;
    }
    if ((ev.target as HTMLElement).closest('#btnVerifyReaderPages')) {
      ev.stopPropagation();
      this.openReader(this.readerMangaId, this.readerChapterNumber, true, true);
      return;
    }
  };
  if (this.isReaderLoadingPages) {
    pagesContainer.innerHTML = `
      <div class="reader-loading-wrap" style="padding:120px 24px;text-align:center;display:flex;flex-direction:column;align-items:center;gap:16px;">
        <div class="reader-spinner" style="width:40px;height:40px;border:3px solid var(--reader-track,rgba(24,24,27,0.14));border-top-color:var(--reader-accent,#18181B);border-radius:50%;animation:spin 0.8s linear infinite;"></div>
        <div style="font-size:15px;font-weight:600;color:var(--reader-ink,#09090B);">${this.l(`Memuat Halaman Chapter ${this.readerChapterNumber}...`, `Loading Chapter ${this.readerChapterNumber} Pages...`)}</div>
        <div style="font-size:12px;color:var(--reader-ink-muted,#71717A);">${this.l('Mengambil & memeriksa akses gambar komik dari sumber', 'Fetching & verifying comic image access from source')}</div>
      </div>
    `;
    return;
  }
  if (!this.readerPages || this.readerPages.length === 0) {
    const errorMsg = this.readerRequiresVerification
      ? this.l(
          `Selesaikan verifikasi di WebView untuk Chapter ${this.readerChapterNumber} lalu coba lagi.`,
          `Complete verification in WebView for Chapter ${this.readerChapterNumber} then try again.`
        )
      : this.l(
          `Halaman untuk Chapter ${this.readerChapterNumber} tidak dapat diambil dari sumber. Sumber mungkin sedang offline, memerlukan verifikasi Cloudflare, atau belum merilis gambar untuk bab ini.`,
          `Pages for Chapter ${this.readerChapterNumber} could not be fetched from the source. The source may be offline, require Cloudflare verification, or has not released images for this chapter yet.`
        );
    pagesContainer.innerHTML = `
      <div class="reader-error-wrap" style="min-height:calc(100vh - 140px);padding:48px 24px;text-align:center;max-width:480px;margin:0 auto;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:14px;">
        <img src="assets/reader/no-data-rafiki.svg" alt="${escapeXml(this.l('Gambar Bab Belum Tersedia', 'Chapter Images Unavailable'))}" draggable="false" style="width:200px;height:auto;max-height:200px;object-fit:contain;user-select:none;margin-bottom:4px;" />
        <h3 style="font-size:18px;font-weight:600;color:var(--reader-ink,#09090B);margin:0;">${this.l(
          this.readerRequiresVerification ? 'Verifikasi Keamanan Diperlukan' : 'Gambar Bab Belum Tersedia',
          this.readerRequiresVerification ? 'Security Verification Required' : 'Chapter Images Unavailable'
        )}</h3>
        <p style="font-size:13px;color:var(--reader-ink-muted,#71717A);margin:0;line-height:1.5;">${errorMsg}</p>
        <div style="display:flex;gap:10px;margin-top:8px;">
          ${
            this.readerRequiresVerification
              ? `<button id="btnVerifyReaderPages" class="reader-action-btn" style="background:#EA580C;color:#fff;border:none;padding:8px 18px;border-radius:8px;font-size:13px;font-weight:500;cursor:pointer;">${this.l('Verifikasi di WebView', 'Verify in WebView')}</button>`
              : ''
          }
          <button id="btnRetryReaderPages" class="reader-action-btn" style="background:var(--reader-accent,#18181B);color:var(--reader-accent-ink,#FFFFFF);border:none;padding:8px 18px;border-radius:8px;font-size:13px;font-weight:600;cursor:pointer;">${this.l('Muat Ulang', 'Reload')}</button>
        </div>
      </div>
    `;
    return;
  }
  const fitMode = this.settings.readerFitMode || 'fit-width';
  const pageImgStyle = buildReaderImgStyle(fitMode, this.readerMode, this.readerZoomPercent);
  const pageAltPrefix = this.l('Halaman', 'Page');
  pagesContainer.dir = 'ltr';
  const eagerIndices = new Set<number>([
    Math.max(0, Math.min(this.readerTotalPages - 1, this.readerCurrentPage - 1)),
    ...computePrefetchPageIndices(this.readerCurrentPage, this.readerTotalPages, 5)
  ]);

  if (this.readerMode === 'single') {
    const realUrl = sanitizeImageSrc(this.readerPages[this.readerCurrentPage - 1] || '');
    pagesContainer.innerHTML = `
      <div class="reader-page-item" id="reader-page-wrap-${this.readerCurrentPage}" style="position:relative;display:inline-block;margin:0 auto;">
        <img id="reader-page-${this.readerCurrentPage}" class="reader-page-art" style="${pageImgStyle}" src="${realUrl}" alt="${pageAltPrefix} ${this.readerCurrentPage}" decoding="async" fetchpriority="high" data-page-num="${this.readerCurrentPage}" />
      </div>
    `;
    scrollArea.scrollTop = 0;
    pagesContainer.style.opacity = '1';
    this.isRestoringReaderScroll = false;
    this.readerPageWrappers = [];
  } else if (this.readerMode === 'dual') {
    const { spreadPages } = getDualSpreadPages(this.readerCurrentPage, this.readerTotalPages);
    const dualStyle = getDualPageImgStyle(this.readerZoomPercent);
    const pagesHtml: string[] = [];
    for (const p of spreadPages) {
      const realUrl = sanitizeImageSrc(this.readerPages[p - 1] || '');
      pagesHtml.push(
        `<div class="reader-page-item reader-dual-spread-item" id="reader-page-wrap-${p}">
          <img id="reader-page-${p}" class="reader-page-art" style="${dualStyle}" src="${realUrl}" alt="${pageAltPrefix} ${p}" loading="eager" decoding="async" fetchpriority="high" data-page-num="${p}" />
        </div>`
      );
    }
    pagesContainer.innerHTML = pagesHtml.join('');
    scrollArea.scrollTop = 0;
    pagesContainer.style.opacity = '1';
    this.isRestoringReaderScroll = false;
    this.readerPageWrappers = [];
  } else {
    const pagesHtml: string[] = [];
    const fallbackHeight = Math.round(baseWidth * 1.35);
    for (let p = 1; p <= this.readerTotalPages; p++) {
      const rawPageUrl = this.readerPages[p - 1] || '';
      const realUrl = sanitizeImageSrc(rawPageUrl);
      const cachedRatio = rawPageUrl ? this.readerAspectRatioCache.get(rawPageUrl) : undefined;
      const estHeight = cachedRatio && cachedRatio > 0.05 ? Math.round(baseWidth / cachedRatio) : fallbackHeight;
      const isEager = eagerIndices.has(p - 1);
      pagesHtml.push(
        `<div class="reader-page-item" id="reader-page-wrap-${p}" style="position:relative;display:block;margin:0 auto;min-height:${estHeight}px;contain-intrinsic-size:auto ${baseWidth}px ${estHeight}px;">
          <img id="reader-page-${p}" class="reader-page-art" style="${pageImgStyle}" src="${realUrl}" alt="${pageAltPrefix} ${p}" loading="${isEager ? 'eager' : 'lazy'}" decoding="async"${isEager ? ' fetchpriority="high"' : ''} data-page-num="${p}" />
        </div>`
      );
    }

    const hasInitialRestore = this.targetRestorePercent > 0;
    const targetPercent = hasInitialRestore
      ? this.targetRestorePercent
      : (this.readerScrollPercent > 0
          ? this.readerScrollPercent
          : (this.readerCurrentPage > 1 && this.readerTotalPages > 1
              ? (this.readerCurrentPage - 1) / (this.readerTotalPages - 1)
              : 0));

    // Only hide with opacity 0 if a dedicated scroll restore is about to run (from openReader)
    if (hasInitialRestore) {
      pagesContainer.style.transition = 'none';
      pagesContainer.style.opacity = '0';
      this.isRestoringReaderScroll = true;
    } else {
      pagesContainer.style.opacity = '1';
      this.isRestoringReaderScroll = false;
    }

    pagesContainer.innerHTML = pagesHtml.join('');
    this.readerPageWrappers = Array.from(pagesContainer.querySelectorAll<HTMLElement>('.reader-page-item'));

    if (targetPercent > 0) {
      scrollArea.style.scrollBehavior = 'auto';
      const maxScroll = Math.max(0, scrollArea.scrollHeight - scrollArea.clientHeight);
      const targetY = targetPercent * maxScroll;
      scrollArea.scrollTop = targetY;
      scrollArea.scrollTo({ top: targetY, behavior: 'instant' });
    }
  }

  // Attach onload & onerror handlers to images so wrappers fit actual image height, cache aspect ratios, and show retry on error
  pagesContainer.querySelectorAll<HTMLImageElement>('img.reader-page-art').forEach((img) => {
    const pageNum = Number(img.dataset.pageNum || '1');
    const rawUrl = this.readerPages[pageNum - 1] || img.getAttribute('src') || '';
    const onImageReady = (): void => {
      const parent = img.parentElement;
      if (img.naturalWidth > 0 && img.naturalHeight > 0) {
        const ratio = img.naturalWidth / img.naturalHeight;
        if (rawUrl) {
          this.readerAspectRatioCache.set(rawUrl, ratio);
        }
        if (parent && this.readerMode === 'webtoon') {
          const exactHeight = Math.round(baseWidth / ratio);
          parent.style.containIntrinsicSize = `auto ${baseWidth}px ${exactHeight}px`;
        }
      }
      if (parent) {
        parent.style.minHeight = '';
      }
    };
    if (img.complete && img.naturalHeight > 0) {
      onImageReady();
    } else {
      img.onload = onImageReady;
    }
    img.onerror = () => {
      const parent = img.parentElement;
      if (parent) {
        parent.style.minHeight = '';
        parent.innerHTML = `
          <div class="reader-img-error" data-retry-page="${pageNum}" style="width:${baseWidth}px;min-height:240px;background:var(--reader-surface,#FFFFFF);border:1px dashed var(--reader-border,#D4D4D8);border-radius:8px;display:flex;flex-direction:column;align-items:center;justify-content:center;color:var(--reader-ink-muted,#71717A);gap:8px;cursor:pointer;margin:0 auto;">
            <span style="font-size:14px;color:#EF4444;">⚠️ ${this.l(`Gagal memuat Halaman ${pageNum}`, `Failed to load Page ${pageNum}`)}</span>
            <span style="font-size:12px;color:var(--reader-ink,#09090B);font-weight:600;">${this.l('Klik untuk coba lagi', 'Click to retry')}</span>
          </div>
        `;
      }
    };
  });

  this.prefetchReaderPagesAround(this.readerCurrentPage);
}

export function updateReaderPageIndicator(this: YomicApp): void {
  if (!this.readerHudRefs) {
    this.readerHudRefs = {
      curText: document.getElementById('readerPageCurrentText'),
      totText: document.getElementById('readerPageTotalText'),
      fillBar: document.getElementById('readerSliderFill'),
      scrub: document.getElementById('readerPageScrubber') as HTMLInputElement | null,
      prevBtn: document.getElementById('btnPrevChapter') as HTMLButtonElement | null,
      nextBtn: document.getElementById('btnNextChapter') as HTMLButtonElement | null
    };
  }
  const { curText, totText, fillBar, scrub, prevBtn, nextBtn } = this.readerHudRefs;
  const isDualMode = this.readerMode === 'dual';
  const dualSpread = isDualMode ? getDualSpreadPages(this.readerCurrentPage, this.readerTotalPages) : null;
  const effectiveEndPage = dualSpread ? (dualSpread.rightPage ?? dualSpread.leftPage) : this.readerCurrentPage;
  const effectiveStartPage = dualSpread ? dualSpread.leftPage : this.readerCurrentPage;

  if (curText) {
    curText.textContent =
      dualSpread && dualSpread.rightPage !== null
        ? `${dualSpread.leftPage}-${dualSpread.rightPage}`
        : String(effectiveStartPage);
  }
  if (totText) totText.textContent = String(this.readerTotalPages);
  if (fillBar) {
    const pct =
      this.readerTotalPages <= 1
        ? 100
        : Math.max(0, Math.min(100, ((effectiveEndPage - 1) / Math.max(1, this.readerTotalPages - 1)) * 100));
    fillBar.style.width = `${pct}%`;
  }
  if (scrub) {
    scrub.max = String(this.readerTotalPages);
    scrub.value = String(effectiveEndPage);
  }
  const isWebtoonMode = this.readerMode === 'webtoon';
  if (prevBtn) {
    const prevSpan = prevBtn.querySelector('span');
    if (prevSpan) {
      prevSpan.textContent = isWebtoonMode ? this.l('Ch Sblm', 'Prev Ch') : this.t('reader.prevBtn');
    }
    const prevTip = isWebtoonMode ? this.t('reader.prevChapterTitle') : this.t('reader.prevPageTitle');
    prevBtn.title = prevTip;
    prevBtn.setAttribute('aria-label', isWebtoonMode ? this.t('reader.prevChapterAria') : this.t('reader.prevPageAria'));
  }
  if (nextBtn) {
    const nextSpan = nextBtn.querySelector('span');
    if (nextSpan) {
      nextSpan.textContent = isWebtoonMode ? this.l('Ch Brkt', 'Next Ch') : this.t('reader.nextBtn');
    }
    const nextTip = isWebtoonMode ? this.t('reader.nextChapterTitle') : this.t('reader.nextPageTitle');
    nextBtn.title = nextTip;
    nextBtn.setAttribute('aria-label', isWebtoonMode ? this.t('reader.nextChapterAria') : this.t('reader.nextPageAria'));
  }

  const manga = this.mangaList.find((m) => m.id === this.readerMangaId);
  if (manga) {
    if (prevBtn) {
      if (isWebtoonMode) {
        prevBtn.disabled = !manga.chapters.some((c) => c.number < this.readerChapterNumber);
      } else {
        prevBtn.disabled = effectiveStartPage <= 1 && !manga.chapters.some((c) => c.number < this.readerChapterNumber);
      }
    }
    if (nextBtn) {
      if (isWebtoonMode) {
        nextBtn.disabled = !manga.chapters.some((c) => c.number > this.readerChapterNumber);
      } else {
        nextBtn.disabled = effectiveEndPage >= this.readerTotalPages && !manga.chapters.some((c) => c.number > this.readerChapterNumber);
      }
    }
  }
}

export function adjustReaderZoom(this: YomicApp, delta: number): void {
  this.readerZoomPercent = Math.max(60, Math.min(150, this.readerZoomPercent + delta));
  const zoomLabel = document.getElementById('readerZoomLabel');
  const zoomSlider = document.getElementById('readerZoomSlider') as HTMLInputElement | null;
  if (zoomLabel) zoomLabel.textContent = `${this.readerZoomPercent}%`;
  if (zoomSlider) zoomSlider.value = String(this.readerZoomPercent);
  document.querySelectorAll<HTMLButtonElement>('[data-set-zoom]').forEach((btn) => {
    const z = Number(btn.dataset.setZoom);
    btn.classList.toggle('active', z === this.readerZoomPercent);
  });

  const pagesContainer = document.getElementById('readerPagesContainer');
  const scrollArea = document.getElementById('readerCanvasScroll');
  if (!pagesContainer || !scrollArea) return;

  // Always ensure reader canvas is fully visible and scroll restoration flag is clean
  pagesContainer.style.opacity = '1';
  pagesContainer.style.transition = '';
  this.isRestoringReaderScroll = false;

  const existingImages = pagesContainer.querySelectorAll<HTMLImageElement>('img.reader-page-art');
  if (existingImages.length === 0) {
    // If not rendered yet, render from scratch
    this.renderReaderPages();
    return;
  }

  // Preserve relative scroll anchor
  const prevMaxScroll = Math.max(1, scrollArea.scrollHeight - scrollArea.clientHeight);
  const scrollRatio = Math.max(0, Math.min(1, scrollArea.scrollTop / prevMaxScroll));

  const fitMode = this.settings.readerFitMode || 'fit-width';

  if (this.readerMode === 'dual') {
    const targetStyle = getDualPageImgStyle(this.readerZoomPercent);
    existingImages.forEach((img) => {
      img.style.cssText = targetStyle;
    });
  } else {
    const baseWidth = Math.round(740 * (this.readerZoomPercent / 100));
    const minPageHeight = Math.round(baseWidth * 1.35);
    const targetStyle = buildReaderImgStyle(fitMode, this.readerMode, this.readerZoomPercent);
    existingImages.forEach((img) => {
      img.style.cssText = targetStyle;
      const parent = img.parentElement;
      if (parent && this.readerMode === 'webtoon') {
        const pageNum = Number(img.dataset.pageNum || '1');
        const rawUrl = this.readerPages[pageNum - 1] || img.getAttribute('src') || '';
        const cachedRatio = rawUrl ? this.readerAspectRatioCache.get(rawUrl) : undefined;
        const estHeight = cachedRatio && cachedRatio > 0.05 ? Math.round(baseWidth / cachedRatio) : minPageHeight;
        parent.style.containIntrinsicSize = `auto ${baseWidth}px ${estHeight}px`;
        parent.style.minHeight = (img.complete && img.naturalHeight > 0) ? '' : `${estHeight}px`;
      }
    });
    // Also update any retry placeholder boxes
    pagesContainer.querySelectorAll<HTMLElement>('.reader-img-error').forEach((box) => {
      box.style.width = `${baseWidth}px`;
    });
  }

  // Restore relative scroll position so user does not lose their place in webtoon mode
  if (scrollRatio > 0 && this.readerMode === 'webtoon') {
    requestAnimationFrame(() => {
      const newMaxScroll = Math.max(1, scrollArea.scrollHeight - scrollArea.clientHeight);
      scrollArea.scrollTop = scrollRatio * newMaxScroll;
    });
  }
}

export function stepReaderChapter(this: YomicApp, delta: number): void {
  const manga = this.mangaList.find((m) => m.id === this.readerMangaId);
  if (!manga || !manga.chapters || manga.chapters.length === 0) return;
  let candidateList = [...manga.chapters];
  if (this.settings.skipFilteredChapters) {
    if (this.detailChapterFilter === 'bookmarked') {
      candidateList = candidateList.filter((c) => c.isBookmarked || Math.abs(c.number - this.readerChapterNumber) < 0.01);
    } else if (this.detailChapterFilter === 'unread') {
      candidateList = candidateList.filter((c) => !c.isRead || Math.abs(c.number - this.readerChapterNumber) < 0.01);
    }
    if (this.settings.isOfflineMode) {
      candidateList = candidateList.filter((c) => c.isDownloaded || Math.abs(c.number - this.readerChapterNumber) < 0.01);
    }
  }
  const sorted = candidateList.sort((a, b) => a.number - b.number);
  const curIdx = sorted.findIndex((c) => Math.abs(c.number - this.readerChapterNumber) < 0.01);
  let targetCh: ChapterItem | undefined;
  if (curIdx >= 0) {
    targetCh = sorted[curIdx + delta];
  } else {
    targetCh = delta > 0
      ? sorted.find((c) => c.number > this.readerChapterNumber)
      : [...sorted].reverse().find((c) => c.number < this.readerChapterNumber);
  }
  if (targetCh) {
    const willMarkCurrentCompleted = delta > 0 && !this.isReaderLoadingPages && this.readerPages.length > 0;
    if (this.readerSaveProgressDebounceTimer) {
      clearTimeout(this.readerSaveProgressDebounceTimer);
      this.readerSaveProgressDebounceTimer = null;
      if (!willMarkCurrentCompleted) {
        this.saveReaderProgressToBackend();
      }
    }
    if (willMarkCurrentCompleted) {
      // Mark current chapter as read and save progress before switching to next chapter (ONLY when pages actually loaded)
      const curCh = manga.chapters.find((c) => Math.abs(c.number - this.readerChapterNumber) < 0.01);
      if (curCh && !curCh.isRead) {
        curCh.isRead = true;
        manga.unreadCount = Math.max(0, manga.unreadCount - 1);
      }
      if (this.bridge.isWebViewHost) {
        void this.bridge
          .invoke('reader:save-progress', {
            mangaId: manga.id,
            mangaUrl: manga.url || '',
            sourceId: Number(manga.sourceId) || 0,
            dbId: manga.dbId,
            chapterId: curCh?.dbId,
            chapterUrl: curCh?.url || '',
            chapterNumber: this.readerChapterNumber,
            currentPage: Math.max(1, this.readerTotalPages),
            totalPages: Math.max(1, this.readerTotalPages),
            completed: true,
            isRead: true
          })
          .catch(() => {});
      }
    }
    this.openReader(manga.id, targetCh.number);
    this.showToast(this.l(`Membuka Chapter ${targetCh.number}`, `Opening Chapter ${targetCh.number}`));
  } else {
    this.showToast(this.l('Sudah berada di batas chapter.', 'Reached chapter boundary.'));
  }
}

export function stepReaderPage(this: YomicApp, delta: number): void {
  if (this.readerMode === 'dual') {
    const { leftPage, rightPage } = getDualSpreadPages(this.readerCurrentPage, this.readerTotalPages);
    const stepSign = delta >= 0 ? 1 : -1;
    if (stepSign > 0) {
      const nextStart = (rightPage ?? leftPage) + 1;
      if (nextStart <= this.readerTotalPages) {
        this.readerCurrentPage = nextStart;
        this.updateReaderPageIndicator();
        this.renderReaderPages();
        this.saveReaderProgressToBackend();
      } else {
        this.stepReaderChapter(1);
      }
    } else {
      const prevStart = leftPage - 2;
      if (prevStart >= 1) {
        this.readerCurrentPage = prevStart;
        this.updateReaderPageIndicator();
        this.renderReaderPages();
        this.saveReaderProgressToBackend();
      } else {
        this.stepReaderChapter(-1);
      }
    }
    return;
  }

  const targetPage = this.readerCurrentPage + delta;
  if (targetPage >= 1 && targetPage <= this.readerTotalPages) {
    this.readerCurrentPage = targetPage;
    this.updateReaderPageIndicator();
    if (this.readerMode === 'single') {
      this.renderReaderPages();
    } else {
      const targetEl = document.getElementById(`reader-page-${targetPage}`);
      targetEl?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }
    this.saveReaderProgressToBackend();
  } else if (delta > 0) {
    this.stepReaderChapter(1);
  } else if (delta < 0) {
    this.stepReaderChapter(-1);
  }
}

export function isReaderFlyoutOpen(this: YomicApp): boolean {
  return !!(
    document.getElementById('readerChapterDropdownMenu')?.classList.contains('open') ||
    document.getElementById('readerChapterSheetMenu')?.classList.contains('open') ||
    document.getElementById('readerModeFlyoutMenu')?.classList.contains('open') ||
    document.getElementById('readerZoomFlyoutMenu')?.classList.contains('open') ||
    document.getElementById('readerImageFlyoutMenu')?.classList.contains('open') ||
    document.getElementById('readerMoreFlyoutMenu')?.classList.contains('open')
  );
}

export function showReaderHud(this: YomicApp): void {
  const overlay = document.getElementById('readerOverlay');
  overlay?.classList.remove('hud-hidden');
}

export function hideReaderHud(this: YomicApp): void {
  if (this.readerHudAutoHideTimer) {
    clearTimeout(this.readerHudAutoHideTimer);
    this.readerHudAutoHideTimer = null;
  }
  const overlay = document.getElementById('readerOverlay');
  overlay?.classList.add('hud-hidden');
  document.getElementById('readerModeFlyoutMenu')?.classList.remove('open');
  document.getElementById('btnReaderModeFlyout')?.classList.remove('active');
  document.getElementById('readerZoomFlyoutMenu')?.classList.remove('open');
  document.getElementById('btnReaderZoomFlyout')?.classList.remove('active');
  document.getElementById('readerChapterDropdownMenu')?.classList.remove('open');
  document.getElementById('btnReaderChapterDropdown')?.classList.remove('active');
  document.getElementById('readerChapterSheetMenu')?.classList.remove('open');
  document.getElementById('btnReaderChapterListDock')?.classList.remove('active');
  document.getElementById('readerImageFlyoutMenu')?.classList.remove('open');
  document.getElementById('btnReaderImageFlyout')?.classList.remove('active');
  document.getElementById('readerMoreFlyoutMenu')?.classList.remove('open');
  document.getElementById('btnReaderMoreFlyout')?.classList.remove('active');
}

export function toggleReaderHud(this: YomicApp): void {
  const overlay = document.getElementById('readerOverlay');
  const isHidden = overlay?.classList.toggle('hud-hidden') ?? false;
  if (isHidden) {
    if (this.readerHudAutoHideTimer) {
      clearTimeout(this.readerHudAutoHideTimer);
      this.readerHudAutoHideTimer = null;
    }
    document.getElementById('readerModeFlyoutMenu')?.classList.remove('open');
    document.getElementById('btnReaderModeFlyout')?.classList.remove('active');
    document.getElementById('readerZoomFlyoutMenu')?.classList.remove('open');
    document.getElementById('btnReaderZoomFlyout')?.classList.remove('active');
    document.getElementById('readerChapterDropdownMenu')?.classList.remove('open');
    document.getElementById('btnReaderChapterDropdown')?.classList.remove('active');
    document.getElementById('readerChapterSheetMenu')?.classList.remove('open');
    document.getElementById('btnReaderChapterListDock')?.classList.remove('active');
    document.getElementById('readerImageFlyoutMenu')?.classList.remove('open');
    document.getElementById('btnReaderImageFlyout')?.classList.remove('active');
    document.getElementById('readerMoreFlyoutMenu')?.classList.remove('open');
    document.getElementById('btnReaderMoreFlyout')?.classList.remove('active');
  } else {
    if (this.readerHudAutoHideTimer) {
      clearTimeout(this.readerHudAutoHideTimer);
    }
    this.readerHudAutoHideTimer = window.setTimeout(() => {
      if (this.isReaderOpen && !this.isReaderFlyoutOpen() && !this.isReaderInteractingWithControls) {
        this.hideReaderHud();
      }
      this.readerHudAutoHideTimer = null;
    }, 3500);
  }
}

export function renderReaderChapterDropdown(this: YomicApp): void {
  const sheetList = document.getElementById('readerChapterSheetList');
  if (!sheetList) return;
  const targets = [sheetList];
  const manga = this.mangaList.find((m) => m.id === this.readerMangaId);
  if (!manga) return;
  if (!manga.chapters || manga.chapters.length === 0) {
    const emptyHtml = `<div style="padding:14px 16px;text-align:center;color:var(--reader-ink-muted,#71717A);font-size:12.5px;">${this.l('Daftar chapter belum dimuat', 'Chapter list not loaded yet')}</div>`;
    targets.forEach((menu) => {
      menu.innerHTML = emptyHtml;
    });
    return;
  }
  const sorted = [...manga.chapters].sort((a, b) => b.number - a.number);
  const itemsHtml = sorted
    .map((ch) => {
      const isActive = Math.abs(ch.number - this.readerChapterNumber) < 0.01;
      const label = ch.title ? `Chapter ${ch.number} — ${escapeXml(ch.title)}` : `Chapter ${ch.number}`;
      return `
        <button class="reader-chapter-dropdown-item ${isActive ? 'active' : ''} ${ch.isRead && !isActive ? 'is-read' : ''}" data-jump-ch="${ch.number}">
          <span class="v1-reader-ch-item-text">${label}</span>
          <span class="v1-reader-ch-item-icons">
            ${ch.isBookmarked ? getIconSvg('bookmark-fill', { size: 12 }) : ''}
          </span>
        </button>
      `;
    })
    .join('');

  targets.forEach((menu) => {
    menu.innerHTML = itemsHtml;
    menu.querySelectorAll<HTMLButtonElement>('[data-jump-ch]').forEach((btn) => {
      btn.addEventListener('click', () => {
        const chNum = Number(btn.dataset.jumpCh);
        if (!Number.isNaN(chNum)) {
          document.getElementById('readerChapterDropdownMenu')?.classList.remove('open');
          document.getElementById('btnReaderChapterDropdown')?.classList.remove('active');
          document.getElementById('readerChapterSheetMenu')?.classList.remove('open');
          document.getElementById('btnReaderChapterListDock')?.classList.remove('active');
          this.openReader(manga.id, chNum);
        }
      });
    });
    requestAnimationFrame(() => {
      const activeBtn = menu.querySelector<HTMLElement>('.reader-chapter-dropdown-item.active');
      activeBtn?.scrollIntoView({ block: 'nearest' });
    });
  });
}

export function toggleReaderAutoScroll(this: YomicApp): void {
  const btn = document.getElementById('btnAutoScroll');
  if (this.readerAutoScrollTimer !== null) {
    this.stopReaderAutoScroll();
    btn?.classList.remove('active');
    this.showToast(this.l('Gulir otomatis dimatikan.', 'Auto-scroll stopped.'));
    return;
  }
  if (this.readerMode !== 'webtoon') {
    this.showToast(this.l('Gulir otomatis hanya tersedia di mode Webtoon.', 'Auto-scroll is only available in Webtoon mode.'), 'warning');
    return;
  }
  btn?.classList.add('active');
  const scrollArea = document.getElementById('readerCanvasScroll');
  this.readerAutoScrollTimer = window.setInterval(() => {
    if (!this.isReaderOpen || this.readerMode !== 'webtoon') {
      this.stopReaderAutoScroll();
      return;
    }
    if (scrollArea) {
      scrollArea.scrollBy({ top: 3, behavior: 'auto' });
    }
  }, 16);
  this.showToast(this.l('Gulir otomatis aktif (tekan S untuk berhenti).', 'Auto-scroll active (press S to stop).'));
}

export function stopReaderAutoScroll(this: YomicApp): void {
  if (this.readerAutoScrollTimer !== null) {
    window.clearInterval(this.readerAutoScrollTimer);
    this.readerAutoScrollTimer = null;
  }
  document.getElementById('btnAutoScroll')?.classList.remove('active');
}

export function bindReaderShellEvents(this: YomicApp): void {
  // Reader Topbar & Bottom Bar Controls (Yomic v1 ReaderView.axaml)
  document.getElementById('btnCloseReader')?.addEventListener('click', () => this.closeReader());
  document.getElementById('btnPrevChapter')?.addEventListener('click', () => {
    if (this.readerMode === 'webtoon') {
      this.stepReaderChapter(-1);
    } else {
      this.stepReaderPage(-1);
    }
  });
  document.getElementById('btnNextChapter')?.addEventListener('click', () => {
    if (this.readerMode === 'webtoon') {
      this.stepReaderChapter(1);
    } else {
      this.stepReaderPage(1);
    }
  });
  document.getElementById('btnReaderRefresh')?.addEventListener('click', () => {
    document.getElementById('readerMoreFlyoutMenu')?.classList.remove('open');
    document.getElementById('btnReaderMoreFlyout')?.classList.remove('active');
    if (!this.readerMangaId || this.readerChapterNumber < 0) return;
    this.showToast(this.l('Memuat ulang halaman komik dari sumber...', 'Reloading comic pages from source...'), 'refresh');
    this.openReader(this.readerMangaId, this.readerChapterNumber, true);
  });
  document.getElementById('btnReaderBookmarkChapter')?.addEventListener('click', (ev) => {
    ev.stopPropagation();
    const manga = this.mangaList.find((m) => m.id === this.readerMangaId);
    const chObj = manga?.chapters.find((c) => Math.abs(c.number - this.readerChapterNumber) < 0.01);
    if (!manga || !chObj) return;
    chObj.isBookmarked = !chObj.isBookmarked;
    const bookmarkBtn = document.getElementById('btnReaderBookmarkChapter');
    if (bookmarkBtn) {
      bookmarkBtn.classList.toggle('is-bookmarked', chObj.isBookmarked);
      const bookmarkIconSlot = document.getElementById('readerBookmarkIconSlot');
      const bookmarkLabel = document.getElementById('readerBookmarkLabel');
      const labelText = chObj.isBookmarked
        ? this.l('Hapus Tanda Chapter', 'Remove Chapter Bookmark')
        : this.l('Tandai Chapter', 'Bookmark Chapter');
      if (bookmarkIconSlot) {
        bookmarkIconSlot.innerHTML = getIconSvg(chObj.isBookmarked ? 'bookmark-fill' : 'bookmark-line', { size: 17 });
      } else {
        bookmarkBtn.innerHTML = getIconSvg(chObj.isBookmarked ? 'bookmark-fill' : 'bookmark-line', { size: 17 });
      }
      if (bookmarkLabel) {
        bookmarkLabel.textContent = labelText;
      }
      bookmarkBtn.title = labelText;
      bookmarkBtn.setAttribute('aria-label', labelText);
    }
    if (this.bridge.isWebViewHost) {
      void this.bridge
        .invoke('manga:toggle-bookmark', {
          mangaId: manga.id,
          url: manga.url || '',
          sourceId: Number(manga.sourceId) || 0,
          chapterNumber: chObj.number,
          chapterUrl: chObj.url || '',
          isBookmarked: chObj.isBookmarked
        })
        .catch(() => {});
    }
    this.showToast(
      chObj.isBookmarked
        ? this.l(`Chapter ${chObj.number} ditandai.`, `Chapter ${chObj.number} bookmarked.`)
        : this.l(`Tanda Chapter ${chObj.number} dihapus.`, `Chapter ${chObj.number} bookmark removed.`)
    );
  });
  document.getElementById('btnReaderChapterListDock')?.addEventListener('click', (ev) => {
    ev.stopPropagation();
    const chSheet = document.getElementById('readerChapterSheetMenu');
    const chDockBtn = document.getElementById('btnReaderChapterListDock');
    document.getElementById('readerChapterDropdownMenu')?.classList.remove('open');
    document.getElementById('btnReaderChapterDropdown')?.classList.remove('active');
    document.getElementById('readerModeFlyoutMenu')?.classList.remove('open');
    document.getElementById('btnReaderModeFlyout')?.classList.remove('active');
    document.getElementById('readerImageFlyoutMenu')?.classList.remove('open');
    document.getElementById('btnReaderImageFlyout')?.classList.remove('active');
    document.getElementById('readerMoreFlyoutMenu')?.classList.remove('open');
    document.getElementById('btnReaderMoreFlyout')?.classList.remove('active');
    const isOpen = chSheet?.classList.toggle('open') ?? false;
    chDockBtn?.classList.toggle('active', isOpen);
    if (isOpen) {
      this.renderReaderChapterDropdown();
    }
  });
  document.getElementById('readerChapterSheetMenu')?.addEventListener('click', (ev) => ev.stopPropagation());
  document.getElementById('btnReaderFullscreen')?.addEventListener('click', () => {
    document.getElementById('readerMoreFlyoutMenu')?.classList.remove('open');
    document.getElementById('btnReaderMoreFlyout')?.classList.remove('active');
    this.bridge.toggleFullscreen();
  });
  const modeFlyoutBtn = document.getElementById('btnReaderModeFlyout');
  const modeFlyoutMenu = document.getElementById('readerModeFlyoutMenu');
  const zoomFlyoutBtn = document.getElementById('btnReaderZoomFlyout');
  const zoomFlyoutMenu = document.getElementById('readerZoomFlyoutMenu');
  const chDropdownBtn = document.getElementById('btnReaderChapterDropdown');
  const chDropdownMenu = document.getElementById('readerChapterDropdownMenu');
  const imgFlyoutBtn = document.getElementById('btnReaderImageFlyout');
  const imgFlyoutMenu = document.getElementById('readerImageFlyoutMenu');
  const moreFlyoutBtn = document.getElementById('btnReaderMoreFlyout');
  const moreFlyoutMenu = document.getElementById('readerMoreFlyoutMenu');

  const syncImageFlyoutToggles = () => {
    const cropVal = Boolean(this.settings.enableAutoCropBorders);
    const sharpenVal = Boolean(this.settings.enableImageSharpening);
    const optVal = Boolean(this.settings.enableImageOptimization);

    const cropEl = document.getElementById('toggleReaderCrop');
    cropEl?.classList.toggle('on', cropVal);
    cropEl?.setAttribute('aria-checked', String(cropVal));

    const sharpenEl = document.getElementById('toggleReaderSharpen');
    sharpenEl?.classList.toggle('on', sharpenVal);
    sharpenEl?.setAttribute('aria-checked', String(sharpenVal));

    const optEl = document.getElementById('toggleReaderOpt');
    optEl?.classList.toggle('on', optVal);
    optEl?.setAttribute('aria-checked', String(optVal));
  };
  syncImageFlyoutToggles();

  chDropdownBtn?.addEventListener('click', (ev) => {
    ev.stopPropagation();
    chDropdownMenu?.classList.remove('open');
    chDropdownBtn.classList.remove('active');
    modeFlyoutMenu?.classList.remove('open');
    modeFlyoutBtn?.classList.remove('active');
    zoomFlyoutMenu?.classList.remove('open');
    zoomFlyoutBtn?.classList.remove('active');
    imgFlyoutMenu?.classList.remove('open');
    imgFlyoutBtn?.classList.remove('active');
    moreFlyoutMenu?.classList.remove('open');
    moreFlyoutBtn?.classList.remove('active');
    const chSheet = document.getElementById('readerChapterSheetMenu');
    const chDockBtn = document.getElementById('btnReaderChapterListDock');
    const isOpen = chSheet?.classList.toggle('open') ?? false;
    chDockBtn?.classList.toggle('active', isOpen);
    if (isOpen) {
      this.renderReaderChapterDropdown();
    }
  });
  chDropdownMenu?.addEventListener('click', (ev) => ev.stopPropagation());
  modeFlyoutBtn?.addEventListener('click', (ev) => {
    ev.stopPropagation();
    chDropdownMenu?.classList.remove('open');
    chDropdownBtn?.classList.remove('active');
    document.getElementById('readerChapterSheetMenu')?.classList.remove('open');
    document.getElementById('btnReaderChapterListDock')?.classList.remove('active');
    zoomFlyoutMenu?.classList.remove('open');
    zoomFlyoutBtn?.classList.remove('active');
    imgFlyoutMenu?.classList.remove('open');
    imgFlyoutBtn?.classList.remove('active');
    moreFlyoutMenu?.classList.remove('open');
    moreFlyoutBtn?.classList.remove('active');
    const isOpen = modeFlyoutMenu?.classList.toggle('open') ?? false;
    modeFlyoutBtn.classList.toggle('active', isOpen);
  });
  modeFlyoutMenu?.addEventListener('click', (ev) => ev.stopPropagation());
  zoomFlyoutBtn?.addEventListener('click', (ev) => {
    ev.stopPropagation();
    chDropdownMenu?.classList.remove('open');
    chDropdownBtn?.classList.remove('active');
    modeFlyoutMenu?.classList.remove('open');
    modeFlyoutBtn?.classList.remove('active');
    imgFlyoutMenu?.classList.remove('open');
    imgFlyoutBtn?.classList.remove('active');
    moreFlyoutMenu?.classList.remove('open');
    moreFlyoutBtn?.classList.remove('active');
    const isOpen = zoomFlyoutMenu?.classList.toggle('open') ?? false;
    zoomFlyoutBtn.classList.toggle('active', isOpen);
  });
  zoomFlyoutMenu?.addEventListener('click', (ev) => ev.stopPropagation());

  imgFlyoutBtn?.addEventListener('click', (ev) => {
    ev.stopPropagation();
    chDropdownMenu?.classList.remove('open');
    chDropdownBtn?.classList.remove('active');
    document.getElementById('readerChapterSheetMenu')?.classList.remove('open');
    document.getElementById('btnReaderChapterListDock')?.classList.remove('active');
    modeFlyoutMenu?.classList.remove('open');
    modeFlyoutBtn?.classList.remove('active');
    zoomFlyoutMenu?.classList.remove('open');
    zoomFlyoutBtn?.classList.remove('active');
    moreFlyoutMenu?.classList.remove('open');
    moreFlyoutBtn?.classList.remove('active');
    const isOpen = imgFlyoutMenu?.classList.toggle('open') ?? false;
    imgFlyoutBtn.classList.toggle('active', isOpen);
    if (isOpen) {
      syncImageFlyoutToggles();
    }
  });
  imgFlyoutMenu?.addEventListener('click', (ev) => ev.stopPropagation());

  moreFlyoutBtn?.addEventListener('click', (ev) => {
    ev.stopPropagation();
    chDropdownMenu?.classList.remove('open');
    chDropdownBtn?.classList.remove('active');
    document.getElementById('readerChapterSheetMenu')?.classList.remove('open');
    document.getElementById('btnReaderChapterListDock')?.classList.remove('active');
    modeFlyoutMenu?.classList.remove('open');
    modeFlyoutBtn?.classList.remove('active');
    zoomFlyoutMenu?.classList.remove('open');
    zoomFlyoutBtn?.classList.remove('active');
    imgFlyoutMenu?.classList.remove('open');
    imgFlyoutBtn?.classList.remove('active');
    const isOpen = moreFlyoutMenu?.classList.toggle('open') ?? false;
    moreFlyoutBtn.classList.toggle('active', isOpen);
  });
  moreFlyoutMenu?.addEventListener('click', (ev) => ev.stopPropagation());

  const appendCacheBustParam = (rawUrl: string, timestamp: number): string => {
    if (!rawUrl) return rawUrl;
    const lower = rawUrl.trim().toLowerCase();
    // Never mutate data: or blob: URIs (adding ?t=... corrupts inline SVG/data/blob URLs)
    if (lower.startsWith('data:') || lower.startsWith('blob:')) {
      return rawUrl;
    }
    try {
      const url = new URL(rawUrl);
      if (url.protocol !== 'http:' && url.protocol !== 'https:') return rawUrl;
      url.searchParams.set('t', String(timestamp));
      return url.toString();
    } catch {
      const clean = rawUrl.replace(/([?&])t=\d+(&|$)/, '$1').replace(/[?&]$/, '');
      const sep = clean.includes('?') ? '&' : '?';
      return `${clean}${sep}t=${timestamp}`;
    }
  };

  const refreshReaderCanvasImages = () => {
    const timestamp = Date.now();
    if (Array.isArray(this.readerPages) && this.readerPages.length > 0) {
      this.readerPages = this.readerPages.map((pageUrl) => appendCacheBustParam(pageUrl, timestamp));
    }

    const imgs = document.querySelectorAll<HTMLImageElement>('img.reader-page-art');
    imgs.forEach((img) => {
      const rawSrc = img.src;
      if (!rawSrc) return;
      const nextSrc = appendCacheBustParam(rawSrc, timestamp);
      if (nextSrc !== rawSrc) {
        img.src = nextSrc;
      }
    });
  };

  document.querySelectorAll<HTMLElement>('[data-toggle-reader-opt]').forEach((row) => {
    const sw = row.querySelector<HTMLElement>('.toggle-switch');
    sw?.addEventListener('keydown', (ev) => {
      if (ev.key === 'Enter' || ev.key === ' ') {
        ev.preventDefault();
        row.click();
      }
    });
    row.addEventListener('click', (ev) => {
      ev.stopPropagation();
      const optKey = row.dataset.toggleReaderOpt as 'enableAutoCropBorders' | 'enableImageSharpening' | 'enableImageOptimization' | undefined;
      if (!optKey) return;

      const currentVal = Boolean(this.settings[optKey]);
      const newVal = !currentVal;
      this.settings[optKey] = newVal;
      sw?.setAttribute('aria-checked', String(newVal));
      syncImageFlyoutToggles();
      this.persistLocalCache();

      if (this.bridge.isWebViewHost) {
        void this.bridge
          .invoke<AppSettingsState>('settings:update', { key: optKey, value: newVal })
          .then((updated) => {
            if (updated) {
              Object.assign(this.settings, updated);
              this.persistLocalCache();
              syncImageFlyoutToggles();
            }
            // Refresh images AFTER backend persists the updated SkiaSharp processing setting
            refreshReaderCanvasImages();
          })
          .catch(() => {
            this.settings[optKey] = currentVal;
            this.persistLocalCache();
            syncImageFlyoutToggles();
            this.showToast(this.l('Gagal memperbarui pengaturan tampilan gambar.', 'Failed to update image processing setting.'), 'warning');
          });
      } else {
        refreshReaderCanvasImages();
      }

      const labelMap: Record<string, string> = {
        enableAutoCropBorders: this.t('reader.autoCropLabel'),
        enableImageSharpening: this.t('reader.sharpenLabel'),
        enableImageOptimization: this.t('reader.optimizeLabel')
      };
      this.showToast(`${labelMap[optKey] || optKey}: ${newVal ? this.l('Aktif', 'On') : this.l('Nonaktif', 'Off')}`, 'info');
    });
  });
  const modeButtons = document.querySelectorAll<HTMLButtonElement>('[data-reader-mode]');
  modeButtons.forEach((btn) => {
    btn.addEventListener('click', () => {
      const mode = btn.dataset.readerMode as ReaderMode | undefined;
      if (mode) {
        this.setReaderMode(mode);
        modeFlyoutMenu?.classList.remove('open');
        modeFlyoutBtn?.classList.remove('active');
      }
    });
  });
  const scrubber = document.getElementById('readerPageScrubber') as HTMLInputElement | null;
  scrubber?.addEventListener('input', () => {
    this.readerCurrentPage = Number(scrubber.value);
    this.updateReaderPageIndicator();
    this.prefetchReaderPagesAround(this.readerCurrentPage);
    if (this.readerMode === 'single' || this.readerMode === 'dual') {
      this.renderReaderPages();
    } else {
      const pageEl = document.getElementById(`reader-page-${this.readerCurrentPage}`);
      pageEl?.scrollIntoView({ behavior: 'auto', block: 'start' });
    }
  });
  scrubber?.addEventListener('change', () => {
    this.saveReaderProgressToBackend();
  });
  document.getElementById('btnZoomOut')?.addEventListener('click', () => this.adjustReaderZoom(-10));
  document.getElementById('btnZoomIn')?.addEventListener('click', () => this.adjustReaderZoom(10));
  const zoomSlider = document.getElementById('readerZoomSlider') as HTMLInputElement | null;
  zoomSlider?.addEventListener('input', () => {
    const targetZoom = Number(zoomSlider.value);
    this.adjustReaderZoom(targetZoom - this.readerZoomPercent);
  });
  document.querySelectorAll<HTMLButtonElement>('[data-set-zoom]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const targetZoom = Number(btn.dataset.setZoom);
      if (!isNaN(targetZoom)) {
        this.adjustReaderZoom(targetZoom - this.readerZoomPercent);
      }
    });
  });
  document.getElementById('btnAutoScroll')?.addEventListener('click', () => this.toggleReaderAutoScroll());
  document.getElementById('btnReaderPrevPageEdge')?.addEventListener('click', () => {
    const isRtl = this.settings.readingDirection === 'rtl';
    this.stepReaderPage(isRtl ? 1 : -1);
  });
  document.getElementById('btnReaderNextPageEdge')?.addEventListener('click', () => {
    const isRtl = this.settings.readingDirection === 'rtl';
    this.stepReaderPage(isRtl ? -1 : 1);
  });

  // 3-Zone Click/Tap System & Auto-Hide on Scroll (Standard Mihon / Tachiyomi)
  const readerCanvas = document.getElementById('readerCanvasScroll');
  let isCanvasDragging = false;
  let canvasDownX = 0;
  let canvasDownY = 0;
  readerCanvas?.addEventListener('mousedown', (ev) => {
    canvasDownX = ev.clientX;
    canvasDownY = ev.clientY;
    isCanvasDragging = false;
  });
  readerCanvas?.addEventListener('mousemove', (ev) => {
    if (Math.abs(ev.clientX - canvasDownX) > 8 || Math.abs(ev.clientY - canvasDownY) > 8) {
      isCanvasDragging = true;
    }
  });
  readerCanvas?.addEventListener('click', (ev) => {
    chDropdownMenu?.classList.remove('open');
    chDropdownBtn?.classList.remove('active');
    document.getElementById('readerChapterSheetMenu')?.classList.remove('open');
    document.getElementById('btnReaderChapterListDock')?.classList.remove('active');
    modeFlyoutMenu?.classList.remove('open');
    modeFlyoutBtn?.classList.remove('active');
    zoomFlyoutMenu?.classList.remove('open');
    zoomFlyoutBtn?.classList.remove('active');
    imgFlyoutMenu?.classList.remove('open');
    imgFlyoutBtn?.classList.remove('active');
    moreFlyoutMenu?.classList.remove('open');
    moreFlyoutBtn?.classList.remove('active');
    if (isCanvasDragging) {
      return;
    }
    const rect = readerCanvas.getBoundingClientRect();
    const clickRatio = (ev.clientX - rect.left) / rect.width;
    if (clickRatio < 0.25) {
      if (this.readerMode === 'webtoon') {
        readerCanvas.scrollBy({ top: -400, behavior: 'smooth' });
      } else {
        this.stepReaderPage(this.settings.readingDirection === 'rtl' ? 1 : -1);
      }
    } else if (clickRatio > 0.75) {
      if (this.readerMode === 'webtoon') {
        readerCanvas.scrollBy({ top: 400, behavior: 'smooth' });
      } else {
        this.stepReaderPage(this.settings.readingDirection === 'rtl' ? -1 : 1);
      }
    } else {
      this.toggleReaderHud();
    }
  });

  // Auto-hide HUD on scroll down & O(log N) Webtoon ScrollSpy (Passive 60-120 FPS)
  let lastReaderScrollTop = 0;
  let readerScrollThrottled = false;
  readerCanvas?.addEventListener('scroll', () => {
    if (readerScrollThrottled) return;
    readerScrollThrottled = true;
    window.requestAnimationFrame(() => {
      readerScrollThrottled = false;
      if (!this.isReaderOpen || !readerCanvas) return;
      const currentScroll = readerCanvas.scrollTop;
      const maxScroll = readerCanvas.scrollHeight - readerCanvas.clientHeight;
      const delta = currentScroll - lastReaderScrollTop;
      if (currentScroll <= 30 || currentScroll >= maxScroll - 30) {
        this.showReaderHud();
      } else if (
        delta > 35 &&
        this.readerMode === 'webtoon' &&
        !this.isReaderFlyoutOpen() &&
        !this.isReaderInteractingWithControls
      ) {
        this.hideReaderHud();
      } else if (delta < -45 && this.readerMode === 'webtoon') {
        this.showReaderHud();
      }

      // If currently restoring percent anchor, do not fight or sync
      if (this.isRestoringReaderScroll) {
        lastReaderScrollTop = currentScroll;
        return;
      }

      // Track scroll percent (Yomic v1 CurrentScrollPercent)
      if (this.readerMode === 'webtoon' && maxScroll > 0) {
        this.readerScrollPercent = Math.min(1.0, Math.max(0.0, currentScroll / maxScroll));
      }

      // Webtoon ScrollSpy: O(log N) Binary Search over cached page wrappers (0 forced layout)
      if (this.readerMode === 'webtoon' && this.readerTotalPages > 0 && !this.isReaderInteractingWithControls) {
        const midContentY = currentScroll + readerCanvas.clientHeight * 0.5;
        const activePage = findActiveWebtoonPageBinarySearch(this.readerPageWrappers, midContentY);
        const pageChanged = activePage !== this.readerCurrentPage;
        if (pageChanged) {
          this.readerCurrentPage = activePage;
          this.updateReaderPageIndicator();
          this.prefetchReaderPagesAround(activePage);
        }

        // Immediate completion flush on reaching the last page (no 400ms delay)
        const chapterKey = `${this.readerMangaId}_${this.readerChapterNumber}`;
        if (
          this.readerCurrentPage >= this.readerTotalPages &&
          this.readerTotalPages > 0 &&
          this.readerLastCompletedFlushKey !== chapterKey
        ) {
          this.readerLastCompletedFlushKey = chapterKey;
          if (this.readerSaveProgressDebounceTimer) {
            clearTimeout(this.readerSaveProgressDebounceTimer);
            this.readerSaveProgressDebounceTimer = null;
          }
          this.saveReaderProgressToBackend();
        } else {
          // Debounced auto-save (400ms) while reading in Webtoon mode
          if (this.readerSaveProgressDebounceTimer) {
            clearTimeout(this.readerSaveProgressDebounceTimer);
          }
          this.readerSaveProgressDebounceTimer = window.setTimeout(() => {
            this.readerSaveProgressDebounceTimer = null;
            if (this.isReaderOpen && !this.isRestoringReaderScroll) {
              this.saveReaderProgressToBackend();
            }
          }, 400);
        }
      }
      lastReaderScrollTop = currentScroll;
    });
  }, { passive: true });

  // =========================================================================
  // AUTO-REVEAL HUD ON CURSOR HOVER NEAR HEADER / FOOTER (MANGA READER UI/UX)
  // =========================================================================
  const readerOverlayEl = document.getElementById('readerOverlay');
  const readerTopbarEl = document.querySelector('.reader-topbar') as HTMLElement | null;
  const readerBottombarEl = document.querySelector('.v1-reader-bottom-bar') as HTMLElement | null;
  const readerScrubberEl = document.getElementById('readerPageScrubber');
  const TOP_HUD_TRIGGER_ZONE = 65;
  const BOTTOM_HUD_TRIGGER_ZONE = 90;
  readerOverlayEl?.addEventListener('mousemove', (ev: MouseEvent) => {
    if (!this.isReaderOpen) return;
    const isNearTop = ev.clientY <= TOP_HUD_TRIGGER_ZONE;
    const isNearBottom = ev.clientY >= window.innerHeight - BOTTOM_HUD_TRIGGER_ZONE;
    if (isNearTop || isNearBottom) {
      if (this.readerHudAutoHideTimer) {
        clearTimeout(this.readerHudAutoHideTimer);
        this.readerHudAutoHideTimer = null;
      }
      if (readerOverlayEl.classList.contains('hud-hidden')) {
        this.showReaderHud();
      }
    } else {
      if (
        !readerOverlayEl.classList.contains('hud-hidden') &&
        !this.isReaderFlyoutOpen() &&
        !this.isReaderInteractingWithControls
      ) {
        if (this.readerHudAutoHideTimer) {
          clearTimeout(this.readerHudAutoHideTimer);
        }
        this.readerHudAutoHideTimer = window.setTimeout(() => {
          if (this.isReaderOpen && !this.isReaderFlyoutOpen() && !this.isReaderInteractingWithControls) {
            this.hideReaderHud();
          }
          this.readerHudAutoHideTimer = null;
        }, 2000);
      }
    }
  });
  readerTopbarEl?.addEventListener('mouseenter', () => {
    if (this.readerHudAutoHideTimer) {
      clearTimeout(this.readerHudAutoHideTimer);
      this.readerHudAutoHideTimer = null;
    }
    this.showReaderHud();
  });
  readerBottombarEl?.addEventListener('mouseenter', () => {
    if (this.readerHudAutoHideTimer) {
      clearTimeout(this.readerHudAutoHideTimer);
      this.readerHudAutoHideTimer = null;
    }
    this.showReaderHud();
  });
  readerScrubberEl?.addEventListener('mousedown', () => {
    this.isReaderInteractingWithControls = true;
    if (this.readerHudAutoHideTimer) {
      clearTimeout(this.readerHudAutoHideTimer);
      this.readerHudAutoHideTimer = null;
    }
  });
  window.addEventListener('mouseup', () => {
    this.isReaderInteractingWithControls = false;
  });
  document.addEventListener('mouseleave', () => {
    if (this.isReaderOpen && !this.isReaderFlyoutOpen() && !this.isReaderInteractingWithControls) {
      if (!readerOverlayEl?.classList.contains('hud-hidden')) {
        if (this.readerHudAutoHideTimer) {
          clearTimeout(this.readerHudAutoHideTimer);
        }
        this.readerHudAutoHideTimer = window.setTimeout(() => {
          if (this.isReaderOpen && !this.isReaderFlyoutOpen() && !this.isReaderInteractingWithControls) {
            this.hideReaderHud();
          }
          this.readerHudAutoHideTimer = null;
        }, 1500);
      }
    }
  });
}

export function syncReaderPageFromScroll(this: YomicApp): void {
  const readerCanvas = document.getElementById('readerCanvasScroll');
  if (!readerCanvas || this.readerMode !== 'webtoon' || this.readerTotalPages <= 0) return;
  const maxScroll = Math.max(1, readerCanvas.scrollHeight - readerCanvas.clientHeight);
  this.readerScrollPercent = Math.min(1.0, Math.max(0.0, readerCanvas.scrollTop / maxScroll));
  const midContentY = readerCanvas.scrollTop + readerCanvas.clientHeight * 0.5;
  const activePage = findActiveWebtoonPageBinarySearch(this.readerPageWrappers, midContentY);
  if (activePage !== this.readerCurrentPage) {
    this.readerCurrentPage = activePage;
    this.updateReaderPageIndicator();
  }
  this.prefetchReaderPagesAround(this.readerCurrentPage);
}

export function restoreReaderScrollPosition(this: YomicApp): void {
  const scrollArea = document.getElementById('readerCanvasScroll');
  const pagesContainer = document.getElementById('readerPagesContainer') as (HTMLElement & { _cancelScrollRestore?: () => void }) | null;
  if (!scrollArea || !pagesContainer) return;

  // Always cancel any prior ResizeObserver/timers BEFORE early returns (e.g. switching Webtoon -> Single/Dual)
  if (pagesContainer._cancelScrollRestore) {
    pagesContainer._cancelScrollRestore();
    pagesContainer._cancelScrollRestore = undefined;
  }

  scrollArea.style.scrollBehavior = 'auto';

  if (this.readerMode === 'single' || this.readerMode === 'dual') {
    this.isRestoringReaderScroll = false;
    this.targetRestorePercent = -1;
    scrollArea.scrollTop = 0;
    pagesContainer.style.opacity = '1';
    this.updateReaderPageIndicator();
    return;
  }

  // Webtoon Mode: Instant Percent-Based Restore Anchor
  const targetPercent = this.targetRestorePercent > 0
    ? this.targetRestorePercent
    : (this.readerScrollPercent > 0
        ? this.readerScrollPercent
        : (this.readerCurrentPage > 1 && this.readerTotalPages > 1
            ? (this.readerCurrentPage - 1) / (this.readerTotalPages - 1)
            : 0));

  if (targetPercent <= 0) {
    this.isRestoringReaderScroll = false;
    this.targetRestorePercent = -1;
    if (this.readerCurrentPage > 1) {
      const targetEl = document.getElementById(`reader-page-wrap-${this.readerCurrentPage}`);
      targetEl?.scrollIntoView({ behavior: 'instant', block: 'start' });
    }
    pagesContainer.style.opacity = '1';
    this.updateReaderPageIndicator();
    return;
  }

  this.isRestoringReaderScroll = true;

  const applyPercentRestore = () => {
    scrollArea.style.scrollBehavior = 'auto';
    const maxScroll = Math.max(0, scrollArea.scrollHeight - scrollArea.clientHeight);
    const targetY = targetPercent * maxScroll;
    scrollArea.scrollTop = targetY;
    scrollArea.scrollTo({ top: targetY, behavior: 'instant' });
  };

  applyPercentRestore();

  let stableCount = 0;
  let lastHeight = scrollArea.scrollHeight;
  let restoreFinished = false;

  let resizeObserver: ResizeObserver | null = null;
  let fallbackTimer: number | null = null;
  let postFadeTimer: number | null = null;
  const restoreChapterNum = this.readerChapterNumber;
  const restoreMangaId = this.readerMangaId;

  const cancelRestore = () => {
    restoreFinished = true;
    if (resizeObserver) {
      resizeObserver.disconnect();
      resizeObserver = null;
    }
    if (fallbackTimer !== null) {
      clearTimeout(fallbackTimer);
      fallbackTimer = null;
    }
    if (postFadeTimer !== null) {
      clearTimeout(postFadeTimer);
      postFadeTimer = null;
    }
  };
  (pagesContainer as HTMLElement & { _cancelScrollRestore?: () => void })._cancelScrollRestore = cancelRestore;

  const finishRestore = () => {
    if (restoreFinished) return;
    if (!this.isReaderOpen || this.readerChapterNumber !== restoreChapterNum || this.readerMangaId !== restoreMangaId) {
      cancelRestore();
      return;
    }
    restoreFinished = true;
    if (resizeObserver) {
      resizeObserver.disconnect();
      resizeObserver = null;
    }
    if (fallbackTimer !== null) {
      clearTimeout(fallbackTimer);
      fallbackTimer = null;
    }
    applyPercentRestore();
    requestAnimationFrame(() => {
      if (!this.isReaderOpen || this.readerChapterNumber !== restoreChapterNum || this.readerMangaId !== restoreMangaId) return;
      applyPercentRestore();
      pagesContainer.style.transition = 'opacity 0.08s ease-out';
      pagesContainer.style.opacity = '1';
      postFadeTimer = window.setTimeout(() => {
        if (!this.isReaderOpen || this.readerChapterNumber !== restoreChapterNum || this.readerMangaId !== restoreMangaId) return;
        this.isRestoringReaderScroll = false;
        this.targetRestorePercent = -1;
        this.syncReaderPageFromScroll();
      }, 50);
    });
  };

  try {
    resizeObserver = new ResizeObserver(() => {
      if (restoreFinished || !this.isReaderOpen) {
        cancelRestore();
        return;
      }
      const curHeight = scrollArea.scrollHeight;
      if (curHeight !== lastHeight) {
        lastHeight = curHeight;
        applyPercentRestore();
        stableCount = 0;
      } else {
        stableCount++;
        if (stableCount >= 2) {
          finishRestore();
        }
      }
    });
    resizeObserver.observe(pagesContainer);
  } catch {
    finishRestore();
    return;
  }

  fallbackTimer = window.setTimeout(() => {
    finishRestore();
  }, 350);
}
