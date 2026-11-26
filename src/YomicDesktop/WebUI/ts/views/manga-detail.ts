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
  MalSearchResult,
  MangaItem,
  NavTabId,
  ReaderMode,
  ReaderPagesResult,
  SettingsSubTab,
  SourceFeedResult
} from '../types.js';

/* =========================================================================
 * 5. MANGA DETAIL VIEW (MOBILE-STYLE + INLINE REAL-TIME CHAPTER DOWNLOADS)
 * ========================================================================= */

function findChapterDownloadItem(
  downloads: DownloadQueueItem[],
  manga: MangaItem,
  ch: ChapterItem
): DownloadQueueItem | undefined {
  if (!downloads || downloads.length === 0) return undefined;
  return downloads.find((d) => {
    if (ch.url && d.chapterUrl && d.chapterUrl === ch.url) return true;
    if (d.mangaId !== manga.id) return false;
    if (typeof d.chapterNumber === 'number' && Math.abs(d.chapterNumber - ch.number) < 0.01) return true;
    const match = d.chapterLabel?.match(/(\d+(?:\.\d+)?)/);
    if (match && Math.abs(parseFloat(match[1]) - ch.number) < 0.01) return true;
    return false;
  });
}

function buildChapterDlStatusHtml(activeDl: DownloadQueueItem | undefined, isEn: boolean): string {
  if (!activeDl || activeDl.status === 'completed') return '';
  const pct = Math.max(0, Math.min(100, Math.round(Number(activeDl.progressPercent) || 0)));
  if (activeDl.status === 'queued') {
    return `<span class="v1-chapter-meta-sep">•</span><span>${isEn ? 'Queued' : 'Dalam Antrean'}</span>`;
  }
  if (activeDl.status === 'paused') {
    return `<span class="v1-chapter-meta-sep">•</span><span>${isEn ? `Paused ${pct}%` : `Dijeda ${pct}%`}</span>`;
  }
  return `<span class="v1-chapter-meta-sep">•</span><span>${isEn ? `Downloading ${pct}%` : `Mengunduh ${pct}%`}</span>`;
}

function buildChapterDlRingSvg(): string {
  const r = 7;
  const circ = 2 * Math.PI * r; // ~43.98
  return `
    <svg class="v1-ch-dl-spinner" width="16" height="16" viewBox="0 0 20 20" aria-hidden="true">
      <circle class="v1-ch-dl-spinner-arc" cx="10" cy="10" r="${r}" fill="none" stroke-width="2" stroke-linecap="round"
        stroke-dasharray="${(circ * 0.72).toFixed(2)} ${circ.toFixed(2)}" />
    </svg>
  `;
}

function buildChapterDlButtonHtml(
  ch: ChapterItem,
  activeDl: DownloadQueueItem | undefined,
  isEn: boolean
): string {
  if (activeDl && activeDl.status !== 'completed') {
    const isQueued = activeDl.status === 'queued';
    const tip = isEn ? 'Cancel Download' : 'Batalkan Unduhan';
    return `<button class="v1-ch-icon-btn ${isQueued ? 'is-queued' : 'is-downloading'}" data-ch-dl-btn="${ch.number}" data-dl-state="active" data-cancel-ch-dl="${escapeXml(activeDl.id)}" data-dl-ch-num="${ch.number}" data-dl-ch-url="${escapeXml(ch.url || activeDl.chapterUrl || '')}" title="${tip}" aria-label="${tip}">
      ${buildChapterDlRingSvg()}
    </button>`;
  }
  if (ch.isDownloaded || activeDl?.status === 'completed') {
    const tip = isEn ? 'Saved Offline (Click to remove download)' : 'Tersimpan Offline (Klik untuk menghapus unduhan)';
    return `<button class="v1-ch-icon-btn is-downloaded" data-ch-dl-btn="${ch.number}" data-dl-state="completed" data-del-single-ch="${ch.number}" title="${tip}" aria-label="${isEn ? 'Saved Offline' : 'Tersimpan Offline'}">
      ${getIconSvg('download-done', { size: 18, color: '#10B981' })}
    </button>`;
  }
  const tip = isEn ? 'Download Chapter' : 'Unduh Bab';
  return `<button class="v1-ch-icon-btn" data-ch-dl-btn="${ch.number}" data-dl-state="idle" data-dl-single-ch="${ch.number}" title="${tip}" aria-label="${tip}">
    ${getIconSvg('download-2-line', { size: 18 })}
  </button>`;
}

export function updateDetailChapterDownloadsInPlace(
  container: HTMLElement,
  manga: MangaItem,
  downloads: DownloadQueueItem[],
  isEn: boolean
): void {
  const listEl = container.querySelector('.v1-chapters-list');
  if (!listEl) return;

  for (const ch of manga.chapters) {
    const activeDl = findChapterDownloadItem(downloads, manga, ch);
    if (activeDl?.status === 'completed' && !ch.isDownloaded) {
      ch.isDownloaded = true;
    }

    const statusEl = listEl.querySelector<HTMLElement>(`[data-ch-dl-status="${ch.number}"]`);
    if (statusEl) {
      const nextStatusHtml = buildChapterDlStatusHtml(activeDl, isEn);
      if (statusEl.innerHTML !== nextStatusHtml) {
        statusEl.innerHTML = nextStatusHtml;
      }
    }

    const btnEl = listEl.querySelector<HTMLButtonElement>(`[data-ch-dl-btn="${ch.number}"]`);
    if (btnEl) {
      const nextState =
        activeDl && activeDl.status !== 'completed'
          ? 'active'
          : ch.isDownloaded
          ? 'completed'
          : 'idle';
      const curState = btnEl.dataset.dlState || '';
      if (nextState === 'active' && curState === 'active') {
        // Keep spinner running smoothly without resetting CSS animation; sync real download id
        if (activeDl?.id) {
          btnEl.dataset.cancelChDl = activeDl.id;
        }
      } else if (nextState !== curState) {
        const temp = document.createElement('div');
        temp.innerHTML = buildChapterDlButtonHtml(ch, activeDl, isEn);
        const newBtn = temp.firstElementChild as HTMLButtonElement | null;
        if (newBtn) {
          btnEl.replaceWith(newBtn);
        }
      }
    }
  }
  manga.downloadedCount = manga.chapters.filter((c) => c.isDownloaded).length;
}

