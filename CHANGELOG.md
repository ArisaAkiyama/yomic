# Changelog

All notable changes to **Yomic** will be documented in this file.

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
