/* =========================================================================
 * YOMIC V2 — BACKEND IPC SYNC, BOOTSTRAP & LOCAL CACHE SERVICE
 * ========================================================================= */

import type { YomicApp } from '../app.js';
import { applyStaticDomTranslations } from '../i18n.js';
import { AppBootstrapPayload, CategoryItem, DownloadQueueItem, ExtensionItem, MangaItem } from '../types.js';
import { updateDownloadsProgressInPlace } from '../views/downloads.js';
import { updateDetailChapterDownloadsInPlace } from '../views/manga-detail.js';
import { updateAppUpdateOverlayProgress } from '../views/settings.js';
import { updateSourceStatusInPlace } from '../views/browse.js';

export function bindBackendEvents(this: YomicApp): void {
  this.bridge.on<{ sourceId: number; status: 'online' | 'warning' | 'offline' | 'checking'; tooltip: string }>(
    'event:source-status',
    (ev) => {
      if (!ev) return;
      let updated = false;
      for (const ext of this.extensions) {
        if (ext.sourceId === ev.sourceId) {
          ext.status = ev.status;
          updated = true;
        }
      }
      if (updated && this.currentTab === 'browse' && !this.activeBrowseSourceId && !this.activeDetailMangaId) {
        const container = document.getElementById('viewContainer');
        if (container) {
          updateSourceStatusInPlace(container, ev.sourceId, ev.status, ev.tooltip);
        }
      }
    }
  );
  this.bridge.on<DownloadQueueItem[]>('event:downloads-updated', (queue) => {
    if (!Array.isArray(queue)) return;
    this.downloads.length = 0;
    this.downloads.push(...queue);

    // Sync completed chapter downloads into in-memory mangaList so Library's "Downloaded" filter
    // and manga.downloadedCount stay fresh even when the user is not viewing Manga Detail.
    let libraryCountsChanged = false;
    for (const dl of queue) {
      if (dl.status !== 'completed') continue;
      const manga = this.mangaList.find((m) => m.id === dl.mangaId);
      if (!manga) continue;
      let mangaChanged = false;
      if (Array.isArray(manga.chapters) && manga.chapters.length > 0) {
        const ch = manga.chapters.find(
          (c) =>
            (dl.chapterUrl && c.url && c.url.toLowerCase() === dl.chapterUrl.toLowerCase()) ||
            (typeof dl.chapterNumber === 'number' && Math.abs(c.number - dl.chapterNumber) < 0.01)
        );
        if (ch && !ch.isDownloaded) {
          ch.isDownloaded = true;
          mangaChanged = true;
        }
        const recomputed = manga.chapters.filter((c) => c.isDownloaded).length;
        if (manga.downloadedCount !== recomputed) {
          manga.downloadedCount = recomputed;
          mangaChanged = true;
        }
      } else if ((manga.downloadedCount || 0) === 0) {
        manga.downloadedCount = 1;
        mangaChanged = true;
      }
      if (mangaChanged) libraryCountsChanged = true;
    }

    this.updateSidebarCounters();
    if (this.activeDetailMangaId) {
      const container = document.getElementById('viewContainer');
      const activeManga = this.mangaList.find((m) => m.id === this.activeDetailMangaId);
      if (container && activeManga) {
        updateDetailChapterDownloadsInPlace(
          container,
          activeManga,
          this.downloads,
          document.documentElement.lang === 'en'
        );
      }
    } else if (this.currentTab === 'downloads') {
      const container = document.getElementById('viewContainer');
      if (container) {
        const inPlaceUpdated = updateDownloadsProgressInPlace(container, this.downloads);
        if (!inPlaceUpdated) {
          this.renderDownloadsView(container);
        }
      }
    } else if (libraryCountsChanged && this.currentTab === 'library') {
      const container = document.getElementById('viewContainer');
      if (container) this.renderLibraryView(container);
    }
  });
  this.bridge.on<ExtensionItem[]>('event:extensions-updated', (exts) => {
    if (!Array.isArray(exts)) return;
    this.extensions.length = 0;
    this.extensions.push(...exts);
    this.updateSidebarCounters();
    this.persistLocalCache();
    if (!this.activeDetailMangaId) {
      const container = document.getElementById('viewContainer');
      if (container && this.currentTab === 'extensions') {
        this.renderExtensionsView(container);
      } else if (container && this.currentTab === 'browse' && !this.activeBrowseSourceId) {
        this.renderBrowseView(container);
      }
    }
  });
  this.bridge.on<{ progress?: number; downloadedBytes?: number; totalBytes?: number; stage?: string }>(
    'event:app-update-progress',
    (prog) => {
      if (!prog) return;
      updateAppUpdateOverlayProgress(prog, document.documentElement.lang === 'en');
    }
  );
  this.bridge.on<{ active: boolean; current: number; total: number }>('event:library-sync-progress', (prog) => {
    if (!prog) return;
    const wasActive = this.syncProgress.active;
    this.syncProgress = {
      active: prog.active,
      current: prog.current,
      total: Math.max(1, prog.total)
    };
    const syncPercent = Math.round((this.syncProgress.current / Math.max(1, this.syncProgress.total)) * 100);
    const counterEls = document.querySelectorAll('.empty-state-counter');
    counterEls.forEach((el) => {
      el.textContent = `(${this.syncProgress.current}/${this.syncProgress.total})`;
    });
    const fillEls = document.querySelectorAll('.empty-state-progress-fill');
    fillEls.forEach((el) => {
      (el as HTMLElement).style.width = `${syncPercent}%`;
    });
    if (this.currentTab === 'library' && !this.activeDetailMangaId) {
      const container = document.getElementById('viewContainer');
      if (container && (prog.active !== wasActive || this.syncProgress.current === 0)) {
        this.renderLibraryView(container);
      }
    } else if (this.currentTab === 'updates' && !this.activeDetailMangaId) {
      const container = document.getElementById('viewContainer');
      if (container && (prog.active !== wasActive || this.syncProgress.current === 0)) {
        this.renderUpdatesView(container);
      }
    }
  });
  this.bridge.on<{ active: boolean; current: number; total: number }>('event:source-check-progress', (prog) => {
    if (this.isManualBrowseRefreshing) {
      if (prog.total > 0) this.browseVerificationProgress.total = prog.total;
      const tot = this.browseVerificationProgress.total || prog.total || 1;
      const cur = Math.min(Math.max(0, prog.current || 0), tot);
      this.browseVerificationProgress.current = cur;
      const counterEl = document.querySelector('.browse-state-counter');
      const progressFill = document.querySelector('.browse-state-progress-fill') as HTMLElement | null;
      if (counterEl) {
        counterEl.textContent = `(${cur}/${tot})`;
      }
      if (progressFill) {
        const percent = Math.round((cur / Math.max(1, tot)) * 100);
        progressFill.style.width = `${percent}%`;
      }
    }
  });
  this.bridge.on<{ id: string; progress: number }>('event:ext-progress', (ev) => {
    if (!ev || !ev.id) return;
    if (ev.progress >= 100) {
      this.downloadingExtProgress.delete(ev.id);
    } else {
      this.downloadingExtProgress.set(ev.id, ev.progress);
    }
    if (this.currentTab === 'extensions' && !this.activeDetailMangaId) {
      const container = document.getElementById('viewContainer');
      if (container) this.renderExtensionsView(container);
    }
  });
  this.bridge.on<{ announcements?: any[]; hasUnread?: boolean; isNewAlert?: boolean }>(
    'event:announcements-updated',
    (ev) => {
      if (!ev) return;
      this.applyBackendAnnouncements(ev.announcements, ev.hasUnread);
      if (ev.isNewAlert && this.announcements.length > 0) {
        this.showToast(this.l(`Pengumuman baru: ${this.announcements[0].title}`, `New announcement: ${this.announcements[0].title}`));
      }
    }
  );
  this.bridge.on<{
    isUpdateAvailable: boolean;
    currentVersion: string;
    latestVersion: string;
    downloadUrl: string;
    releaseNotes?: string;
  }>('event:app-update-available', (info) => {
    if (info && info.isUpdateAvailable) {
      this.showToast(
        this.l(
          `Pembaruan Yomic ${info.latestVersion} tersedia di Pengaturan > Versi Aplikasi.`,
          `Yomic ${info.latestVersion} update available in Settings > App Version.`
        )
      );
    }
  });
}

