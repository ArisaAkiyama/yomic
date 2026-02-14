<div align="center">

<img src="./Yomic/Assets/app.ico" alt="Yomic logo" title="Yomic logo" width="128"/>

# Yomic
### The Ultimate Desktop Manga Reader

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](./LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows-blue.svg)](https://github.com/ArisaAkiyama/yomic)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/)
[![Extensions](https://img.shields.io/badge/Extensions-Available-green.svg)](https://github.com/ArisaAkiyama/extension-yomic)

**Yomic** brings the best manga reading experience to your Windows desktop. Discover, read, and organize your favorite manga, webtoons, and comics from multiple sources in one beautiful, ad-free application.

[**Download Latest Release**](https://github.com/ArisaAkiyama/yomic/releases)

</div>

---

## Features

### Comprehensive Library
- **Smart Organization**: Automatically categorizes your manga based on reading status (blue dot for unread, "New" badge for updates).
- **Search & Filter**: Find any manga in your library instantly with powerful search and sort options.
- **Reading History**: Pick up exactly where you left off with synchronized history.

### Unlimited Sources (Extensions)
- **Plugin Architecture**: Support for varied sources via external extensions.
- **Supported Sources**:
  - **MangaDex** (Global)
  - **Mangabats** (English)
  - **Weebcentral** (English)
  - **Kiryuu** (Indonesia)
  - **Komiku** (Indonesia)
  - **KomikCast** (Indonesia)
- **Extensible**: Developers can easily create new extensions in C#.

### Immersive Reading
- **Webtoon Mode**: Smooth, continuous scrolling for vertical webtoons.
- **Paged Mode**: Traditional left-to-right or right-to-left reading.
- **Zoom & Fit**: Auto-fit to width/height or custom zoom levels.
- **Keyboard Navigation**: Use arrow keys or PageUp/PageDown for seamless reading.

### Performance & Utility
- **Offline Mode**: Download chapters to read without an internet connection.
- **Image Caching**: Smart caching system for fast loading and offline fallback.
- **VPN Bypass**: Integrated secure headers and proxy support to bypass regional blocks (e.g., for MangaDex).
- **System Tray**: Minimize to tray for quick access.
- **Auto-Update**: Built-in updater keeps your app and extensions fresh.

---

## Installation

### Prerequisites
- **Windows 10** or higher (64-bit).
- **.NET Desktop Runtime 8.0** (The installer usually handles this).

### Detailed Steps
1. Go to the [**Releases Page**](https://github.com/ArisaAkiyama/yomic/releases).
2. Download the latest `Setup.exe`.
3. Run the installer and follow the on-screen instructions.
4. Launch **Yomic** from your desktop or start menu.

---

## Extensions Setup

Yomic uses a modular extension system. You need to install extensions to access manga sources.

1. Download the **[Extensions.zip](https://github.com/ArisaAkiyama/extension-yomic/raw/main/Extensions.zip)**.
2. Open Yomic and go to the **Extensions** tab (or Settings > Extensions).
3. Click **"Open Extensions Folder"**.
4. Extract the contents of `Extensions.zip` into this folder.
   - Structure should look like: `.../Plugins/Yomic.Extensions.Mangabats.dll`
5. **Restart Yomic**.
6. Go to **Browse Sources** to see your new sources!

> **Note**: You can check for extension updates directly within the app or visit the [Extension Repository](https://github.com/ArisaAkiyama/extension-yomic).

---

## Building from Source

Requirements:
- **.NET 8.0 SDK**
- **Visual Studio 2022** or **VS Code**

```bash
# Clone the repository
git clone https://github.com/ArisaAkiyama/yomic.git
cd yomic

# Restore dependencies
dotnet restore

# Build Main App
dotnet build Yomic/Yomic.csproj

# Build Extensions (Optional)
dotnet build Yomic/Extensions/Mangabats/Yomic.Extensions.Mangabats.csproj
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

The developer(s) of this application does not have any affiliation with the content providers available. This application is a strictly a tool for browsing and viewing media hosted on third-party websites. All content is retrieved via extensions from third-party sources.

---

## License

Distributed under the MIT License. See `LICENSE` for more information.
