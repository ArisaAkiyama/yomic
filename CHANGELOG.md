# Changelog

All notable changes to **Yomic** will be documented in this file.

## [2.6.2] - 2026-11-28

Update Details：
◆ Add one-click Reset All Progress (Ch. N) action (data-action="reset-all-progress") in the Manga Detail chapter context menu (manga-detail.ts) to clear every chapter's read flag, saved page position (lastPageRead = 0), localStorage chapter checkpoints (yomic_ch_pos_*), and LastViewed timestamp back to the lowest chapter with optimistic UI sync and snapshot rollback on backend errors.
◆ Add contextual Reset Page Progress action in the chapter context menu whenever an unread chapter still holds partial page progress (lastPageRead > 0 or saved localStorage page > 1), paired with centralized clearLocalChapterPos and recomputeMangaReadProgress helpers in manga-detail.ts.
◆ Add targeted backend page-cache eviction helpers (ClearChapterLastPageCacheForChapter and ClearChapterLastPageCacheForManga) in BackendIpcController.MangaDetail.cs to purge stale _chapterLastPageReadCache entries across all cache key formats (source|chapterUrl, rawMangaId_ch, db-id_ch, and source|mangaUrl_ch) on single-unread and all-unread actions.
◆ Add automated C# xUnit and Node.js WebUI regression tests in BehavioralServicesTests.cs and tests/WebUI.Tests/webui.test.mjs verifying single-chapter and manga-wide LastPageRead/LastViewed SQLite resets, localStorage yomic_ch_pos_ cleanup, partial-progress detection, and Start/Resume button restoration.
◆ Improve Manga Detail floating Resume/Start CTA and last-read chapter highlight (getSavedLocalChapterPage & hasAnyProgress) plus authoritative backend MangaItem DTO reconciliation (upsertMangaItem) across mark-read, mark-unread, mark-before-read, and reset-all-progress actions so progress resets immediately restore Start Ch. <lowest> without stale highlights.
◆ Improve Library context menu Mark Unread (ctxMarkUnread in library.ts) to reset every chapter's lastPageRead to 0, restore lastReadChapter to the lowest chapter, clear all matching yomic_ch_pos_* localStorage entries (both id and db-{dbId} prefixes), and preserve complete rollback on sync failure.
◆ Improve README.md visual documentation by refreshing all 8 high-resolution 1280x720 Dark Mode English screenshots in docs/screenshots/ reflecting the v2.6 interface.
◆ Fix marking a chapter or manga as unread leaving partial page progress behind in SQLite (LibraryService.SetChapterReadStatusAsync & MarkChaptersAsUnreadAsync now zero chapter.LastPageRead = 0) and fix LastViewed reset detection to check sibling chapters for both Read and LastPageRead > 0.
◆ Fix Chapter 0 (ChapterNumber == 0) and empty-chapter fallback coercion in BackendIpcController.BootstrapAndMapping.cs (MapMangaToDto checking ChapterNumber >= 0 instead of > 0 and returning 0 when totalCh == 0).

## [2.6.1] - 2026-11-27