export function upsertMangaItem(this: YomicApp, incoming: MangaItem): MangaItem {
  if (!incoming.coverPalette || incoming.coverPalette.length < 3) {
    incoming.coverPalette = ['#071426', '#0066CC', '#38BDF8'];
  }
  const existingIdx = this.mangaList.findIndex(
    (m) => m.id === incoming.id || (incoming.url && m.url === incoming.url && String(m.sourceId) === String(incoming.sourceId))
  );
  if (existingIdx >= 0) {
    const prev = this.mangaList[existingIdx];
    const preserveCategories =
      prev.inLibrary &&
      incoming.inLibrary &&
      (!incoming.categoryIds || incoming.categoryIds.length === 0) &&
      prev.categoryIds &&
      prev.categoryIds.length > 0;
    const preserveMal =
      prev.malTracked &&
      !incoming.malTracked &&
      (!incoming.malId || incoming.malId === 0);

    const merged: MangaItem = {
      ...prev,
      ...incoming,
      chapters: incoming.chapters && incoming.chapters.length > 0 ? incoming.chapters : prev.chapters || [],
      categoryIds: preserveCategories ? prev.categoryIds : incoming.categoryIds,
      malTracked: preserveMal ? prev.malTracked : incoming.malTracked,
      malId: preserveMal ? prev.malId : incoming.malId,
      malTitle: preserveMal ? prev.malTitle : incoming.malTitle,
      malStatus: preserveMal ? prev.malStatus : incoming.malStatus,
      malScore: preserveMal ? prev.malScore : incoming.malScore
    };
    this.mangaList[existingIdx] = merged;
    if (this.activeDetailMangaId === prev.id) {
      this.activeDetailMangaId = merged.id;
    }
    if (this.readerMangaId === prev.id) {
      this.readerMangaId = merged.id;
    }
    return merged;
  } else {
    if (!incoming.chapters) incoming.chapters = [];
    this.mangaList.push(incoming);
    return incoming;
  }
}