export function buildMangaDetailHtml(this: YomicApp, manga: MangaItem): string {
  const fallbackSvg = buildMangaCoverSvg(manga);
  const coverSrc = this.getMangaCoverHtmlSrc(manga);
  const latestChapter = manga.chapters.length > 0
    ? manga.chapters.reduce((prev, curr) => (curr.number > prev.number ? curr : prev), manga.chapters[0])
    : null;

  let chapters = [...manga.chapters];
  if (this.detailChapterFilter === 'unread') {
    chapters = chapters.filter((c) => !c.isRead);
  } else if (this.detailChapterFilter === 'bookmarked') {
    chapters = chapters.filter((c) => c.isBookmarked);
  }
  if (this.detailChapterAsc) {
    chapters.reverse();
  }

  const isOngoing = manga.status === 'Ongoing';
  const isHiatus = manga.status === 'Hiatus';
  const statusColor = isOngoing ? '#E53935' : 'var(--ink-secondary)';
  const statusText = isOngoing
    ? this.l('Berjalan', 'Ongoing')
    : isHiatus
    ? this.l('Hiatus', 'Hiatus')
    : this.l('Tamat', 'Completed');

  const rawAuthor = (manga.author || 'Unknown').trim();
  const rawArtist = (manga.artist || '').trim();
  const authorDisplay =
    !rawArtist ||
    rawArtist.toLowerCase() === rawAuthor.toLowerCase() ||
    rawArtist.toLowerCase() === 'unknown' ||
    rawArtist.toLowerCase() === 'studio'
      ? rawAuthor
      : `${rawAuthor} (${rawArtist})`;

  const hasAnyDownloaded = manga.chapters.some((c) => c.isDownloaded);
  const lowestChapterNum =
    manga.chapters.length > 0
      ? manga.chapters.reduce((min, c) => (c.number < min ? c.number : min), manga.chapters[0].number)
      : 1;
  const resumeChapterNum = manga.lastReadChapter > 0 ? manga.lastReadChapter : lowestChapterNum;
  const resumeFabLabel =
    manga.lastReadChapter > 0
      ? this.l(`Lanjut Ch. ${resumeChapterNum}`, `Resume Ch. ${resumeChapterNum}`)
      : this.l(`Mulai Ch. ${resumeChapterNum}`, `Start Ch. ${resumeChapterNum}`);

  // Bookmark / Saved Icon for Library Toggle: Solar Bold when in library, Linear outline when not
  const librarySvg = manga.inLibrary
    ? getIconSvg('bookmark-bold', { size: 24 })
    : getIconSvg('bookmark', { size: 24 });
  const shouldBlurCover = this.settings.blurExplicitCovers && this.isExplicitManga(manga);
  return `
    <div class="v1-detail-wrap">
      <!-- AMBIENT BACKGROUND -->
      <div class="v1-ambient-bg">
        <img class="v1-ambient-img" src="${sanitizeImageSrc(coverSrc) || escapeXml(fallbackSvg)}" alt="" data-fallback-src="${escapeXml(fallbackSvg)}" />
        <div class="v1-ambient-black-overlay"></div>
        <div class="v1-ambient-fade-gradient"></div>
      </div>
      <!-- FOREGROUND INTERACTIVE CONTENT -->
      <div class="v1-detail-foreground">
        <!-- Mobile-Style Top Bar with Borderless Back Arrow -->
        <div class="v1-detail-topbar">
          <button id="btnBackFromDetail" class="v1-detail-back-btn" title="${this.l('Kembali (Esc)', 'Back (Esc)')}" aria-label="${this.t('modal.close')}">
            ${getIconSvg('arrow-left-line', { size: 20 })}
          </button>
        </div>

      <!-- HEADER CONTENT (2-Column Grid: Cover Left + Info & 5-Action Grid Right) -->
      <div class="v1-header-grid">
        <!-- Left Column: Sharp Cover Image -->
        <div class="v1-cover-border">
          <img class="v1-cover-img${shouldBlurCover ? ' is-blurred' : ''}" src="${sanitizeImageSrc(coverSrc) || escapeXml(fallbackSvg)}" alt="${escapeXml(manga.title)}" data-fallback-src="${escapeXml(fallbackSvg)}" />
        </div>
        <!-- Right Column: Title, Vertical Meta Stack, 5-Col Action Grid, Genres, Expandable Synopsis -->
        <div class="v1-info-col">
          <div class="v1-title-row">
            <h2 class="v1-manga-title">${escapeXml(manga.title)}</h2>
          </div>
          <!-- Author, Status, Source Vertical Stack -->
          <div class="v1-meta-stack">
            <!-- Author -->
            <div class="v1-meta-row">
              ${getIconSvg('user-line', { size: 15 })}
              <span>${escapeXml(authorDisplay)}</span>
            </div>
            <!-- Status with Timer Icon -->
            <div class="v1-meta-row" style="color:${statusColor};">
              ${getIconSvg('time-line', { size: 15, color: statusColor })}
              <span>${escapeXml(statusText)}</span>
            </div>
            <!-- Source Info -->
            <div class="v1-meta-row">
              ${(() => {
                const matched = this.extensions.find((e) => String(e.sourceId) === String(manga.sourceId) || e.name.toLowerCase() === manga.sourceName.toLowerCase());
                return matched ? this.buildSourceLogoHtml(matched, 18) : `<span class="v1-source-badge">${escapeXml(manga.sourceName.slice(0, 2).toUpperCase())}</span>`;
              })()}
              <span>${escapeXml(manga.sourceName)}</span>
            </div>
          </div>
          <!-- Action Buttons (5 Columns matching Mobile Image 2: Di Pustaka, Lacak, Browser, Unduh, Segarkan) -->
          <div class="v1-action-grid v1-action-grid-5">
            <!-- 1. Library Toggle -->
            <button id="btnToggleInLibrary" class="v1-action-btn ${manga.inLibrary ? 'is-active' : ''}">
              ${librarySvg}
              <span>${manga.inLibrary ? this.t('detail.inLibrary') : this.t('detail.addToLibrary')}</span>
            </button>
            <!-- 2. Track (MyAnimeList) -->
            <button id="btnOpenTrackerPopup" class="v1-action-btn">
              ${getIconSvg('checkbox-circle-line', { size: 24 })}
              <span>${this.l('Lacak', 'Track')}</span>
            </button>
            <!-- 3. Open in Browser -->
            <button id="btnOpenWebViewSource" class="v1-action-btn">
              ${getIconSvg('globe', { size: 24 })}
              <span>Browser</span>
            </button>
            <!-- 4. Download All -->
            <button id="btnDownloadAllChapters" class="v1-action-btn">
              ${getIconSvg('download-2-line', { size: 24 })}
              <span>${this.l('Unduh', 'Download')}</span>
            </button>
            <!-- 5. Refresh -->
            <button id="btnRefreshMangaDetail" class="v1-action-btn">
              ${getIconSvg('refresh-line', { size: 24 })}
              <span>${this.t('common.refresh')}</span>
            </button>
          </div>
          <!-- GENRES WrapPanel (Below Action Grid, Above Synopsis matching Image 2) -->
          <div class="v1-genres-wrap">
            ${manga.genres.map((g) => `<span class="v1-genre-pill">${escapeXml(g)}</span>`).join('')}
          </div>
          <!-- Description / Synopsis -->
          <div class="v1-synopsis-heading">${this.l('Sinopsis', 'Synopsis')}</div>
          <div class="v1-synopsis-body ${this.isSynopsisExpanded ? '' : 'collapsed'}">
            ${escapeXml(manga.synopsis)}
          </div>
          <button id="btnToggleSynopsis" class="v1-synopsis-toggle" title="${escapeXml(this.isSynopsisExpanded ? this.l('Ciutkan Sinopsis', 'Collapse Synopsis') : this.l('Perluas Sinopsis', 'Expand Synopsis'))}" aria-label="${escapeXml(this.l('Perluas atau ciutkan sinopsis', 'Expand or collapse synopsis'))}">
            ${getIconSvg(this.isSynopsisExpanded ? 'chevron-up' : 'chevron-down', { size: 20 })}
          </button>
        </div>
      </div>
      <!-- CHAPTERS DIVIDER (Title + Count Badge Left | Filter & Sort Right) -->
      <div class="v1-chapters-divider">
        <div class="v1-chapters-left">
          <span class="v1-chapters-title">${this.l('Daftar Chapter', 'Chapters')}</span>
          <span class="v1-chapters-count-badge tabular-nums">(${chapters.length})</span>
        </div>
        <div class="v1-chapters-controls">
          <!-- 1. Filter Modal Trigger Button -->
          <button id="btnToggleFilterFlyout" class="v1-chapter-ctrl-btn ${this.detailChapterFilter !== 'all' ? 'has-filter' : ''}" title="${escapeXml(this.l('Filter Chapter', 'Filter Chapters'))}" aria-label="${escapeXml(this.l('Filter Chapter', 'Filter Chapters'))}">
            ${getIconSvg('filter', { size: 16 })}
          </button>
          <!-- 2. Sort Button (Icon only with smooth animated flip) -->
          <button id="btnToggleChOrder" class="v1-chapter-ctrl-btn ${this.detailChapterAsc ? 'asc' : 'desc'}" title="${this.detailChapterAsc ? this.l('Urutan: Terlama Dahulu (Klik untuk Terbaru)', 'Order: Oldest First (Click for Latest)') : this.l('Urutan: Terbaru Dahulu (Klik untuk Terlama)', 'Order: Latest First (Click for Oldest)')}" aria-label="${this.detailChapterAsc ? this.l('Urutkan Terlama Dahulu', 'Sort Oldest First') : this.l('Urutkan Terbaru Dahulu', 'Sort Latest First')}">
            <span class="v1-ch-sort-icon">
              ${getIconSvg('arrow-up-down-line', { size: 16 })}
            </span>
          </button>
        </div>
      </div>
      <!-- UNIFIED FLAT CHAPTER LIST (Mobile Style Image 2) -->
      <div class="v1-chapters-list">
        ${
          this.isDetailLoading && chapters.length === 0
            ? Array.from({ length: 8 })
                .map(
                  () => `
                  <div class="v1-skeleton-chapter-row" aria-hidden="true">
                    <div class="v1-skeleton-chapter-left">
                      <div class="v1-skeleton-line v1-skeleton-shimmer" style="width: 42%; height: 13px;"></div>
                      <div class="v1-skeleton-line v1-skeleton-shimmer" style="width: 22%; height: 10px;"></div>
                    </div>
                    <div class="v1-skeleton-chapter-right">
                      <div class="v1-skeleton-dot v1-skeleton-shimmer"></div>
                      <div class="v1-skeleton-dot v1-skeleton-shimmer"></div>
                    </div>
                  </div>`
                )
                .join('')
            : chapters
          .map((ch: ChapterItem) => {
            const isNewRelease =
              latestChapter != null &&
              (ch.id != null ? ch.id === latestChapter.id : ch.number === latestChapter.number) &&
              !ch.isRead;
            const isLastRead = manga.lastReadChapter > 0 && ch.number === manga.lastReadChapter;

            // Compute saved page progress label ("Hal. 16/25") like Mobile Image 2
            let savedPage = 0;
            let savedTotalPages = 0;
            try {
              const localRaw = localStorage.getItem(`yomic_ch_pos_${manga.id}_${ch.number}`);
              if (localRaw) {
                const parsed = JSON.parse(localRaw);
                if (typeof parsed.page === 'number' && parsed.page > 0) {
                  savedPage = parsed.page;
                }
                if (typeof parsed.totalPages === 'number' && parsed.totalPages > 0) {
                  savedTotalPages = parsed.totalPages;
                }
              }
            } catch {}
            if (savedPage <= 0 && ch.lastPageRead && ch.lastPageRead > 0 && ch.lastPageRead <= 1000) {
              savedPage = ch.lastPageRead;
            }
            if (savedPage <= 0 && isLastRead && manga.lastReadPage && manga.lastReadPage > 0) {
              savedPage = manga.lastReadPage;
              if (savedTotalPages <= 0 && manga.lastReadTotalPages && manga.lastReadTotalPages > 0) {
                savedTotalPages = manga.lastReadTotalPages;
              }
            }
            const pageProgressText =
              !ch.isRead && savedPage > 1 && savedTotalPages > 1
                ? this.l(`Hal. ${savedPage}/${savedTotalPages}`, `P. ${savedPage}/${savedTotalPages}`)
                : !ch.isRead && savedPage > 1
                ? this.l(`Hal. ${savedPage}`, `P. ${savedPage}`)
                : '';

            const activeDl = findChapterDownloadItem(this.downloads, manga, ch);
            const isEn = document.documentElement.lang === 'en';
            const dlStatusHtml = buildChapterDlStatusHtml(activeDl, isEn);

            return `
              <div class="v1-chapter-row ${ch.isRead ? 'is-read' : ''} ${isLastRead ? 'is-last-read' : ''}" data-read-ch="${ch.number}">
                <div class="v1-chapter-main">
                  <div class="v1-chapter-title-row">
                    <span class="v1-chapter-title">Chapter ${ch.number}${ch.title ? ` — ${escapeXml(ch.title)}` : ''}</span>
                    ${isNewRelease ? `<span class="v1-ch-new-pill">${this.l('Baru', 'New')}</span>` : ''}
                  </div>
                  <div class="v1-chapter-meta-sub">
                    <span>${escapeXml(ch.releaseDate)}</span>
                    ${pageProgressText ? `<span class="v1-chapter-meta-sep">•</span><span>${escapeXml(pageProgressText)}</span>` : ''}
                    <span class="v1-ch-dl-status" data-ch-dl-status="${ch.number}">${dlStatusHtml}</span>
                    ${ch.scanlator ? `<span class="v1-chapter-meta-sep">•</span><span>${escapeXml(ch.scanlator)}</span>` : ''}
                  </div>
                </div>
                <div class="v1-chapter-actions">
                  <!-- Bookmark Toggle Star -->
                  <button class="v1-ch-icon-btn ${ch.isBookmarked ? 'bookmarked' : ''}" data-toggle-bookmark="${ch.number}" title="${ch.isBookmarked ? this.l('Hapus Penanda', 'Remove Bookmark') : this.l('Tandai Chapter', 'Bookmark Chapter')}" aria-label="${this.l('Alihkan Penanda', 'Toggle Bookmark')}">
                    ${getIconSvg(ch.isBookmarked ? 'bookmark-bold' : 'bookmark', { size: 18, color: ch.isBookmarked ? 'var(--ocean-primary)' : 'currentColor' })}
                  </button>
                  <!-- Download / Live Circular Progress / Offline Saved Indicator -->
                  ${buildChapterDlButtonHtml(ch, activeDl, isEn)}
                  <!-- Chapter More Options Button -->
                  <button class="v1-ch-icon-btn v1-ch-more-btn" data-ch-more="${ch.number}" title="${this.l('Opsi Bab', 'Chapter Options')}" aria-label="${this.l('Opsi Bab', 'Chapter Options')}">
                    ${getIconSvg('more-vertical', { size: 16 })}
                  </button>
                </div>
              </div>`;
          })
          .join('')}
      </div>
      </div>

      <!-- Floating Bottom-Right Resume Action Button (Matching Mobile Image 2) -->
      ${
        manga.chapters.length > 0
          ? `<button type="button" class="v1-detail-resume-fab" data-quick-read="${escapeXml(manga.id)}" data-ch="${resumeChapterNum}">
               ${getIconSvg('play-linear', { size: 17 })}
               <span>${resumeFabLabel}</span>
             </button>`
          : ''
      }

      <!-- Chapter Filter Centered Modal (rendered outside .v1-detail-foreground so it always stacks above Resume FAB) -->
      <div id="chapterFilterModalBackdrop" class="v1-mal-backdrop ${this.isChapterFilterFlyoutOpen ? 'open' : ''}">
        <div class="v1-setting-select-modal v1-unified-filter-dialog" role="dialog" aria-modal="true" aria-label="${escapeXml(this.l('Filter Chapter', 'Filter Chapters'))}">
          <div class="v1-unified-filter-head">
            <h3 class="v1-setting-select-title">${this.l('Filter Chapter', 'Filter Chapters')}</h3>
            <button type="button" id="btnCloseChapterFilterModal" class="v1-unified-filter-close" aria-label="${escapeXml(this.t('modal.close'))}">
              ${getIconSvg('close', { size: 18 })}
            </button>
          </div>
          <div class="v1-setting-select-list" role="listbox">
            <button type="button" class="v1-setting-select-option ${this.detailChapterFilter === 'all' ? 'selected' : ''}" data-ch-filter="all" role="option" aria-selected="${this.detailChapterFilter === 'all' ? 'true' : 'false'}">
              <span class="v1-setting-select-check">
                ${this.detailChapterFilter === 'all' ? getIconSvg('check', { size: 16, strokeWidth: 2.4 }) : ''}
              </span>
              <span class="v1-setting-select-label">${this.l('Semua Bab', 'All Chapters')}</span>
            </button>
            <button type="button" class="v1-setting-select-option ${this.detailChapterFilter === 'unread' ? 'selected' : ''}" data-ch-filter="unread" role="option" aria-selected="${this.detailChapterFilter === 'unread' ? 'true' : 'false'}">
              <span class="v1-setting-select-check">
                ${this.detailChapterFilter === 'unread' ? getIconSvg('check', { size: 16, strokeWidth: 2.4 }) : ''}
              </span>
              <span class="v1-setting-select-label">${this.libraryFilterMode ? this.t('library.filterUnread') : this.l('Belum Dibaca', 'Unread')}</span>
            </button>
            <button type="button" class="v1-setting-select-option ${this.detailChapterFilter === 'bookmarked' ? 'selected' : ''}" data-ch-filter="bookmarked" role="option" aria-selected="${this.detailChapterFilter === 'bookmarked' ? 'true' : 'false'}">
              <span class="v1-setting-select-check">
                ${this.detailChapterFilter === 'bookmarked' ? getIconSvg('check', { size: 16, strokeWidth: 2.4 }) : ''}
              </span>
              <span class="v1-setting-select-label">${this.l('Ditandai', 'Bookmarked')}</span>
            </button>
          </div>
        </div>
      </div>

      <!-- MyAnimeList Tracker Edit / Search Dialog — Clean & Deslopped -->
      <div id="malTrackerModalBackdrop" class="v1-mal-backdrop ${this.isMalTrackerPopupOpen ? 'open' : ''}">
        <div class="v1-mal-dialog" role="dialog" aria-modal="true" aria-labelledby="malModalTitle">
          ${renderMalModalContent.call(this, manga)}
        </div>
      </div>
    </div>
  `;
}

