/* =========================================================================
 * YOMIC V2 — MAIN TYPESCRIPT APPLICATION CONTROLLER
 * ========================================================================= */

import { HostBridge } from './bridge.js';
import { getIconSvg } from './icons.js';
import { applyStaticDomTranslations, translate, TranslationKey } from './i18n.js';
import { DEFAULT_APP_SETTINGS, DEFAULT_HOTKEYS, HOTKEY_DEFINITIONS, escapeXml, sanitizeCssColor } from './mock-data.js';
import {
  ActivityUpdateItem,
  AnnouncementItem,
  AppSettingsState,
  CategoryItem,
  ConfirmModalOptions,
  DownloadQueueItem,
  ExtensionItem,
  HotkeyDefinition,
  LibraryCategory,
  LibraryFilterMode,
  LibrarySortBy,
  MalSearchResult,
  MangaItem,
  NavTabId,
  ReaderMode,
  ReadingStatisticsPayload,
  SettingsSubTab
} from './types.js';

import {
  bindBackendEvents,
  upsertMangaItem,
  hydrateInitialState,
  persistLocalCache,
  bootstrapFromBackend,
  fetchMangaDetailFromBackend
} from './services/backend-sync.js';
import {
  getMangaCoverHtmlSrc,
  buildSourceLogoHtml,
  buildSourceFaviconHtml
} from './utils/source-icons.js';
import {
  DEFAULT_ANNOUNCEMENTS,
  applyBackendAnnouncements,
  closeModalWithAnimation,
  openAnnouncementsModal,
  closeAnnouncementsModal,
  renderAnnouncementModalBody,
  openFeedbackModal,
  closeFeedbackModal,
  bindModalShellEvents
} from './views/modals.js';
import { TutorialController } from './views/tutorial.js';
import {
  renderLibraryView,
  triggerLibrarySyncProgress,
  isExplicitManga,
  buildMangaGridCardHtml,
  buildMangaListRowHtml
} from './views/library.js';
import {
  renderUpdatesView,
  renderHistoryView,
  renderReadingStatisticsView,
  fetchReadingStatistics,
  computeClientFallbackStatistics
} from './views/updates-history.js';
import {
  buildLangBadgeHtml,
  buildLangFlagsHtml,
  renderBrowseView,
  fetchSourceCatalogFeed,
  renderSourceCatalogFeed
} from './views/browse.js';
import { renderExtensionsView } from './views/extensions.js';
import {
  renderDownloadsView,
  enqueueDownload,
  startDownloadProgressTicker
} from './views/downloads.js';
import {
  buildMangaDetailHtml,
  bindMangaDetailEvents
} from './views/manga-detail.js';
import {
  openReader,
  retryPageImg,
  saveReaderProgressToBackend,
  closeReader,
  setReaderMode,
  renderReaderPages,
  updateReaderPageIndicator,
  adjustReaderZoom,
  stepReaderChapter,
  stepReaderPage,
  isReaderFlyoutOpen,
  showReaderHud,
  hideReaderHud,
  toggleReaderHud,
  renderReaderChapterDropdown,
  toggleReaderAutoScroll,
  stopReaderAutoScroll,
  bindReaderShellEvents,
  syncReaderPageFromScroll,
  restoreReaderScrollPosition,
  prefetchReaderPagesAround
} from './views/reader.js';
import { renderSettingsView } from './views/settings.js';

export class YomicApp {
  public readonly bridge = new HostBridge();

  // Core Application State
  public currentTab: NavTabId = 'library';
  public activeDetailMangaId: string | null = null;
  public searchQuery = '';
  public isLibrarySearchOpen = false;

  // Sub-view States
  public categories: CategoryItem[] = [];
  public libraryCategory: LibraryCategory = 'all';
  public libraryCategoryFilter: number | 'all' = 'all';
  public libraryFilterMode: LibraryFilterMode = 'all';
  public librarySourceFilter = 'all';
  public librarySort: LibrarySortBy = 'lastRead';
  public libraryViewMode: 'grid' | 'list' = 'grid';
  public historyViewMode: 'grid' | 'list' = 'list';
  public readonly removedHistoryIds = new Set<string>();
  public readerLoadSeq = 0;
  public malSearchSeq = 0;
  public malSelectSeq = 0;
  public readonly busyExtOps = new Set<string>();
  public isLibraryFlyoutOpen = false;
  public isLibraryStatsOpen = false;
  public readingStatsData: ReadingStatisticsPayload | null = null;
  public isReadingStatsLoading = false;
  public readerLastSaveEpoch = 0;
  public libraryFlyoutTab: 'category' | 'filter' | 'source' | 'sort' = 'category';
  public contextMenuMangaId: string | null = null;
  public categoryManageMangaId: string | null = null;
  public contextMenuPos = { x: 0, y: 0 };
  public syncProgress = { active: false, current: 0, total: 8 };
  public isManualBrowseRefreshing = false;
  public browseVerificationProgress = { active: false, current: 0, total: 0 };
  public extVerificationProgress = { active: false, current: 0, total: 0 };
  public discoverSourceFilter = 'all';
  public isSourceFeedFilterFlyoutOpen = false;
  public sourceFeedFlyoutTab: 'filter' | 'genre' | 'sort' = 'filter';
  public sourceFeedSort: 'popular' | 'latest' = 'popular';
  public sourceFeedStatusFilter: 'all' | 'Ongoing' | 'Completed' = 'all';
  public sourceFeedTypeFilter: 'all' | 'Manhwa' | 'Manga' | 'Manhua' = 'all';
  public sourceFeedGenreFilter = 'Semua';
  public sourceFeedSearchQuery = '';
  public browseSearchQuery = '';
  public isBrowseSearchOpen = false;
  public browseLangFilter: 'ALL' | 'ID' | 'EN' = 'ALL';
  public isBrowseLangFlyoutOpen = false;
  public activeBrowseSourceId: string | null = null;
  public savedBrowseScrollTop = 0;
  public extensionSearchQuery = '';
  public isExtensionSearchOpen = false;
  public extensionLangFilter: 'ALL' | 'ID' | 'EN' = 'ALL';
  public isExtensionLangFlyoutOpen = false;
  public readonly downloadingExtProgress = new Map<string, number>();
  public settingsSubTab: SettingsSubTab = 'appearance';
  public announcements: AnnouncementItem[] = [...DEFAULT_ANNOUNCEMENTS];
  public selectedAnnouncementId = DEFAULT_ANNOUNCEMENTS[0].id;