export function hydrateInitialState(this: YomicApp): void {
  let data: AppBootstrapPayload | null = null;
  try {
    const preload = (window as any).__YOMIC_PRELOAD__;
    if (preload && typeof preload === 'object') {
      data = preload as AppBootstrapPayload;
    }
  } catch { }
  if (!data) {
    try {
      const cached = localStorage.getItem('yomic_bootstrap_cache');
      if (cached) {
        data = JSON.parse(cached) as AppBootstrapPayload;
      }
    } catch { }
  }
  if (!data) return;
  if (Array.isArray(data.extensions) && data.extensions.length > 0) {
    this.extensions.length = 0;
    this.extensions.push(...data.extensions);
  }
  if (Array.isArray(data.categories) && data.categories.length > 0) {
    this.categories.length = 0;
    this.categories.push(...data.categories);
  }
  if (Array.isArray(data.mangaList) && data.mangaList.length > 0) {
    this.mangaList.length = 0;
    for (const m of data.mangaList) {
      this.upsertMangaItem(m);
    }
  }
  if (Array.isArray(data.activityUpdates) && data.activityUpdates.length > 0) {
    this.activityUpdates.length = 0;
    this.activityUpdates.push(...data.activityUpdates);
  }
  if (Array.isArray(data.downloads) && data.downloads.length > 0) {
    this.downloads.length = 0;
    this.downloads.push(...data.downloads);
  }
  if (data.settings) {
    Object.assign(this.settings, data.settings);
    this.readerMode = this.settings.defaultReaderMode || 'webtoon';
    this.applyTheme(this.settings.isDarkMode);
    this.toggleSidebarCollapse(Boolean(this.settings.compactSidebar), false);
  }
}

export function persistLocalCache(this: YomicApp): void {
  try {
    const payload: AppBootstrapPayload = {
      mangaList: this.mangaList,
      categories: this.categories,
      extensions: this.extensions,
      activityUpdates: this.activityUpdates,
      downloads: this.downloads,
      settings: this.settings
    };
    localStorage.setItem('yomic_bootstrap_cache', JSON.stringify(payload));
  } catch { }
}

