import type { YomicApp } from '../app.js';
import { getIconSvg, SOLAR_PATHS, icons } from '../icons.js';
import { applyStaticDomTranslations, TranslationKey } from '../i18n.js';
import {
  buildComicPageSvg,
  buildMangaCoverSvg,
  DEFAULT_APP_SETTINGS,
  DEFAULT_HOTKEYS,
  HOTKEY_DEFINITIONS,
  escapeXml
} from '../mock-data.js';
import {
  ActivityUpdateItem,
  AppSettingsState,
  ChapterItem,
  DownloadQueueItem,
  ExtensionItem,
  HotkeyDefinition,
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
 * 7. SETTINGS VIEW (MIRROR OF YOMIC V1 SettingsView.axaml — SINGLE SCROLL)
 * ========================================================================= */

export function renderSettingsView(this: YomicApp, container: HTMLElement): void {
  const isReRender = Boolean(container.querySelector('.v1-settings-page'));
  const savedContainerScroll = isReRender ? container.scrollTop : 0;
  const savedParentScroll = isReRender && container.parentElement ? container.parentElement.scrollTop : 0;
  this.setTopbarHeader(this.t('settings.title'));

  const formatHotkeyBadgeHtml = (hotkeyStr: string): string => {
    if (!hotkeyStr) return `<span class="v1-hotkey-empty">${this.settings.appLanguage === 'en' ? 'Unbound' : 'Belum diatur'}</span>`;
    const parts = hotkeyStr.split('+');
    return parts
      .map((p) => {
        let display = p;
        const isEn = this.settings.appLanguage === 'en';
        if (p.toLowerCase() === 'arrowright') display = isEn ? '→ Right' : '→ Kanan';
        else if (p.toLowerCase() === 'arrowleft') display = isEn ? '← Left' : '← Kiri';
        else if (p.toLowerCase() === 'arrowup') display = isEn ? '↑ Up' : '↑ Atas';
        else if (p.toLowerCase() === 'arrowdown') display = isEn ? '↓ Down' : '↓ Bawah';
        else if (p.toLowerCase() === 'space') display = isEn ? 'Space' : 'Spasi';
        else if (p.length === 1) display = p.toUpperCase();
        return `<kbd class="v1-hotkey-badge">${escapeXml(display)}</kbd>`;
      })
      .join('<span class="v1-hotkey-plus">+</span>');
  };

  const renderHotkeyItem = (def: HotkeyDefinition): string => {
    const currentKey = this.getEffectiveHotkey(def.id);
    const isCustom = Boolean(this.settings.customHotkeys && this.settings.customHotkeys[def.id]);
    const labelText = this.t(`hotkey.${def.id}.label` as TranslationKey) || def.label;
    const descText = this.t(`hotkey.${def.id}.desc` as TranslationKey) || def.description;
    return `
      <div class="v1-setting-row v1-hotkey-row" data-hotkey-action="${def.id}">
        <div class="v1-setting-info">
          <div class="v1-setting-title-row">
            <span class="v1-setting-title">${escapeXml(labelText)}</span>
            ${isCustom ? `<span class="v1-hotkey-custom-tag">${this.settings.appLanguage === 'en' ? 'Custom' : 'Kustom'}</span>` : ''}
          </div>
          <span class="v1-setting-desc">${escapeXml(descText)}</span>
        </div>
        <div class="v1-hotkey-controls">
          <button class="v1-hotkey-bind-btn" data-rebind-action="${def.id}" title="${escapeXml(currentKey)}" aria-label="${escapeXml(labelText)}">
            <span class="v1-hotkey-display">${formatHotkeyBadgeHtml(currentKey)}</span>
          </button>
          ${
            isCustom
              ? `<button class="v1-hotkey-reset-btn" data-reset-hotkey="${def.id}" title="${escapeXml(this.t('settings.hotkeys.resetTooltip', { key: def.defaultKey }))}" aria-label="${escapeXml(this.t('settings.hotkeys.resetTooltip', { key: def.defaultKey }))}">
                  ${getIconSvg('restart-linear', { size: 14 })}
                 </button>`
              : ''
          }
        </div>
      </div>
    `;
  };

  const readerHotkeysHtml = HOTKEY_DEFINITIONS.filter((d) => d.category === 'reader')
    .map(renderHotkeyItem)
    .join('');

  const globalHotkeysHtml = HOTKEY_DEFINITIONS.filter((d) => d.category === 'global')
    .map(renderHotkeyItem)
    .join('');

  const renderSettingDropdown = (
    id: string,
    options: { value: string; label: string }[],
    selectedValue: string,
    ariaLabel: string
  ): string => {
    const activeOption = options.find((opt) => opt.value === selectedValue) || options[0];
    return `
      <div class="v1-mal-dropdown v1-setting-dropdown" data-dropdown-for="${escapeXml(id)}">
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
                </button>
              `;
            })
            .join('')}
        </div>
      </div>
    `;
  };

  const activeSub = !this.activeSettingsTab || this.activeSettingsTab === 'all' ? 'hub' : this.activeSettingsTab;
  const subTitleMap: Record<string, string> = {
    appearance: this.l('Tampilan', 'Appearance'),
    library: this.l('Pustaka', 'Library'),
    reader: this.l('Pembaca', 'Reader'),
    downloads: this.l('Unduhan', 'Downloads'),
    tracker: this.l('Pelacakan', 'Tracking'),
    network: this.l('Jaringan & Sumber', 'Network & Sources'),
    storage: this.l('Data dan penyimpanan', 'Data and storage'),
    hotkeys: this.l('Pintasan Keyboard', 'Keyboard Shortcuts'),
    about: this.l('Tentang', 'About')
  };

  const hubHtml = `
    <div class="v1-settings-hub is-minimal">
      <!-- Category Navigation List (Ultra-Minimalist Mobile Style Gambar 2) -->
      <div class="v1-settings-hub-list">
        <button type="button" class="v1-settings-hub-row" data-open-settings-sub="appearance">
          <div class="v1-settings-hub-icon">${getIconSvg('palette-linear', { size: 21 })}</div>
          <span class="v1-settings-hub-title">${this.l('Tampilan', 'Appearance')}</span>
        </button>

        <button type="button" class="v1-settings-hub-row" data-open-settings-sub="library">
          <div class="v1-settings-hub-icon">${getIconSvg('book-2-linear', { size: 21 })}</div>
          <span class="v1-settings-hub-title">${this.l('Pustaka', 'Library')}</span>
        </button>

        <button type="button" class="v1-settings-hub-row" data-open-settings-sub="reader">
          <div class="v1-settings-hub-icon">${getIconSvg('reader-book-open', { size: 21 })}</div>
          <span class="v1-settings-hub-title">${this.l('Pembaca', 'Reader')}</span>
        </button>

        <button type="button" class="v1-settings-hub-row" data-open-settings-sub="downloads">
          <div class="v1-settings-hub-icon">${getIconSvg('download-2-line', { size: 21 })}</div>
          <span class="v1-settings-hub-title">${this.l('Unduhan', 'Downloads')}</span>
        </button>

        <button type="button" class="v1-settings-hub-row" data-open-settings-sub="tracker">
          <div class="v1-settings-hub-icon">${getIconSvg('link', { size: 21 })}</div>
          <span class="v1-settings-hub-title">${this.l('Pelacakan', 'Tracking')}</span>
        </button>

        <button type="button" class="v1-settings-hub-row" data-open-settings-sub="network">
          <div class="v1-settings-hub-icon">${getIconSvg('global-linear', { size: 21 })}</div>
          <span class="v1-settings-hub-title">${this.l('Jaringan & Sumber', 'Network & Sources')}</span>
        </button>

        <button type="button" class="v1-settings-hub-row" data-open-settings-sub="storage">
          <div class="v1-settings-hub-icon">${getIconSvg('database-2-line', { size: 21 })}</div>
          <span class="v1-settings-hub-title">${this.l('Data dan penyimpanan', 'Data and storage')}</span>
        </button>

        <button type="button" class="v1-settings-hub-row" data-open-settings-sub="hotkeys">
          <div class="v1-settings-hub-icon">${getIconSvg('command-linear', { size: 21 })}</div>
          <span class="v1-settings-hub-title">${this.l('Pintasan Keyboard', 'Keyboard Shortcuts')}</span>
        </button>

        <button type="button" class="v1-settings-hub-row" data-open-settings-sub="about">
          <div class="v1-settings-hub-icon">${getIconSvg('info-circle-linear', { size: 21 })}</div>
          <span class="v1-settings-hub-title">${this.l('Tentang', 'About')}</span>
        </button>
      </div>
    </div>
  `;

  container.innerHTML = `
    <div class="v1-settings-page">
      <!-- Header: Settings Icon + Title (or Sub-Page Back Button) -->
      <div class="v1-lib-top" style="margin-bottom:4px;">
        <div class="v1-lib-title-row">
          ${
            activeSub === 'hub'
              ? `${getIconSvg('settings-3-line', { size: 24 })}
                 <span class="v1-lib-title-text">${this.t('settings.title')}</span>`
              : `<button type="button" id="btnBackToSettingsHub" class="v1-settings-back-btn" title="${escapeXml(this.l('Kembali ke Pengaturan', 'Back to Settings'))}" aria-label="${escapeXml(this.l('Kembali ke Pengaturan', 'Back to Settings'))}">
                   ${getIconSvg('arrow-left-line', { size: 22 })}
                 </button>
                 <span class="v1-lib-title-text">${escapeXml(subTitleMap[activeSub] || this.t('settings.title'))}</span>`
          }
        </div>
      </div>
      ${activeSub === 'hub' ? hubHtml : ''}
      <!-- 1A. TEMA (Ala Mobile Gambar 1) -->
      <section class="v1-settings-section" style="${activeSub === 'appearance' ? '' : 'display:none;'}">
        <div class="v1-settings-sec-head">
          <h2 class="v1-settings-sec-title">${this.l('Tema', 'Theme')}</h2>
        </div>
        <div class="v1-settings-group">
          <div class="v1-setting-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.l('Mode Gelap', 'Dark Mode')}</span>
              </div>
              <span class="v1-setting-desc">${this.l('Beralih antara Carbon Dark dan Oceanic Light', 'Switch between Carbon Dark and Oceanic Light')}</span>
            </div>
            <div class="toggle-switch ${this.settings.isDarkMode ? 'on' : ''}" data-toggle-setting="isDarkMode" role="switch" aria-checked="${this.settings.isDarkMode}">
              <div class="toggle-thumb"></div>
            </div>
          </div>
        </div>
      </section>
      <!-- 1B. TAMPILAN & BAHASA (Ala Mobile Gambar 1) -->
      <section class="v1-settings-section has-top-divider" style="${activeSub === 'appearance' ? '' : 'display:none;'}">
        <div class="v1-settings-sec-head">
          <h2 class="v1-settings-sec-title">${this.l('Tampilan &amp; Bahasa', 'Display &amp; Language')}</h2>
        </div>
        <div class="v1-settings-group">
          <div class="v1-setting-row is-select-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.l('Bahasa Antarmuka', 'Interface Language')}</span>
              </div>
            </div>
            ${renderSettingDropdown(
              'selAppLanguage',
              [
                { value: 'id', label: this.t('settings.appearance.languageId') },
                { value: 'en', label: this.t('settings.appearance.languageEn') }
              ],
              this.settings.appLanguage === 'en' ? 'en' : 'id',
              this.t('settings.appearance.languageTitle')
            )}
          </div>
          <div class="v1-setting-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.l('Lencana bab belum dibaca', 'Unread chapter badges')}</span>
              </div>
              <span class="v1-setting-desc">${this.l('Tampilkan jumlah bab baru pada sampul pustaka', 'Show unread chapter count on library covers')}</span>
            </div>
            <div class="toggle-switch ${this.settings.showUnreadBadges ? 'on' : ''}" data-toggle-setting="showUnreadBadges" role="switch" aria-checked="${this.settings.showUnreadBadges}">
              <div class="toggle-thumb"></div>
            </div>
          </div>
          <div class="v1-setting-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.t('settings.library.showNsfwTitle')}</span>
              </div>
              <span class="v1-setting-desc">${this.t('settings.library.showNsfwDesc')}</span>
            </div>
            <div class="toggle-switch ${this.settings.showNsfw ? 'on' : ''}" data-toggle-setting="showNsfw" role="switch" aria-checked="${this.settings.showNsfw}">
              <div class="toggle-thumb"></div>
            </div>
          </div>
          <div class="v1-setting-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.t('settings.library.blurNsfwTitle')}</span>
              </div>
              <span class="v1-setting-desc">${this.t('settings.library.blurNsfwDesc')}</span>
            </div>
            <div class="toggle-switch ${this.settings.blurExplicitCovers ? 'on' : ''}" data-toggle-setting="blurExplicitCovers" role="switch" aria-checked="${this.settings.blurExplicitCovers}">
              <div class="toggle-thumb"></div>
            </div>
          </div>
          <div class="v1-setting-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.settings.appLanguage === 'en' ? 'Refresh Library Covers' : 'Segarkan Sampul Pustaka'}</span>
              </div>
              <span class="v1-setting-desc">${this.settings.appLanguage === 'en' ? 'Reload all comic cover images in your Library from their original sources.' : 'Muat ulang seluruh gambar sampul (cover) komik di Pustaka dari sumber aslinya.'}</span>
            </div>
            <button id="btnRefreshLibraryCovers" class="v1-setting-btn">${this.t('common.refresh')}</button>
          </div>
        </div>
      </section>
      <!-- 1C. PEMBACA -->
      <section class="v1-settings-section" style="${activeSub === 'reader' ? '' : 'display:none;'}">
        <div class="v1-settings-sec-head">
          <h2 class="v1-settings-sec-title">${this.l('Tata Letak & Navigasi', 'Layout & Navigation')}</h2>
        </div>
        <div class="v1-settings-group">
          <div class="v1-setting-row is-select-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.t('settings.appearance.readerModeTitle')}</span>
              </div>
            </div>
            ${renderSettingDropdown(
              'selDefaultReaderMode',
              [
                { value: 'webtoon', label: this.t('settings.appearance.readerModeWebtoon') },
                { value: 'single', label: this.t('settings.appearance.readerModeSingle') },
                { value: 'dual', label: this.t('settings.appearance.readerModeDual') }
              ],
              this.settings.defaultReaderMode || 'webtoon',
              this.t('settings.appearance.readerModeTitle')
            )}
          </div>
          <div class="v1-setting-row is-select-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.t('settings.appearance.directionTitle')}</span>
              </div>
            </div>
            ${renderSettingDropdown(
              'selReadingDirection',
              [
                { value: 'ltr', label: this.t('settings.appearance.directionLtr') },
                { value: 'rtl', label: this.t('settings.appearance.directionRtl') }
              ],
              this.settings.readingDirection || 'ltr',
              this.t('settings.appearance.directionTitle')
            )}
          </div>
          <div class="v1-setting-row is-select-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.t('settings.appearance.fitModeTitle')}</span>
              </div>
            </div>
            ${renderSettingDropdown(
              'selReaderFitMode',
              [
                { value: 'fit-width', label: this.t('settings.appearance.fitWidth') },
                { value: 'fit-height', label: this.t('settings.appearance.fitHeight') },
                { value: 'original', label: this.t('settings.appearance.fitOriginal') }
              ],
              this.settings.readerFitMode || 'fit-width',
              this.t('settings.appearance.fitModeTitle')
            )}
          </div>
        </div>
      </section>
      <!-- 1D. UNDUHAN — PERILAKU UNDUHAN (Ala Mobile Gambar 1) -->
      <section class="v1-settings-section" style="${activeSub === 'downloads' ? '' : 'display:none;'}">
        <div class="v1-settings-sec-head">
          <h2 class="v1-settings-sec-title">${this.l('Perilaku unduhan', 'Download behavior')}</h2>
        </div>
        <div class="v1-settings-group">
          <button type="button" id="btnOpenDownloadsFolderFromDl" class="v1-setting-row is-action-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.l('Folder Penyimpanan Unduhan', 'Downloads Storage Folder')}</span>
              </div>
              <span class="v1-setting-desc">${escapeXml(this.settings.downloadsFolder)}</span>
            </div>
          </button>
          <div class="v1-setting-row is-select-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.l('Kecepatan Unduhan', 'Download Speed')}</span>
              </div>
              <span class="v1-setting-desc">${this.l('Atur jumlah bab dan halaman yang diunduh secara bersamaan', 'Configure concurrent chapter and page download connections')}</span>
            </div>
            ${renderSettingDropdown(
              'selDownloadSpeedMode',
              [
                { value: 'safe', label: this.l('Aman (2 Bab × 4 Halaman)', 'Safe (2 Chapters × 4 Pages)') },
                { value: 'fast', label: this.l('Cepat — Rekomendasi (3 Bab × 6 Halaman)', 'Fast — Recommended (3 Chapters × 6 Pages)') },
                { value: 'max', label: this.l('Maksimal (4 Bab × 8 Halaman)', 'Maximum (4 Chapters × 8 Pages)') }
              ],
              this.settings.downloadSpeedMode || 'fast',
              this.l('Kecepatan Unduhan', 'Download Speed')
            )}
          </div>
          <div class="v1-setting-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.l('Simpan bab sebagai arsip CBZ', 'Save chapters as CBZ archives')}</span>
              </div>
              <span class="v1-setting-desc">${this.l('Gabungkan halaman unduhan menjadi satu file .cbz', 'Combine downloaded pages into a single .cbz file')}</span>
            </div>
            <div class="toggle-switch ${this.settings.saveDownloadsAsCbz ? 'on' : ''}" data-toggle-setting="saveDownloadsAsCbz" role="switch" aria-checked="${this.settings.saveDownloadsAsCbz}">
              <div class="toggle-thumb"></div>
            </div>
          </div>
        </div>
      </section>
      <!-- 1E. UNDUHAN — OTOMATISASI (Ala Mobile Gambar 1) -->
      <section class="v1-settings-section has-top-divider" style="${activeSub === 'downloads' ? '' : 'display:none;'}">
        <div class="v1-settings-sec-head">
          <h2 class="v1-settings-sec-title">${this.l('Otomatisasi', 'Automation')}</h2>
        </div>
        <div class="v1-settings-group">
          <div class="v1-setting-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.l('Hapus otomatis setelah dibaca', 'Auto-delete after reading')}</span>
              </div>
              <span class="v1-setting-desc">${this.l('Hapus file bab unduhan otomatis setelah selesai dibaca', 'Automatically delete downloaded chapter files after reading')}</span>
            </div>
            <div class="toggle-switch ${this.settings.autoDeleteAfterRead ? 'on' : ''}" data-toggle-setting="autoDeleteAfterRead" role="switch" aria-checked="${this.settings.autoDeleteAfterRead}">
              <div class="toggle-thumb"></div>
            </div>
          </div>
          <div class="v1-setting-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.l('Unduh otomatis bab berikutnya', 'Auto-download next chapter')}</span>
              </div>
              <span class="v1-setting-desc">${this.l('Otomatis antrekan bab berikutnya saat membaca', 'Automatically queue the next chapter while reading')}</span>
            </div>
            <div class="toggle-switch ${this.settings.autoDownloadNextChapter ? 'on' : ''}" data-toggle-setting="autoDownloadNextChapter" role="switch" aria-checked="${this.settings.autoDownloadNextChapter}">
              <div class="toggle-thumb"></div>
            </div>
          </div>
          <div class="v1-setting-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.l('Pra-muat bab berikutnya', 'Preload next chapter')}</span>
              </div>
              <span class="v1-setting-desc">${this.l('Muat halaman berikutnya lebih awal agar gulir mulus', 'Preload upcoming pages early for smooth scrolling')}</span>
            </div>
            <div class="toggle-switch ${this.settings.preloadNextChapter ? 'on' : ''}" data-toggle-setting="preloadNextChapter" role="switch" aria-checked="${this.settings.preloadNextChapter}">
              <div class="toggle-thumb"></div>
            </div>
          </div>
          <div class="v1-setting-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.l('Lewati bab yang difilter', 'Skip filtered chapters')}</span>
              </div>
              <span class="v1-setting-desc">${this.l('Lewati bab terfilter saat berpindah bab', 'Skip filtered chapters when navigating between chapters')}</span>
            </div>
            <div class="toggle-switch ${this.settings.skipFilteredChapters ? 'on' : ''}" data-toggle-setting="skipFilteredChapters" role="switch" aria-checked="${this.settings.skipFilteredChapters}">
              <div class="toggle-thumb"></div>
            </div>
          </div>
        </div>
      </section>
      <!-- 2. PEMROSESAN & KUALITAS GAMBAR (SKIASHARP) -->
      <section class="v1-settings-section has-top-divider" style="${activeSub === 'reader' ? '' : 'display:none;'}">
        <div class="v1-settings-sec-head">
          <h2 class="v1-settings-sec-title">${this.t('settings.appearance.imageHeaderTitle')}</h2>
        </div>
        <div class="v1-settings-group">
          <!-- 1. Smart Image Optimization -->
          <div class="v1-setting-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.t('settings.appearance.smartOptTitle')}</span>
              </div>
              <span class="v1-setting-desc">${this.t('settings.appearance.smartOptDesc')}</span>
            </div>
            <div class="toggle-switch ${this.settings.enableImageOptimization ? 'on' : ''}" data-toggle-setting="enableImageOptimization" role="switch" aria-checked="${this.settings.enableImageOptimization}">
              <div class="toggle-thumb"></div>
            </div>
          </div>
          <!-- 2. Max Image Dimension -->
          <div class="v1-setting-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.t('settings.appearance.maxResTitle')}</span>
              </div>
              <span class="v1-setting-desc">${this.t('settings.appearance.maxResDesc')}</span>
            </div>
            ${renderSettingDropdown(
              'selMaxImageDimension',
              [
                { value: '1600', label: `1600 px (${this.settings.appLanguage === 'en' ? 'Memory Saver' : 'Hemat Memori'})` },
                { value: '2160', label: this.t('settings.appearance.maxRes2160') },
                { value: '2880', label: this.t('settings.appearance.maxRes2880') },
                { value: '0', label: this.t('settings.appearance.maxRes0') }
              ],
              String(this.settings.maxImageDimension ?? 2160),
              this.t('settings.appearance.maxResTitle')
            )}
          </div>
        </div>
      </section>
      <!-- 2. PINTASAN KEYBOARD (HOTKEYS) -->
      <section class="v1-settings-section" style="${activeSub === 'hotkeys' ? '' : 'display:none;'}">
        <div class="v1-settings-sec-head" style="display:flex; justify-content:space-between; align-items:center; width:100%;">
          <div style="display:flex; align-items:center; gap:8px;">
            <h2 class="v1-settings-sec-title">${this.t('settings.hotkeys.headerTitle')}</h2>
          </div>
          <button id="btnResetAllHotkeys" class="v1-setting-btn blue-soft" style="font-size:12px; height:28px; padding:0 12px; gap:6px; display:${this.hasCustomHotkeys() ? 'inline-flex' : 'none'}; align-items:center;" title="${escapeXml(this.t('settings.hotkeys.resetAll'))}">
            ${getIconSvg('restart-linear', { size: 14 })}
            <span>${this.t('settings.hotkeys.resetAll')}</span>
          </button>
        </div>
        <div class="v1-settings-group">
          <div class="v1-hotkey-group-header">
            <span class="v1-hotkey-group-title">${this.t('settings.hotkeys.groupReader')}</span>
            <span class="v1-hotkey-group-hint">${this.t('settings.hotkeys.headerDesc')}</span>
          </div>
          ${readerHotkeysHtml}

          <div class="v1-hotkey-group-header">
            <span class="v1-hotkey-group-title">${this.t('settings.hotkeys.groupGlobal')}</span>
            <span class="v1-hotkey-group-hint">${this.t('settings.hotkeys.headerDesc')}</span>
          </div>
          ${globalHotkeysHtml}
        </div>
      </section>
      <!-- 3. PUSTAKA & PEMBARUAN (Fitur Asli Yomic v1 LibraryHeader) -->
      <section class="v1-settings-section" style="${activeSub === 'library' ? '' : 'display:none;'}">
        <div class="v1-settings-sec-head">
          <h2 class="v1-settings-sec-title">${this.t('settings.library.headerTitle')}</h2>
        </div>
        <div class="v1-settings-group">
          <div class="v1-setting-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.t('settings.library.autoSyncStartupTitle')}</span>
              </div>
              <span class="v1-setting-desc">${this.t('settings.library.autoSyncStartupDesc')}</span>
            </div>
            <div class="toggle-switch ${this.settings.autoSyncStartup ? 'on' : ''}" data-toggle-setting="autoSyncStartup" role="switch" aria-checked="${this.settings.autoSyncStartup}">
              <div class="toggle-thumb"></div>
            </div>
          </div>
          <div class="v1-setting-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.t('settings.library.smartUpdateTitle')}</span>
              </div>
              <span class="v1-setting-desc">${this.t('settings.library.smartUpdateDesc')}</span>
            </div>
            <div class="toggle-switch ${this.settings.smartUpdate ? 'on' : ''}" data-toggle-setting="smartUpdate" role="switch" aria-checked="${this.settings.smartUpdate}">
              <div class="toggle-thumb"></div>
            </div>
          </div>
          <div class="v1-setting-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.t('settings.library.bgUpdateTitle')}</span>
              </div>
              <span class="v1-setting-desc">${this.t('settings.library.bgUpdateDesc')}</span>
            </div>
            ${renderSettingDropdown(
              'selBackgroundUpdate',
              [
                { value: 'off', label: this.t('settings.library.bgOff') },
                { value: '6h', label: this.t('settings.library.bg6h') },
                { value: '12h', label: this.t('settings.library.bg12h') },
                { value: '24h', label: this.t('settings.library.bg24h') }
              ],
              this.settings.backgroundUpdateInterval || 'off',
              this.t('settings.library.bgUpdateTitle')
            )}
          </div>
        </div>
      </section>
      <!-- 3. JARINGAN & DNS -->
      <section class="v1-settings-section" style="${activeSub === 'network' ? '' : 'display:none;'}">
        <div class="v1-settings-sec-head">
          <h2 class="v1-settings-sec-title">${this.t('settings.tab.network')}</h2>
        </div>
        <div class="v1-settings-group">
          <!-- Fitur Asli Yomic v1: Offline Mode -->
          <div class="v1-setting-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.t('settings.network.offlineTitle')}</span>
              </div>
              <span class="v1-setting-desc">${this.t('settings.network.offlineDesc')}</span>
            </div>
            <div class="toggle-switch ${this.settings.isOfflineMode ? 'on' : ''}" data-toggle-setting="isOfflineMode" role="switch" aria-checked="${this.settings.isOfflineMode}">
              <div class="toggle-thumb"></div>
            </div>
          </div>
          <div class="v1-setting-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.t('settings.network.dohTitle')}</span>
              </div>
              <span class="v1-setting-desc">${this.t('settings.network.dohDesc')}</span>
            </div>
            ${renderSettingDropdown(
              'selDohProvider',
              [
                { value: 'Nonaktif', label: this.t('settings.network.dohSystem') },
                { value: 'Cloudflare (1.1.1.1)', label: 'Cloudflare (1.1.1.1)' },
                { value: 'Google (8.8.8.8)', label: 'Google (8.8.8.8)' },
                { value: 'AdGuard (94.140.14.14)', label: 'AdGuard (94.140.14.14)' }
              ],
              this.settings.dohProvider || 'Nonaktif',
              'DNS over HTTPS'
            )}
          </div>
          <!-- Fitur Asli Yomic v1: Source Status Interval -->
          <div class="v1-setting-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.t('settings.network.sourceStatusTitle')}</span>
              </div>
              <span class="v1-setting-desc">${this.t('settings.network.sourceStatusDesc')}</span>
            </div>
            ${renderSettingDropdown(
              'selSourceStatusInterval',
              [
                { value: '15m', label: this.t('settings.network.interval15m') },
                { value: '30m', label: this.t('settings.network.interval30m') },
                { value: '1h', label: this.t('settings.network.interval1h') },
                { value: 'off', label: this.t('settings.network.intervalOff') }
              ],
              this.settings.sourceStatusInterval || '15m',
              this.t('settings.network.sourceStatusTitle')
            )}
          </div>
        </div>
      </section>
      <!-- 4A. CADANGAN DAN PEMULIHAN (Ala Mobile Gambar 2) -->
      <section class="v1-settings-section" style="${activeSub === 'storage' ? '' : 'display:none;'}">
        <div class="v1-settings-sec-head">
          <h2 class="v1-settings-sec-title">${this.l('Cadangan dan pemulihan', 'Backup and restore')}</h2>
        </div>
        <div class="v1-settings-group">
          <button type="button" id="btnCreateBackup" class="v1-setting-row is-action-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.l('Buat Cadangan', 'Create Backup')}</span>
              </div>
              <span id="createBackupDesc" class="v1-setting-desc">${escapeXml(this.settings.lastBackupText || this.l('Siap dicadangkan (.yomicbackup)', 'Ready to back up (.yomicbackup)'))}</span>
            </div>
          </button>
          <button type="button" id="btnRestoreBackup" class="v1-setting-row is-action-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.l('Pulihkan Cadangan', 'Restore Backup')}</span>
              </div>
              <span id="restoreBackupDesc" class="v1-setting-desc">${this.l('Impor pustaka, kategori, dan progres baca dari file', 'Import library, categories, and reading progress from file')}</span>
            </div>
          </button>
        </div>
      </section>
      <!-- 4B. PENYIMPANAN & CACHE (Ala Mobile Gambar 2) -->
      <section class="v1-settings-section has-top-divider" style="${activeSub === 'storage' ? '' : 'display:none;'}">
        <div class="v1-settings-sec-head">
          <h2 class="v1-settings-sec-title">${this.l('Penyimpanan &amp; Cache', 'Storage &amp; Cache')}</h2>
        </div>
        <div class="v1-settings-group">
          <div class="v1-setting-row is-select-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.l('Kapasitas cache pembaca', 'Reader cache capacity')}</span>
              </div>
            </div>
            ${renderSettingDropdown(
              'selMaxReaderCacheSize',
              [
                { value: '250MB', label: '250 MB' },
                { value: '500MB', label: '500 MB' },
                { value: '1GB', label: '1 GB' },
                { value: '2GB', label: '2 GB' }
              ],
              this.settings.maxReaderCacheSize || '500MB',
              this.l('Kapasitas cache pembaca', 'Reader cache capacity')
            )}
          </div>
          <button type="button" id="btnDangerClearCache" class="v1-setting-row is-action-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.l('Bersihkan Cache', 'Clear Cache')}</span>
              </div>
              <span id="clearCacheDesc" class="v1-setting-desc">${this.l('Terpakai:', 'Used:')} ${escapeXml(this.settings.dbSizeText)}</span>
            </div>
          </button>
          <button type="button" id="btnClearNonLibraryCache" class="v1-setting-row is-action-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.l('Hapus Cache Non-Pustaka', 'Clear Non-Library Cache')}</span>
              </div>
              <span id="clearNonLibDesc" class="v1-setting-desc">${this.l('Hapus berkas gambar dan metadata komik yang tidak tersimpan di pustaka', 'Delete image files and comic metadata not saved in library')}</span>
            </div>
          </button>
          <button type="button" id="btnOpenDownloadsFolder" class="v1-setting-row is-action-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.l('Folder Penyimpanan Unduhan', 'Downloads Storage Folder')}</span>
              </div>
              <span class="v1-setting-desc">${escapeXml(this.settings.downloadsFolder)}</span>
            </div>
          </button>
          <button type="button" id="btnExportAppLogs" class="v1-setting-row is-action-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.l('Ekspor Log Aplikasi', 'Export App Logs')}</span>
              </div>
              <span class="v1-setting-desc">${this.l('Simpan berkas catatan aktivitas dan diagnostik aplikasi (.log)', 'Save application activity and diagnostic log files (.log)')}</span>
            </div>
          </button>
          <button type="button" id="btnDangerClearHistory" class="v1-setting-row is-action-row is-danger-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.l('Kosongkan riwayat baca', 'Clear reading history')}</span>
              </div>
              <span id="clearHistoryDesc" class="v1-setting-desc">${this.l('Hapus daftar riwayat tanpa menghapus komik dari pustaka', 'Clear history list without removing comics from library')}</span>
            </div>
          </button>
        </div>
      </section>
      <!-- 5. PELACAKAN (TRACKER) -->
      <section id="settingsTrackerSection" class="v1-settings-section" style="${activeSub === 'tracker' ? '' : 'display:none;'}">
        <div class="v1-settings-sec-head">
          <h2 class="v1-settings-sec-title">${this.l('Layanan Pelacak', 'Tracking Services')}</h2>
        </div>
        <div class="v1-settings-group">
          <button type="button" id="btnToggleMalConnect" class="v1-setting-row is-action-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">MyAnimeList</span>
              </div>
              <span id="malConnectDesc" class="v1-setting-desc">
                ${
                  this.settings.isMalConnected
                    ? this.t('settings.tracker.malConnectedAs', { user: escapeXml(this.settings.malConnectedUser) })
                    : this.t('settings.tracker.malNotConnected')
                }
              </span>
            </div>
          </button>
          <div class="v1-setting-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.t('settings.tracker.autoSyncTitle')}</span>
              </div>
              <span class="v1-setting-desc">${this.t('settings.tracker.autoSyncDesc')}</span>
            </div>
            <div class="toggle-switch ${this.settings.autoSyncTracker ? 'on' : ''}" data-toggle-setting="autoSyncTracker" role="switch" aria-checked="${this.settings.autoSyncTracker}">
              <div class="toggle-thumb"></div>
            </div>
          </div>
        </div>
      </section>
      <!-- 5. TENTANG (Ala Mobile Gambar 1) -->
      <section class="v1-settings-section" style="${activeSub === 'about' ? '' : 'display:none;'}">
        <div class="v1-about-mobile-hero">
          <img src="app-logo.png" class="v1-about-mobile-logo" alt="Yomic Logo" />
          <span class="v1-about-mobile-title">Yomic</span>
        </div>

        <div class="v1-settings-hub-divider" style="margin: 12px 0;"></div>

        <div class="v1-settings-group">
          <div class="v1-setting-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.l('Versi', 'Version')}</span>
              </div>
              <span class="v1-setting-desc">Stable 2.4.0 (Windows Desktop)</span>
            </div>
          </div>

          <button type="button" id="btnAboutCheckUpdate" class="v1-setting-row is-action-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span id="aboutCheckUpdateTitle" class="v1-setting-title">${this.l('Periksa Pembaruan', 'Check for Updates')}</span>
              </div>
              <span id="aboutCheckUpdateDesc" class="v1-setting-desc">${this.l('Periksa versi rilis terbaru dari server', 'Check latest release version from server')}</span>
            </div>
          </button>

          <button type="button" id="btnToggleReleaseNotes" class="v1-setting-row is-action-row" aria-expanded="false">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.l('Catatan rilis', 'Release notes')}</span>
              </div>
              <span class="v1-setting-desc">v2.4.0</span>
            </div>
            <span id="aboutReleaseNotesChevron" class="v1-about-release-chevron">
              ${getIconSvg('alt-arrow-down-linear', { size: 18 })}
            </span>
          </button>
          <div id="aboutReleaseNotesPanel" class="v1-about-release-notes" style="display:none;">
            <div class="v1-about-release-head">[2.4.0] - Major Update</div>
            <div class="v1-about-release-subhead">Update Details:</div>
            <ul class="v1-about-release-list">
              <li>Add deep SSRF &amp; DNS-rebinding defense in UrlSecurityHelper.cs blocking RFC 6890/2544/TEST-NET IPv4 ranges and IPv6 transition/tunnel ranges (::/96, SIIT, NAT64, 6to4, Teredo, RFC 6666), plus dual-stack DoH/System-DNS resolution verification (IsSafeResolvedPublicUrlAsync with 60s anti-rebinding cache), IsHostOrSubdomain helper, and strict magic-byte image payload validation (IsValidImagePayload for JPEG, PNG, GIF, WebP, BMP, TIFF, ICO, AVIF/HEIF, JPEG XL, and strict SVG).</li>
              <li>Add manual per-hop redirect validation (IsTrustedUpdateDownloadUri), 250 MB installer download safety cap, concurrency guard (_isInstalling), and official .sha256 sidecar checksum fetching &amp; parsing (ParseSha256SidecarText &amp; TryFetchSidecarSha256Async) in UpdateService.</li>
              <li>Add unified process-wide SQLite write &amp; maintenance lock (MangaDbContext.DatabaseWriteGate) shared across LibraryService, BackendIpcController writes, and BackupService restore swaps, paired with ReplaceSqliteFileWithRetry, strict WAL/SHM removal checks against SQLITE_CORRUPT, and DownloadService.WaitForActiveDownloadsToStopAsync preserving .tmp progress before restore.</li>
              <li>Add deferred background Jint plugin loading (SourceManager deferExtensionLoad + EnsureExtensionsLoadedAsync) and non-blocking cached announcement bootstrap (event:announcements-updated) so MainWindow opens instantly without UI-thread freezes.</li>
              <li>Add bounded single-flight next-chapter page list preloading (_preloadedPageListCache, max 32 entries, 10-min TTL) triggered within 3 pages of chapter end in BackendIpcController.Reader.cs and transient src-* memory eviction during long browsing sessions.</li>
              <li>Add manual per-hop redirect handling with strict host-scoped Cookie &amp; Authorization isolation across DownloadService (SendPageRequestWithSafeRedirectsAsync), WebResourceImageProxy, and JsMangaSource to prevent cross-origin cookie/auth leakage on 302 redirects, plus magic-byte image validation and curl --resolve DoH public-IP pinning.</li>
              <li>Add host-specific Cloudflare pre-solved cookie lookup (HasPreSolvedCookiesFor), parallel IPv4+IPv6 DoH SSRF guards with race-free ConditionalWeakTable page tracking (_guardedPages), automatic headless popup closure, and parent-domain cookie merge cleanup in CloudflareBypassService.</li>
              <li>Add parallel IPv4/IPv6 DoH resolution with 5-minute caching and zero ISP DNS leakage when DoH succeeds (NetworkService.ResolveHostIpsAsync), socket-level SSRF blocking with 3s per-IP connect caps, and single-flight connectivity checks (_isCheckingConnectivity).</li>
              <li>Add official-GitHub-only index.min.json fetching in ExtensionRepositoryService (preventing third-party CDN manifest forgery), cached repo DoH resolution (_repoDohCache), and atomic index cache writes (WriteIndexCacheAtomically).</li>
              <li>Add MyAnimeList auth epoch protection (_authEpoch &amp; TryApplyRefreshedTokens in MyAnimeListService) preventing stale background token refreshes from reviving logged-out sessions, plus strict host-only keyword matching in SourceIdMigrationService.</li>
              <li>Add optimistic WebUI updates with full state snapshot rollback on backend errors across Library (library.ts), Manga Detail (manga-detail.ts — MAL tracker, bookmarks, chapter read/download actions, Hiatus status badge), and Settings (settings.ts — history/cache/library maintenance), plus real-time sync of completed downloads into Library downloadedCount and filters (backend-sync.ts).</li>
              <li>Add Reader image fit/zoom builder (buildReaderImgStyle), debounced progress-save flush/cancel before chapter switching to prevent cross-chapter progress corruption, URL-hashed cover cache precedence in WebResourceImageProxy, modal/busy-dialog Tab focus trapping (app.ts &amp; settings.ts), capture-phase Escape isolation in tutorial.ts, and formatted reading progress in updates-history.ts.</li>
              <li>Improve 90+ bundled JavaScript extensions in Plugins/Catalog with getMangaList sort ("latest" vs popular) support (including komiku.js and Blogger/Madara/MangaStream sources), mangadex.js chapter feed offset &amp; totalChapters pagination fix, cleaned unsupported filter metadata, and null-safe JSON feed parsing.</li>
              <li>Improve Inno Setup installer (Installer/Yomic.iss) with graceful close before force-kill so App.OnExit cleanly flushes SQLite WAL/logs and shuts down Puppeteer on upgrades.</li>
              <li>Add upgraded Windows CI pipeline (.github/workflows/ci-desktop.yml enforcing strict tsc compile, automated version parity across csproj/iss/updater/WebUI, -warnaserror Release builds, and clean self-contained publish verification) and massively expanded automated test suites (new BehavioralServicesTests.cs + expanded CoreServicesTests, IntegrityAndCatalogTests, and WebUI node:test coverage).</li>
            </ul>
          </div>
        </div>

        <div class="v1-settings-hub-divider" style="margin: 12px 0;"></div>

        <div class="v1-settings-group">
          <button type="button" id="btnDangerResetAll" class="v1-setting-row is-action-row is-danger-row">
            <div class="v1-setting-info">
              <div class="v1-setting-title-row">
                <span class="v1-setting-title">${this.l('Reset Aplikasi', 'Reset Application')}</span>
              </div>
              <span class="v1-setting-desc">${this.l('Kembalikan seluruh pengaturan dan data ke bawaan', 'Restore all settings and data to factory defaults')}</span>
            </div>
          </button>
        </div>
      </section>
    </div>
  `;
  const persistSetting = (key: string, value: unknown) => {
    this.persistLocalCache();
    if (this.bridge.isWebViewHost) {
      void this.bridge
        .invoke<AppSettingsState>('settings:update', { key, value })
        .then((updated) => {
          if (updated) {
            Object.assign(this.settings, updated);
            this.persistLocalCache();
          }
        })
        .catch(() => {
          this.showToast(this.l('Gagal menyimpan pengaturan ke backend.', 'Failed to save setting to backend.'), 'warning');
        });
    }
  };

  // Hub Category Navigation & Sub-Page Back Button
  container.querySelectorAll<HTMLElement>('[data-open-settings-sub]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const sub = btn.dataset.openSettingsSub;
      if (sub) {
        this.activeSettingsTab = sub;
        container.scrollTop = 0;
        if (container.parentElement) container.parentElement.scrollTop = 0;
        this.renderSettingsView(container);
      }
    });
  });

  document.getElementById('btnBackToSettingsHub')?.addEventListener('click', () => {
    this.activeSettingsTab = 'hub';
    container.scrollTop = 0;
    if (container.parentElement) container.parentElement.scrollTop = 0;
    this.renderSettingsView(container);
  });

  // Toggle switches (mouse + keyboard accessible via Tab + Space/Enter, with accessible label & focus preservation)
  container.querySelectorAll<HTMLElement>('[data-toggle-setting]').forEach((el) => {
    if (!el.hasAttribute('tabindex')) {
      el.tabIndex = 0;
    }
    if (!el.hasAttribute('aria-label') && !el.hasAttribute('aria-labelledby')) {
      const labelText = el.closest('.v1-setting-row')?.querySelector('.v1-setting-title')?.textContent?.trim();
      if (labelText) el.setAttribute('aria-label', labelText);
    }
    let triggeredByKeyboard = false;
    el.addEventListener('keydown', (ev) => {
      if (ev.key === 'Enter' || ev.key === ' ') {
        ev.preventDefault();
        triggeredByKeyboard = true;
        el.click();
      }
    });
    el.addEventListener('click', () => {
      const shouldRestoreFocus = triggeredByKeyboard;
      triggeredByKeyboard = false;
      const key = el.dataset.toggleSetting as keyof AppSettingsState | undefined;
      if (!key) return;
      const currentVal = this.settings[key];
      if (typeof currentVal === 'boolean') {
        const nextVal = !currentVal;
        (this.settings[key] as boolean) = nextVal;
        persistSetting(key, nextVal);
        if (key === 'compactSidebar') {
          this.toggleSidebarCollapse(this.settings.compactSidebar);
        } else if (key === 'isDarkMode') {
          this.applyTheme(this.settings.isDarkMode);
          this.showToast(
            this.settings.isDarkMode
              ? this.l('Mode gelap diaktifkan.', 'Dark mode enabled.')
              : this.l('Mode gelap dinonaktifkan.', 'Dark mode disabled.')
          );
        } else if (key === 'autoDarkReader') {
          this.showToast(
            this.settings.autoDarkReader
              ? this.l('Mode gelap otomatis saat membaca diaktifkan.', 'Auto dark mode while reading enabled.')
              : this.l('Mode gelap otomatis saat membaca dinonaktifkan.', 'Auto dark mode while reading disabled.')
          );
        } else if (key === 'showNsfw') {
          this.showToast(
            this.settings.showNsfw
              ? this.l('Katalog sumber NSFW (18+) ditampilkan.', 'NSFW (18+) source catalog shown.')
              : this.l('Katalog sumber NSFW (18+) disembunyikan.', 'NSFW (18+) source catalog hidden.')
          );
          if (!this.settings.showNsfw) {
            this.activeBrowseSourceId = null;
          }
          if (this.bridge.isWebViewHost) {
            void this.bridge
              .invoke<ExtensionItem[]>('ext:list', { refreshRemote: false })
              .then((list) => {
                if (Array.isArray(list)) {
                  this.extensions.length = 0;
                  this.extensions.push(...list);
                  this.persistLocalCache();
                }
              })
              .catch(() => {});
          }
        } else if (key === 'blurExplicitCovers') {
          this.showToast(
            this.settings.blurExplicitCovers
              ? this.l('Penyamaran sampul eksplisit diaktifkan.', 'Explicit cover blur enabled.')
              : this.l('Penyamaran sampul eksplisit dinonaktifkan.', 'Explicit cover blur disabled.')
          );
        } else if (key === 'autoSyncTracker') {
          this.showToast(
            this.settings.autoSyncTracker
              ? this.l('Sinkronisasi progres bab otomatis diaktifkan.', 'Auto chapter progress sync enabled.')
              : this.l('Sinkronisasi progres bab otomatis dinonaktifkan.', 'Auto chapter progress sync disabled.')
          );
        } else if (key === 'preloadNextChapter') {
          this.showToast(
            this.settings.preloadNextChapter
              ? this.l('Muat awal bab berikutnya diaktifkan.', 'Preload next chapter enabled.')
              : this.l('Muat awal bab berikutnya dinonaktifkan.', 'Preload next chapter disabled.')
          );
        } else if (key === 'autoDownloadNextChapter') {
          this.showToast(
            this.settings.autoDownloadNextChapter
              ? this.l('Unduh otomatis bab berikutnya diaktifkan.', 'Auto-download next chapter enabled.')
              : this.l('Unduh otomatis bab berikutnya dinonaktifkan.', 'Auto-download next chapter disabled.')
          );
        } else if (key === 'skipFilteredChapters') {
          this.showToast(
            this.settings.skipFilteredChapters
              ? this.l('Lewati bab yang difilter diaktifkan.', 'Skip filtered chapters enabled.')
              : this.l('Lewati bab yang difilter dinonaktifkan.', 'Skip filtered chapters disabled.')
          );
        } else if (key === 'showUnreadBadges') {
          this.showToast(
            this.settings.showUnreadBadges
              ? this.l('Lencana belum dibaca di sampul ditampilkan.', 'Unread badges on covers shown.')
              : this.l('Lencana belum dibaca di sampul disembunyikan.', 'Unread badges on covers hidden.')
          );
        } else if (key === 'autoSyncStartup') {
          this.showToast(
            this.settings.autoSyncStartup
              ? this.l('Sinkronisasi otomatis saat mulai diaktifkan.', 'Auto-sync on startup enabled.')
              : this.l('Sinkronisasi otomatis saat mulai dinonaktifkan.', 'Auto-sync on startup disabled.')
          );
        } else if (key === 'smartUpdate') {
          this.showToast(
            this.settings.smartUpdate
              ? this.l('Pembaruan Cerdas (Smart Update) diaktifkan.', 'Smart Update enabled.')
              : this.l('Pembaruan Cerdas (Smart Update) dinonaktifkan.', 'Smart Update disabled.')
          );
        } else if (key === 'isOfflineMode') {
          this.showToast(
            this.settings.isOfflineMode
              ? this.l('Mode Offline aktif (Hanya membaca komik terunduh).', 'Offline Mode active (Downloaded comics only).')
              : this.l('Mode Offline dinonaktifkan (Koneksi online aktif).', 'Offline Mode disabled (Online connection active).')
          );
        } else if (key === 'enableAutoCropBorders') {
          this.showToast(
            this.settings.enableAutoCropBorders
              ? this.l('Potong tepi putih otomatis (Auto-Crop) diaktifkan.', 'Auto-Crop borders enabled.')
              : this.l('Potong tepi putih otomatis (Auto-Crop) dinonaktifkan.', 'Auto-Crop borders disabled.')
          );
        } else if (key === 'enableImageSharpening') {
          this.showToast(
            this.settings.enableImageSharpening
              ? this.l('Penajaman gambar (Sharpening) diaktifkan.', 'Image sharpening enabled.')
              : this.l('Penajaman gambar (Sharpening) dinonaktifkan.', 'Image sharpening disabled.')
          );
        } else if (key === 'enableImageOptimization') {
          this.showToast(
            this.settings.enableImageOptimization
              ? this.l('Optimasi gambar cerdas diaktifkan.', 'Smart image optimization enabled.')
              : this.l('Optimasi gambar cerdas dinonaktifkan.', 'Smart image optimization disabled.')
          );
        } else if (key === 'saveDownloadsAsCbz') {
          this.showToast(
            this.settings.saveDownloadsAsCbz
              ? this.l('Unduhan baru akan disimpan sebagai arsip .cbz.', 'New downloads will be saved as .cbz archives.')
              : this.l('Unduhan baru akan disimpan sebagai folder gambar biasa.', 'New downloads will be saved as regular image folders.')
          );
        } else if (key === 'autoDeleteAfterRead') {
          this.showToast(
            this.settings.autoDeleteAfterRead
              ? this.l('Hapus otomatis bab unduhan setelah selesai dibaca diaktifkan.', 'Auto-delete downloaded chapters after reading enabled.')
              : this.l('Hapus otomatis bab unduhan setelah selesai dibaca dinonaktifkan.', 'Auto-delete downloaded chapters after reading disabled.')
          );
        }
        this.renderSettingsView(container);
        if (shouldRestoreFocus) {
          container.querySelector<HTMLElement>(`[data-toggle-setting="${key}"]`)?.focus({ preventScroll: true });
        }
      }
    });
  });

  // Mobile-Style Centered Selection Pop-Up Modal for all Settings Select Rows
  const openSettingSelectModal = (
    titleText: string,
    options: { value: string; label: string }[],
    currentValue: string,
    onSelect: (val: string, label: string) => void
  ) => {
    const prevSelectOverlay = document.getElementById('settingSelectModalOverlay') as (HTMLElement & { _closeModal?: () => void }) | null;
    if (prevSelectOverlay?._closeModal) {
      prevSelectOverlay._closeModal();
    } else {
      prevSelectOverlay?.remove();
    }
    const checkIcon = getIconSvg('check', { size: 16, strokeWidth: 2.4 });
    const overlay = document.createElement('div') as HTMLDivElement & { _closeModal?: () => void };
    overlay.id = 'settingSelectModalOverlay';
    overlay.className = 'v1-setting-select-modal-overlay';
    overlay.innerHTML = `
      <div class="v1-setting-select-modal" role="dialog" aria-modal="true" aria-label="${escapeXml(titleText)}">
        <h3 class="v1-setting-select-title">${escapeXml(titleText)}</h3>
        <div class="v1-setting-select-list" role="listbox">
          ${options
            .map((opt) => {
              const isSelected = opt.value === currentValue;
              return `
                <button type="button" class="v1-setting-select-option ${isSelected ? 'selected' : ''}" data-select-value="${escapeXml(opt.value)}" role="option" aria-selected="${isSelected ? 'true' : 'false'}">
                  <span class="v1-setting-select-check">${isSelected ? checkIcon : ''}</span>
                  <span class="v1-setting-select-label">${escapeXml(opt.label)}</span>
                </button>
              `;
            })
            .join('')}
        </div>
      </div>
    `;
    document.body.appendChild(overlay);
    const previousFocus = document.activeElement as HTMLElement | null;

    const closeModal = () => {
      window.removeEventListener('keydown', onKeyDown, true);
      overlay.remove();
      if (previousFocus && document.contains(previousFocus)) {
        previousFocus.focus();
      }
    };
    overlay._closeModal = closeModal;
    const onKeyDown = (ev: KeyboardEvent) => {
      if (ev.key === 'Escape') {
        ev.preventDefault();
        ev.stopPropagation();
        closeModal();
        return;
      }
      if (ev.key === 'Tab') {
        const focusables = Array.from(overlay.querySelectorAll<HTMLElement>('button:not([disabled]), [tabindex]:not([tabindex="-1"])'));
        if (focusables.length > 0) {
          const first = focusables[0];
          const last = focusables[focusables.length - 1];
          if (ev.shiftKey && document.activeElement === first) {
            ev.preventDefault();
            last.focus();
          } else if (!ev.shiftKey && document.activeElement === last) {
            ev.preventDefault();
            first.focus();
          }
        }
      }
    };
    window.addEventListener('keydown', onKeyDown, true);
    const initialFocus = overlay.querySelector<HTMLElement>('.v1-setting-select-option.selected, .v1-setting-select-option');
    initialFocus?.focus();

    overlay.addEventListener('click', (ev) => {
      if (ev.target === overlay) {
        closeModal();
      }
    });

    overlay.querySelectorAll<HTMLButtonElement>('.v1-setting-select-option').forEach((btn) => {
      btn.addEventListener('click', (ev) => {
        ev.stopPropagation();
        const val = btn.dataset.selectValue ?? '';
        const label = btn.querySelector('.v1-setting-select-label')?.textContent ?? '';
        closeModal();
        onSelect(val, label);
      });
    });
  };

  container.querySelectorAll<HTMLElement>('.v1-setting-dropdown').forEach((dropdown) => {
    const trigger = dropdown.querySelector<HTMLButtonElement>('.v1-mal-dropdown-trigger');
    const optionBtns = Array.from(dropdown.querySelectorAll<HTMLButtonElement>('.v1-mal-dropdown-option'));
    const targetId = dropdown.dataset.dropdownFor;
    const row = dropdown.closest<HTMLElement>('.v1-setting-row');

    const handleOpenPopup = (ev: Event) => {
      ev.stopPropagation();
      const input = targetId ? (document.getElementById(targetId) as HTMLInputElement | null) : null;
      const currentVal = input?.value ?? '';
      const rowTitle =
        row?.querySelector('.v1-setting-title')?.textContent?.trim() ||
        trigger?.getAttribute('aria-label') ||
        '';
      const opts = optionBtns.map((btn) => ({
        value: btn.dataset.value ?? '',
        label: btn.querySelector('span')?.textContent ?? ''
      }));

      openSettingSelectModal(rowTitle, opts, currentVal, (val, labelText) => {
        const valueSpan = dropdown.querySelector<HTMLElement>('.v1-mal-dropdown-value');
        if (valueSpan) valueSpan.textContent = labelText;

        optionBtns.forEach((opt) => {
          const selected = (opt.dataset.value ?? '') === val;
          opt.classList.toggle('selected', selected);
          opt.setAttribute('aria-selected', selected ? 'true' : 'false');
        });

        if (input && input.value !== val) {
          input.value = val;
          input.dispatchEvent(new Event('change', { bubbles: true }));
        }
      });
    };

    if (row) {
      row.classList.add('is-select-row');
      row.addEventListener('click', handleOpenPopup);
    }
    trigger?.addEventListener('click', handleOpenPopup);
  });

  // Select dropdowns
  document.getElementById('selMaxImageDimension')?.addEventListener('change', (ev) => {
    const val = Number((ev.target as HTMLSelectElement).value);
    this.settings.maxImageDimension = isNaN(val) ? 2160 : val;
    persistSetting('maxImageDimension', this.settings.maxImageDimension);
    this.showToast(
      val === 0
        ? this.l('Batas resolusi gambar: Tanpa Batas (Ukuran Asli).', 'Image resolution limit: Unlimited (Original Size).')
        : this.l(`Batas resolusi gambar diatur ke: ${val} px.`, `Image resolution limit set to: ${val} px.`)
    );
  });
  document.getElementById('selMaxReaderCacheSize')?.addEventListener('change', (ev) => {
    const val = (ev.target as HTMLSelectElement).value as '250MB' | '500MB' | '1GB' | '2GB';
    this.settings.maxReaderCacheSize = val;
    persistSetting('maxReaderCacheSize', val);
    this.showToast(this.l(`Batas cache pembaca diatur ke: ${val}.`, `Reader cache limit set to: ${val}.`));
  });
  document.getElementById('selBackgroundUpdate')?.addEventListener('change', (ev) => {
    const val = (ev.target as HTMLSelectElement).value as 'off' | '6h' | '12h' | '24h';
    this.settings.backgroundUpdateInterval = val;
    persistSetting('backgroundUpdateInterval', val);
    const label =
      val === 'off'
        ? this.l('Nonaktif', 'Off')
        : val === '6h'
        ? this.l('Tiap 6 Jam', 'Every 6 Hours')
        : val === '12h'
        ? this.l('Tiap 12 Jam', 'Every 12 Hours')
        : this.l('Harian', 'Daily');
    this.showToast(this.l(`Pembaruan latar belakang diatur ke: ${label}.`, `Background update interval set to: ${label}.`));
  });
  document.getElementById('selSourceStatusInterval')?.addEventListener('change', (ev) => {
    const val = (ev.target as HTMLSelectElement).value as '15m' | '30m' | '1h' | 'off';
    this.settings.sourceStatusInterval = val;
    persistSetting('sourceStatusInterval', val);
    const label =
      val === 'off'
        ? this.l('Nonaktif', 'Off')
        : val === '15m'
        ? this.l('Tiap 15 Menit', 'Every 15 Minutes')
        : val === '30m'
        ? this.l('Tiap 30 Menit', 'Every 30 Minutes')
        : this.l('Tiap 1 Jam', 'Every 1 Hour');
    this.showToast(this.l(`Interval status sumber diatur ke: ${label}.`, `Source status interval set to: ${label}.`));
  });
  document.getElementById('btnClearNonLibraryCache')?.addEventListener('click', () => {
    const btn = document.getElementById('btnClearNonLibraryCache') as HTMLButtonElement | null;
    const descEl = document.getElementById('clearNonLibDesc');
    const prevDesc = descEl?.textContent || '';
    const removedNonLib: MangaItem[] = [];
    if (btn) btn.disabled = true;
    if (descEl) descEl.textContent = this.l('Menghapus berkas cache non-pustaka...', 'Clearing non-library cache files...');
    for (let i = this.mangaList.length - 1; i >= 0; i--) {
      if (!this.mangaList[i].inLibrary) {
        removedNonLib.push(this.mangaList[i]);
        this.mangaList.splice(i, 1);
      }
    }
    if (this.bridge.isWebViewHost) {
      void this.bridge
        .invoke<{ dbSizeText?: string }>('settings:clear-non-library', undefined, 60000)
        .then((res) => {
          if (res?.dbSizeText) {
            this.settings.dbSizeText = res.dbSizeText;
          }
          if (this.currentTab === 'settings' && !this.activeDetailMangaId) {
            this.renderSettingsView(container);
          }
          this.showToast(this.l('Seluruh cache gambar komik non-pustaka berhasil dibersihkan.', 'All non-library comic image cache cleared.'));
        })
        .catch(() => {
          this.mangaList.push(...removedNonLib);
          if (descEl) descEl.textContent = prevDesc;
          this.showToast(this.l('Gagal membersihkan cache non-pustaka.', 'Failed to clear non-library cache.'), 'warning');
        })
        .finally(() => {
          if (btn) btn.disabled = false;
        });
      return;
    }
    this.renderSettingsView(container);
    this.showToast(this.l('Seluruh cache gambar komik non-pustaka berhasil dibersihkan.', 'All non-library comic image cache cleared.'));
  });
  document.getElementById('btnExportAppLogs')?.addEventListener('click', () => {
    const btn = document.getElementById('btnExportAppLogs') as HTMLButtonElement | null;
    if (this.bridge.isWebViewHost) {
      if (btn) btn.disabled = true;
      void this.bridge
        .invoke<{ ok?: boolean; cancelled?: boolean; path?: string }>('settings:export-logs', undefined, 180000)
        .then((res) => {
          if (res?.path) {
            this.showToast(this.l(`Log aktivitas diekspor ke: ${res.path}`, `Activity logs exported to: ${res.path}`));
          } else if (res && !res.ok && !res.cancelled) {
            this.showToast(this.l('Gagal mengekspor log aktivitas.', 'Failed to export activity logs.'));
          }
        })
        .catch(() => {
          this.showToast(this.l('Gagal mengekspor log aktivitas.', 'Failed to export activity logs.'));
        })
        .finally(() => {
          if (btn) btn.disabled = false;
        });
      return;
    }
    this.showToast(this.l('Log aktivitas aplikasi berhasil diekspor.', 'Application activity logs exported.'));
  });
  document.getElementById('selAppLanguage')?.addEventListener('change', (ev) => {
    const val = (ev.target as HTMLSelectElement).value === 'en' ? 'en' : 'id';
    this.settings.appLanguage = val;
    persistSetting('appLanguage', val);
    applyStaticDomTranslations(val);
    this.renderCurrentWorkspace(true);
    const langName = val === 'en' ? 'English' : 'Bahasa Indonesia';
    this.showToast(this.t('settings.appearance.languageChanged', { lang: langName }));
  });
  document.getElementById('selDefaultReaderMode')?.addEventListener('change', (ev) => {
    const val = (ev.target as HTMLSelectElement).value as ReaderMode;
    this.settings.defaultReaderMode = val;
    this.readerMode = val;
    persistSetting('defaultReaderMode', val);
    const label = val === 'webtoon' ? this.l('Webtoon (Gulir)', 'Webtoon (Scroll)') : val === 'single' ? this.l('Halaman Tunggal', 'Single Page') : this.l('Dua Halaman', 'Double Page');
    this.showToast(this.l(`Mode baca bawaan diatur ke: ${label}.`, `Default reading mode set to: ${label}.`));
  });
  document.getElementById('selReadingDirection')?.addEventListener('change', (ev) => {
    const val = (ev.target as HTMLSelectElement).value as 'ltr' | 'rtl';
    this.settings.readingDirection = val;
    persistSetting('readingDirection', val);
    const label = val === 'ltr' ? this.l('Kiri ke Kanan (Manhwa / Barat)', 'Left to Right (Manhwa / Western)') : this.l('Kanan ke Kiri (Manga Jepang)', 'Right to Left (Japanese Manga)');
    this.showToast(this.l(`Arah membaca diatur ke: ${label}.`, `Reading direction set to: ${label}.`));
  });
  document.getElementById('selReaderFitMode')?.addEventListener('change', (ev) => {
    const val = (ev.target as HTMLSelectElement).value as 'fit-width' | 'fit-height' | 'original';
    this.settings.readerFitMode = val;
    persistSetting('readerFitMode', val);
    const label = val === 'fit-width' ? this.l('Pas Lebar', 'Fit Width') : val === 'fit-height' ? this.l('Pas Tinggi', 'Fit Height') : this.l('Ukuran Asli', 'Original Size');
    this.showToast(this.l(`Skala tampilan gambar diatur ke: ${label}.`, `Image fit mode set to: ${label}.`));
  });
  document.getElementById('selDownloadSpeedMode')?.addEventListener('change', (ev) => {
    const raw = (ev.target as HTMLSelectElement).value;
    const val = (raw === 'safe' || raw === 'max' ? raw : 'fast') as 'safe' | 'fast' | 'max';
    this.settings.downloadSpeedMode = val;
    persistSetting('downloadSpeedMode', val);
    const label =
      val === 'safe'
        ? this.l('Aman (2 Bab × 4 Halaman)', 'Safe (2 Chapters × 4 Pages)')
        : val === 'max'
        ? this.l('Maksimal (4 Bab × 8 Halaman)', 'Maximum (4 Chapters × 8 Pages)')
        : this.l('Cepat — Rekomendasi (3 Bab × 6 Halaman)', 'Fast — Recommended (3 Chapters × 6 Pages)');
    this.showToast(this.l(`Kecepatan unduhan diatur ke: ${label}.`, `Download speed set to: ${label}.`));
  });

  // Action buttons
  document.getElementById('btnRefreshLibraryCovers')?.addEventListener('click', () => {
    const btn = document.getElementById('btnRefreshLibraryCovers') as HTMLButtonElement | null;
    if (btn) {
      btn.disabled = true;
      btn.textContent = this.l('Menyegarkan...', 'Refreshing...');
    }
    this.showToast(this.l('Menyegarkan seluruh gambar sampul komik di Pustaka...', 'Refreshing all comic cover images in Library...'));
    if (this.bridge.isWebViewHost) {
      void this.bridge
        .invoke<{ ok?: boolean; mangaList?: MangaItem[]; dbSizeText?: string }>('settings:refresh-covers', undefined, 180000)
        .then((res) => {
          if (res?.mangaList && Array.isArray(res.mangaList)) {
            for (const item of res.mangaList) this.upsertMangaItem(item);
          }
          if (res?.dbSizeText) {
            this.settings.dbSizeText = res.dbSizeText;
          }
          if (this.currentTab === 'settings' && !this.activeDetailMangaId) {
            this.renderSettingsView(container);
          }
          this.showToast(this.l('Sampul pustaka berhasil disegarkan!', 'Library covers refreshed!'));
        })
        .catch(() => {
          this.showToast(this.l('Gagal menyegarkan sampul pustaka.', 'Failed to refresh library covers.'));
        })
        .finally(() => {
          if (btn) {
            btn.disabled = false;
            btn.textContent = this.l('Segarkan', 'Refresh');
          }
        });
    } else if (btn) {
      btn.disabled = false;
      btn.textContent = this.l('Segarkan', 'Refresh');
    }
  });
  const openDownloadsFolderAction = () => {
    this.bridge.openFolder();
    this.showToast(this.l('Membuka folder unduhan di File Explorer...', 'Opening downloads folder in File Explorer...'));
  };
  document.getElementById('btnOpenDownloadsFolder')?.addEventListener('click', openDownloadsFolderAction);
  document.getElementById('btnOpenDownloadsFolderFromDl')?.addEventListener('click', openDownloadsFolderAction);
  document.getElementById('btnDangerClearCache')?.addEventListener('click', () => {
    const btn = document.getElementById('btnDangerClearCache') as HTMLButtonElement | null;
    const descEl = document.getElementById('clearCacheDesc');
    const prevDesc = descEl?.textContent || '';
    if (btn) btn.disabled = true;
    if (descEl) descEl.textContent = this.l('Membersihkan cache...', 'Clearing cache...');
    if (this.bridge.isWebViewHost) {
      void this.bridge
        .invoke<{ dbSizeText?: string }>('settings:clear-cache', undefined, 60000)
        .then((res) => {
          this.settings.dbSizeText = res?.dbSizeText || '0.0 MB';
          if (this.currentTab === 'settings' && !this.activeDetailMangaId) {
            this.renderSettingsView(container);
          }
          this.showToast(this.l('Seluruh berkas cache gambar berhasil dibersihkan.', 'All image cache files cleared.'));
        })
        .catch(() => {
          if (descEl) descEl.textContent = prevDesc;
          this.showToast(this.l('Gagal membersihkan cache gambar.', 'Failed to clear image cache.'), 'warning');
        })
        .finally(() => {
          if (btn) btn.disabled = false;
        });
      return;
    }
    this.settings.dbSizeText = '0.0 MB';
    this.renderSettingsView(container);
    this.showToast(this.l('Seluruh berkas cache gambar berhasil dibersihkan.', 'All image cache files cleared.'));
  });
  document.getElementById('btnDangerClearHistory')?.addEventListener('click', async () => {
    const confirmed = await this.showConfirmModal({
      title: this.l('Hapus Seluruh Riwayat Baca', 'Clear All Reading History'),
      message: this.l(
        'Apakah Anda yakin ingin menghapus seluruh riwayat baca dan mereset status baca bab di pustaka? Tindakan ini tidak dapat dibatalkan.',
        'Are you sure you want to clear all reading history and reset chapter read status in your library? This action cannot be undone.'
      ),
      confirmText: this.l('Hapus Riwayat', 'Clear History'),
      cancelText: this.t('modal.cancel'),
      isDanger: true
    });
    if (!confirmed) return;

    const btn = document.getElementById('btnDangerClearHistory') as HTMLButtonElement | null;
    const descEl = document.getElementById('clearHistoryDesc');
    const prevDesc = descEl?.textContent || '';
    const historySnapshot = this.mangaList.map((m) => ({
      manga: m,
      wasRemoved: this.removedHistoryIds.has(m.id),
      lastViewedEpoch: m.lastViewedEpoch,
      lastReadTimeAgo: m.lastReadTimeAgo,
      unreadCount: m.unreadCount,
      chapterReadFlags: Array.isArray(m.chapters) ? m.chapters.map((c) => c.isRead) : []
    }));
    if (btn) btn.disabled = true;
    if (descEl) descEl.textContent = this.l('Menghapus riwayat baca...', 'Clearing reading history...');
    this.mangaList.forEach((m) => {
      this.removedHistoryIds.add(m.id);
      m.lastViewedEpoch = 0;
      m.lastReadTimeAgo = this.l('Belum dibaca', 'Unread');
      if (Array.isArray(m.chapters)) {
        m.chapters.forEach((c) => {
          c.isRead = false;
        });
        m.unreadCount = m.chapters.length;
      }
    });
    if (this.bridge.isWebViewHost) {
      void this.bridge
        .invoke<{ ok?: boolean; dbSizeText?: string; mangaList?: MangaItem[] }>('history:clear-all', undefined, 60000)
        .then((res) => {
          if (res?.mangaList && Array.isArray(res.mangaList)) {
            for (const item of res.mangaList) this.upsertMangaItem(item);
          }
          if (res?.dbSizeText) {
            this.settings.dbSizeText = res.dbSizeText;
          }
          if (this.currentTab === 'settings' && !this.activeDetailMangaId) {
            this.renderSettingsView(container);
          }
          this.showToast(this.l('Seluruh riwayat baca telah dibersihkan.', 'All reading history cleared.'));
        })
        .catch(() => {
          for (const snap of historySnapshot) {
            if (!snap.wasRemoved) this.removedHistoryIds.delete(snap.manga.id);
            snap.manga.lastViewedEpoch = snap.lastViewedEpoch;
            snap.manga.lastReadTimeAgo = snap.lastReadTimeAgo;
            snap.manga.unreadCount = snap.unreadCount;
            if (Array.isArray(snap.manga.chapters)) {
              snap.manga.chapters.forEach((c, idx) => {
                if (idx < snap.chapterReadFlags.length) c.isRead = snap.chapterReadFlags[idx];
              });
            }
          }
          if (descEl) descEl.textContent = prevDesc;
          this.showToast(this.l('Gagal membersihkan riwayat baca.', 'Failed to clear reading history.'), 'warning');
        })
        .finally(() => {
          if (btn) btn.disabled = false;
        });
      return;
    }
    this.renderSettingsView(container);
    this.showToast(this.l('Seluruh riwayat baca telah dibersihkan.', 'All reading history cleared.'));
  });
  document.getElementById('selDohProvider')?.addEventListener('change', (e) => {
    const val = (e.target as HTMLSelectElement).value;
    this.settings.dohProvider = val;
    persistSetting('dohProvider', val);
    this.showToast(this.l(`Resolver DNS over HTTPS diatur ke: ${val}`, `DNS over HTTPS resolver set to: ${val}`));
  });
  document.getElementById('btnCreateBackup')?.addEventListener('click', () => {
    const btn = document.getElementById('btnCreateBackup') as HTMLButtonElement | null;
    const descEl = document.getElementById('createBackupDesc');
    if (this.bridge.isWebViewHost) {
      if (btn) btn.disabled = true;
      if (descEl) descEl.textContent = this.l('Membuat berkas cadangan...', 'Creating backup file...');
      void this.bridge
        .invoke<{ saved?: boolean; cancelled?: boolean; lastBackupText?: string }>('backup:create', undefined, 180000)
        .then((res) => {
          if (res?.saved) {
            this.settings.lastBackupText = res.lastBackupText || this.l('Cadangan terakhir: Baru saja disimpan', 'Last backup: Just saved');
            if (this.currentTab === 'settings' && !this.activeDetailMangaId) {
              this.renderSettingsView(container);
            }
            this.showToast(this.l('Berkas cadangan (.yomicbackup) berhasil dibuat.', 'Backup file (.yomicbackup) created.'));
          } else if (!res?.cancelled) {
            this.showToast(this.l('Gagal membuat berkas cadangan.', 'Failed to create backup file.'));
          }
        })
        .catch(() => {
          this.showToast(this.l('Gagal membuat berkas cadangan.', 'Failed to create backup file.'));
        })
        .finally(() => {
          if (btn) btn.disabled = false;
          if (descEl) descEl.textContent = this.settings.lastBackupText || this.l('Siap dicadangkan (.yomicbackup)', 'Ready to back up (.yomicbackup)');
        });
      return;
    }
    this.settings.lastBackupText = this.l('Cadangan terakhir: Baru saja disimpan', 'Last backup: Just saved');
    this.renderSettingsView(container);
    this.showToast(this.l('Berkas cadangan (.yomicbackup) berhasil dibuat.', 'Backup file (.yomicbackup) created.'));
  });
  document.getElementById('btnRestoreBackup')?.addEventListener('click', () => {
    const btn = document.getElementById('btnRestoreBackup') as HTMLButtonElement | null;
    const descEl = document.getElementById('restoreBackupDesc');
    if (this.bridge.isWebViewHost) {
      if (btn) btn.disabled = true;
      if (descEl) descEl.textContent = this.l('Memulihkan cadangan...', 'Restoring backup...');
      this.showToast(this.l('Memilih berkas cadangan (.yomicbackup) untuk dipulihkan...', 'Selecting backup file (.yomicbackup) to restore...'));
      void this.bridge
        .invoke<{ restored?: boolean; cancelled?: boolean }>('backup:restore', undefined, 180000)
        .then((res) => {
          if (res?.restored) {
            void this.bootstrapFromBackend();
            this.showToast(this.l('Cadangan berhasil dipulihkan!', 'Backup restored!'));
          } else if (!res?.cancelled) {
            this.showToast(this.l('Gagal memulihkan berkas cadangan.', 'Failed to restore backup file.'));
          }
        })
        .catch(() => {
          this.showToast(this.l('Gagal memulihkan berkas cadangan.', 'Failed to restore backup file.'));
        })
        .finally(() => {
          if (btn) btn.disabled = false;
          if (descEl) descEl.textContent = this.l('Impor pustaka, kategori, dan progres baca dari file', 'Import library, categories, and reading progress from file');
        });
      return;
    }
    this.showToast(this.l('Memilih berkas cadangan (.yomicbackup) untuk dipulihkan...', 'Selecting backup file (.yomicbackup) to restore...'));
  });
  document.getElementById('btnToggleMalConnect')?.addEventListener('click', () => {
    const btn = document.getElementById('btnToggleMalConnect') as HTMLButtonElement | null;
    const descEl = document.getElementById('malConnectDesc');
    const wasConnected = this.settings.isMalConnected;

    const executeMalToggle = () => {
      if (this.bridge.isWebViewHost) {
        if (!wasConnected) {
          this.showToast(this.l('Membuka browser untuk login MyAnimeList...', 'Opening browser for MyAnimeList login...'));
          if (btn) btn.disabled = true;
          if (descEl) descEl.textContent = this.l('Menunggu login di browser...', 'Waiting for browser login...');
        } else if (btn) {
          btn.disabled = true;
          if (descEl) descEl.textContent = this.l('Memutuskan koneksi...', 'Disconnecting...');
        }
        void this.bridge
          .invoke<AppSettingsState>('mal:toggle-connect', undefined, 180000)
          .then((updated) => {
            if (updated) {
              Object.assign(this.settings, updated);
              if (this.currentTab === 'settings' && !this.activeDetailMangaId) {
                this.renderSettingsView(container);
              }
              if (this.settings.isMalConnected) {
                this.showToast(
                  this.l(`Terhubung ke MyAnimeList (@${this.settings.malConnectedUser}).`, `Connected to MyAnimeList (@${this.settings.malConnectedUser}).`)
                );
              } else if (wasConnected) {
                this.showToast(
                  this.l('Koneksi akun MyAnimeList diputuskan.', 'MyAnimeList account disconnected.')
                );
              } else {
                this.showToast(
                  this.l('Login MyAnimeList dibatalkan atau waktu habis.', 'MyAnimeList login cancelled or timed out.')
                );
              }
            }
          })
          .catch(() => {
            this.showToast(
              this.l('Gagal menghubungi MyAnimeList. Silakan coba lagi.', 'Failed to connect to MyAnimeList. Please try again.')
            );
          })
          .finally(() => {
            if (btn) btn.disabled = false;
            if (descEl) {
              if (this.settings.isMalConnected) {
                descEl.innerHTML = this.t('settings.tracker.malConnectedAs', {
                  user: escapeXml(this.settings.malConnectedUser)
                });
              } else {
                descEl.textContent = this.t('settings.tracker.malNotConnected');
              }
            }
          });
        return;
      }
      this.settings.isMalConnected = !this.settings.isMalConnected;
      this.renderSettingsView(container);
      this.showToast(
        this.settings.isMalConnected
          ? this.l(`Terhubung kembali ke MyAnimeList (@${this.settings.malConnectedUser}).`, `Reconnected to MyAnimeList (@${this.settings.malConnectedUser}).`)
          : this.l('Koneksi akun MyAnimeList diputuskan.', 'MyAnimeList account disconnected.')
      );
    };

    if (wasConnected) {
      const prevMalOverlay = document.getElementById('malDisconnectModalOverlay') as (HTMLElement & { _closeModal?: () => void }) | null;
      if (prevMalOverlay?._closeModal) {
        prevMalOverlay._closeModal();
      } else {
        prevMalOverlay?.remove();
      }
      const overlay = document.createElement('div') as HTMLDivElement & { _closeModal?: () => void };
      overlay.id = 'malDisconnectModalOverlay';
      overlay.style.cssText =
        'position:fixed;inset:0;background:rgba(0,0,0,0.65);z-index:99999;display:flex;align-items:center;justify-content:center;padding:16px;';
      overlay.innerHTML = `
        <div role="dialog" aria-modal="true" style="width:360px;max-width:92vw;background:var(--bg-surface,#18181b);border:none;border-radius:20px;padding:24px 24px 20px;box-shadow:0 24px 48px rgba(0,0,0,0.45);color:var(--ink-primary,#f4f4f5);font-family:inherit;">
          <h3 style="margin:0 0 10px 0;font-size:19px;font-weight:700;color:var(--ink-primary,#f4f4f5);letter-spacing:-0.01em;">
            ${this.l('Putuskan MyAnimeList', 'Disconnect MyAnimeList')}
          </h3>
          <p style="margin:0 0 22px 0;font-size:13.5px;line-height:1.5;color:var(--ink-secondary,#a1a1aa);">
            ${this.l(
              `Putuskan koneksi akun MyAnimeList (${escapeXml(this.settings.malConnectedUser)}) dari Yomic?`,
              `Disconnect MyAnimeList account (${escapeXml(this.settings.malConnectedUser)}) from Yomic?`
            )}
          </p>
          <div style="display:flex;justify-content:flex-end;align-items:center;gap:12px;">
            <button id="btnCancelMalDisconnect" type="button" style="height:38px;padding:0 14px;border-radius:10px;border:none;background:transparent;color:var(--ink-secondary,#a1a1aa);font-size:13.5px;font-weight:600;cursor:pointer;display:inline-flex;align-items:center;justify-content:center;">
              ${this.t('modal.cancel')}
            </button>
            <button id="btnConfirmMalDisconnect" type="button" style="height:38px;padding:0 20px;border-radius:10px;border:none;background:#e11d48;color:#ffffff;font-size:13.5px;font-weight:600;cursor:pointer;display:inline-flex;align-items:center;justify-content:center;">
              <span>${this.l('Putuskan', 'Disconnect')}</span>
            </button>
          </div>
        </div>
      `;
      document.body.appendChild(overlay);
      const previousFocus = document.activeElement as HTMLElement | null;
      const closeModal = () => {
        window.removeEventListener('keydown', onKeyDown, true);
        overlay.remove();
        if (previousFocus && document.contains(previousFocus)) {
          previousFocus.focus();
        }
      };
      overlay._closeModal = closeModal;
      const onKeyDown = (ev: KeyboardEvent) => {
        if (ev.key === 'Escape') {
          ev.preventDefault();
          ev.stopPropagation();
          closeModal();
          return;
        }
        if (ev.key === 'Tab') {
          const focusables = Array.from(overlay.querySelectorAll<HTMLElement>('button:not([disabled])'));
          if (focusables.length > 0) {
            const first = focusables[0];
            const last = focusables[focusables.length - 1];
            if (ev.shiftKey && document.activeElement === first) {
              ev.preventDefault();
              last.focus();
            } else if (!ev.shiftKey && document.activeElement === last) {
              ev.preventDefault();
              first.focus();
            }
          }
        }
      };
      window.addEventListener('keydown', onKeyDown, true);
      document.getElementById('btnCancelMalDisconnect')?.focus();
      overlay.addEventListener('click', (ev) => {
        if (ev.target === overlay) closeModal();
      });
      document.getElementById('btnCancelMalDisconnect')?.addEventListener('click', closeModal);
      document.getElementById('btnConfirmMalDisconnect')?.addEventListener('click', () => {
        closeModal();
        executeMalToggle();
      });
      return;
    }

    executeMalToggle();
  });
  document.getElementById('btnDangerResetAll')?.addEventListener('click', () => {
    const existingOverlay = document.getElementById('dangerClearDataModalOverlay') as (HTMLElement & { _closeModal?: () => void }) | null;
    if (existingOverlay?._closeModal) {
      existingOverlay._closeModal();
    } else if (existingOverlay) {
      existingOverlay.remove();
    }
    const overlay = document.createElement('div') as HTMLDivElement & { _closeModal?: () => void };
    overlay.id = 'dangerClearDataModalOverlay';
    overlay.style.cssText =
      'position:fixed;inset:0;background:rgba(0,0,0,0.65);z-index:99999;display:flex;align-items:center;justify-content:center;padding:16px;';
    overlay.innerHTML = `
      <div role="dialog" aria-modal="true" style="width:360px;max-width:92vw;background:var(--bg-surface,#18181b);border:none;border-radius:20px;padding:24px 24px 20px;box-shadow:0 24px 48px rgba(0,0,0,0.45);color:var(--ink-primary,#f4f4f5);font-family:inherit;">
        <h3 style="margin:0 0 10px 0;font-size:19px;font-weight:700;color:var(--ink-primary,#f4f4f5);letter-spacing:-0.01em;">
          ${this.l('Reset Aplikasi', 'Reset Application')}
        </h3>
        <p id="dangerClearDataModalMsg" style="margin:0 0 22px 0;font-size:13.5px;line-height:1.5;color:var(--ink-secondary,#a1a1aa);">
          ${this.l(
            'Atur ulang seluruh data dan pengaturan aplikasi ke bawaan pabrik?',
            'Reset all application data and settings to factory defaults?'
          )}
        </p>
        <div id="dangerClearDataModalActions" style="display:flex;justify-content:flex-end;align-items:center;gap:12px;">
          <button id="btnCancelClearAllData" type="button" style="height:38px;padding:0 14px;border-radius:10px;border:none;background:transparent;color:var(--ink-secondary,#a1a1aa);font-size:13.5px;font-weight:600;cursor:pointer;display:inline-flex;align-items:center;justify-content:center;">
            ${this.t('modal.cancel')}
          </button>
          <button id="btnConfirmClearAllData" type="button" style="height:38px;padding:0 20px;border-radius:10px;border:none;background:#e11d48;color:#ffffff;font-size:13.5px;font-weight:600;cursor:pointer;display:inline-flex;align-items:center;justify-content:center;">
            <span>${this.l('Atur Ulang', 'Reset')}</span>
          </button>
        </div>
      </div>
    `;
    document.body.appendChild(overlay);
    const previousFocus = document.activeElement as HTMLElement | null;
    let countdownTimer: number | null = null;
    let isBusy = false;
    const onKeyDown = (ev: KeyboardEvent) => {
      if (ev.key === 'Escape') {
        ev.preventDefault();
        ev.stopPropagation();
        if (!isBusy) closeModal();
        return;
      }
      if (ev.key === 'Tab') {
        const focusables = Array.from(overlay.querySelectorAll<HTMLElement>('button:not([disabled])'));
        if (focusables.length > 0) {
          const first = focusables[0];
          const last = focusables[focusables.length - 1];
          if (ev.shiftKey && document.activeElement === first) {
            ev.preventDefault();
            last.focus();
          } else if (!ev.shiftKey && document.activeElement === last) {
            ev.preventDefault();
            first.focus();
          }
        }
      }
    };
    const closeModal = (): void => {
      if (countdownTimer !== null) {
        window.clearInterval(countdownTimer);
        countdownTimer = null;
      }
      window.removeEventListener('keydown', onKeyDown, true);
      overlay.remove();
      if (previousFocus && document.contains(previousFocus)) {
        previousFocus.focus();
      }
    };
    overlay._closeModal = closeModal;
    window.addEventListener('keydown', onKeyDown, true);
    document.getElementById('btnCancelClearAllData')?.focus();
    overlay.addEventListener('click', (ev) => {
      if (ev.target === overlay && !isBusy) {
        closeModal();
      }
    });
    document.getElementById('btnCancelClearAllData')?.addEventListener('click', () => {
      if (!isBusy) closeModal();
    });
    document.getElementById('btnConfirmClearAllData')?.addEventListener('click', () => {
      if (isBusy) return;
      isBusy = true;
      const confirmBtn = document.getElementById('btnConfirmClearAllData') as HTMLButtonElement | null;
      const cancelBtn = document.getElementById('btnCancelClearAllData') as HTMLButtonElement | null;
      const msgEl = document.getElementById('dangerClearDataModalMsg');
      if (confirmBtn) {
        confirmBtn.disabled = true;
        confirmBtn.style.opacity = '0.7';
        confirmBtn.textContent = this.l('Menghapus Data...', 'Deleting Data...');
      }
      if (cancelBtn) {
        cancelBtn.style.display = 'none';
      }
      const finishAndCountdown = (serverMsg?: string): void => {
        // Purge all persisted client localStorage state so factory reset leaves zero stale theme/cache/positions
        try {
          const keysToRemove: string[] = [];
          for (let i = 0; i < window.localStorage.length; i++) {
            const k = window.localStorage.key(i);
            if (k && k.startsWith('yomic_')) keysToRemove.push(k);
          }
          keysToRemove.forEach((k) => window.localStorage.removeItem(k));
        } catch {
          // ignore storage access errors
        }
        this.mangaList.splice(0, this.mangaList.length);
        this.categories.splice(0, this.categories.length);
        this.activityUpdates.splice(0, this.activityUpdates.length);
        this.downloads.splice(0, this.downloads.length);
        this.chapterPagesCache.clear();
        this.readingStatsData = null;
        this.removedHistoryIds.clear();
        this.libraryCategoryFilter = 'all';
        this.activeDetailMangaId = null;
        this.contextMenuMangaId = null;
        this.categoryManageMangaId = null;
        Object.assign(this.settings, DEFAULT_APP_SETTINGS, {
          customHotkeys: { ...DEFAULT_APP_SETTINGS.customHotkeys }
        });
        this.readerMode = this.settings.defaultReaderMode;
        this.applyTheme(this.settings.isDarkMode);
        applyStaticDomTranslations(this.settings.appLanguage);
        this.updateSidebarCounters();
        const finalNotice =
          serverMsg ||
          this.l(
            'Semua data berhasil dihapus. Aplikasi akan dimuat ulang otomatis dalam 3 detik.',
            'All data cleared. The application will automatically reload in 3 seconds.'
          );
        if (msgEl) {
          msgEl.style.color = '#e11d48';
          msgEl.style.fontWeight = '600';
          msgEl.textContent = finalNotice;
        }
        if (confirmBtn) {
          confirmBtn.textContent = this.l('Memuat Ulang (3 dtk)...', 'Reloading (3s)...');
        }
        this.showToast(finalNotice);
        let remaining = 3;
        countdownTimer = window.setInterval(() => {
          remaining -= 1;
          if (remaining > 0) {
            if (confirmBtn) {
              confirmBtn.textContent = this.l(`Memuat Ulang (${remaining} dtk)...`, `Reloading (${remaining}s)...`);
            }
          } else {
            closeModal();
            if (this.currentTab === 'settings' && !this.activeDetailMangaId) {
              this.renderSettingsView(container);
            }
          }
        }, 1000);
      };
      if (this.bridge.isWebViewHost) {
        void this.bridge
          .invoke<{ ok: boolean; message?: string }>('data:clear-all')
          .then((res) => {
            if (res && res.ok === false) {
              isBusy = false;
              closeModal();
              this.showToast(res.message || this.l('Gagal menghapus data aplikasi.', 'Failed to clear application data.'));
              return;
            }
            finishAndCountdown(res?.message);
          })
          .catch(() => {
            isBusy = false;
            closeModal();
            this.showToast(this.l('Gagal menghapus data aplikasi. Silakan coba lagi.', 'Failed to clear application data. Please try again.'));
          });
        return;
      }
      finishAndCountdown();
    });
  });
  // Hotkey rebind logic
  let activeRecordingActionId: string | null = null;
  let recordingCleanUp: (() => void) | null = null;

  const cancelRecording = () => {
    if (recordingCleanUp) {
      recordingCleanUp();
      recordingCleanUp = null;
    }
    activeRecordingActionId = null;
  };

  container.querySelectorAll<HTMLButtonElement>('[data-rebind-action]').forEach((btn) => {
    btn.addEventListener('click', (ev) => {
      ev.stopPropagation();
      const actionId = btn.dataset.rebindAction;
      if (!actionId) return;

      if (activeRecordingActionId === actionId) {
        cancelRecording();
        this.renderSettingsView(container);
        return;
      }

      cancelRecording();
      activeRecordingActionId = actionId;

      const targetDef = HOTKEY_DEFINITIONS.find((d) => d.id === actionId);
      btn.classList.add('recording');
      btn.innerHTML = `<span class="v1-hotkey-recording-label">${this.l('Tekan tombol baru... (Esc untuk batal)', 'Press new key... (Esc to cancel)')}</span>`;

      const onKeyDown = (keyEv: KeyboardEvent) => {
        keyEv.preventDefault();
        keyEv.stopPropagation();

        if (keyEv.key === 'Escape') {
          cancelRecording();
          this.renderSettingsView(container);
          this.showToast(this.l('Perekaman tombol dibatalkan.', 'Key recording cancelled.'));
          return;
        }

        const serialized = this.serializeKeyboardEvent(keyEv);
        if (!serialized) {
          // Lone modifier key, keep waiting
          return;
        }

        // Check for conflict
        const conflict = this.findConflictingAction(serialized, actionId);
        if (conflict) {
          const oldTargetKey = this.getEffectiveHotkey(actionId);
          this.setCustomHotkey(conflict.id, oldTargetKey);
          this.setCustomHotkey(actionId, serialized);
          this.showToast(this.l(`Tombol "${serialized}" ditukar dengan "${conflict.label}".`, `Key "${serialized}" swapped with "${conflict.label}".`));
        } else {
          this.setCustomHotkey(actionId, serialized);
          this.showToast(this.l(`Pintasan "${targetDef?.label ?? actionId}" diatur ke: ${serialized}`, `Shortcut "${targetDef?.label ?? actionId}" set to: ${serialized}`));
        }

        cancelRecording();
        this.renderSettingsView(container);
      };

      const onDocClick = (clickEv: MouseEvent) => {
        if (!btn.contains(clickEv.target as Node)) {
          cancelRecording();
          if (this.currentTab === 'settings') {
            this.renderSettingsView(container);
          }
        }
      };

      window.addEventListener('keydown', onKeyDown, { capture: true });
      window.addEventListener('click', onDocClick);

      recordingCleanUp = () => {
        window.removeEventListener('keydown', onKeyDown, { capture: true });
        window.removeEventListener('click', onDocClick);
        btn.classList.remove('recording');
      };
    });
  });

  // Individual reset buttons (ALWAYS cancel any active capture-phase keydown recorder first!)
  container.querySelectorAll<HTMLButtonElement>('[data-reset-hotkey]').forEach((btn) => {
    btn.addEventListener('click', (ev) => {
      ev.stopPropagation();
      cancelRecording();
      const actionId = btn.dataset.resetHotkey;
      if (!actionId) return;
      const def = HOTKEY_DEFINITIONS.find((d) => d.id === actionId);
      this.resetCustomHotkey(actionId);
      this.showToast(this.l(`Pintasan "${def?.label ?? actionId}" dikembalikan ke bawaan (${def?.defaultKey ?? ''}).`, `Shortcut "${def?.label ?? actionId}" reset to default (${def?.defaultKey ?? ''}).`));
      this.renderSettingsView(container);
    });
  });

  // Reset all hotkeys button with confirmation modal
  document.getElementById('btnResetAllHotkeys')?.addEventListener('click', () => {
    cancelRecording();
    const existingOverlay = document.getElementById('resetHotkeysModalOverlay') as (HTMLElement & { _closeModal?: () => void }) | null;
    if (existingOverlay?._closeModal) {
      existingOverlay._closeModal();
    } else if (existingOverlay) {
      existingOverlay.remove();
    }

    const overlay = document.createElement('div') as HTMLDivElement & { _closeModal?: () => void };
    overlay.id = 'resetHotkeysModalOverlay';
    overlay.style.cssText =
      'position:fixed;inset:0;background:rgba(15,23,42,0.55);backdrop-filter:blur(4px);z-index:99999;display:flex;align-items:center;justify-content:center;padding:16px;';
    overlay.innerHTML = `
      <div role="dialog" aria-modal="true" style="width:420px;max-width:92vw;background:var(--bg-surface,#ffffff);border:1px solid var(--border-subtle);border-radius:16px;padding:24px;box-shadow:0 20px 45px rgba(0,0,0,0.28);color:var(--ink-primary,#0f172a);font-family:inherit;">
        <div style="display:flex;align-items:center;gap:10px;margin-bottom:12px;">
          <span style="display:inline-flex;align-items:center;justify-content:center;width:36px;height:36px;border-radius:10px;background:var(--ocean-tint);color:var(--ink-primary);flex-shrink:0;">
            ${getIconSvg('restart-linear', { size: 20 })}
          </span>
          <h3 style="margin:0;font-size:18px;font-weight:700;color:var(--ink-primary);">${this.l('Atur Ulang Semua Pintasan?', 'Reset All Shortcuts?')}</h3>
        </div>
        <p style="margin:0 0 22px 0;font-size:13.5px;line-height:1.55;color:var(--ink-secondary);">
          ${this.l('Apakah Anda yakin ingin mengembalikan semua pintasan tombol keyboard ke setelan awal bawaan aplikasi?', 'Are you sure you want to reset all keyboard shortcuts to application defaults?')}
        </p>
        <div style="display:flex;justify-content:flex-end;gap:10px;">
          <button id="btnCancelResetHotkeys" type="button" style="height:36px;padding:0 18px;border-radius:8px;border:1px solid var(--border-subtle);background:transparent;color:var(--ink-primary);font-size:13px;font-weight:600;cursor:pointer;">
            ${this.t('modal.cancel')}
          </button>
          <button id="btnConfirmResetHotkeys" type="button" style="height:36px;padding:0 18px;border-radius:8px;border:none;background:var(--ocean-primary);color:var(--ink-on-ocean);font-size:13px;font-weight:600;cursor:pointer;display:inline-flex;align-items:center;gap:6px;">
            ${getIconSvg('restart-linear', { size: 15 })}
            <span>${this.l('Kembalikan Bawaan', 'Reset Defaults')}</span>
          </button>
        </div>
      </div>
    `;
    document.body.appendChild(overlay);
    const previousFocus = document.activeElement as HTMLElement | null;

    const onKeyDown = (ev: KeyboardEvent) => {
      if (ev.key === 'Escape') {
        ev.preventDefault();
        ev.stopPropagation();
        closeResetModal();
        return;
      }
      if (ev.key === 'Tab') {
        const focusables = Array.from(overlay.querySelectorAll<HTMLElement>('button:not([disabled])'));
        if (focusables.length > 0) {
          const first = focusables[0];
          const last = focusables[focusables.length - 1];
          if (ev.shiftKey && document.activeElement === first) {
            ev.preventDefault();
            last.focus();
          } else if (!ev.shiftKey && document.activeElement === last) {
            ev.preventDefault();
            first.focus();
          }
        }
      }
    };
    const closeResetModal = () => {
      window.removeEventListener('keydown', onKeyDown, { capture: true });
      overlay.remove();
      if (previousFocus && document.contains(previousFocus)) {
        previousFocus.focus();
      }
    };
    overlay._closeModal = closeResetModal;
    window.addEventListener('keydown', onKeyDown, { capture: true });
    document.getElementById('btnCancelResetHotkeys')?.focus();
    overlay.addEventListener('click', (e) => {
      if (e.target === overlay) closeResetModal();
    });
    document.getElementById('btnCancelResetHotkeys')?.addEventListener('click', closeResetModal);
    document.getElementById('btnConfirmResetHotkeys')?.addEventListener('click', () => {
      closeResetModal();
      this.resetAllHotkeys();
      this.showToast(this.l('Semua tombol pintas berhasil dikembalikan ke bawaan pabrik.', 'All keyboard shortcuts reset to factory defaults.'));
      this.renderSettingsView(container);
    });
  });

  document.getElementById('btnAboutWebsite')?.addEventListener('click', () => {
    this.bridge.openExternalUrl('https://github.com/ArisaAkiyama/Yomic');
  });
  document.getElementById('btnToggleReleaseNotes')?.addEventListener('click', () => {
    const btn = document.getElementById('btnToggleReleaseNotes');
    const panel = document.getElementById('aboutReleaseNotesPanel');
    const chevron = document.getElementById('aboutReleaseNotesChevron');
    if (!panel || !btn) return;
    const isExpanded = panel.style.display !== 'none';
    panel.style.display = isExpanded ? 'none' : 'block';
    btn.setAttribute('aria-expanded', isExpanded ? 'false' : 'true');
    if (chevron) {
      chevron.classList.toggle('is-open', !isExpanded);
    }
  });
  document.getElementById('btnAboutCheckUpdate')?.addEventListener('click', () => {
    const btn = document.getElementById('btnAboutCheckUpdate') as HTMLButtonElement | null;
    const isEn = document.documentElement.lang === 'en';

    const startInstall = (dlUrl: string, targetVer: string, releaseNotes: string) => {
      const isDirectExe = /\.exe(?:[?#]|$)/i.test(dlUrl);
      if (!isDirectExe) {
        // Non-.exe release page: launch in browser and close overlay cleanly instead of soft-locking
        if (this.bridge.isWebViewHost) {
          void this.bridge.invoke<{ ok?: boolean }>('app:install-update', { downloadUrl: dlUrl }, 30000).catch(() => {});
        }
        closeAppUpdateOverlay();
        this.showToast(this.l('Membuka halaman rilis Yomic di browser...', 'Opening Yomic release page in browser...'));
        return;
      }

      openAppUpdateOverlay({
        version: targetVer,
        releaseNotes,
        isEn,
        mode: 'downloading'
      });
      if (this.bridge.isWebViewHost) {
        void this.bridge
          .invoke<{ ok?: boolean }>('app:install-update', { downloadUrl: dlUrl }, 300000)
          .then((res) => {
            if (!res?.ok) {
              updateAppUpdateOverlayProgress({ progress: 0, downloadedBytes: 0, totalBytes: 0, stage: 'error' }, isEn);
            }
          })
          .catch(() => {
            updateAppUpdateOverlayProgress({ progress: 0, downloadedBytes: 0, totalBytes: 0, stage: 'error' }, isEn);
          });
      }
    };

    // Immediately open the full-screen Updater UI when Check for Updates is clicked
    openAppUpdateOverlay({
      version: '2.4.0',
      releaseNotes: '',
      isEn,
      mode: 'checking'
    });

    if (!this.bridge.isWebViewHost) {
      window.setTimeout(() => {
        openAppUpdateOverlay({
          version: '2.4.0',
          releaseNotes: '',
          isEn,
          mode: 'uptodate'
        });
      }, 450);
      return;
    }

    if (btn) btn.disabled = true;
    void this.bridge
      .invoke<{
        isUpdateAvailable?: boolean;
        currentVersion?: string;
        latestVersion?: string;
        downloadUrl?: string;
        releaseNotes?: string;
      }>('app:check-update', undefined, 15000)
      .then((info) => {
        if (btn) btn.disabled = false;
        // If user already dismissed the checking overlay, do not forcibly pop it back open
        if (!document.getElementById('appUpdateOverlay')?.classList.contains('open')) return;
        const ver = info?.latestVersion || info?.currentVersion || '2.4.0';
        const notes = info?.releaseNotes || '';
        if (info?.isUpdateAvailable && info.downloadUrl) {
          openAppUpdateOverlay({
            version: ver,
            releaseNotes: notes,
            isEn,
            mode: 'available',
            onStartInstall: () => startInstall(info.downloadUrl || '', ver, notes)
          });
        } else {
          openAppUpdateOverlay({
            version: ver,
            releaseNotes: notes,
            isEn,
            mode: 'uptodate'
          });
        }
      })
      .catch(() => {
        if (btn) btn.disabled = false;
        if (!document.getElementById('appUpdateOverlay')?.classList.contains('open')) return;
        openAppUpdateOverlay({
          version: '2.4.0',
          releaseNotes: '',
          isEn,
          mode: 'error'
        });
      });
  });
  document.getElementById('btnAboutTrakteer')?.addEventListener('click', () => {
    this.bridge.openExternalUrl('https://trakteer.id/Arisa-Akiyama');
  });
  document.getElementById('btnAboutLicense')?.addEventListener('click', () => {
    this.bridge.openExternalUrl('https://github.com/ArisaAkiyama/Yomic/blob/master/LICENSE');
  });
  container.querySelectorAll<HTMLButtonElement>('[data-social-url]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const url = btn.dataset.socialUrl;
      if (url) this.bridge.openExternalUrl(url);
    });
  });

  if (isReRender) {
    if (savedContainerScroll > 0) container.scrollTop = savedContainerScroll;
    if (savedParentScroll > 0 && container.parentElement) container.parentElement.scrollTop = savedParentScroll;
  }
}


let appUpdateEscHandler: ((ev: KeyboardEvent) => void) | null = null;
let appUpdateActiveMode: 'checking' | 'available' | 'downloading' | 'uptodate' | 'error' = 'checking';

function formatUpdateReleaseNotesHtml(rawNotes: string, isEn: boolean): string {
  const cleaned = (rawNotes || '').trim();
  if (!cleaned) {
    return `
      <div class="app-update-notes-section">
        <div class="app-update-notes-heading">${isEn ? "What's new" : 'Yang Baru'}</div>
        <ul class="app-update-notes-list">
          <li>${isEn ? 'Performance optimizations, bug fixes, and smoother reading experience.' : 'Peningkatan performa, perbaikan bug, dan pengalaman membaca yang lebih mulus.'}</li>
        </ul>
      </div>
    `;
  }

  const lines = cleaned.split(/\r?\n/);
  const parts: string[] = [];
  let inList = false;
  let hasAnyHeading = false;

  const closeList = () => {
    if (inList) {
      parts.push('</ul>');
      inList = false;
    }
  };

  for (const rawLine of lines) {
    const line = rawLine.trim();
    if (!line) continue;
    // Skip redundant version banner line like "[2.2.0] - Update" since version is already shown in footer
    if (/^\[?\d+\.\d+(?:\.\d+)?\]?\s*[-–—]\s*update\b/i.test(line)) {
      continue;
    }
    const headingMatch = line.match(/^#{1,4}\s+(.+)$/);
    if (headingMatch) {
      closeList();
      hasAnyHeading = true;
      parts.push(`<div class="app-update-notes-heading">${escapeXml(headingMatch[1].replace(/\*\*/g, '').replace(/:$/, ''))}</div>`);
      continue;
    }
    // Treat section labels ending with ":" (e.g., "Update Details:", "Improvements:") as clean headings
    if (/^[A-Za-z][A-Za-z0-9\s'&/-]{2,36}:$/.test(line)) {
      closeList();
      hasAnyHeading = true;
      const label = line.replace(/:$/, '');
      const normalized = /^update details$/i.test(label) ? (isEn ? "What's new" : 'Yang Baru') : label;
      parts.push(`<div class="app-update-notes-heading">${escapeXml(normalized)}</div>`);
      continue;
    }
    const bulletMatch = line.match(/^(?:[-*•◆▪▸►]|\d+\.)\s+(.+)$/u);
    if (bulletMatch) {
      if (!hasAnyHeading) {
        hasAnyHeading = true;
        parts.push(`<div class="app-update-notes-heading">${isEn ? "What's new" : 'Yang Baru'}</div>`);
      }
      if (!inList) {
        parts.push('<ul class="app-update-notes-list">');
        inList = true;
      }
      const itemText = escapeXml(bulletMatch[1]).replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
      parts.push(`<li>${itemText}</li>`);
      continue;
    }
    closeList();
    const paraText = escapeXml(line).replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
    parts.push(`<p class="app-update-notes-para">${paraText}</p>`);
  }
  closeList();
  return parts.join('');
}

export function openAppUpdateOverlay(opts: {
  version: string;
  releaseNotes?: string;
  isEn?: boolean;
  mode?: 'checking' | 'available' | 'downloading' | 'uptodate' | 'error';
  onStartInstall?: () => void;
}): void {
  const overlay = document.getElementById('appUpdateOverlay');
  if (!overlay) return;
  const isEn = Boolean(opts.isEn);
  const mode = opts.mode || 'downloading';
  appUpdateActiveMode = mode;
  const statusEl = document.getElementById('appUpdateStatusText');
  const barFill = document.getElementById('appUpdateBarFill');
  const bytesEl = document.getElementById('appUpdateBytesText');
  const notesBody = document.getElementById('appUpdateNotesBody');
  const verEl = document.getElementById('appUpdateVersionText');
  const startBtn = document.getElementById('btnStartAppUpdateNow') as HTMLButtonElement | null;
  const closeBtn = document.getElementById('btnCloseAppUpdateOverlay') as HTMLButtonElement | null;
  const cleanVer = (opts.version || '2.4.0').replace(/^v/i, '');

  if (barFill) {
    barFill.classList.toggle('indeterminate', mode === 'checking');
  }

  if (mode === 'checking') {
    if (statusEl) statusEl.textContent = isEn ? 'Checking for Yomic updates...' : 'Memeriksa pembaruan Yomic...';
    if (barFill) barFill.style.width = '35%';
    if (bytesEl) {
      bytesEl.textContent = '';
      bytesEl.style.display = 'none';
    }
  } else if (mode === 'available') {
    if (statusEl) statusEl.textContent = isEn ? `Update available: v${cleanVer}` : `Pembaruan tersedia: v${cleanVer}`;
    if (barFill) barFill.style.width = '0%';
    if (bytesEl) {
      bytesEl.style.display = '';
      bytesEl.textContent = isEn ? 'Ready to download & install' : 'Siap diunduh & dipasang';
    }
  } else if (mode === 'uptodate') {
    if (statusEl) statusEl.textContent = isEn ? 'Yomic is up to date!' : 'Yomic sudah versi terbaru!';
    if (barFill) barFill.style.width = '100%';
    if (bytesEl) {
      bytesEl.style.display = '';
      bytesEl.textContent = isEn ? 'You are running the latest version' : 'Anda sudah menggunakan versi terbaru';
    }
  } else if (mode === 'error') {
    if (statusEl) statusEl.textContent = isEn ? 'Failed to check for updates.' : 'Gagal memeriksa pembaruan.';
    if (barFill) barFill.style.width = '0%';
    if (bytesEl) {
      bytesEl.style.display = '';
      bytesEl.textContent = isEn ? 'Please check your internet connection' : 'Periksa koneksi internet Anda';
    }
  } else {
    if (statusEl) statusEl.textContent = isEn ? 'Downloading Yomic...' : 'Mengunduh Yomic...';
    if (barFill) barFill.style.width = '0%';
    if (bytesEl) {
      bytesEl.style.display = '';
      bytesEl.textContent = '0.00 MB / 0.00 MB';
    }
  }

  const notesCard = overlay.querySelector<HTMLElement>('.app-update-notes-card');
  const shouldShowNotes = mode !== 'checking' && mode !== 'error';
  if (notesCard) {
    notesCard.style.display = shouldShowNotes ? '' : 'none';
  }
  if (notesBody) {
    notesBody.innerHTML = shouldShowNotes ? formatUpdateReleaseNotesHtml(opts.releaseNotes || '', isEn) : '';
  }
  if (verEl) {
    verEl.textContent = isEn ? `Version: ${cleanVer}` : `Versi: ${cleanVer}`;
  }
  if (startBtn) {
    if (mode === 'available' && opts.onStartInstall) {
      startBtn.style.display = 'inline-flex';
      startBtn.textContent = isEn ? 'Update Now' : 'Perbarui Sekarang';
      startBtn.onclick = opts.onStartInstall;
    } else {
      startBtn.style.display = 'none';
      startBtn.onclick = null;
    }
  }
  if (closeBtn) {
    closeBtn.style.display = mode === 'downloading' ? 'none' : 'inline-flex';
    closeBtn.title = isEn ? 'Close (Esc)' : 'Tutup (Esc)';
    closeBtn.setAttribute('aria-label', isEn ? 'Close' : 'Tutup');
    closeBtn.onclick = () => closeAppUpdateOverlay();
  }

  if (appUpdateEscHandler) {
    document.removeEventListener('keydown', appUpdateEscHandler);
  }
  appUpdateEscHandler = (ev: KeyboardEvent) => {
    if (ev.key === 'Escape' && appUpdateActiveMode !== 'downloading') {
      ev.preventDefault();
      closeAppUpdateOverlay();
    }
  };
  document.addEventListener('keydown', appUpdateEscHandler);

  overlay.classList.add('open');
  overlay.setAttribute('aria-hidden', 'false');
}

export function updateAppUpdateOverlayProgress(
  payload: { progress?: number; downloadedBytes?: number; totalBytes?: number; stage?: string },
  isEn = false
): void {
  const overlay = document.getElementById('appUpdateOverlay');
  if (!overlay || !overlay.classList.contains('open')) return;

  const statusEl = document.getElementById('appUpdateStatusText');
  const barFill = document.getElementById('appUpdateBarFill');
  const bytesEl = document.getElementById('appUpdateBytesText');
  const closeBtn = document.getElementById('btnCloseAppUpdateOverlay') as HTMLButtonElement | null;

  const pct = Math.max(0, Math.min(100, Number(payload.progress) || 0));
  const dlMb = ((Number(payload.downloadedBytes) || 0) / (1024 * 1024)).toFixed(2);
  const totMb = ((Number(payload.totalBytes) || 0) / (1024 * 1024)).toFixed(2);

  if (barFill) {
    barFill.classList.remove('indeterminate');
    barFill.style.width = `${pct}%`;
  }
  if (bytesEl && (Number(payload.totalBytes) || 0) > 0) {
    bytesEl.textContent = `${dlMb} MB / ${totMb} MB`;
  } else if (bytesEl && (Number(payload.downloadedBytes) || 0) > 0) {
    bytesEl.textContent = `${dlMb} MB`;
  }

  if (payload.stage === 'installing') {
    appUpdateActiveMode = 'downloading';
    if (statusEl) statusEl.textContent = isEn ? 'Installing Yomic...' : 'Memasang Yomic...';
    if (barFill) barFill.style.width = '100%';
  } else if (payload.stage === 'error') {
    appUpdateActiveMode = 'error';
    if (statusEl) statusEl.textContent = isEn ? 'Failed to download update.' : 'Gagal mengunduh pembaruan.';
    if (closeBtn) closeBtn.style.display = 'inline-flex';
  } else {
    appUpdateActiveMode = 'downloading';
    if (statusEl) statusEl.textContent = isEn ? 'Downloading Yomic...' : 'Mengunduh Yomic...';
  }
}

export function closeAppUpdateOverlay(): void {
  const overlay = document.getElementById('appUpdateOverlay');
  if (!overlay) return;
  overlay.classList.remove('open');
  overlay.setAttribute('aria-hidden', 'true');
  if (appUpdateEscHandler) {
    document.removeEventListener('keydown', appUpdateEscHandler);
    appUpdateEscHandler = null;
  }
}