  // Detail View State (Mirror of Yomic v1 MangaDetailViewModel)
  public detailChapterFilter: 'all' | 'unread' | 'bookmarked' = 'all';
  public detailChapterAsc = false;
  public isSynopsisExpanded = false;
  public isChapterFilterFlyoutOpen = false;
  public isMalTrackerPopupOpen = false;
  public isMalSearchMode = false;
  public malSearchQuery = '';
  public malSearchResults: MalSearchResult[] = [];
  public isMalSearching = false;
  public malSearchError = '';
  public malSelectedEntry: { id: number; title: string; coverUrl?: string } | null = null;

  // Reader State
  public isReaderOpen = false;
  public readerHudAutoHideTimer: number | null = null;
  public isReaderInteractingWithControls = false;
  public readerMangaId = '';
  public readerChapterNumber = 0;
  public readerChapterUrl = '';
  public readerPages: string[] = [];
  public readerRequiresVerification = false;
  public chapterPagesCache: Map<string, string[]> = new Map();
  public isReaderLoadingPages = false;
  public readerMode: ReaderMode = 'webtoon';
  public readerCurrentPage = 1;
  public readerTotalPages = 5;
  public readerZoomPercent = 100;
  public readerAutoScrollTimer: number | null = null;
  public readerScrollPercent = 0;
  public targetRestorePercent = -1;
  public isRestoringReaderScroll = false;
  public readerSaveProgressDebounceTimer: number | null = null;
  public readerPageWrappers: HTMLElement[] = [];
  public readerAspectRatioCache: Map<string, number> = new Map();
  public readerPrefetchedUrls: Set<string> = new Set();
  public readerHudRefs: {
    curText: HTMLElement | null;
    totText: HTMLElement | null;
    fillBar: HTMLElement | null;
    scrub: HTMLInputElement | null;
    prevBtn: HTMLButtonElement | null;
    nextBtn: HTMLButtonElement | null;
  } | null = null;
  public readerLastCompletedFlushKey = '';
  public viewScrollPositions: Map<string, number> = new Map();
  public isRestoringWorkspaceScroll = false;

  // Source Catalog Feed Remote State
  public sourceFeedItems: MangaItem[] = [];
  public isSourceFeedLoading = false;
  public sourceFeedPage = 1;
  public sourceFeedHasMore = false;
  public sourceFeedError: 'offline' | 'error' | null = null;
  public isDetailLoading = false;

  // Data Collections (clean state populated strictly from C# backend / SQLite)
  public readonly mangaList: MangaItem[] = [];
  public readonly activityUpdates: ActivityUpdateItem[] = [];
  public readonly extensions: ExtensionItem[] = [];
  public readonly downloads: DownloadQueueItem[] = [];
  public readonly settings: AppSettingsState = {
    ...DEFAULT_APP_SETTINGS,
    customHotkeys: { ...DEFAULT_APP_SETTINGS.customHotkeys }
  };
  public activeSettingsTab: string = 'hub';
  public readonly tutorial = new TutorialController(this);

  public init(): void {
    (window as any).yomicApp = this;
    this.bindGlobalImageFallbacks();
    this.hydrateInitialState();
    applyStaticDomTranslations(this.settings.appLanguage);
    this.applyTheme(this.settings.isDarkMode);
    this.bindShellEvents();
    this.tutorial.init();
    this.tutorial.checkAutoTrigger(true);
    this.bindBackendEvents();
    this.updateSidebarCounters();
    this.renderCurrentWorkspace();
    if (this.bridge.isWebViewHost) {
      void this.bootstrapFromBackend();
    } else {
      setTimeout(() => {
        if (this.bridge.isWebViewHost) {
          void this.bootstrapFromBackend();
        }
      }, 250);
      this.startDownloadProgressTicker();
    }
  }

  public t(key: TranslationKey, params?: Record<string, string | number>): string {
    return translate(this.settings.appLanguage, key, params);
  }

  public l(idText: string, enText: string): string {
    return this.settings.appLanguage === 'en' ? enText : idText;
  }

  public openTutorial(slideIndex = 0): void {
    this.tutorial.open(slideIndex);
  }

  public bindShellEvents(): void {
    // Sidebar Collapse Toggle
    const btnToggleSidebar = document.getElementById('btnToggleSidebar');
    btnToggleSidebar?.addEventListener('click', () => this.toggleSidebarCollapse());

    // Main Sidebar Navigation Buttons
    const navBtns = document.querySelectorAll<HTMLButtonElement>('.nav-btn[data-nav]');
    navBtns.forEach((btn) => {
      btn.addEventListener('click', () => {
        const navId = btn.dataset.nav as NavTabId | undefined;
        if (navId) {
          this.navigateToTab(navId);
        }
      });
    });

    // Reader & Modal Shell Events
    this.bindReaderShellEvents();
    this.bindModalShellEvents();

    // Global Keyboard Shortcuts & Outside-Click Dismissal for Popovers
    window.addEventListener('keydown', (ev) => this.handleGlobalKeyDown(ev));
    document.addEventListener('click', () => {
      if (this.contextMenuMangaId) {
        this.contextMenuMangaId = null;
        if (this.currentTab === 'library' && !this.activeDetailMangaId) {
          const container = document.getElementById('viewContainer');
          if (container) this.renderLibraryView(container);
        }
      }
      if (this.isBrowseLangFlyoutOpen) {
        this.isBrowseLangFlyoutOpen = false;
        if (this.currentTab === 'browse' && !this.activeDetailMangaId) {
          const container = document.getElementById('viewContainer');
          if (container) this.renderBrowseView(container);
        }
      }
      if (this.isExtensionLangFlyoutOpen) {
        this.isExtensionLangFlyoutOpen = false;
        if (this.currentTab === 'extensions' && !this.activeDetailMangaId) {
          const container = document.getElementById('viewContainer');
          if (container) this.renderExtensionsView(container);
        }
      }
    });
  }

  public serializeKeyboardEvent(ev: KeyboardEvent): string | null {
    // Ignore standalone modifier presses
    if (['Control', 'Shift', 'Alt', 'Meta'].includes(ev.key)) {
      return null;
    }

    const parts: string[] = [];
    if (ev.ctrlKey || ev.metaKey) parts.push('Ctrl');
    if (ev.altKey) parts.push('Alt');
    if (ev.shiftKey) parts.push('Shift');

    // Normalize main key
    let key = ev.key;
    if (key === ' ') {
      key = 'Space';
    } else if (key.length === 1) {
      key = key.toLowerCase();
    }
    parts.push(key);

    return parts.join('+');
  }

  public getEffectiveHotkey(actionId: string): string {
    if (this.settings.customHotkeys && this.settings.customHotkeys[actionId]) {
      return this.settings.customHotkeys[actionId];
    }
    const def = HOTKEY_DEFINITIONS.find((d) => d.id === actionId);
    return def ? def.defaultKey : '';
  }