function cleanMangaTitleForMalSearch(title: string): string {
  if (!title) return '';
  return title
    .replace(/\s*(\(.*\)|\[.*\])\s*$/g, '')
    .replace(/\s*(bahasa\s+indonesia|indonesia|raw|colored|color)\s*$/gi, '')
    .trim();
}

function renderMalDropdown(
  id: string,
  options: { value: string; label: string }[],
  selectedValue: string,
  ariaLabel: string,
  openUpward = false
): string {
  const activeOption = options.find((opt) => opt.value === selectedValue) || options[0];
  return `
    <div class="v1-mal-dropdown ${openUpward ? 'open-up' : ''}" data-dropdown-for="${escapeXml(id)}">
      <input type="hidden" id="${escapeXml(id)}" value="${escapeXml(activeOption?.value ?? selectedValue)}" />
      <button type="button" class="v1-mal-dropdown-trigger" aria-haspopup="listbox" aria-expanded="false" aria-label="${escapeXml(ariaLabel)}">
        <span class="v1-mal-dropdown-value">${escapeXml(activeOption?.label ?? '')}</span>
        <span class="v1-mal-dropdown-arrow">${getIconSvg('alt-arrow-down-linear', { size: 16 })}</span>
      </button>
      <div class="v1-mal-dropdown-menu" role="listbox">
        ${options
          .map((opt) => {
            const isSelected = opt.value === (activeOption?.value ?? selectedValue);
            return `
              <button type="button" class="v1-mal-dropdown-option ${isSelected ? 'selected' : ''}" data-value="${escapeXml(opt.value)}" role="option" aria-selected="${isSelected ? 'true' : 'false'}">
                <span>${escapeXml(opt.label)}</span>
                ${isSelected ? `<span class="v1-mal-dropdown-check">${getIconSvg('check', { size: 14 })}</span>` : ''}
              </button>
            `;
          })
          .join('')}
      </div>
    </div>
  `;
}

function setMalDropdownValue(id: string, value: string): void {
  const input = document.getElementById(id) as HTMLInputElement | null;
  if (!input) return;
  const dropdown = input.closest('.v1-mal-dropdown');
  if (!dropdown) {
    input.value = value;
    return;
  }
  const options = Array.from(dropdown.querySelectorAll<HTMLButtonElement>('.v1-mal-dropdown-option'));
  const target = options.find((opt) => opt.dataset.value === value) || options[0];
  if (!target) return;
  input.value = target.dataset.value ?? value;
  const valueSpan = dropdown.querySelector<HTMLElement>('.v1-mal-dropdown-value');
  const labelText = target.querySelector('span')?.textContent ?? '';
  if (valueSpan) valueSpan.textContent = labelText;

  options.forEach((opt) => {
    const selected = opt === target;
    opt.classList.toggle('selected', selected);
    opt.setAttribute('aria-selected', selected ? 'true' : 'false');
    const existingCheck = opt.querySelector('.v1-mal-dropdown-check');
    if (selected && !existingCheck) {
      opt.insertAdjacentHTML('beforeend', `<span class="v1-mal-dropdown-check">${getIconSvg('check', { size: 14 })}</span>`);
    } else if (!selected && existingCheck) {
      existingCheck.remove();
    }
  });
}