export async function bootstrapFromBackend(this: YomicApp): Promise<void> {
  try {
    const data = await this.bridge.invoke<AppBootstrapPayload>('backend:bootstrap');
    if (!data) return;
    if (Array.isArray(data.extensions) && data.extensions.length > 0) {
      this.extensions.length = 0;
      this.extensions.push(...data.extensions);
    }
    if (Array.isArray(data.categories)) {
      this.categories.length = 0;
      this.categories.push(...data.categories);
    }
    if (Array.isArray(data.mangaList)) {
      this.mangaList.length = 0;
      for (const m of data.mangaList) {
        this.upsertMangaItem(m);
      }
    }
    if (Array.isArray(data.activityUpdates)) {
      this.activityUpdates.length = 0;
      this.activityUpdates.push(...data.activityUpdates);
    }
    if (Array.isArray(data.downloads)) {
      this.downloads.length = 0;
      this.downloads.push(...data.downloads);
    }
    if (data.settings) {
      const preservedCompactSidebar = Boolean(this.settings.compactSidebar);
      Object.assign(this.settings, data.settings);
      if (typeof data.settings.compactSidebar !== 'boolean') {
        this.settings.compactSidebar = preservedCompactSidebar;
      }
      this.readerMode = this.settings.defaultReaderMode || 'webtoon';
      this.applyTheme(this.settings.isDarkMode);
      this.toggleSidebarCollapse(Boolean(this.settings.compactSidebar), false);
      applyStaticDomTranslations(this.settings.appLanguage);
    }
    if (Array.isArray(data.announcements) && data.announcements.length > 0) {
      this.applyBackendAnnouncements(data.announcements, data.hasUnreadAnnouncements);
    } else if (typeof data.hasUnreadAnnouncements === 'boolean') {
      document.getElementById('announcementUnreadDot')?.classList.toggle('hidden', !data.hasUnreadAnnouncements);
    }
    this.removedHistoryIds.clear();
    this.updateSidebarCounters();
    this.persistLocalCache();
    this.renderCurrentWorkspace(true);
    this.tutorial.refreshLocale();
    if (this.settings.autoSyncStartup && this.mangaList.some((m) => m.inLibrary)) {
      void this.triggerLibrarySyncProgress(false);
    }

    // Fetch live announcements via C# AnnouncementService (Atom SHA + CDN bypass)
    void this.bridge
      .invoke<{ announcements?: any[]; hasUnread?: boolean }>('announcements:get-list', { force: false })
      .then((res) => {
        if (res) {
          this.applyBackendAnnouncements(res.announcements, res.hasUnread);
        }
      })
      .catch(() => {});
  } catch {
    // Keep fallback data if host is unreachable
  }
}

let activeDetailFetchSeq = 0;

export async function fetchMangaDetailFromBackend(
  this: YomicApp,
  mangaId: string,
  url?: string,
  sourceId?: string | number,
  forceRefresh = false,
  sourceName?: string
): Promise<void> {
  const reqSeq = ++activeDetailFetchSeq;
  try {
    this.isDetailLoading = true;
    const detail = await this.bridge.invoke<MangaItem | null>('manga:get-detail', {
      mangaId,
      url: url || '',
      sourceId: typeof sourceId === 'number' ? sourceId : Number(sourceId) || 0,
      sourceName: sourceName || '',
      forceRefresh
    });
    if (reqSeq !== activeDetailFetchSeq) return;
    this.isDetailLoading = false;
    if (detail) {
      const updated = this.upsertMangaItem(detail);
      if (this.activeDetailMangaId === mangaId || this.activeDetailMangaId === updated.id) {
        this.activeDetailMangaId = updated.id;
        this.renderCurrentWorkspace(true);
      }
    } else if (this.activeDetailMangaId === mangaId) {
      // Clear skeleton loading rows cleanly even when backend returns null
      this.renderCurrentWorkspace(true);
    }
  } catch {
    if (reqSeq !== activeDetailFetchSeq) return;
    this.isDetailLoading = false;
    if (this.activeDetailMangaId === mangaId) {
      this.renderCurrentWorkspace(true);
    }
  }
}