  public isHotmatched(ev: KeyboardEvent, hotkeyStr: string): boolean {
    if (!hotkeyStr) return false;
    const current = this.serializeKeyboardEvent(ev);
    if (!current) return false;
    const normCurrent = current.toLowerCase();
    const normTarget = hotkeyStr.trim().toLowerCase();
    if (normCurrent === normTarget) return true;
    // Allow '=' as zoom-in alias if target is '+' or vice versa
    if (
      (normTarget === '+' && (normCurrent === '=' || normCurrent === 'shift++')) ||
      (normTarget === '=' && normCurrent === '+')
    ) {
      return true;
    }
    return false;
  }

  public setCustomHotkey(actionId: string, hotkey: string): void {
    if (!this.settings.customHotkeys) {
      this.settings.customHotkeys = {};
    }
    this.settings.customHotkeys[actionId] = hotkey;
    this.persistLocalCache();
    if (this.bridge.isWebViewHost) {
      void this.bridge
        .invoke('settings:update', {
          key: 'customHotkeys',
          value: this.settings.customHotkeys
        })
        .catch(() => {
          this.showToast(this.l('Gagal menyimpan pintasan keyboard.', 'Failed to save keyboard shortcut.'), 'warning');
        });
    }
  }

  public resetCustomHotkey(actionId: string): void {
    if (this.settings.customHotkeys && actionId in this.settings.customHotkeys) {
      delete this.settings.customHotkeys[actionId];
      this.persistLocalCache();
      if (this.bridge.isWebViewHost) {
        void this.bridge
          .invoke('settings:update', {
            key: 'customHotkeys',
            value: this.settings.customHotkeys
          })
          .catch(() => {});
      }
    }
  }

  public resetAllHotkeys(): void {
    this.settings.customHotkeys = {};
    this.persistLocalCache();
    if (this.bridge.isWebViewHost) {
      void this.bridge
        .invoke('settings:update', {
          key: 'customHotkeys',
          value: this.settings.customHotkeys
        })
        .catch(() => {});
    }
  }

  public hasCustomHotkeys(): boolean {
    if (!this.settings.customHotkeys) return false;
    for (const def of HOTKEY_DEFINITIONS) {
      const custom = this.settings.customHotkeys[def.id];
      if (custom && custom.trim().toLowerCase() !== def.defaultKey.trim().toLowerCase()) {
        return true;
      }
    }
    return false;
  }

  public findConflictingAction(newHotkey: string, targetActionId: string): HotkeyDefinition | null {
    const targetDef = HOTKEY_DEFINITIONS.find((d) => d.id === targetActionId);
    if (!targetDef) return null;
    const normNew = newHotkey.trim().toLowerCase();
    for (const def of HOTKEY_DEFINITIONS) {
      if (def.id === targetActionId) continue;
      if (def.category !== targetDef.category) continue;
      const effectiveKey = this.getEffectiveHotkey(def.id).trim().toLowerCase();
      if (effectiveKey === normNew) {
        return def;
      }
    }
    return null;
  }