function renderMalModalContent(this: YomicApp, manga: MangaItem): string {
  if (!this.settings.isMalConnected) {
    return `
      <div class="v1-mal-header">
        <div class="v1-mal-title-wrap">
          <span id="malModalTitle" class="v1-mal-title">${this.l('Pelacakan MyAnimeList', 'MyAnimeList Tracking')}</span>
        </div>
        <button id="btnCloseMalTracker" class="v1-mal-close-btn" aria-label="${escapeXml(this.t('modal.close'))}">
          ${getIconSvg('close-linear', { size: 18 })}
        </button>
      </div>
      <div class="v1-mal-not-connected-box">
        <div class="v1-mal-warning-icon">
          ${getIconSvg('alert-triangle', { size: 36, color: '#F59E0B' })}
        </div>
        <div class="v1-mal-warning-title">${this.l('Akun MyAnimeList Belum Terhubung', 'MyAnimeList Account Not Connected')}</div>
        <p class="v1-mal-warning-text">
          ${this.l(
            'Hubungkan akun MyAnimeList Anda di menu Pengaturan untuk mengaktifkan sinkronisasi status membaca, progres bab, dan penilaian skor secara otomatis.',
            'Connect your MyAnimeList account in Settings to enable automatic syncing of reading status, chapter progress, and score.'
          )}
        </p>
        <button id="btnMalGoToSettings" class="v1-mal-btn-primary" type="button">
          ${this.l('Buka Pengaturan', 'Open Settings')}
        </button>
      </div>
    `;
  }

  if (this.isMalSearchMode) {
    const queryVal = this.malSearchQuery || cleanMangaTitleForMalSearch(manga.title);
    return `
      <div class="v1-mal-header">
        <div class="v1-mal-title-wrap">
          ${(manga.malTracked || this.malSelectedEntry) ? `
            <button id="btnMalBackToEdit" class="v1-mal-back-btn" title="${escapeXml(this.l('Kembali ke formulir status', 'Back to status form'))}" aria-label="${escapeXml(this.t('detail.backToLibrary'))}">
              ${getIconSvg('alt-arrow-left-linear', { size: 18 })}
            </button>
          ` : ''}
          <span id="malModalTitle" class="v1-mal-title">${this.l('Cari di MyAnimeList', 'Search MyAnimeList')}</span>
        </div>
        <button id="btnCloseMalTracker" class="v1-mal-close-btn" aria-label="${escapeXml(this.t('modal.close'))}">
          ${getIconSvg('close-linear', { size: 18 })}
        </button>
      </div>
      <div class="v1-mal-subtitle" title="${escapeXml(manga.title)}">${this.l('Mencari entri untuk:', 'Searching entry for:')} <strong>${escapeXml(manga.title)}</strong></div>

      <div class="v1-mal-search-bar">
        <div class="v1-mal-search-input-wrap">
          <span class="v1-mal-search-icon">${getIconSvg('search', { size: 16 })}</span>
          <input id="malSearchQueryInput" type="text" class="v1-mal-search-input" placeholder="${escapeXml(this.t('detail.malSearchPlaceholder'))}" value="${escapeXml(queryVal)}" autocomplete="off" />
        </div>
        <button id="btnExecuteMalSearch" class="v1-mal-btn-search" type="button" ${this.isMalSearching ? 'disabled' : ''}>
          ${this.isMalSearching ? this.l('Mencari...', 'Searching...') : this.t('detail.malSearchBtn')}
        </button>
      </div>

      <div class="v1-mal-results-area">
        ${this.isMalSearching ? `
          <div class="v1-mal-state-box">
            <div class="v1-spinner"></div>
            <span>${this.t('detail.malSearching')}</span>
          </div>
        ` : this.malSearchError ? `
          <div class="v1-mal-state-box error">
            <span>${escapeXml(this.malSearchError)}</span>
            <button id="btnRetryMalSearch" class="v1-mal-btn-retry" type="button">${this.t('reader.retryPage')}</button>
          </div>
        ` : this.malSearchResults.length === 0 ? `
          <div class="v1-mal-state-box empty">
            <span>${this.l('Tidak ada manga ditemukan. Coba gunakan judul alternatif dalam bahasa Inggris atau Romaji.', 'No manga found. Try using an alternative English or Romaji title.')}</span>
          </div>
        ` : `
          <div class="v1-mal-results-list">
            ${this.malSearchResults.map((r) => `
              <div class="v1-mal-result-card" data-select-mal-id="${r.id}" data-select-mal-title="${escapeXml(r.title)}" data-select-mal-cover="${escapeXml(r.coverUrl || '')}">
                <div class="v1-mal-result-thumb-wrap">
                  ${r.coverUrl
                    ? `<img class="v1-mal-result-thumb" src="${sanitizeImageSrc(r.coverUrl)}" alt="${escapeXml(r.title)}" loading="lazy" data-fallback-hide="1" />`
                    : `<div class="v1-mal-result-no-thumb">📖</div>`
                  }
                </div>
                <div class="v1-mal-result-info">
                  <div class="v1-mal-result-title" title="${escapeXml(r.title)}">${escapeXml(r.title)}</div>
                  <div class="v1-mal-result-meta">
                    <span class="v1-mal-id-badge">MAL ID: #${r.id}</span>
                    ${
                      r.inUserList
                        ? `<span class="v1-mal-inlist-badge">${this.l(
                            `Di Daftar MAL${r.userChaptersRead ? ` • Ch. ${r.userChaptersRead}` : ''}`,
                            `In MAL List${r.userChaptersRead ? ` • Ch. ${r.userChaptersRead}` : ''}`
                          )}</span>`
                        : ''
                    }
                  </div>
                </div>
                <button class="v1-mal-btn-select" type="button" data-select-mal-id="${r.id}" data-select-mal-title="${escapeXml(r.title)}" data-select-mal-cover="${escapeXml(r.coverUrl || '')}">
                  ${this.t('detail.malSelectBtn')}
                </button>
              </div>
            `).join('')}
          </div>
        `}
      </div>
    `;
  }

  // Edit Mode
  const activeMalId = this.malSelectedEntry?.id || manga.malId || 0;
  const activeTitle = this.malSelectedEntry?.title || manga.malTitle || manga.title;
  const activeStatus = manga.malStatus || 'reading';
  const activeChaptersRead = manga.lastReadChapter ?? 0;
  const activeScore = typeof manga.malScore === 'number' ? manga.malScore : 0;

  return `
    <div class="v1-mal-header">
      <div class="v1-mal-title-wrap">
        <span id="malModalTitle" class="v1-mal-title">${this.l('Status Pelacakan', 'Tracking Status')}</span>
      </div>
      <button id="btnCloseMalTracker" class="v1-mal-close-btn" aria-label="${escapeXml(this.t('modal.close'))}">
        ${getIconSvg('close-linear', { size: 18 })}
      </button>
    </div>
    <div class="v1-mal-subtitle" title="${escapeXml(manga.title)}">${escapeXml(manga.title)}</div>

    <div class="v1-mal-linked-card">
      <div class="v1-mal-linked-info">
        <span class="v1-mal-linked-label">${this.l('Tertaut ke MyAnimeList:', 'Linked to MyAnimeList:')}</span>
        <span class="v1-mal-linked-title" title="${escapeXml(activeTitle)}">${escapeXml(activeTitle)}</span>
        <span class="v1-mal-linked-id">MAL ID: #${activeMalId}</span>
      </div>
      <button id="btnMalChangeManga" class="v1-mal-btn-change" type="button" title="${escapeXml(this.l('Ganti entri judul di MyAnimeList', 'Change MyAnimeList title entry'))}">
        ${this.l('Ganti Entri', 'Change Entry')}
      </button>
    </div>

    <!-- Field 1: Status Membaca -->
    <div class="v1-mal-field">
      <label class="v1-mal-label">${this.t('detail.malStatusLabel')}</label>
      ${renderMalDropdown(
        'malStatusSelect',
        [
          { value: 'reading', label: this.l('Sedang Membaca (Reading)', 'Reading') },
          { value: 'completed', label: this.l('Selesai Dibaca (Completed)', 'Completed') },
          { value: 'on_hold', label: this.l('Ditunda (On Hold)', 'On Hold') },
          { value: 'dropped', label: this.l('Dibatalkan (Dropped)', 'Dropped') },
          { value: 'plan_to_read', label: this.l('Rencana Dibaca (Plan to Read)', 'Plan to Read') }
        ],
        activeStatus,
        this.t('detail.malStatusLabel'),
        false
      )}
    </div>

    <!-- Field 2: Kemajuan Bab (Progress) -->
    <div class="v1-mal-field">
      <label class="v1-mal-label" for="malChaptersReadInput">${this.t('detail.malChapterLabel')}</label>
      <div class="v1-mal-stepper-container">
        <div class="v1-mal-stepper-box">
          <button id="btnMalDecCh" class="v1-mal-stepper-btn" aria-label="${escapeXml(this.l('Kurangi Bab', 'Decrease Chapter'))}">${getIconSvg('minus', { size: 15 })}</button>
          <input id="malChaptersReadInput" type="text" inputmode="numeric" class="v1-mal-stepper-input tabular-nums" value="${activeChaptersRead}" />
          <button id="btnMalIncCh" class="v1-mal-stepper-btn" aria-label="${escapeXml(this.l('Tambah Bab', 'Increase Chapter'))}">${getIconSvg('plus', { size: 15 })}</button>
        </div>
        <div class="v1-mal-total-badge tabular-nums">/ ${manga.totalChapters} ${this.l('bab', 'ch.')}</div>
      </div>
    </div>

    <!-- Field 3: Skor / Penilaian -->
    <div class="v1-mal-field">
      <label class="v1-mal-label">${this.t('detail.malScoreLabel')}</label>
      ${renderMalDropdown(
        'malScoreSelect',
        [
          { value: '10', label: `10 — ${this.l('Mahakarya (Masterpiece)', 'Masterpiece')}` },
          { value: '9', label: `9 — ${this.l('Hebat (Great)', 'Great')}` },
          { value: '8', label: `8 — ${this.l('Sangat Bagus (Very Good)', 'Very Good')}` },
          { value: '7', label: `7 — ${this.l('Bagus (Good)', 'Good')}` },
          { value: '6', label: `6 — ${this.l('Cukup Baik (Fine)', 'Fine')}` },
          { value: '5', label: `5 — ${this.l('Rata-rata (Average)', 'Average')}` },
          { value: '4', label: `4 — ${this.l('Kurang (Bad)', 'Bad')}` },
          { value: '3', label: `3 — ${this.l('Sangat Kurang (Very Bad)', 'Very Bad')}` },
          { value: '2', label: `2 — ${this.l('Mengerikan (Horrible)', 'Horrible')}` },
          { value: '1', label: `1 — ${this.l('Tak Tertolong (Appalling)', 'Appalling')}` },
          { value: '0', label: this.l('Belum Dinilai (No Score)', 'No Score') }
        ],
        String(activeScore),
        this.t('detail.malScoreLabel'),
        true
      )}
    </div>

    <!-- Footer: Aksi Aman dengan Pemisahan Destruktif -->
    <div class="v1-mal-footer">
      ${manga.malTracked ? `
        <button id="btnUnbindMalTracker" class="v1-mal-btn-danger" type="button" title="${escapeXml(this.l('Putuskan koneksi pelacakan komik ini dari MyAnimeList', 'Disconnect this comic from MyAnimeList'))}">
          ${this.t('detail.malUnlinkBtn')}
        </button>
      ` : '<div></div>'}
      <div class="v1-mal-footer-actions">
        ${manga.malTracked ? `
          <button id="btnSyncMalTracker" class="v1-mal-btn-secondary" type="button" title="${escapeXml(this.l('Sinkronkan data dari MyAnimeList', 'Sync data from MyAnimeList'))}">
            ${this.l('Sinkronkan', 'Sync')}
          </button>
        ` : ''}
        <button id="btnSaveMalTracker" class="v1-mal-btn-primary" type="button">
          ${this.t('modal.save')}
        </button>
      </div>
    </div>
  `;
}

