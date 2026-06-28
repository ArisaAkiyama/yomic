<div align="center">

<img src="./Yomic/Assets/app.ico" alt="Yomic logo" title="Yomic logo" width="128"/>

# Yomic
### The Ultimate Desktop Manga Reader

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](./LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows-blue.svg)](https://github.com/ArisaAkiyama/yomic)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/Avalonia-11.3.11-orange.svg)](https://avaloniaui.net/)
[![Extensions](https://img.shields.io/badge/Extensions-Available-green.svg)](https://github.com/ArisaAkiyama/extension-yomic)

**Yomic** brings the best manga reading experience to your Windows desktop. Discover, read, and organize your favorite manga, webtoons, and comics from multiple sources in one beautiful, ad-free application.

[**Download Latest Release**](https://github.com/ArisaAkiyama/yomic/releases)

</div>

---

## Features

### Comprehensive Library & Modern UI
- **Mihon-Inspired Design**: Sleek, compact grid views with square covers, inline unread indicators, and dimmed text for read items.
- **Smart Organization**: Automatically categorizes your manga based on reading status (unread dot indicator, "NEW" badge for updates).
- **Search & Filter**: Find any manga in your library instantly with powerful search, sort, and tag filtering options.
- **Reading History**: Pick up exactly where you left off with localized reading progress tracking.

### Unlimited Sources (Extensions)
- **Plugin Architecture**: Support for varied sources via external DLL extensions.
- **Supported Sources**:
  - **Global / English**: Mangabats, Weebcentral, NHentai
  - **Indonesian**: Aarlas, AstralScans, ComicAzen, Kiryuu, KomikCast, KomikStation, Komiku, Kuromanga, Luvyaa, Maid, ManhwaIndo, WestManga
- **Grouped Extensions View**: Clear categorization between **Installed** (currently active) and **Available** (remotely hosted, ready to download) extensions with dynamic badges and counts.

### Immersive & Intelligent Reading
- **Webtoon Mode**: Smooth, continuous vertical scrolling optimized for webtoons with inertia scroll support.
- **Paged Mode**: Traditional left-to-right or right-to-left reading formats.
- **Smart Next-Chapter Preloading**: Automatically pre-downloads the pages of the next chapter in the background when you are within 3 pages of finishing the current chapter, ensuring instant page loading.
- **Zoom & Fit**: Auto-fit to width/height, custom zoom levels, and zoom-scale memory.
- **Keyboard & Mouse Navigation**: Use arrow keys, PageUp/PageDown, or mouse scroll/buttons for seamless reading.

### Performance & Utilities (QoL)
- **Auto-Clean Cache Limits**: Configurable max reader cache size (Disabled, 250MB, 500MB, 1GB, 2GB). Automatically clears the oldest cached pages using a Write-Time LRU-style cleanup upon exiting the reader or updating settings.
- **Offline Mode**: Full offline mode support that switches UI cleanly to local-only files with dedicated offline indicator screens.
- **VPN Bypass**: Integrated secure headers and sing-box proxy client support to bypass regional ISP blocks.
- **Auto-Update**: Built-in update checker checks for app releases on startup.

---

## Installation

### Prerequisites
- **Windows 10** or higher (64-bit).
- **.NET Desktop Runtime 10.0** (The installer handles this automatically).

### Detailed Steps
1. Go to the [**Releases Page**](https://github.com/ArisaAkiyama/yomic/releases).
2. Download the latest `Setup.exe` installer.
3. Run the installer and follow the on-screen instructions.
4. Launch **Yomic** from your desktop or start menu.

---

## Extensions Setup

Yomic features a modular extension system. You can discover, download, and update extensions directly from the user interface without any manual setup!

### Automatic Installation (Recommended)
1. Launch **Yomic** and navigate to the **Extensions** tab.
2. Under the **Available** section, you will see a list of all official sources fetched dynamically from the extension repository.
3. Click the **Download** button on any extension you want to install. It will download, install, and load automatically.
4. Go to the **Browse** tab to start reading!

### Manual Installation (For developers or custom DLLs)
1. Prepare your custom extension DLL (e.g., `Yomic.Extensions.CustomSource.dll`).
2. Go to the **Extensions** tab in Yomic.
3. Click the **Add** button in the top-right corner.
4. Select your `.dll` file from the file explorer. Yomic will copy and load it immediately.

---

## Building from Source

Requirements:
- **.NET 10.0 SDK**
- **Visual Studio 2022 (v17.12+)** or **VS Code** with C# Dev Kit

```bash
# Clone the repository
git clone https://github.com/ArisaAkiyama/yomic.git
cd yomic

# Build the Solution
dotnet build Yomic.sln
```

---

## Keyboard Shortcuts

| Key | Action |
|-----|--------|
| `Arrow Right` / `Page Down` | Next Page / Scroll Down |
| `Arrow Left` / `Page Up` | Previous Page / Scroll Up |
| `F11` | Toggle Fullscreen |
| `Esc` | Exit Fullscreen / Close Reader |
| `Ctrl+R` | Refresh Library |

---

## Contributing

Contributions are what make the open-source community such an amazing place to learn, inspire, and create. Any contributions you make are **greatly appreciated**.

1. Fork the Project
2. Create your Feature Branch (`git checkout -b feature/AmazingFeature`)
3. Commit your Changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the Branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

---

## Disclaimer

The developer(s) of this application does not have any affiliation with the content providers available. This application is strictly a tool for browsing and viewing media hosted on third-party websites. All content is retrieved via extensions from third-party sources.

---

## License

Distributed under the MIT License. See `LICENSE` for more information.