  public handleGlobalKeyDown(ev: KeyboardEvent): void {
    // 0. Detect topmost visible aria-modal dialog (for focus trap & blocking background shell hotkeys)
    const openDialogs = Array.from(document.querySelectorAll<HTMLElement>('[role="dialog"][aria-modal="true"]')).filter((dlg) => {
      const style = window.getComputedStyle(dlg);
      if (style.display === 'none' || style.visibility === 'hidden' || style.opacity === '0') return false;
      const backdrop = dlg.closest<HTMLElement>('.modal-backdrop, .v1-mal-backdrop, .app-update-overlay, .v1-modal-backdrop');
      if (backdrop) {
        const bStyle = window.getComputedStyle(backdrop);
        if (bStyle.display === 'none' || bStyle.visibility === 'hidden' || bStyle.opacity === '0') return false;
      }
      return dlg.getBoundingClientRect().width > 0 && dlg.getBoundingClientRect().height > 0;
    });
    const topDialog = openDialogs[openDialogs.length - 1];

    if (topDialog) {
      if (ev.key === 'Tab') {
        const focusables = Array.from(
          topDialog.querySelectorAll<HTMLElement>(
            'a[href], button:not([disabled]), input:not([disabled]):not([type="hidden"]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'
          )
        ).filter((el) => {
          const s = window.getComputedStyle(el);
          return s.display !== 'none' && s.visibility !== 'hidden' && el.getBoundingClientRect().width > 0;
        });
        if (focusables.length > 0) {
          const first = focusables[0];
          const last = focusables[focusables.length - 1];
          const active = document.activeElement as HTMLElement | null;
          if (ev.shiftKey) {
            if (!active || !topDialog.contains(active) || active === first) {
              ev.preventDefault();
              last.focus();
            }
          } else {
            if (!active || !topDialog.contains(active) || active === last) {
              ev.preventDefault();
              first.focus();
            }
          }
          return;
        }
        // Trap Tab inside busy/loading dialogs even when zero focusable controls exist
        ev.preventDefault();
        return;
      }
      // Block background shell navigation/reader hotkeys while a modal dialog is open
      if (ev.key !== 'Escape') {
        return;
      }
    }

    // Check if user is typing in an editable text element (do NOT block hotkeys when focus is on <input type="range|checkbox|radio|button">)
    const activeEl = document.activeElement as HTMLElement | null;
    const isTextInput =
      activeEl instanceof HTMLInputElement &&
      !['range', 'checkbox', 'radio', 'button', 'submit', 'reset', 'color', 'file'].includes((activeEl.type || '').toLowerCase());
    const isEditingText = Boolean(
      activeEl && (
        isTextInput ||
        activeEl.tagName === 'TEXTAREA' ||
        activeEl.tagName === 'SELECT' ||
        activeEl.isContentEditable
      )
    );

    if (isEditingText && activeEl) {
      if (ev.key === 'Escape') {
        activeEl.blur();
        return;
      }
      if (this.isHotmatched(ev, this.getEffectiveHotkey('globalSearch')) && !this.isReaderOpen) {
        ev.preventDefault();
        this.focusActiveSearchInput();
        return;
      }
      return;
    }

    // 1. Global / Shell Hotkeys
    if (this.isHotmatched(ev, this.getEffectiveHotkey('globalSearch')) && !this.isReaderOpen) {
      ev.preventDefault();
      this.focusActiveSearchInput();
      return;
    }
    if (this.isHotmatched(ev, this.getEffectiveHotkey('globalToggleSidebar')) && !this.isReaderOpen) {
      ev.preventDefault();
      this.toggleSidebarCollapse();
      return;
    }
    if (this.isHotmatched(ev, this.getEffectiveHotkey('globalToggleFullscreen'))) {
      ev.preventDefault();
      this.bridge.toggleFullscreen();
      return;
    }
    if (this.isHotmatched(ev, this.getEffectiveHotkey('navLibrary')) && !this.isReaderOpen) {
      ev.preventDefault();
      this.navigateToTab('library');
      return;
    }
    if (this.isHotmatched(ev, this.getEffectiveHotkey('navUpdates')) && !this.isReaderOpen) {
      ev.preventDefault();
      this.navigateToTab('updates');
      return;
    }
    if (this.isHotmatched(ev, this.getEffectiveHotkey('navHistory')) && !this.isReaderOpen) {
      ev.preventDefault();
      this.navigateToTab('history');
      return;
    }
    if (this.isHotmatched(ev, this.getEffectiveHotkey('navDownloads')) && !this.isReaderOpen) {
      ev.preventDefault();
      this.navigateToTab('downloads');
      return;
    }
    if (this.isHotmatched(ev, this.getEffectiveHotkey('navBrowse')) && !this.isReaderOpen) {
      ev.preventDefault();
      this.navigateToTab('browse');
      return;
    }
    if (this.isHotmatched(ev, this.getEffectiveHotkey('navExtensions')) && !this.isReaderOpen) {
      ev.preventDefault();
      this.navigateToTab('extensions');
      return;
    }
    if (this.isHotmatched(ev, this.getEffectiveHotkey('navSettings')) && !this.isReaderOpen) {
      ev.preventDefault();
      this.navigateToTab('settings');
      return;
    }

    // 2. Reader Hotkeys (only when Reader is open)
    if (this.isReaderOpen) {
      if (this.isHotmatched(ev, this.getEffectiveHotkey('readerClose'))) {
        ev.preventDefault();
        this.closeReader();
        return;
      }
      if (this.isHotmatched(ev, this.getEffectiveHotkey('readerToggleHud'))) {
        ev.preventDefault();
        this.toggleReaderHud();
        return;
      }
      if (this.isHotmatched(ev, this.getEffectiveHotkey('readerModeWebtoon'))) {
        ev.preventDefault();
        this.setReaderMode('webtoon');
        return;
      }
      if (this.isHotmatched(ev, this.getEffectiveHotkey('readerModeSingle'))) {
        ev.preventDefault();
        this.setReaderMode('single');
        return;
      }
      if (this.isHotmatched(ev, this.getEffectiveHotkey('readerModeDual'))) {
        ev.preventDefault();
        this.setReaderMode('dual');
        return;
      }
      if (this.isHotmatched(ev, this.getEffectiveHotkey('readerZoomIn'))) {
        ev.preventDefault();
        this.adjustReaderZoom(10);
        return;
      }
      if (this.isHotmatched(ev, this.getEffectiveHotkey('readerZoomOut'))) {
        ev.preventDefault();
        this.adjustReaderZoom(-10);
        return;
      }
      if (this.isHotmatched(ev, this.getEffectiveHotkey('readerResetZoom'))) {
        ev.preventDefault();
        this.adjustReaderZoom(100 - this.readerZoomPercent);
        this.showToast(this.l('Skala tampilan diatur ulang ke 100%.', 'Display scale reset to 100%.'));
        return;
      }
      if (this.isHotmatched(ev, this.getEffectiveHotkey('readerToggleAutoScroll'))) {
        ev.preventDefault();
        this.toggleReaderAutoScroll();
        return;
      }
      if (this.isHotmatched(ev, this.getEffectiveHotkey('readerToggleFullscreen'))) {
        ev.preventDefault();
        this.bridge.toggleFullscreen();
        return;
      }
      if (this.isHotmatched(ev, this.getEffectiveHotkey('readerNextChapter'))) {
        ev.preventDefault();
        this.stepReaderChapter(1);
        return;
      }
      if (this.isHotmatched(ev, this.getEffectiveHotkey('readerPrevChapter'))) {
        ev.preventDefault();
        this.stepReaderChapter(-1);
        return;
      }
      if (this.isHotmatched(ev, this.getEffectiveHotkey('readerNextPage'))) {
        ev.preventDefault();
        if (this.readerMode === 'webtoon') {
          this.stepReaderChapter(1);
        } else {
          this.stepReaderPage(this.settings.readingDirection === 'rtl' ? -1 : 1);
        }
        return;
      }
      if (this.isHotmatched(ev, this.getEffectiveHotkey('readerPrevPage'))) {
        ev.preventDefault();
        if (this.readerMode === 'webtoon') {
          this.stepReaderChapter(-1);
        } else {
          this.stepReaderPage(this.settings.readingDirection === 'rtl' ? 1 : -1);
        }
        return;
      }
      return;
    }

    // 3. Fallback Escape for modals / detail views
    if (ev.key === 'Escape') {
      const tutorialBackdrop = document.getElementById('tutorialModalBackdrop');
      const annBackdrop = document.getElementById('announcementModalBackdrop');
      const fbBackdrop = document.getElementById('feedbackModalBackdrop');
      const malBackdrop = document.getElementById('malTrackerModalBackdrop');
      const libFilterBackdrop = document.getElementById('libFilterModalBackdrop');
      const libCategoryBackdrop = document.getElementById('libCategoryModalBackdrop');
      const sourceFilterBackdrop = document.getElementById('sourceFilterModalBackdrop');
      const browseLangBackdrop = document.getElementById('browseLangModalBackdrop');
      const extLangBackdrop = document.getElementById('extLangModalBackdrop');
      const chapterFilterBackdrop = document.getElementById('chapterFilterModalBackdrop');
      if (tutorialBackdrop?.classList.contains('open')) {
        this.tutorial.close(true);
      } else if (annBackdrop?.classList.contains('open')) {
        this.closeAnnouncementsModal();
      } else if (fbBackdrop?.classList.contains('open')) {
        this.closeFeedbackModal();
      } else if (libCategoryBackdrop?.classList.contains('open')) {
        this.categoryManageMangaId = null;
        libCategoryBackdrop.classList.remove('open');
      } else if (libFilterBackdrop?.classList.contains('open')) {
        this.isLibraryFlyoutOpen = false;
        const container = document.getElementById('viewContainer');
        if (container && this.currentTab === 'library') this.renderLibraryView(container);
      } else if (sourceFilterBackdrop?.classList.contains('open')) {
        this.isSourceFeedFilterFlyoutOpen = false;
        this.renderCurrentWorkspace(true);
      } else if (browseLangBackdrop?.classList.contains('open')) {
        this.isBrowseLangFlyoutOpen = false;
        browseLangBackdrop.classList.remove('open');
        document.getElementById('btnBrowseLangFlyout')?.classList.remove('active');
      } else if (extLangBackdrop?.classList.contains('open')) {
        this.isExtensionLangFlyoutOpen = false;
        extLangBackdrop.classList.remove('open');
        document.getElementById('btnExtLangFlyout')?.classList.remove('active');
      } else if (chapterFilterBackdrop?.classList.contains('open')) {
        this.isChapterFilterFlyoutOpen = false;
        this.renderCurrentWorkspace(true);
      } else if (malBackdrop?.classList.contains('open')) {
        this.isMalTrackerPopupOpen = false;
        this.renderCurrentWorkspace(true);
      } else if (this.activeDetailMangaId) {
        this.activeDetailMangaId = null;
        this.renderCurrentWorkspace(true);
      } else if (this.isLibraryStatsOpen && this.currentTab === 'library') {
        this.isLibraryStatsOpen = false;
        const container = document.getElementById('viewContainer');
        if (container) this.renderLibraryView(container);
      }
    }
  }

