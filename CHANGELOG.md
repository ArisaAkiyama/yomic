# Changelog

All notable changes to **Yomic** will be documented in this file.

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