Update Details：
◆ Add full theme-aware Reader design token palette in tokens.css defining complete Crisp White / Light Mode tokens (--reader-bg: #FAFAFA, --reader-surface: #FFFFFF, --reader-hud: rgba(255,255,255,0.92), --reader-hud-strong, --reader-surface-hover/active, --reader-ink, --reader-ink-secondary/muted, --reader-accent/accent-ink, --reader-track, --reader-thumb-bg/shadow, --reader-toggle-off, --reader-page-border, --reader-shadow/shadow-lg) alongside Carbon Dark Mode tokens (--reader-bg: #000000, --reader-surface: #161618, --reader-hud: rgba(14,14,16,0.90)).
◆ Add automated Light/Dark Reader theme regression test in tests/WebUI.Tests/webui.test.mjs verifying CSS token parity across tokens.css, 05-reader.css, and 07-refinements-updater.css.
◆ Improve Reader Overlay, Top HUD Bar, Chapter Dropdown, Floating Scrubber Pill, Circular Prev/Next Navigation Buttons, Bottom Dock Sheets, Zoom Slider & Presets, and Display/Image-Processing Toggle Switches in 05-reader.css and 07-refinements-updater.css to adapt seamlessly to both Light Mode and Dark Mode via CSS variables with smooth background-color transitions.
◆ Improve Reader interactive states in reader.ts (empty chapter dropdown, failed page retry card, and reload/action buttons) to use theme-aware CSS variables (--reader-surface, --reader-border, --reader-ink, --reader-ink-muted, --reader-accent, --reader-accent-ink) instead of hardcoded dark colors.
◆ Fix Reader staying forced dark with mismatched hardcoded surfaces, borders, edge navigation hover zones, and toggle-switch OFF tracks when Light Mode is active by migrating all remaining Reader UI styles to semantic --reader-* tokens.

## [2.6.0] - 2026-11-26

Update Details：
◆ Add shared DoH-first public IP resolver (UrlSecurityHelper.ResolvePublicIpsWithDoHAsync with 5-minute cache, parallel A+AAAA resolution, socket-level SSRF protection, and Offline Mode enforcement before literal IP fast-path) wired into UpdateService (CreateUpdateHttpClient) and unified DNS cache clearing across NetworkService, HttpSource, ExtensionRepositoryService, and UrlSecurityHelper.
◆ Add bulk single-query chapter download status reset (LibraryService.MarkAllChaptersNotDownloadedAsync) invoked by BackendIpcController.Downloads.cs when deleting all downloaded chapters of a manga, and exclusive DB maintenance barrier protection during SQLite file reset/recreation in LibraryService.
◆ Add expanded CI pipeline checks in .github/workflows/ci-desktop.yml enforcing 9-surface version parity (csproj Version/FileVersion/AssemblyVersion, app.manifest, Installer/Yomic.iss, UpdateService.cs, index.html, main.css, settings.ts, and compiled settings.js) plus automated Inno Setup 6 installer script compilation validation.
◆ Add expanded regression test coverage across BehavioralServicesTests.cs, IntegrityAndCatalogTests.cs, and tests/WebUI.Tests/webui.test.mjs covering Chapter 0 preservation, DownloadService cancel-during-finalize & resume-while-unwinding races, UpdateService DoH/Offline Mode, SQLite maintenance lease disposal, and dual-page zoom centering.
◆ Improve UpdateService security and resilience with Offline Mode guard on update downloads, manual per-hop redirect validation on .sha256 sidecar fetches (TryFetchSidecarSha256Async), and unified DoH-protected Update HttpClient across API checks, installer downloads, and sidecar requests.
◆ Improve MangaDbContext maintenance barrier lifecycle with explicit Dispose/DisposeAsync lease cleanup (SqlitePragmaInterceptor.ReleaseLeaseIfAttached), EF Core-managed connection opening (context.Database.OpenConnection) so raw connections never bypass the barrier, and internal bypassMaintenanceBarrier support for maintenance self-checks.
◆ Improve NetworkService DNS resilience by adding Cloudflare 1.1.1.1 failover to Google DoH & default profiles, immediately reflecting Offline Mode when toggled mid-probe, and re-checking IsOfflineMode after HTTP/ICMP probes complete so offline state is never overwritten by in-flight probes.
◆ Improve Dual Page Reader zoom range and horizontal centering (getDualPageImgStyle supporting 25%-300% scale in reader.ts + safe center flex alignment with width: max-content in 07-refinements-updater.css) and clipped hover-unblur transitions on list/update thumbnails in 06-modals-dark-stats.css.
◆ Fix Chapter 0 (Prologue / Ch. 0) being coerced into Chapter 1 across Downloads queue (BackendIpcController.Downloads.cs & downloads.ts), Updates feed (BackendIpcController.LibraryAndHistory.cs), and Reader page progress caching (BackendIpcController.Reader.cs) by checking ChapterNumber >= 0 instead of > 0.
◆ Fix DownloadService concurrency edge cases: prevent cancellation during CBZ packaging/directory move from leaving orphaned files (rolling back promoted files cleanly), preserve ResumeRequestedWhileUnwinding so resuming a still-unwinding paused chapter auto-queues cleanly, and clean up leftover .tmp folders when cancelling paused downloads in BackendIpcController.Downloads.cs.
◆ Fix CloudflareBypassService interception race condition by attaching page.Request handler BEFORE calling SetRequestInterceptionAsync(true) and immediately closing unexpected JS-spawned popups (args.Target.Opener != null) to eliminate window.open SSRF races.
◆ Fix SourceManager.GetSourceByName substring shadowing (preferring exact case-insensitive match first so "Komiku.com" never shadows "Komiku") and ExtensionRepositoryService custom .js stem extraction (stripping .js before splitting on '.' so "mycustom.js" resolves to "mycustom" instead of "js").
◆ Fix bundled extensions (MaidManga v1.2.0, MangaDex v1.2.0, Mangalay v1.2.0, WeebCentral v1.2.0): fix pipe-header |Origin= delimiter, add WeebCentral lazy-load image priority & placeholder filtering, and prevent duplicate infinite-scroll pages in Mangalay.

## [2.5.0] - 2026-11-26

Update Details：
◆ Add per-database reader/writer maintenance barrier (DbMaintenanceState, ConnectionLease, EnterExclusiveMaintenanceAsync, and SqlitePragmaInterceptor connection lifecycle hooks) in MangaDbContext.cs so BackupService.RestoreBackupAsync drains and pauses both concurrent writers and concurrent readers before ClearAllPools(), WAL/SHM deletion, and file swap.
◆ Add split-horizon DNS & hosts-file SSRF defense (ResolveAndValidateHostForBrowserAsync, ResolveViaConfiguredDoHAsync, ResolveViaSystemDnsAsync) in UrlSecurityHelper.cs querying both System DNS and the configured DoH provider in parallel and failing closed if any resolved IP is private/loopback/link-local, with a 5s cache and cache-bypass on top-level navigations in CloudflareBypassService and WebViewPageExtractorWindow.
◆ Add atomic single-transaction history and reading progress reset (LibraryService.ClearAllHistoryAndProgressAsync) clearing chapter read flags, partial page progress, LastViewed timestamps, and history entries in one lock/transaction, paired with full UI state snapshot rollback on backend errors in updates-history.ts.
◆ Add consistent 18+ explicit cover blurring (blurExplicitCovers via CSS blur class) across Source Catalog Feed (browse.ts), Manga Detail (manga-detail.ts), Updates (updates-history.ts), History Grid & List (updates-history.ts), Downloads (downloads.ts), and Library (library.ts).
◆ Add URL deduplication and zero-new-item infinite-scroll circuit breaker in Browse Source Catalog (fetchSourceCatalogFeed in browse.ts) alongside explicit plugin totalPages handling in BackendIpcController.SourcesAndExtensions.cs to stop duplicate-card infinite scroll loops.
◆ Add deterministic LF line-ending pinning in .gitattributes for bundled Plugins/Catalog/*.js and index.min.json so SHA-256 digests stay 100% identical across local Windows checkouts, CI runners, and GitHub raw/CDN.
◆ Add expanded automated regression test coverage in BehavioralServicesTests.cs, IntegrityAndCatalogTests.cs, and tests/WebUI.Tests/webui.test.mjs covering split-horizon DNS SSRF guards, DB maintenance barriers, DoH failover, explicit cover blur, and plugin catalog integrity.
◆ Improve bundled extension catalog with minor version bumps across 101 updated extensions (e.g. 1.0.0 -> 1.1.0, 1.0.3 -> 1.1.0, 1.0.4 -> 1.1.0, 1.11.2 -> 1.12.0), Blogger paginated chapter feeds up to 1,500 chapters, lazy-load image priority & placeholder filtering in kiryuu.js, graceful empty-list handling in softkomik.js, and synced Git-blob SHA-256 manifests.
◆ Improve ExtensionRepositoryService.UninstallExtension with strict identifier validation, fallback catalog merging (LoadFallbackCatalogEntriesSync), and atomic source path registration inside _sourcesLock before firing OnSourcesChanged in SourceManager.cs.
◆ Improve DownloadService cancellation and resource lifecycle by guarding terminal failure status updates under _processingLock (preventing overwrites of concurrent Pause/Cancel) and disposing CancellationTokenSource instances across completed, cancelled, queued, and history download requests.
◆ Improve real-time download badge synchronization in backend-sync.ts and downloads.ts (matching deleted chapter downloads by normalized chapterUrl or mangaId+chapterNumber and computing completedCountByMangaId accurately) plus CSS polish across Reader, Modals/Dark Stats, and Updater modules.
◆ Fix silent in-app auto-update failing to relaunch Yomic after installation in Installer/Yomic.iss by removing the postinstall flag from the /SILENT relaunch entry (Inno Setup skips postinstall in /SILENT mode) and defaulting ShouldRelaunchAfterSilentUpdate to relaunch unless /RELAUNCH=0 is explicitly passed.
◆ Fix active download file-handle locks and IsDownloaded race conditions in BackendIpcController.Downloads.cs by cancelling active/queued chapter downloads before deleting files on disk.
◆ Fix Reader refresh cache-busting corrupting inline data: and blob: image URIs in reader.ts by introducing appendCacheBustParam to safely update query timestamps only on HTTP/HTTPS URLs.
◆ Fix premature DoH failover termination in NetworkService.cs and HttpSource.cs (only stopping failover when IPs are resolved or both A & AAAA queries succeed cleanly, and appending StringBuilder logs sequentially after Task.WhenAll for thread safety).
◆ Fix one-time v1 -> v2 SourceIdMigrationService repeatedly re-running on every launch when orphaned rows from uninstalled extensions remain by always marking migration complete once run with loaded sources.

## [2.4.0] - 2026-11-26

Update Details：
◆ Add deep SSRF & DNS-rebinding defense in UrlSecurityHelper.cs blocking RFC 6890/2544/TEST-NET IPv4 ranges and IPv6 transition/tunnel ranges (::/96, SIIT, NAT64, 6to4, Teredo, RFC 6666), plus dual-stack DoH/System-DNS resolution verification (IsSafeResolvedPublicUrlAsync with 60s anti-rebinding cache), IsHostOrSubdomain helper, and strict magic-byte image payload validation (IsValidImagePayload for JPEG, PNG, GIF, WebP, BMP, TIFF, ICO, AVIF/HEIF, JPEG XL, and strict SVG).
◆ Add manual per-hop redirect validation (IsTrustedUpdateDownloadUri), 250 MB installer download safety cap, concurrency guard (_isInstalling), and official .sha256 sidecar checksum fetching & parsing (ParseSha256SidecarText & TryFetchSidecarSha256Async) in UpdateService.
◆ Add unified process-wide SQLite write & maintenance lock (MangaDbContext.DatabaseWriteGate) shared across LibraryService, BackendIpcController writes, and BackupService restore swaps, paired with ReplaceSqliteFileWithRetry, strict WAL/SHM removal checks against SQLITE_CORRUPT, and DownloadService.WaitForActiveDownloadsToStopAsync preserving .tmp progress before restore.
◆ Add deferred background Jint plugin loading (SourceManager deferExtensionLoad + EnsureExtensionsLoadedAsync) and non-blocking cached announcement bootstrap (event:announcements-updated) so MainWindow opens instantly without UI-thread freezes.
◆ Add bounded single-flight next-chapter page list preloading (_preloadedPageListCache, max 32 entries, 10-min TTL) triggered within 3 pages of chapter end in BackendIpcController.Reader.cs and transient src-* memory eviction during long browsing sessions.
◆ Add manual per-hop redirect handling with strict host-scoped Cookie & Authorization isolation across DownloadService (SendPageRequestWithSafeRedirectsAsync), WebResourceImageProxy, and JsMangaSource to prevent cross-origin cookie/auth leakage on 302 redirects, plus magic-byte image validation and curl --resolve DoH public-IP pinning.
◆ Add host-specific Cloudflare pre-solved cookie lookup (HasPreSolvedCookiesFor), parallel IPv4+IPv6 DoH SSRF guards with race-free ConditionalWeakTable page tracking (_guardedPages), automatic headless popup closure, and parent-domain cookie merge cleanup in CloudflareBypassService.
◆ Add parallel IPv4/IPv6 DoH resolution with 5-minute caching and zero ISP DNS leakage when DoH succeeds (NetworkService.ResolveHostIpsAsync), socket-level SSRF blocking with 3s per-IP connect caps, and single-flight connectivity checks (_isCheckingConnectivity).
◆ Add official-GitHub-only index.min.json fetching in ExtensionRepositoryService (preventing third-party CDN manifest forgery), cached repo DoH resolution (_repoDohCache), and atomic index cache writes (WriteIndexCacheAtomically).
◆ Add MyAnimeList auth epoch protection (_authEpoch & TryApplyRefreshedTokens in MyAnimeListService) preventing stale background token refreshes from reviving logged-out sessions, plus strict host-only keyword matching in SourceIdMigrationService.
◆ Add optimistic WebUI updates with full state snapshot rollback on backend errors across Library (library.ts), Manga Detail (manga-detail.ts — MAL tracker, bookmarks, chapter read/download actions, Hiatus status badge), and Settings (settings.ts — history/cache/library maintenance), plus real-time sync of completed downloads into Library downloadedCount and filters (backend-sync.ts).
◆ Add Reader image fit/zoom builder (buildReaderImgStyle), debounced progress-save flush/cancel before chapter switching to prevent cross-chapter progress corruption, URL-hashed cover cache precedence in WebResourceImageProxy, modal/busy-dialog Tab focus trapping (app.ts & settings.ts), capture-phase Escape isolation in tutorial.ts, and formatted reading progress in updates-history.ts.
◆ Improve 90+ bundled JavaScript extensions in Plugins/Catalog with getMangaList sort ("latest" vs popular) support (including komiku.js and Blogger/Madara/MangaStream sources), mangadex.js chapter feed offset & totalChapters pagination fix, cleaned unsupported filter metadata, and null-safe JSON feed parsing.
◆ Improve Inno Setup installer (Installer/Yomic.iss) with graceful close before force-kill so App.OnExit cleanly flushes SQLite WAL/logs and shuts down Puppeteer on upgrades.
◆ Add upgraded Windows CI pipeline (.github/workflows/ci-desktop.yml enforcing strict tsc compile, automated version parity across csproj/iss/updater/WebUI, -warnaserror Release builds, and clean self-contained publish verification) and massively expanded automated test suites (new BehavioralServicesTests.cs + expanded CoreServicesTests, IntegrityAndCatalogTests, and WebUI node:test coverage).

## [2.3.0] - 2026-11-24

Update Details：
◆ Add centralized URL, SSRF, Cookie, and Filesystem security helper (UrlSecurityHelper.cs) enforcing public HTTP/HTTPS validation, IPv4/IPv6 private/loopback/CGNAT/metadata IP blocking, bounded HTTP stream reading (ReadContentBoundedAsync) against OOM DoS, strict RFC 6265 cookie domain matching, Google Apps Script webhook allowlisting, and directory containment checks.
◆ Add fail-closed SHA-256 cryptographic integrity verification in UpdateService (ExtractExpectedSha256 & VerifyHashMatchesExpectedHex using constant-time CryptographicOperations.FixedTimeEquals), strict official repository URL/redirect scoping, stale update directory cleanup, single-instance mutex handoff (ReleaseSingleInstanceMutexForRestart), and silent auto-relaunch support in Installer/Yomic.iss.
◆ Add SHA-256 release checksum generator script (scripts/generate-release-sha256.ps1) to emit verified .sha256 files and release-note integrity blocks.
◆ Add strict WebView2 security boundaries in MainWindow.xaml.cs and index.html: Content-Security-Policy (CSP) meta header, external script preload-theme.js, strict app.yomic.local IPC origin validation, blocked external navigation/frames, and prevented unrestricted popup windows.
◆ Add comprehensive XSS & injection hardening across all WebUI views (app.ts, library.ts, manga-detail.ts, reader.ts, downloads.ts, browse.ts, extensions.ts, settings.ts, modals.ts, updates-history.ts) with escapeXml, sanitizeImageSrc, sanitizeCssColor, modal keyboard focus trapping, and safe Cancel-first focus on destructive confirmation dialogs.
◆ Add DownloadSpeedMode presets (Safe, Fast, Max) in SettingsService, DownloadService, BackendIpcController, and Settings UI for configurable concurrent chapter/page download throughput.
◆ Add automatic installed extension updating on startup (AutoUpdateInstalledExtensionsAsync & event:extensions-updated) plus fail-closed SHA-256 verification on third-party CDN mirrors, staged plugin validation before replacing working scripts, and path traversal protection in ExtensionRepositoryService.
◆ Add DoH-first SSRF request interception guard (InstallSsrfRequestGuardAsync) and thread-safe host-scoped cookie merging in CloudflareBypassService so ISP DNS poisoning never causes false blocks while blocking private/loopback targets.
◆ Add pooled reusable HttpClient with DoH-change reset, atomic disk cache writes (WriteFileAtomicAsync), curl --resolve public-IP pinning without unsafe auto-redirects, HTML/Cloudflare error payload rejection (LooksLikeHtmlPayload), and remote SVG script neutralization in WebResourceImageProxy.
◆ Add online live SQLite backup (CopyLiveSqliteDatabaseOnline), zip-bomb bounded extraction caps (512 MB DB / 5 MB settings), isolated staging validation with SQLite integrity check before restore, and OAuth/webhook credential sanitization in BackupService.
◆ Add relational SQLite foreign keys with cascade deletes, legacy orphan cleanup on upgrade, performance composite indexes (Source+Url, Favorite, MangaId+Url, DateFetch, MangaId+LastRead), and tuned WAL/busy_timeout/memory-temp PRAGMAs in MangaDbContext.
◆ Add full BackendIpcController modularization into 7 focused partial handler files (BootstrapAndMapping, Downloads, LibraryAndHistory, MangaDetail, Reader, SettingsAndSystem, SourcesAndExtensions) and CSS modularization of main.css into 7 ordered stylesheets under WebUI/styles/modules/.
◆ Add automated CI & Test Infrastructure: GitHub Actions workflow (.github/workflows/ci-desktop.yml), C# xUnit test suite (SecurityTests, CoreServicesTests, BackupAndDatabaseTests, IntegrityAndCatalogTests covering 102-plugin Jint loading & SQLite FK/indexes), and Node.js WebUI automated tests (tests/WebUI.Tests/webui.test.mjs).
◆ Improve 90+ bundled JavaScript extensions in Plugins/Catalog (including VoraToon v1.2.2 fast disjoint regex RSC parser & MangaDex v1.0.4 translated chapter feed) with null-safe response guards and try/catch JSON parsing to prevent plugin crashes on transient network errors.
◆ Improve reliability across MyAnimeListService (serialized SemaphoreSlim token refresh & cryptographically unbiased PKCE state), AutoUpdateTaskService (schtasks DAILY fallback for 24h+ & concurrent stdout/stderr drain), WebViewPageExtractorWindow (deadlock-free watchdog before init/polling), MainWindow (adaptive MinWidth/MinHeight on compact high-DPI laptops), and Google Sheets feedback webhook (atomic rate limiting, 6 MB magic-byte screenshot validation, private Drive storage & formula injection sanitization).

## [2.2.1] - 2026-11-22

Update Details：
◆ Add full-screen native-themed App Updater Overlay (#appUpdateOverlay in index.html, main.css, and settings.ts) featuring Yomic branding, real-time progress bar, live MB downloaded/total counter, formatted GitHub release notes card, one-click Update Now button, and Escape/close dismissal.
◆ Add detailed update download progress reporting (onDetailedProgress callback in UpdateService.DownloadAndInstallUpdateAsync) streaming throttled (120ms) percentage, downloadedBytes, totalBytes, and lifecycle stage (downloading, installing, error).
◆ Add live IPC update progress bridge (event:app-update-progress in BackendIpcController and backend-sync.ts) wired directly to updateAppUpdateOverlayProgress in settings.ts with an extended 300-second installer download timeout.
◆ Improve Check for Updates flow in Settings About view to open the full-screen Updater Overlay immediately across checking, available, up-to-date, downloading, installing, and error states with bilingual (ID/EN) status labels.

## [2.2.0] - 2026-11-22

Update Details：
◆ Add thread-safe 48 MB in-memory LRU image cache and zero-copy fast path (<= 1.5 MB JPEG/WebP) in WebResourceImageProxy for instant 0ms scroll-back and chapter reopening, alongside increased 6x concurrent page download throttling.
◆ Add smart Reader lookahead page prefetching (5 pages ahead + 1 behind with fetchpriority="high" and async decode) and O(log N) binary search active-page tracking (findActiveWebtoonPageBinarySearch) in reader.ts to eliminate forced synchronous layouts during 60-120 FPS Webtoon scrolling.
◆ Add redesigned mobile-style Reader Top HUD and Bottom Dock (index.html & reader.ts) featuring left-aligned title & chapter picker, in-reader chapter bookmark toggle (btnReaderBookmarkChapter), floating chapter sheet dock, and dual-page spread alignment with dynamic zoom scaling.
◆ Add offline CBZ comic archive download support (SaveDownloadsAsCbz with automatic cached extraction) and automatic downloaded chapter deletion after reading (AutoDeleteAfterRead) in DownloadPathService, DownloadService, SettingsService, and BackendIpcController.
◆ Add real-time inline chapter download progress spinners and status badges in Manga Detail view (updateDetailChapterDownloadsInPlace in manga-detail.ts) with instant cancel/delete actions without re-rendering the view.
◆ Add ultra-minimalist Settings Hub architecture (settings.ts) with dedicated category sub-pages (Appearance, Library, Reader, Downloads, Tracking, Network & Sources, Data & Storage, Keyboard Shortcuts, About) and header back navigation.
◆ Add Source Pinning in Browse view (browse.ts) with dedicated Pinned vs. All Sources sections and per-source pin/unpin toggle buttons persisted locally.
◆ Add dedicated filter flyout modals for Browse language, Extensions language, Source Catalog filters, and Chapter filters with unified Escape-key dismissal and smooth workspace view transitions.
◆ Add enriched MyAnimeList (MAL) search metadata (InUserList, UserStatus, UserChaptersRead, UserScore, TotalChapters) in MyAnimeListService and one-click autoBind tracker linking in BackendIpcController.
◆ Add rich Announcements modal renderer (modals.ts & Announcement.cs) with persistent read tracking, localized date formatting, color-coded tone badges (Pembaruan, Peringatan, Penting), and formatted bullet/blockquote parsing.
◆ Improve Downloads queue progress formatting (downloads.ts) and byte-accurate chapter size display (BackendIpcController) eliminating duplicate percentage prefixes.
◆ Improve first-launch onboarding tutorial (tutorial.ts & index.html) with immediate flash-free overlay trigger (data-onboarding-pending) before initial workspace paint.
◆ Improve clean initial extension setup by removing automatic default plugin seeding in ExtensionRepositoryService so new users choose and install preferred sources directly from Extensions.
◆ Improve toast notification stacking in app.ts (capped at 3 concurrent pills with smooth scale-out transitions and alert-aware icons).
◆ Improve WebUI styling and visual polish across Library, Manga Detail, Reader, Browse, Extensions, Downloads, Updates, History, and Settings in main.css and tokens.css.

## [2.1.0] - 2026-11-19

Update Details：
◆ Add smart WebView2 chapter page extractor window (WebViewPageExtractorWindow) that runs off-screen silently during an 8-second grace period to resolve automatic security checks without popups, and automatically reveals itself centered on screen only when interactive verification (Cloudflare Turnstile / CAPTCHA) is required.
◆ Add custom per-source WebView page extraction hooks (chapterReadySelector & pageWebViewExtractScript) and verification status reporting (GetPageListWithStatusAsync & RequiresVerification) in JsMangaSource.
◆ Add automatic & manual WebView verification fallback flow in BackendIpcController (reader:pages with manualVerify & requiresVerification flags) to seamlessly recover chapter image lists when HTTP scraping is gated by dynamic client-side challenges.
◆ Add "Verify in WebView" (Verifikasi di WebView) interactive recovery button and dedicated "Security Verification Required" state in Reader view (reader.ts) when a chapter requires user verification.
◆ Add dynamic Next.js Server Action ID resolver (resolveUnlockActionId) in VoraToon extension (voratoon.js v1.2.1) to automatically extract the active unlockChapterPages action hash from Next.js series chunks instead of relying on a static hardcoded hash.
◆ Add automatic Next-Action cache invalidation and 404 retry logic in VoraToon extension when unlocking pagesTicket chapter images, plus automatic Turnstile/pagesTicket verification detection.
◆ Add standard multi-resolution Windows application icon (Assets/app.ico) containing 16x16, 24x24, 32x32, 48x48, and 64x64 32-bit ARGB BMP DIB frames alongside a 256x256 high-DPI PNG frame for crisp rendering across Windows 11 Taskbar, Desktop shortcuts, and Start Menu.
◆ Improve RSC (React Server Components) JSON chunk parser performance in voratoon.js using compiled regular expression token scanning instead of character-by-character loops.
◆ Improve MSBuild output packaging in YomicDesktop.csproj and Inno Setup script (Installer/Yomic.iss) to deploy app.ico to both {app}\app.ico and {app}\Assets\app.ico while keeping Assets/app.ico embedded as a WPF resource.
◆ Fix blank white document icon on Windows 11 Taskbar, Desktop shortcut, and Start Menu shortcut after installation by ensuring {app}\app.ico is always packaged and deployed by the installer.
◆ Fix startup XamlParseException / DirectoryNotFoundException crash on MainWindow initialization by keeping Assets/app.ico as an embedded WPF Resource instead of an external AssemblyAssociatedContentFile.
◆ Fix VoraToon chapter images failing to load when Next.js deployment updates rotate the unlockChapterPages Server Action ID or require client-side ticket verification.

## [2.0.0] - 2026-11-17

### Added
- **Complete Hybrid Architecture Rewrite**: Migrated from Avalonia UI to **.NET 8 WPF + Microsoft Edge WebView2 + Modular TypeScript**.
- **SkiaSharp Image Processing Engine**: Auto-crop borders, smart downscaling, optional sharpening, and WebP compression via `WebResourceImageProxy`.
- **Reading Insights Dashboard**: 14-day activity chart, top genres, completion rate, estimated reading time, and **Daily Streak** tracking across Library and History.
- **MangaDex Language Flag Switcher (`ID` / `EN`)**: Direct header switcher for Indonesian and English MangaDex catalogs and chapters.
- **102+ Extension Catalog**: Integrated offline catalog merge with [ArisaAkiyama/extension-yomic](https://github.com/ArisaAkiyama/extension-yomic) (`src/en` & `src/id`).
- **Enhanced Security & Stability**: Single-instance `Mutex`, headless `--background-update`, DPAPI-encrypted MyAnimeList OAuth2 tokens, SQLite WAL checkpointing in `.yomicbackup`, and local proxy path validation.
- **Clean v1 → v2 Installer Upgrade**: Automated legacy v1 cleanup in `Installer/Yomic.iss` while preserving user data and installed `.js` extensions.

## [1.8.0] - 2026-08-10

### Added
- Skeleton loading placeholder animations for library and source list view items.
- Enhanced window title bar header styling with solid Charcoal Black (`#111214`) background.
- Improved window caption button alignment and title bar margins on maximized window state.

### Changed
- Updated application version to 1.8.0.
- Built new installer package for distribution.

## [1.7.1] - 2026-07-26

### Added
- Native .NET 10 Desktop Runtime auto-downloader inside Inno Setup installer.
- MSBuild auto-installer compiler target in `Yomic.csproj`.
- Terminal / Command Prompt CLI integration (`yomic` command).
- Single-instance protection (`AppMutex`) and automatic cache cleanup on upgrade.
- Complete 100% Bahasa Indonesia language support in installer.

## [1.7.0] - 2026-07-23

### Changed
- Updated application version to 1.7.0.
- Built new installer package for distribution.

## [1.3.0] - Major Update & UI Overhaul

Update Details：
◆ Add the ability to backup and restore user data, including library, read history, and app settings, into a single secure `.yomic` file.
◆ Add DNS over HTTPS configuration in Settings with multiple providers (Google DNS set as default) to improve connectivity and bypass restrictions.
◆ Improve the overall UI of the Settings page by introducing a more modern, premium aesthetic with rounded borders, soft shadows, and clean layouts.
◆ Improve the Sidebar Navigation by implementing a dedicated `SidebarBtn` style featuring a Windows 11 Fluent UI style pill selection indicator.
◆ Improve the layout and alignment of the "About" section in the Settings page, ensuring the "Check for Updates" and version number display (`v1.3.0`) are perfectly positioned.
◆ Fix a widespread visual bug where standard navigation buttons (like Back, Refresh, and Reader Next/Prev) incorrectly inherited a square gray/blue hover box from the Sidebar.
◆ Fix a bug where clicking on Fluent Icons inside buttons would sometimes trigger text selection, causing a solid blue background box to appear.
◆ Fix button border radius inheritance in `ReaderView`, ensuring all small icon buttons return to their intended smooth, circular hover effects.
◆ Fix the layout alignment and spacing logic for the backup data action buttons within `SettingsView.axaml`.
◆ Improve localization and consistency of button states across Dark and Light mode themes.

## [1.2.0] - 2026-03-25

### Added
- **New Extension**: Added support for `maid.my.id` source.
- **Infinite Scroll**: Implemented automatic infinite scroll behavior to the "Latest" section of WestManga.
- **Auto-Update Dialog**: Replaced the previous update toast notification with a new fully-featured Modal Dialog for application updates.

### Changed
- **Server Startup Performance**: Greatly optimized the local Node.js server startup speed (bypassed long installation checks and reduced port timeout delays, providing near-instant Online status).
- **Settings UI**: Refined button hover styles for "Visit Website" and "Check for Updates" to maintain consistent aesthetic theming in Light Mode.
- **Removed Screen Translator**: Completely stripped out the experimental Groq/Gemini translation `ItemsControl` overlays from `ReaderView` due to the unreliability of vision models strictly adhering to bounding boxes.

### Fixed
- **Reader Image Loading**: Remedied persistent `503 Service Unavailable` errors when loading chapter images.
- **WestManga Dates**: Fixed an issue where newly updated manga strings were displaying as "20492d ago" instead of "today".
- **Feedback UI**: Cleaned up unintentional textbox hover effects in the feedback dialog.

## [1.0.1] - 2026-02-02

### Changed
- **UI Refinement**: Standardized circular "Back" buttons with orange hover effect across all views.
- **Installer**: Fixed execution error ("File not found") and updated to use correct executable name.
- **About Page**: Dynamic version display binding to assembly version.
- **Architecture**: Separated Extensions into a dedicated repository ([ArisaAkiyama/extension-yomic](https://github.com/ArisaAkiyama/extension-yomic)).

## [1.0.0] - 2026-02-01

### Added
- Initial release of Yomic Desktop App.
- Core library management and reading features.
- Extension support for dynamic sources.
- Download manager for offline reading.
- Webtoon and Single/Double Page reading modes.
- Integrated VPN bypass for restricted sources.
- Modern Fluent UI with Dark/Light mode.
- Settings for customization (Secure Screen, Auto-Update).
- **Auto Update Checker**: Check for new releases directly from the app.
- **Smart Installer**: Supports auto-updates, firewall configuration, and clean uninstalls.