  public toggleSidebarCollapse(forceState?: boolean, persist = true): void {
    const sidebar = document.getElementById('appSidebar');
    if (!sidebar) return;
    const nextCollapsed = forceState ?? !sidebar.classList.contains('collapsed');
    sidebar.classList.toggle('collapsed', nextCollapsed);
    this.settings.compactSidebar = nextCollapsed;
    if (persist) {
      this.persistLocalCache();
      if (this.bridge.isWebViewHost) {
        void this.bridge.invoke('settings:update', { key: 'compactSidebar', value: nextCollapsed }).catch(() => {});
      }
    }
  }

  public navigateToTab(tab: NavTabId): void {
    this.currentTab = tab;
    this.activeDetailMangaId = null;
    if (tab === 'library') {
      this.isLibraryStatsOpen = false;
    }
    if (tab === 'browse') {
      this.activeBrowseSourceId = null;
      this.sourceFeedSearchQuery = '';
      this.sourceFeedSort = 'popular';
      this.sourceFeedStatusFilter = 'all';
      this.sourceFeedTypeFilter = 'all';
      this.sourceFeedGenreFilter = 'Semua';
      this.isSourceFeedFilterFlyoutOpen = false;
      this.savedBrowseScrollTop = 0;
      this.viewScrollPositions.delete('browse_sources_list');
      for (const key of Array.from(this.viewScrollPositions.keys())) {
        if (key.startsWith('browse_source_')) {
          this.viewScrollPositions.delete(key);
        }
      }
    } else {
      for (const key of Array.from(this.viewScrollPositions.keys())) {
        if (key.startsWith('browse_source_')) {
          this.viewScrollPositions.delete(key);
        }
      }
    }
    if (tab === 'settings') {
      this.activeSettingsTab = 'hub';
    }
    document.querySelectorAll<HTMLButtonElement>('.nav-btn[data-nav]').forEach((btn) => {
      const isTarget = btn.dataset.nav === tab;
      if (isTarget) {
        btn.classList.remove('active');
        void btn.offsetWidth;
        btn.classList.add('active');
      } else {
        btn.classList.remove('active');
      }
    });
    document.getElementById('btnOpenAnnouncements')?.classList.remove('active');
    document.getElementById('btnOpenFeedback')?.classList.remove('active');
    document.getElementById('btnOpenTutorialSidebar')?.classList.remove('active');
    const viewContainer = document.getElementById('viewContainer');
    if (viewContainer) {
      viewContainer.scrollTop = 0;
      viewContainer.classList.remove('view-transition-in');
      void viewContainer.offsetWidth;
      viewContainer.classList.add('view-transition-in');
    }
    this.renderCurrentWorkspace();
  }

  public getCurrentWorkspaceScrollKey(): string {
    if (this.activeDetailMangaId) {
      return `manga_detail_${this.activeDetailMangaId}`;
    }
    if (this.currentTab === 'browse') {
      return this.activeBrowseSourceId ? `browse_source_${this.activeBrowseSourceId}` : 'browse_sources_list';
    }
    return this.currentTab;
  }

  public saveWorkspaceScroll(customKey?: string): void {
    const container = document.getElementById('viewContainer');
    if (!container) return;
    const key = customKey ?? this.getCurrentWorkspaceScrollKey();
    this.viewScrollPositions.set(key, container.scrollTop);
  }

  public restoreWorkspaceScroll(customKey?: string): void {
    const container = document.getElementById('viewContainer');
    if (!container) return;
    const key = customKey ?? this.getCurrentWorkspaceScrollKey();
    const targetY = this.viewScrollPositions.get(key) ?? 0;
    if (targetY > 0) {
      this.isRestoringWorkspaceScroll = true;
      container.style.scrollBehavior = 'auto';
      container.scrollTop = targetY;
      container.scrollTo({ top: targetY, behavior: 'instant' });
      requestAnimationFrame(() => {
        container.scrollTop = targetY;
        requestAnimationFrame(() => {
          if (container.scrollTop !== targetY) {
            container.scrollTop = targetY;
          }
          this.isRestoringWorkspaceScroll = false;
        });
      });
    }
  }

  public openMangaDetail(mangaId: string): void {
    this.saveWorkspaceScroll();
    this.activeDetailMangaId = mangaId;
    this.viewScrollPositions.delete(`manga_detail_${mangaId}`);
    this.renderCurrentWorkspace(false);
    const container = document.getElementById('viewContainer');
    if (container) {
      container.scrollTop = 0;
      container.scrollTo({ top: 0, behavior: 'instant' });
    }
    if (this.bridge.isWebViewHost) {
      const target = this.mangaList.find((m) => m.id === mangaId) || this.sourceFeedItems.find((m) => m.id === mangaId);
      void this.fetchMangaDetailFromBackend(mangaId, target?.url, target?.sourceId, false, target?.sourceName);
    }
  }

  public updateSidebarCounters(): void {
    // Minimalist Yomic v1 sidebar has no numeric counter badges
  }

  public applyTheme(isDark = this.settings.isDarkMode, subtitle?: string): void {
    document.documentElement.setAttribute('data-theme', isDark ? 'dark' : 'light');
    document.body.classList.toggle('dark-mode', Boolean(isDark));
    this.bridge.setWindowTheme(isDark ? 'antigravity-dark' : 'ocean-light', subtitle);
  }

  public setTopbarHeader(title: string): void {
    const titleEl = document.getElementById('topbarViewTitle');
    if (titleEl) titleEl.textContent = title;
    this.applyTheme(this.settings.isDarkMode, title);
  }