export function bindMangaDetailEvents(this: YomicApp, manga: MangaItem): void {
  document.getElementById('btnBackFromDetail')?.addEventListener('click', () => {
    this.activeDetailMangaId = null;
    this.isChapterFilterFlyoutOpen = false;
    this.isMalTrackerPopupOpen = false;
    this.renderCurrentWorkspace(true);
  });
  document.getElementById('btnToggleInLibrary')?.addEventListener('click', () => {
    const prevId = manga.id;
    const prevInLibrary = manga.inLibrary;
    manga.inLibrary = !prevInLibrary;
    this.updateSidebarCounters();
    if (this.bridge.isWebViewHost) {
      void this.bridge
        .invoke<MangaItem>('manga:toggle-library', {
          mangaId: manga.id,
          dbId: manga.dbId,
          sourceId: Number(manga.sourceId) || 0,
          url: manga.url
        })
        .then((updated) => {
          if (updated) {
            const merged = this.upsertMangaItem(updated);
            this.persistLocalCache();
            this.updateSidebarCounters();
            // Re-render detail cleanly (preserving scroll) when object identity/ID changed (e.g., ext-... -> db-42) so closures never go stale
            if (this.activeDetailMangaId === merged.id && (merged.id !== prevId || merged.inLibrary !== manga.inLibrary)) {
              this.renderCurrentWorkspace(true);
            }
          } else {
            this.updateSidebarCounters();
          }
        })
        .catch(() => {
          manga.inLibrary = prevInLibrary;
          this.persistLocalCache();
          this.updateSidebarCounters();
          this.showToast(this.l('Gagal memperbarui status pustaka.', 'Failed to update library status.'), 'warning');
          if (this.activeDetailMangaId === manga.id) {
            this.renderCurrentWorkspace(true);
          }
        });
    }
    this.showToast(
      manga.inLibrary
        ? this.l(`${manga.title} ditambahkan ke Pustaka.`, `${manga.title} added to Library.`)
        : this.l(`${manga.title} dihapus dari Pustaka.`, `${manga.title} removed from Library.`)
    );

    // Immediate optimistic button update while backend responds
    const libBtn = document.getElementById('btnToggleInLibrary');
    if (libBtn) {
      libBtn.classList.toggle('is-active', manga.inLibrary);
      const svg = manga.inLibrary
        ? getIconSvg('bookmark-bold', { size: 24 })
        : getIconSvg('bookmark', { size: 24 });
      libBtn.innerHTML = `${svg}<span>${manga.inLibrary ? this.t('detail.inLibrary') : this.t('detail.addToLibrary')}</span>`;
      this.persistLocalCache();
    }
  });
  const normalizeMalTitle = (s: string): string =>
    (s || '')
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, ' ')
      .trim();

  const executeMalSearch = async (query: string, autoBindMatched = false) => {
    const q = query.trim();
    if (!q) return;
    const searchSeq = ++this.malSearchSeq;
    const targetMangaId = manga.id;
    this.malSearchQuery = q;
    this.isMalSearching = true;
    this.malSearchError = '';
    this.renderCurrentWorkspace(true);

    try {
      const res = await this.bridge.invoke<{ ok: boolean; results?: MalSearchResult[]; error?: string }>('mal:search', {
        query: q
      });
      if (this.malSearchSeq !== searchSeq || this.activeDetailMangaId !== targetMangaId) {
        return;
      }
      this.isMalSearching = false;
      if (res && res.ok && Array.isArray(res.results)) {
        this.malSearchResults = res.results;
        this.malSearchError = '';

        // Auto-detect & bind if this manga is already tracked on the user's MAL account (e.g. from another device)
        if (autoBindMatched) {
          const targetNorm = normalizeMalTitle(cleanMangaTitleForMalSearch(manga.title));
          const rawNorm = normalizeMalTitle(manga.title);
          const inListMatch = res.results.find((r) => {
            if (!r.inUserList) return false;
            const rNorm = normalizeMalTitle(r.title);
            return rNorm === targetNorm || rNorm === rawNorm;
          });
          if (inListMatch) {
            manga.malTracked = true;
            manga.malId = inListMatch.id;
            manga.malTitle = inListMatch.title;
            if (inListMatch.userStatus) manga.malStatus = inListMatch.userStatus;
            if (typeof inListMatch.userScore === 'number' && inListMatch.userScore > 0) {
              manga.malScore = inListMatch.userScore;
            }
            if (typeof inListMatch.userChaptersRead === 'number' && inListMatch.userChaptersRead > 0) {
              manga.lastReadChapter = Math.max(manga.lastReadChapter, inListMatch.userChaptersRead);
            }
            this.persistLocalCache();
            this.showToast(
              this.l(
                'Ditemukan di akun MyAnimeList Anda & disinkronkan.',
                'Found in your MyAnimeList account & synced.'
              )
            );
            await handleSelectMalEntry(inListMatch.id, inListMatch.title, inListMatch.coverUrl, true);
            return;
          }
        }
      } else {
        this.malSearchResults = [];
        this.malSearchError = res?.error || this.l('Gagal mencari manga di MyAnimeList.', 'Failed to search manga on MyAnimeList.');
      }
    } catch (err: any) {
      if (this.malSearchSeq !== searchSeq || this.activeDetailMangaId !== targetMangaId) {
        return;
      }
      this.isMalSearching = false;
      this.malSearchResults = [];
      this.malSearchError = err?.message || this.l('Terjadi kesalahan saat menghubungi MyAnimeList.', 'An error occurred while contacting MyAnimeList.');
    }
    if (this.malSearchSeq === searchSeq && this.activeDetailMangaId === targetMangaId) {
      this.renderCurrentWorkspace(true);
    }
  };

  const handleSelectMalEntry = async (id: number, title: string, coverUrl?: string, autoBind = false) => {
    const selectSeq = ++this.malSelectSeq;
    const targetMangaId = manga.id;
    this.malSelectedEntry = { id, title, coverUrl };
    this.isMalSearchMode = false;
    this.renderCurrentWorkspace(true);

    try {
      const res = await this.bridge.invoke<{
        ok: boolean;
        track?: {
          remoteId: number;
          title: string;
          status: string;
          score: number;
          lastChapterRead: number;
          totalChapters: number;
        };
      }>('mal:get-track', {
        mangaId: manga.id,
        remoteId: id,
        autoBind
      });

      if (this.malSelectSeq !== selectSeq || this.activeDetailMangaId !== targetMangaId) {
        return;
      }

      if (res && res.ok && res.track) {
        if (autoBind) {
          manga.malTracked = true;
          manga.malId = res.track.remoteId || id;
          manga.malTitle = res.track.title || title;
        }
        if (res.track.status) {
          manga.malStatus = res.track.status;
          setMalDropdownValue('malStatusSelect', res.track.status);
        }
        if (typeof res.track.lastChapterRead === 'number' && res.track.lastChapterRead > 0) {
          manga.lastReadChapter = Math.max(manga.lastReadChapter, res.track.lastChapterRead);
          const inp = document.getElementById('malChaptersReadInput') as HTMLInputElement | null;
          if (inp) inp.value = manga.lastReadChapter.toString();
        }
        if (typeof res.track.score === 'number' && res.track.score > 0) {
          manga.malScore = res.track.score;
          setMalDropdownValue('malScoreSelect', res.track.score.toString());
        }
      }
    } catch {
      // Keep defaults
    }
  };

  document.getElementById('btnOpenTrackerPopup')?.addEventListener('click', () => {
    this.isMalTrackerPopupOpen = true;
    if (this.settings.isMalConnected) {
      if (manga.malTracked && manga.malId && manga.malId > 0) {
        this.isMalSearchMode = false;
        this.malSelectedEntry = {
          id: manga.malId,
          title: manga.malTitle || manga.title
        };
      } else {
        this.isMalSearchMode = true;
        this.malSelectedEntry = null;
        this.malSearchQuery = cleanMangaTitleForMalSearch(manga.title);
        this.malSearchResults = [];
        this.malSearchError = '';
        if (this.malSearchQuery) {
          void executeMalSearch(this.malSearchQuery, true);
        }
      }
    }
    this.renderCurrentWorkspace(true);
  });

  document.getElementById('btnCloseMalTracker')?.addEventListener('click', () => {
    this.isMalTrackerPopupOpen = false;
    this.malSearchSeq++;
    this.malSelectSeq++;
    this.renderCurrentWorkspace(true);
  });

  document.getElementById('malTrackerModalBackdrop')?.addEventListener('click', (e) => {
    const backdrop = document.getElementById('malTrackerModalBackdrop');
    if (e.target === backdrop) {
      this.isMalTrackerPopupOpen = false;
      this.malSearchSeq++;
      this.malSelectSeq++;
      this.renderCurrentWorkspace(true);
      return;
    }

    const targetEl = e.target as HTMLElement | null;
    const trigger = targetEl?.closest('.v1-mal-dropdown-trigger') as HTMLButtonElement | null;
    if (trigger) {
      const dropdown = trigger.closest('.v1-mal-dropdown');
      const willOpen = !dropdown?.classList.contains('open');
      backdrop?.querySelectorAll('.v1-mal-dropdown.open').forEach((d) => {
        d.classList.remove('open');
        d.querySelector('.v1-mal-dropdown-trigger')?.setAttribute('aria-expanded', 'false');
      });
      if (dropdown && willOpen) {
        dropdown.classList.add('open');
        trigger.setAttribute('aria-expanded', 'true');
      }
      return;
    }

    const optionBtn = targetEl?.closest('.v1-mal-dropdown-option') as HTMLButtonElement | null;
    if (optionBtn) {
      const dropdown = optionBtn.closest('.v1-mal-dropdown') as HTMLElement | null;
      const targetId = dropdown?.dataset.dropdownFor;
      const val = optionBtn.dataset.value ?? '';
      if (targetId) {
        setMalDropdownValue(targetId, val);
      }
      dropdown?.classList.remove('open');
      dropdown?.querySelector('.v1-mal-dropdown-trigger')?.setAttribute('aria-expanded', 'false');
      return;
    }

    if (!targetEl?.closest('.v1-mal-dropdown')) {
      backdrop?.querySelectorAll('.v1-mal-dropdown.open').forEach((d) => {
        d.classList.remove('open');
        d.querySelector('.v1-mal-dropdown-trigger')?.setAttribute('aria-expanded', 'false');
      });
    }
  });

  document.getElementById('btnMalGoToSettings')?.addEventListener('click', () => {
    this.isMalTrackerPopupOpen = false;
    this.malSearchSeq++;
    this.malSelectSeq++;
    this.navigateToTab('settings');
    this.activeSettingsTab = 'tracker';
    this.renderCurrentWorkspace(false);
    document.getElementById('settingsTrackerSection')?.scrollIntoView({ behavior: 'instant' as ScrollBehavior, block: 'center' });
  });

  document.getElementById('btnMalBackToEdit')?.addEventListener('click', () => {
    this.isMalSearchMode = false;
    this.renderCurrentWorkspace(true);
  });

  document.getElementById('btnMalChangeManga')?.addEventListener('click', () => {
    this.isMalSearchMode = true;
    if (!this.malSearchQuery) {
      this.malSearchQuery = cleanMangaTitleForMalSearch(manga.title);
    }
    this.renderCurrentWorkspace(true);
    if (this.malSearchResults.length === 0 && this.malSearchQuery) {
      void executeMalSearch(this.malSearchQuery);
    }
  });

  const searchInput = document.getElementById('malSearchQueryInput') as HTMLInputElement | null;
  const triggerSearch = () => {
    const q = searchInput?.value.trim() || '';
    if (q) {
      void executeMalSearch(q);
    }
  };

  document.getElementById('btnExecuteMalSearch')?.addEventListener('click', triggerSearch);
  document.getElementById('btnRetryMalSearch')?.addEventListener('click', triggerSearch);
  searchInput?.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') {
      e.preventDefault();
      triggerSearch();
    }
  });

  document.querySelectorAll<HTMLElement>('[data-select-mal-id]').forEach((el) => {
    el.addEventListener('click', (ev) => {
      ev.stopPropagation();
      const idStr = el.dataset.selectMalId;
      const title = el.dataset.selectMalTitle || '';
      const cover = el.dataset.selectMalCover || '';
      const malId = Number(idStr);
      if (!isNaN(malId) && malId > 0) {
        void handleSelectMalEntry(malId, title, cover);
      }
    });
  });

  const getDraftMalChapter = (): number => {
    const inp = document.getElementById('malChaptersReadInput') as HTMLInputElement | null;
    if (inp && inp.value.trim() !== '') {
      const parsed = Number(inp.value.replace(/[^\d]/g, ''));
      if (!isNaN(parsed)) {
        return manga.totalChapters > 0 ? Math.min(manga.totalChapters, Math.max(0, parsed)) : Math.max(0, parsed);
      }
    }
    return Math.max(0, manga.lastReadChapter || 0);
  };

  document.getElementById('btnMalDecCh')?.addEventListener('click', () => {
    const nextVal = Math.max(0, getDraftMalChapter() - 1);
    const inp = document.getElementById('malChaptersReadInput') as HTMLInputElement | null;
    if (inp) {
      inp.value = nextVal.toString();
    }
  });

  document.getElementById('btnMalIncCh')?.addEventListener('click', () => {
    const rawNext = getDraftMalChapter() + 1;
    const nextVal = manga.totalChapters > 0 ? Math.min(manga.totalChapters, rawNext) : rawNext;
    const inp = document.getElementById('malChaptersReadInput') as HTMLInputElement | null;
    if (inp) {
      inp.value = nextVal.toString();
    }
  });

  const malChInput = document.getElementById('malChaptersReadInput') as HTMLInputElement | null;
  malChInput?.addEventListener('input', () => {
    const digitsOnly = malChInput.value.replace(/[^\d]/g, '');
    if (digitsOnly === '') {
      malChInput.value = '';
      return;
    }
    const parsed = Number(digitsOnly);
    const clamped = manga.totalChapters > 0 ? Math.min(manga.totalChapters, Math.max(0, parsed)) : Math.max(0, parsed);
    if (malChInput.value !== clamped.toString()) {
      malChInput.value = clamped.toString();
    }
  });
  malChInput?.addEventListener('blur', () => {
    if (malChInput.value.trim() === '') {
      malChInput.value = Math.max(0, manga.lastReadChapter || 0).toString();
    }
  });

  document.getElementById('btnSaveMalTracker')?.addEventListener('click', () => {
    const statusSel = document.getElementById('malStatusSelect') as HTMLSelectElement | null;
    const scoreSel = document.getElementById('malScoreSelect') as HTMLSelectElement | null;
    const statusVal = statusSel?.value || 'reading';
    const scoreVal = Number(scoreSel?.value || '0');
    const nextChaptersRead = getDraftMalChapter();
    const targetRemoteId = this.malSelectedEntry?.id || manga.malId || 0;
    const targetTitle = this.malSelectedEntry?.title || manga.malTitle || manga.title;

    if (!targetRemoteId || targetRemoteId <= 0) {
      this.showToast(this.l('Silakan pilih entri manga di MyAnimeList terlebih dahulu.', 'Please select a MyAnimeList manga entry first.'), 'warning');
      this.isMalSearchMode = true;
      this.renderCurrentWorkspace(true);
      return;
    }

    const prevMal = {
      malTracked: manga.malTracked,
      malId: manga.malId,
      malTitle: manga.malTitle,
      malStatus: manga.malStatus,
      malScore: manga.malScore,
      lastReadChapter: manga.lastReadChapter
    };

    manga.malTracked = true;
    manga.malId = targetRemoteId;
    manga.malTitle = targetTitle;
    manga.malStatus = statusVal;
    manga.malScore = scoreVal;
    manga.lastReadChapter = nextChaptersRead;
    this.isMalTrackerPopupOpen = false;

    if (this.bridge.isWebViewHost) {
      void this.bridge
        .invoke('mal:save-track', {
          mangaId: manga.id,
          dbId: manga.dbId,
          remoteId: targetRemoteId,
          malTitle: targetTitle,
          status: statusVal,
          chaptersRead: nextChaptersRead,
          score: scoreVal
        })
        .catch(() => {
          Object.assign(manga, prevMal);
          this.persistLocalCache();
          this.showToast(this.l('Gagal menyimpan status pelacakan MyAnimeList.', 'Failed to save MyAnimeList tracking status.'), 'warning');
          if (this.activeDetailMangaId === manga.id) this.renderCurrentWorkspace(true);
        });
    }

    this.persistLocalCache();
    this.showToast(this.l('Status pelacakan MyAnimeList berhasil disimpan.', 'MyAnimeList tracking status saved.'));
    this.renderCurrentWorkspace(true);
  });

  document.getElementById('btnSyncMalTracker')?.addEventListener('click', async () => {
    const targetRemoteId = manga.malId || this.malSelectedEntry?.id || 0;
    if (!targetRemoteId || targetRemoteId <= 0) {
      this.showToast(this.l('Komik belum ditautkan ke MyAnimeList.', 'Comic is not linked to MyAnimeList yet.'), 'warning');
      return;
    }

    const syncSeq = ++this.malSelectSeq;
    const targetMangaId = manga.id;
    this.showToast(this.l('Menyinkronkan data dengan MyAnimeList...', 'Syncing data with MyAnimeList...'), 'refresh');
    try {
      const res = await this.bridge.invoke<{
        ok: boolean;
        track?: {
          remoteId: number;
          title: string;
          status: string;
          score: number;
          lastChapterRead: number;
          totalChapters: number;
        };
      }>('mal:get-track', {
        mangaId: manga.id,
        remoteId: targetRemoteId
      });

      if (this.malSelectSeq !== syncSeq || this.activeDetailMangaId !== targetMangaId) {
        return;
      }

      if (res && res.ok && res.track) {
        if (res.track.status) {
          manga.malStatus = res.track.status;
          setMalDropdownValue('malStatusSelect', res.track.status);
        }
        if (typeof res.track.lastChapterRead === 'number') {
          manga.lastReadChapter = Math.max(manga.lastReadChapter, res.track.lastChapterRead);
          const inp = document.getElementById('malChaptersReadInput') as HTMLInputElement | null;
          if (inp) inp.value = manga.lastReadChapter.toString();
        }
        if (typeof res.track.score === 'number') {
          manga.malScore = res.track.score;
          setMalDropdownValue('malScoreSelect', res.track.score.toString());
        }
        this.persistLocalCache();
        this.showToast(this.l('Berhasil menyinkronkan data dari MyAnimeList.', 'Successfully synced data from MyAnimeList.'));
      } else {
        this.showToast(this.l('Gagal menyinkronkan data dari MyAnimeList.', 'Failed to sync data from MyAnimeList.'), 'warning');
      }
    } catch {
      if (this.malSelectSeq !== syncSeq || this.activeDetailMangaId !== targetMangaId) {
        return;
      }
      this.showToast(this.l('Gagal menyinkronkan data dari MyAnimeList.', 'Failed to sync data from MyAnimeList.'), 'warning');
    }
  });

  document.getElementById('btnUnbindMalTracker')?.addEventListener('click', () => {
    const prevMal = {
      malTracked: manga.malTracked,
      malId: manga.malId,
      malTitle: manga.malTitle,
      malStatus: manga.malStatus,
      malScore: manga.malScore
    };
    const prevSelected = this.malSelectedEntry;
    manga.malTracked = false;
    manga.malId = 0;
    manga.malTitle = '';
    manga.malStatus = 'reading';
    manga.malScore = 0;
    this.malSelectedEntry = null;
    this.isMalTrackerPopupOpen = false;

    if (this.bridge.isWebViewHost) {
      void this.bridge
        .invoke('mal:unbind-track', {
          mangaId: manga.id,
          dbId: manga.dbId
        })
        .catch(() => {
          Object.assign(manga, prevMal);
          this.malSelectedEntry = prevSelected;
          this.persistLocalCache();
          this.showToast(this.l('Gagal melepas tautan MyAnimeList.', 'Failed to unlink MyAnimeList.'), 'warning');
          if (this.activeDetailMangaId === manga.id) this.renderCurrentWorkspace(true);
        });
    }

    this.persistLocalCache();
    this.showToast(this.l('Tautan MyAnimeList telah dilepas.', 'MyAnimeList link removed.'));
    this.renderCurrentWorkspace(true);
  });
  document.getElementById('btnOpenWebViewSource')?.addEventListener('click', () => {
    const ext = this.extensions.find(
      (e) => e.id === manga.sourceId || String(e.sourceId) === String(manga.sourceId) || e.name === manga.sourceName
    );
    const targetUrl =
      manga.url && manga.url.startsWith('http')
        ? manga.url
        : ext?.baseUrl && manga.url
        ? `${ext.baseUrl.replace(/\/$/, '')}${manga.url.startsWith('/') ? '' : '/'}${manga.url}`
        : ext?.baseUrl || 'https://google.com';
    this.bridge.openExternalUrl(targetUrl);
    this.showToast(this.l(`Membuka halaman sumber ${manga.sourceName} di browser...`, `Opening ${manga.sourceName} source page in browser...`));
  });
  document.getElementById('btnRefreshMangaDetail')?.addEventListener('click', () => {
    this.showToast(this.l(`Menyegarkan daftar chapter ${manga.title}...`, `Refreshing chapter list for ${manga.title}...`));
    if (this.bridge.isWebViewHost) {
      void this.fetchMangaDetailFromBackend(manga.id, manga.url, manga.sourceId, true, manga.sourceName);
    }
  });
  document.getElementById('btnDownloadAllChapters')?.addEventListener('click', () => {
    this.enqueueDownload(manga, manga.lastReadChapter, 'all');
    this.renderCurrentWorkspace(true);
  });
  document.getElementById('btnToggleSynopsis')?.addEventListener('click', () => {
    this.isSynopsisExpanded = !this.isSynopsisExpanded;
    const body = document.querySelector('.v1-synopsis-body');
    const btn = document.getElementById('btnToggleSynopsis');
    if (body && btn) {
      body.classList.toggle('collapsed', !this.isSynopsisExpanded);
      btn.title = this.isSynopsisExpanded ? this.l('Ciutkan Sinopsis', 'Collapse Synopsis') : this.l('Perluas Sinopsis', 'Expand Synopsis');
      btn.innerHTML = getIconSvg(this.isSynopsisExpanded ? 'chevron-up' : 'chevron-down', { size: 20 });
    } else {
      this.renderCurrentWorkspace(true);
    }
  });
  document.getElementById('btnToggleFilterFlyout')?.addEventListener('click', (ev) => {
    ev.stopPropagation();
    this.isChapterFilterFlyoutOpen = !this.isChapterFilterFlyoutOpen;
    this.renderCurrentWorkspace(true);
  });
  document.getElementById('btnCloseChapterFilterModal')?.addEventListener('click', (ev) => {
    ev.stopPropagation();
    this.isChapterFilterFlyoutOpen = false;
    this.renderCurrentWorkspace(true);
  });
  document.getElementById('chapterFilterModalBackdrop')?.addEventListener('click', (ev) => {
    if (ev.target === document.getElementById('chapterFilterModalBackdrop')) {
      this.isChapterFilterFlyoutOpen = false;
      this.renderCurrentWorkspace(true);
    }
  });
  document.getElementById('chapterFilterModalBackdrop')?.querySelector('.v1-unified-filter-dialog')?.addEventListener('click', (ev) => {
    ev.stopPropagation();
  });
  document.getElementById('btnDeleteAllDownloads')?.addEventListener('click', () => {
    manga.chapters.forEach((c) => {
      c.isDownloaded = false;
    });
    for (let i = this.downloads.length - 1; i >= 0; i--) {
      if (this.downloads[i].mangaId === manga.id) {
        this.downloads.splice(i, 1);
      }
    }
    this.updateSidebarCounters();
    if (this.bridge.isWebViewHost) {
      void this.bridge
        .invoke('downloads:delete-manga', { mangaId: manga.id, dbId: manga.dbId })
        .catch(() => {
          this.showToast(this.l('Gagal menghapus unduhan dari disk.', 'Failed to delete downloads from disk.'), 'warning');
        });
    }
    this.showToast(this.l(`Seluruh unduhan chapter untuk ${manga.title} telah dihapus.`, `All chapter downloads for ${manga.title} deleted.`));
    this.renderCurrentWorkspace(true);
  });
  const container = document.getElementById('viewContainer');
  if (!container) return;
  container.querySelectorAll<HTMLButtonElement>('[data-ch-filter]').forEach((btn) => {
    btn.addEventListener('click', () => {
      this.detailChapterFilter = (btn.dataset.chFilter as 'all' | 'unread' | 'bookmarked') || 'all';
      this.isChapterFilterFlyoutOpen = false;
      this.renderCurrentWorkspace(true);
    });
  });
  document.getElementById('btnToggleChOrder')?.addEventListener('click', () => {
    this.detailChapterAsc = !this.detailChapterAsc;
    this.renderCurrentWorkspace(true);
  });

  // In-place bookmark toggle: never scrolls to top
  container.querySelectorAll<HTMLElement>('[data-toggle-bookmark]').forEach((btn) => {
    btn.addEventListener('click', (ev) => {
      ev.stopPropagation();
      const chNum = Number(btn.dataset.toggleBookmark);
      const found = manga.chapters.find((c) => c.number === chNum);
      if (found) {
        const prevBookmarked = found.isBookmarked;
        found.isBookmarked = !prevBookmarked;
        if (this.bridge.isWebViewHost) {
          void this.bridge
            .invoke('manga:toggle-bookmark', {
              mangaId: manga.id,
              url: manga.url || '',
              sourceId: manga.sourceId,
              dbId: manga.dbId,
              chapterId: found.dbId,
              chapterUrl: found.url || '',
              chapterNumber: chNum,
              isBookmarked: found.isBookmarked
            })
            .catch(() => {
              found.isBookmarked = prevBookmarked;
              this.showToast(this.l('Gagal memperbarui penanda bab.', 'Failed to update chapter bookmark.'), 'warning');
              if (this.activeDetailMangaId === manga.id) this.renderCurrentWorkspace(true);
            });
        }
        if (this.detailChapterFilter === 'bookmarked') {
          this.renderCurrentWorkspace(true);
          return;
        }
        btn.classList.toggle('bookmarked', found.isBookmarked);
        btn.title = found.isBookmarked ? this.l('Hapus Penanda', 'Remove Bookmark') : this.l('Tandai Chapter', 'Bookmark Chapter');
        btn.innerHTML = getIconSvg(found.isBookmarked ? 'bookmark-bold' : 'bookmark', {
          size: 18,
          color: found.isBookmarked ? 'var(--ocean-primary)' : 'currentColor'
        });
      }
    });
  });

  // Event-delegated in-place download / cancel / delete actions (never scrolls to top, survives in-place button swaps)
  const chapterListEl = container.querySelector<HTMLElement>('.v1-chapters-list');
  chapterListEl?.addEventListener('click', (ev) => {
    const target = ev.target as HTMLElement;
    const isEn = document.documentElement.lang === 'en';

    // 1. Cancel active/queued chapter download
    const cancelBtn = target.closest<HTMLElement>('[data-cancel-ch-dl]');
    if (cancelBtn) {
      ev.stopPropagation();
      const dlId = cancelBtn.dataset.cancelChDl || '';
      const chNum = Number(cancelBtn.dataset.dlChNum || '0');
      const chUrl = cancelBtn.dataset.dlChUrl || '';
      if (this.bridge.isWebViewHost) {
        void this.bridge
          .invoke<DownloadQueueItem[]>('downloads:cancel', { id: dlId, chapterUrl: chUrl })
          .then((queue) => {
            if (Array.isArray(queue)) {
              this.downloads.length = 0;
              this.downloads.push(...queue);
              this.updateSidebarCounters();
              updateDetailChapterDownloadsInPlace(container, manga, this.downloads, isEn);
            }
          })
          .catch(() => {});
      } else {
        const idx = this.downloads.findIndex((d) => d.id === dlId);
        if (idx >= 0) this.downloads.splice(idx, 1);
        this.updateSidebarCounters();
        updateDetailChapterDownloadsInPlace(container, manga, this.downloads, isEn);
      }
      this.showToast(this.l(`Unduhan Chapter ${chNum} dibatalkan.`, `Chapter ${chNum} download cancelled.`));
      return;
    }

    // 2. Delete already-downloaded chapter
    const delBtn = target.closest<HTMLElement>('[data-del-single-ch]');
    if (delBtn) {
      ev.stopPropagation();
      const chNum = Number(delBtn.dataset.delSingleCh);
      const found = manga.chapters.find((c) => c.number === chNum);
      if (!found) return;
      const prevDownloaded = found.isDownloaded;
      const prevDlCount = manga.downloadedCount;
      const removedDownloads: DownloadQueueItem[] = [];
      found.isDownloaded = false;
      manga.downloadedCount = manga.chapters.filter((c) => c.isDownloaded).length;
      // Remove stale completed entry from in-memory downloads so UI immediately flips back to download-2-line
      for (let i = this.downloads.length - 1; i >= 0; i--) {
        const d = this.downloads[i];
        const sameUrl = Boolean(found.url && d.chapterUrl && d.chapterUrl === found.url);
        const sameCh =
          d.mangaId === manga.id &&
          ((typeof d.chapterNumber === 'number' && Math.abs(d.chapterNumber - found.number) < 0.01) ||
            (d.chapterLabel && Math.abs(parseFloat((d.chapterLabel.match(/(\d+(?:\.\d+)?)/) || ['0', '0'])[1]) - found.number) < 0.01));
        if (sameUrl || sameCh) {
          removedDownloads.push(d);
          this.downloads.splice(i, 1);
        }
      }
      if (this.bridge.isWebViewHost) {
        void this.bridge
          .invoke('downloads:delete-chapter', {
            mangaId: manga.id,
            dbId: manga.dbId,
            chapterId: found.dbId,
            chapterNumber: chNum
          })
          .catch(() => {
            found.isDownloaded = prevDownloaded;
            manga.downloadedCount = prevDlCount;
            this.downloads.push(...removedDownloads);
            this.updateSidebarCounters();
            if (this.activeDetailMangaId === manga.id) {
              updateDetailChapterDownloadsInPlace(container, manga, this.downloads, isEn);
            }
            this.showToast(this.l(`Gagal menghapus unduhan Chapter ${chNum}.`, `Failed to delete Chapter ${chNum} download.`), 'warning');
          });
      }
      this.showToast(this.l(`Unduhan Chapter ${chNum} dihapus.`, `Chapter ${chNum} download deleted.`));
      this.updateSidebarCounters();
      updateDetailChapterDownloadsInPlace(container, manga, this.downloads, isEn);
      return;
    }

    // 3. Start single chapter download (optimistic inline queued ring + status, NOT fake completed!)
    const dlBtn = target.closest<HTMLElement>('[data-dl-single-ch]');
    if (dlBtn) {
      ev.stopPropagation();
      const chNum = Number(dlBtn.dataset.dlSingleCh);
      const found = manga.chapters.find((c) => c.number === chNum);
      if (!found) return;
      const optimisticItem: DownloadQueueItem = {
        id: `dl-opt-${manga.id}-${chNum}`,
        mangaId: manga.id,
        mangaTitle: manga.title,
        chapterNumber: chNum,
        chapterLabel: `Chapter ${chNum}`,
        chapterUrl: found.url || '',
        sourceName: manga.sourceName,
        progressPercent: 0,
        speedText: isEn ? 'Queued' : 'Dalam Antrean',
        sizeText: '0%',
        status: 'queued'
      };
      if (!findChapterDownloadItem(this.downloads, manga, found)) {
        this.downloads.push(optimisticItem);
      }
      updateDetailChapterDownloadsInPlace(container, manga, this.downloads, isEn);
      this.enqueueDownload(manga, chNum, 'single');
      return;
    }
  });
  // Chapter Context Menu & More Options (Tandai Sudah Dibaca / Belum Dibaca / Bab Sebelumnya)
  const hideChapterMenu = () => {
    const existing = document.getElementById('chapterContextMenu');
    if (existing) {
      existing.remove();
    }
  };

  const showChapterMenu = (chNum: number, clientX: number, clientY: number) => {
    hideChapterMenu();
    const ch = manga.chapters.find((c) => Math.abs(c.number - chNum) < 0.01);
    if (!ch) return;

    const menu = document.createElement('div');
    menu.id = 'chapterContextMenu';
    menu.className = 'v1-context-menu v1-chapter-ctx-menu open';

    const isRead = !!ch.isRead;
    menu.innerHTML = `
      ${
        !isRead
          ? `<button class="v1-ctx-item" data-action="mark-read">
               ${getIconSvg('check-line', { size: 16 })}
               <span>${this.l('Tandai Sudah Dibaca', 'Mark as Read')}</span>
             </button>`
          : `<button class="v1-ctx-item" data-action="mark-unread">
               ${getIconSvg('eye-off-line', { size: 16 })}
               <span>${this.l('Tandai Belum Dibaca', 'Mark as Unread')}</span>
             </button>`
      }
      <button class="v1-ctx-item" data-action="mark-before-read">
        ${getIconSvg('check-double-line', { size: 16 })}
        <span>${this.l('Tandai Bab Sebelumnya Sudah Dibaca', 'Mark Previous Chapters as Read')}</span>
      </button>
    `;

    document.body.appendChild(menu);

    // Position menu safely within viewport bounds
    const menuWidth = 250;
    const menuHeight = 76;
    let posX = clientX;
    let posY = clientY;
    if (posX + menuWidth > window.innerWidth - 10) {
      posX = Math.max(10, clientX - menuWidth);
    }
    if (posY + menuHeight > window.innerHeight - 10) {
      posY = Math.max(10, clientY - menuHeight);
    }
    menu.style.left = `${posX}px`;
    menu.style.top = `${posY}px`;

    menu.querySelectorAll<HTMLButtonElement>('[data-action]').forEach((actionBtn) => {
      actionBtn.addEventListener('click', (ev) => {
        ev.stopPropagation();
        const action = actionBtn.dataset.action;
        hideChapterMenu();

        if (action === 'mark-read') {
          const prevRead = ch.isRead;
          const prevUnread = manga.unreadCount;
          const prevLastViewed = manga.lastViewedEpoch;
          ch.isRead = true;
          manga.unreadCount = Math.max(0, manga.unreadCount - 1);
          manga.lastViewedEpoch = Date.now();
          this.readingStatsData = null;
          const row = container.querySelector<HTMLElement>(`[data-read-ch="${ch.number}"]`);
          row?.classList.add('is-read');
          row?.querySelector('.v1-ch-new-pill')?.remove();
          if (this.bridge.isWebViewHost) {
            void this.bridge
              .invoke('manga:mark-chapters', {
                mangaId: manga.id,
                mode: 'single-read',
                chapterNumber: ch.number,
                chapterUrl: ch.url,
                mangaUrl: manga.url,
                sourceId: manga.sourceId
              })
              .catch(() => {
                ch.isRead = prevRead;
                manga.unreadCount = prevUnread;
                manga.lastViewedEpoch = prevLastViewed;
                this.showToast(this.l('Gagal memperbarui status baca bab.', 'Failed to update chapter read status.'), 'warning');
                if (this.activeDetailMangaId === manga.id) this.renderCurrentWorkspace(true);
              });
          }
          this.showToast(this.l(`Chapter ${ch.number} ditandai sudah dibaca.`, `Chapter ${ch.number} marked as read.`));
          this.updateSidebarCounters();
          if (this.detailChapterFilter === 'unread') {
            this.renderCurrentWorkspace(true);
          }
        } else if (action === 'mark-unread') {
          const prevRead = ch.isRead;
          const prevUnread = manga.unreadCount;
          const prevLastViewed = manga.lastViewedEpoch;
          ch.isRead = false;
          manga.unreadCount++;
          if (manga.unreadCount >= Math.max(manga.totalChapters || 0, manga.chapters.length)) {
            manga.lastViewedEpoch = 0;
          }
          this.readingStatsData = null;
          const row = container.querySelector<HTMLElement>(`[data-read-ch="${ch.number}"]`);
          row?.classList.remove('is-read');
          if (this.bridge.isWebViewHost) {
            void this.bridge
              .invoke('manga:mark-chapters', {
                mangaId: manga.id,
                mode: 'single-unread',
                chapterNumber: ch.number,
                chapterUrl: ch.url,
                mangaUrl: manga.url,
                sourceId: manga.sourceId
              })
              .catch(() => {
                ch.isRead = prevRead;
                manga.unreadCount = prevUnread;
                manga.lastViewedEpoch = prevLastViewed;
                this.showToast(this.l('Gagal memperbarui status belum dibaca bab.', 'Failed to update chapter unread status.'), 'warning');
                if (this.activeDetailMangaId === manga.id) this.renderCurrentWorkspace(true);
              });
          }
          this.showToast(this.l(`Chapter ${ch.number} ditandai belum dibaca.`, `Chapter ${ch.number} marked as unread.`));
          this.updateSidebarCounters();
          if (this.detailChapterFilter === 'unread') {
            this.renderCurrentWorkspace(true);
          }
        } else if (action === 'mark-before-read') {
          const prevChapters = manga.chapters.filter((c) => c.number < ch.number && !c.isRead);
          const prevUnread = manga.unreadCount;
          const prevLastViewed = manga.lastViewedEpoch;
          prevChapters.forEach((c) => {
            c.isRead = true;
            const r = container.querySelector<HTMLElement>(`[data-read-ch="${c.number}"]`);
            r?.classList.add('is-read');
            r?.querySelector('.v1-ch-new-pill')?.remove();
          });
          manga.unreadCount = manga.chapters.filter((c) => !c.isRead).length;
          if (prevChapters.length > 0) {
            manga.lastViewedEpoch = Date.now();
          }
          this.readingStatsData = null;
          if (this.bridge.isWebViewHost) {
            void this.bridge
              .invoke('manga:mark-chapters', {
                mangaId: manga.id,
                mode: 'before-read',
                chapterNumber: ch.number,
                mangaUrl: manga.url,
                sourceId: manga.sourceId
              })
              .catch(() => {
                prevChapters.forEach((c) => {
                  c.isRead = false;
                });
                manga.unreadCount = prevUnread;
                manga.lastViewedEpoch = prevLastViewed;
                this.showToast(this.l('Gagal memperbarui status baca bab sebelumnya.', 'Failed to update previous chapters read status.'), 'warning');
                if (this.activeDetailMangaId === manga.id) this.renderCurrentWorkspace(true);
              });
          }
          this.showToast(this.l(`Seluruh bab sebelum Chapter ${ch.number} ditandai sudah dibaca.`, `All chapters before Chapter ${ch.number} marked as read.`));
          this.updateSidebarCounters();
          if (this.detailChapterFilter === 'unread') {
            this.renderCurrentWorkspace(true);
          }
        }
      });
    });
  };

  // Close context menu on outside clicks / escape with single active listener (capture phase prevents Escape from also closing Manga Detail)
  const onGlobalClick = (ev: MouseEvent) => {
    const menu = document.getElementById('chapterContextMenu');
    if (menu && !menu.contains(ev.target as Node)) {
      hideChapterMenu();
    }
  };
  const onGlobalKeyDown = (ev: KeyboardEvent) => {
    if (ev.key === 'Escape' && document.getElementById('chapterContextMenu')) {
      ev.preventDefault();
      ev.stopPropagation();
      hideChapterMenu();
    }
  };
  const win = window as unknown as { _onChapterCtxClick?: (ev: MouseEvent) => void; _onChapterCtxKey?: (ev: KeyboardEvent) => void };
  if (win._onChapterCtxClick) window.removeEventListener('click', win._onChapterCtxClick);
  if (win._onChapterCtxKey) window.removeEventListener('keydown', win._onChapterCtxKey, { capture: true });
  win._onChapterCtxClick = onGlobalClick;
  win._onChapterCtxKey = onGlobalKeyDown;
  window.addEventListener('click', onGlobalClick);
  window.addEventListener('keydown', onGlobalKeyDown, { capture: true });

  // Chapter row click to open reader
  container.querySelectorAll<HTMLElement>('[data-read-ch]').forEach((el) => {
    el.addEventListener('click', (ev) => {
      // Ignore if user clicked on one of the action buttons inside
      if ((ev.target as HTMLElement).closest('.v1-ch-icon-btn, button')) {
        return;
      }
      const chNum = Number(el.dataset.readCh);
      if (!Number.isNaN(chNum)) {
        this.openReader(manga.id, chNum);
      }
    });

    // Right-click contextmenu on chapter row
    el.addEventListener('contextmenu', (ev) => {
      ev.preventDefault();
      ev.stopPropagation();
      const chNum = Number(el.dataset.readCh);
      if (!Number.isNaN(chNum)) {
        showChapterMenu(chNum, ev.clientX, ev.clientY);
      }
    });
  });

  // More options button (•••) click
  container.querySelectorAll<HTMLElement>('[data-ch-more]').forEach((btn) => {
    btn.addEventListener('click', (ev) => {
      ev.stopPropagation();
      const chNum = Number(btn.dataset.chMore);
      if (!Number.isNaN(chNum)) {
        const rect = btn.getBoundingClientRect();
        showChapterMenu(chNum, rect.right, rect.bottom + 4);
      }
    });
  });

  this.bindCommonCardTriggers(container);
}