  public renderCurrentWorkspace(preserveScroll = false): void {
    const container = document.getElementById('viewContainer');
    if (!container) return;
    const savedScrollTop = container.scrollTop;
    if (!preserveScroll) {
      container.scrollTop = 0;
    }
    if (this.activeDetailMangaId) {
      const manga = this.mangaList.find((m) => m.id === this.activeDetailMangaId);
      if (manga) {
        this.setTopbarHeader(manga.title);
        container.innerHTML = this.buildMangaDetailHtml(manga);
        this.bindMangaDetailEvents(manga);
        const targetDetailY = this.viewScrollPositions.get(`manga_detail_${manga.id}`) ?? savedScrollTop;
        if (preserveScroll && targetDetailY > 0) {
          this.isRestoringWorkspaceScroll = true;
          container.style.scrollBehavior = 'auto';
          container.scrollTop = targetDetailY;
          container.scrollTo({ top: targetDetailY, behavior: 'instant' });
          requestAnimationFrame(() => {
            container.scrollTop = targetDetailY;
            requestAnimationFrame(() => {
              if (container.scrollTop !== targetDetailY) {
                container.scrollTop = targetDetailY;
              }
              this.isRestoringWorkspaceScroll = false;
            });
          });
        } else if (!preserveScroll) {
          container.scrollTop = 0;
          container.scrollTo({ top: 0, behavior: 'instant' });
        }
        return;
      }
    }
    // Clean up any Manga Detail window context-menu listeners when leaving detail view
    const win = window as unknown as { _onChapterCtxClick?: (ev: MouseEvent) => void; _onChapterCtxKey?: (ev: KeyboardEvent) => void };
    if (win._onChapterCtxClick) {
      window.removeEventListener('click', win._onChapterCtxClick);
      win._onChapterCtxClick = undefined;
    }
    if (win._onChapterCtxKey) {
      window.removeEventListener('keydown', win._onChapterCtxKey, { capture: true });
      win._onChapterCtxKey = undefined;
    }
    document.getElementById('chapterContextMenu')?.remove();

    switch (this.currentTab) {
      case 'library':
        this.renderLibraryView(container);
        break;
      case 'updates':
        this.renderUpdatesView(container);
        break;
      case 'history':
        this.renderHistoryView(container);
        break;
      case 'downloads':
        this.renderDownloadsView(container);
        break;
      case 'browse':
        this.renderBrowseView(container, preserveScroll);
        break;
      case 'extensions':
        this.renderExtensionsView(container);
        break;
      case 'settings':
        this.renderSettingsView(container);
        break;
    }
    if (preserveScroll) {
      this.restoreWorkspaceScroll();
    }
  }

  public bindCommonCardTriggers(container: HTMLElement): void {
    container.querySelectorAll<HTMLElement>('[data-quick-read]').forEach((btn) => {
      btn.addEventListener('click', (ev) => {
        ev.stopPropagation();
        const mangaId = btn.dataset.quickRead;
        const ch = Number(btn.dataset.ch || '1');
        if (mangaId) {
          this.openReader(mangaId, ch);
        }
      });
    });
    container.querySelectorAll<HTMLElement>('[data-open-detail]').forEach((el) => {
      el.addEventListener('click', () => {
        const mangaId = el.dataset.openDetail;
        if (mangaId) {
          this.openMangaDetail(mangaId);
        }
      });
    });
  }

  public focusActiveSearchInput(): void {
    // Do not clobber Manga Detail or Reading Insights view when pressing Ctrl+K
    if (this.isReaderOpen || this.activeDetailMangaId || this.isLibraryStatsOpen) {
      return;
    }
    if (this.currentTab === 'library' && !this.isLibrarySearchOpen) {
      this.isLibrarySearchOpen = true;
      const box = document.getElementById('libraryExpandSearch');
      if (box) {
        box.classList.remove('animating');
        void box.offsetWidth;
        box.classList.add('open', 'animating');
        window.setTimeout(() => box.classList.remove('animating'), 400);
      } else {
        const viewContainer = document.getElementById('viewContainer');
        if (viewContainer) {
          this.renderLibraryView(viewContainer);
        }
      }
    } else if (this.currentTab === 'browse') {
      if (this.activeBrowseSourceId) {
        const catInput = document.getElementById('sourceCatalogSearchInput') as HTMLInputElement | null;
        if (catInput) {
          catInput.focus({ preventScroll: true });
          catInput.select();
          return;
        }
      } else if (!this.isBrowseSearchOpen) {
        this.isBrowseSearchOpen = true;
        const box = document.getElementById('browseExpandSearch');
        if (box) {
          box.classList.remove('animating');
          void box.offsetWidth;
          box.classList.add('open', 'animating');
          window.setTimeout(() => box.classList.remove('animating'), 400);
        } else {
          const viewContainer = document.getElementById('viewContainer');
          if (viewContainer) {
            this.renderBrowseView(viewContainer);
          }
        }
      }
    } else if (this.currentTab === 'extensions' && !this.isExtensionSearchOpen) {
      this.isExtensionSearchOpen = true;
      const box = document.getElementById('extExpandSearch');
      if (box) {
        box.classList.remove('animating');
        void box.offsetWidth;
        box.classList.add('open', 'animating');
        window.setTimeout(() => box.classList.remove('animating'), 400);
      } else {
        const viewContainer = document.getElementById('viewContainer');
        if (viewContainer) {
          this.renderExtensionsView(viewContainer);
        }
      }
    }
    const activeSearch = document.querySelector<HTMLInputElement>(
      '#sourceCatalogSearchInput, #librarySearchInput, #browseSearchInput, #extSearchInput'
    );
    window.setTimeout(() => {
      if (activeSearch) {
        activeSearch.focus({ preventScroll: true });
        activeSearch.select();
      }
    }, 120);
  }

  private bindGlobalImageFallbacks(): void {
    document.addEventListener(
      'error',
      (ev) => {
        const target = ev.target;
        if (!(target instanceof HTMLImageElement)) return;

        const fallbackSrc = target.getAttribute('data-fallback-src');
        if (fallbackSrc) {
          target.removeAttribute('data-fallback-src');
          if (target.src !== fallbackSrc) {
            target.src = fallbackSrc;
          }
          return;
        }

        if (target.getAttribute('data-fallback-hide') === '1') {
          target.removeAttribute('data-fallback-hide');
          target.style.display = 'none';
          return;
        }

        const fallbackInitials = target.getAttribute('data-fallback-initials');
        if (fallbackInitials !== null) {
          target.removeAttribute('data-fallback-initials');
          const parent = target.parentElement;
          if (parent) {
            parent.style.background = sanitizeCssColor(target.getAttribute('data-fallback-bg'));
            parent.style.color = '#FFFFFF';
            parent.textContent = fallbackInitials;
          }
        }
      },
      true
    );
  }

  public showToast(message: string, iconName?: string): void {
    const container = document.getElementById('toastContainer');
    if (!container) return;
    while (container.children.length >= 3) {
      container.firstElementChild?.remove();
    }
    const pill = document.createElement('div');
    pill.className = 'toast-pill';
    const lower = String(message ?? '').toLowerCase();
    const isAlert =
      lower.includes('dihapus') ||
      lower.includes('terlepas') ||
      lower.includes('diputuskan') ||
      lower.includes('gagal') ||
      lower.includes('failed') ||
      lower.includes('error') ||
      lower.includes('deleted') ||
      lower.includes('removed') ||
      lower.includes('disconnected');
    const icon = iconName || (isAlert ? 'info-circle-linear' : 'check-circle-linear');
    const iconSvg = getIconSvg(icon, { size: 15, color: 'currentColor' });
    pill.innerHTML = `<span class="toast-icon">${iconSvg}</span><span class="toast-text">${escapeXml(message)}</span>`;
    container.appendChild(pill);
    window.setTimeout(() => {
      pill.style.opacity = '0';
      pill.style.transform = 'translateY(6px) scale(0.98)';
      pill.style.transition = 'opacity 0.2s ease-out, transform 0.2s ease-out';
      window.setTimeout(() => pill.remove(), 220);
    }, 3000);
  }

  private activeConfirmCleanup: ((result: boolean) => void) | null = null;

  public showConfirmModal(options: ConfirmModalOptions): Promise<boolean> {
    return new Promise<boolean>((resolve) => {
      // Properly clean up any previous open confirmation modal so its listener & promise never leak
      if (this.activeConfirmCleanup) {
        const prevCleanup = this.activeConfirmCleanup;
        this.activeConfirmCleanup = null;
        prevCleanup(false);
      }
      const existing = document.getElementById('yomicConfirmModalOverlay');
      if (existing) existing.remove();

      const overlay = document.createElement('div');
      overlay.id = 'yomicConfirmModalOverlay';
      overlay.className = 'yomic-confirm-overlay';

      const isDanger = options.isDanger !== false;
      const title = options.title || (isDanger ? this.l('Konfirmasi Tindakan', 'Confirm Action') : this.l('Konfirmasi', 'Confirm'));
      const confirmText = options.confirmText || (isDanger ? this.l('Hapus', 'Delete') : this.l('Konfirmasi', 'Confirm'));
      const cancelText = options.cancelText || this.t('modal.cancel');

      overlay.innerHTML = `
        <div class="yomic-confirm-modal ${isDanger ? 'danger' : 'info'}" role="dialog" aria-modal="true" aria-labelledby="yomicConfirmTitle">
          <div class="yomic-confirm-header">
            <h3 id="yomicConfirmTitle" class="yomic-confirm-title ${isDanger ? 'danger' : 'info'}">${escapeXml(title)}</h3>
          </div>
          <p class="yomic-confirm-message">${escapeXml(options.message)}</p>
          <div class="yomic-confirm-actions">
            <button id="btnYomicConfirmCancel" type="button" class="yomic-confirm-btn cancel">
              ${escapeXml(cancelText)}
            </button>
            <button id="btnYomicConfirmOk" type="button" class="yomic-confirm-btn confirm ${isDanger ? 'danger' : 'primary'}">
              <span>${escapeXml(confirmText)}</span>
            </button>
          </div>
        </div>
      `;

      document.body.appendChild(overlay);

      let settled = false;
      const cleanup = (result: boolean): void => {
        if (settled) return;
        settled = true;
        if (this.activeConfirmCleanup === cleanup) {
          this.activeConfirmCleanup = null;
        }
        document.removeEventListener('keydown', handleKeyDown, { capture: true });
        overlay.classList.add('closing');
        window.setTimeout(() => {
          overlay.remove();
        }, 160);
        resolve(result);
      };
      this.activeConfirmCleanup = cleanup;

      const handleKeyDown = (ev: KeyboardEvent): void => {
        if (ev.key === 'Escape') {
          ev.preventDefault();
          ev.stopPropagation();
          cleanup(false);
        } else if (ev.key === 'Enter') {
          ev.preventDefault();
          ev.stopPropagation();
          const cancelBtn = document.getElementById('btnYomicConfirmCancel');
          const okBtn = document.getElementById('btnYomicConfirmOk');
          if (document.activeElement === cancelBtn) {
            cleanup(false);
            return;
          }
          // Never blindly confirm destructive danger modals via Enter unless the confirm button has explicit focus
          if (isDanger && document.activeElement !== okBtn) {
            return;
          }
          cleanup(true);
        }
      };

      document.addEventListener('keydown', handleKeyDown, { capture: true });

      overlay.addEventListener('click', (ev) => {
        if (ev.target === overlay) {
          cleanup(false);
        }
      });

      document.getElementById('btnYomicConfirmCancel')?.addEventListener('click', () => {
        cleanup(false);
      });

      document.getElementById('btnYomicConfirmOk')?.addEventListener('click', () => {
        cleanup(true);
      });

      // Focus Cancel on destructive modals so accidental Enter never deletes data; focus OK on safe confirm modals
      const initialFocusBtn = document.getElementById(
        isDanger ? 'btnYomicConfirmCancel' : 'btnYomicConfirmOk'
      ) as HTMLButtonElement | null;
      if (initialFocusBtn) {
        window.setTimeout(() => initialFocusBtn.focus(), 50);
      }
    });
  }

  public async loadCategoriesFromBackend(): Promise<CategoryItem[]> {
    if (!this.bridge.isWebViewHost) return this.categories;
    try {
      const cats = await this.bridge.invoke<CategoryItem[]>('library:get-categories');
      if (Array.isArray(cats)) {
        this.categories = cats;
        this.persistLocalCache();
      }
    } catch (err) {
      console.error('Failed to load categories', err);
    }
    return this.categories;
  }

  public async addCategory(name: string): Promise<boolean> {
    const trimmed = name.trim();
    if (!trimmed) return false;
    if (this.bridge.isWebViewHost) {
      try {
        const cats = await this.bridge.invoke<CategoryItem[]>('library:add-category', { name: trimmed });
        if (Array.isArray(cats)) {
          this.categories = cats;
          this.persistLocalCache();
          this.showToast(this.l(`Kategori "${trimmed}" berhasil ditambahkan.`, `Category "${trimmed}" added.`));
          return true;
        }
      } catch (err) {
        this.showToast(this.l(`Gagal menambahkan kategori: ${String(err)}`, `Failed to add category: ${String(err)}`));
        return false;
      }
    } else {
      const newCat: CategoryItem = {
        id: Date.now(),
        name: trimmed,
        color: '#FFFFFF',
        sortOrder: this.categories.length
      };
      this.categories.push(newCat);
      this.persistLocalCache();
      this.showToast(this.l(`Kategori "${trimmed}" berhasil ditambahkan.`, `Category "${trimmed}" added.`));
      return true;
    }
    return false;
  }

  public async deleteCategory(categoryId: number): Promise<boolean> {
    const target = this.categories.find((c) => c.id === categoryId);
    const catName = target ? target.name : this.l('Kategori', 'Category');
    if (this.bridge.isWebViewHost) {
      try {
        const cats = await this.bridge.invoke<CategoryItem[]>('library:delete-category', { categoryId });
        if (Array.isArray(cats)) {
          this.categories = cats;
          for (const m of this.mangaList) {
            if (m.categoryIds) {
              m.categoryIds = m.categoryIds.filter((id) => id !== categoryId);
            }
          }
          if (this.libraryCategoryFilter === categoryId) {
            this.libraryCategoryFilter = 'all';
          }
          this.persistLocalCache();
          this.showToast(this.l(`Kategori "${catName}" telah dihapus.`, `Category "${catName}" deleted.`));
          return true;
        }
      } catch (err) {
        this.showToast(this.l(`Gagal menghapus kategori: ${String(err)}`, `Failed to delete category: ${String(err)}`));
        return false;
      }
    } else {
      this.categories = this.categories.filter((c) => c.id !== categoryId);
      for (const m of this.mangaList) {
        if (m.categoryIds) {
          m.categoryIds = m.categoryIds.filter((id) => id !== categoryId);
        }
      }
      if (this.libraryCategoryFilter === categoryId) {
        this.libraryCategoryFilter = 'all';
      }
      this.persistLocalCache();
      this.showToast(this.l(`Kategori "${catName}" telah dihapus.`, `Category "${catName}" deleted.`));
      return true;
    }
    return false;
  }

  public async setMangaCategories(mangaId: string, categoryIds: number[]): Promise<boolean> {
    const manga = this.mangaList.find((m) => m.id === mangaId);
    const prevCategoryIds = manga ? [...(manga.categoryIds || [])] : null;
    if (manga) {
      manga.categoryIds = [...categoryIds];
    }
    if (this.bridge.isWebViewHost) {
      try {
        await this.bridge.invoke('library:set-manga-categories', {
          mangaId,
          mangaUrl: manga?.url,
          sourceId: manga?.sourceId,
          categoryIds
        });
        this.persistLocalCache();
        this.showToast(this.l('Kategori komik berhasil diperbarui.', 'Comic categories updated.'));
        return true;
      } catch (err) {
        if (manga && prevCategoryIds) {
          manga.categoryIds = prevCategoryIds;
          this.persistLocalCache();
          this.renderCurrentWorkspace(true);
        }
        this.showToast(this.l(`Gagal memperbarui kategori: ${String(err)}`, `Failed to update categories: ${String(err)}`), 'warning');
        return false;
      }
    } else {
      this.persistLocalCache();
      this.showToast(this.l('Kategori komik berhasil diperbarui.', 'Comic categories updated.'));
      return true;
    }
  }

  /* =========================================================================
   * MODULAR VIEW, SERVICE & UTILITY DELEGATIONS
   * ========================================================================= */
  public bindBackendEvents = bindBackendEvents;
  public upsertMangaItem = upsertMangaItem;
  public hydrateInitialState = hydrateInitialState;
  public persistLocalCache = persistLocalCache;
  public bootstrapFromBackend = bootstrapFromBackend;
  public fetchMangaDetailFromBackend = fetchMangaDetailFromBackend;

  public getMangaCoverHtmlSrc = getMangaCoverHtmlSrc;
  public buildSourceLogoHtml = buildSourceLogoHtml;
  public buildSourceFaviconHtml = buildSourceFaviconHtml;

  public applyBackendAnnouncements = applyBackendAnnouncements;
  public closeModalWithAnimation = closeModalWithAnimation;
  public openAnnouncementsModal = openAnnouncementsModal;
  public closeAnnouncementsModal = closeAnnouncementsModal;
  public renderAnnouncementModalBody = renderAnnouncementModalBody;
  public openFeedbackModal = openFeedbackModal;
  public closeFeedbackModal = closeFeedbackModal;
  public bindModalShellEvents = bindModalShellEvents;

  public renderLibraryView = renderLibraryView;
  public triggerLibrarySyncProgress = triggerLibrarySyncProgress;
  public isExplicitManga = isExplicitManga;
  public buildMangaGridCardHtml = buildMangaGridCardHtml;
  public buildMangaListRowHtml = buildMangaListRowHtml;

  public renderUpdatesView = renderUpdatesView;
  public renderHistoryView = renderHistoryView;
  public renderReadingStatisticsView = renderReadingStatisticsView;
  public fetchReadingStatistics = fetchReadingStatistics;
  public computeClientFallbackStatistics = computeClientFallbackStatistics;

  public buildLangBadgeHtml = buildLangBadgeHtml;
  public buildLangFlagsHtml = buildLangFlagsHtml;
  public renderBrowseView = renderBrowseView;
  public fetchSourceCatalogFeed = fetchSourceCatalogFeed;
  public renderSourceCatalogFeed = renderSourceCatalogFeed;

  public renderExtensionsView = renderExtensionsView;

  public renderDownloadsView = renderDownloadsView;
  public enqueueDownload = enqueueDownload;
  public startDownloadProgressTicker = startDownloadProgressTicker;

  public buildMangaDetailHtml = buildMangaDetailHtml;
  public bindMangaDetailEvents = bindMangaDetailEvents;

  public openReader = openReader;
  public retryPageImg = retryPageImg;
  public saveReaderProgressToBackend = saveReaderProgressToBackend;
  public closeReader = closeReader;
  public setReaderMode = setReaderMode;
  public renderReaderPages = renderReaderPages;
  public updateReaderPageIndicator = updateReaderPageIndicator;
  public adjustReaderZoom = adjustReaderZoom;
  public stepReaderChapter = stepReaderChapter;
  public stepReaderPage = stepReaderPage;
  public isReaderFlyoutOpen = isReaderFlyoutOpen;
  public showReaderHud = showReaderHud;
  public hideReaderHud = hideReaderHud;
  public toggleReaderHud = toggleReaderHud;
  public renderReaderChapterDropdown = renderReaderChapterDropdown;
  public toggleReaderAutoScroll = toggleReaderAutoScroll;
  public stopReaderAutoScroll = stopReaderAutoScroll;
  public bindReaderShellEvents = bindReaderShellEvents;
  public syncReaderPageFromScroll = syncReaderPageFromScroll;
  public restoreReaderScrollPosition = restoreReaderScrollPosition;
  public prefetchReaderPagesAround = prefetchReaderPagesAround;

  public renderSettingsView = renderSettingsView;
}

document.addEventListener('DOMContentLoaded', () => {
  const app = new YomicApp();
  app.init();
});
