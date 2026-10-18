<div align="center">

<img src="docs/images/logo.png" width="120" alt="Rock Preview logo">

# Rock Preview

**Find any file instantly. Browse with the real Windows 11 Explorer. Preview anything without opening it.**

One fast Windows app that combines an instant whole-disk search engine, File Explorer itself, and a preview pane
that understands hundreds of formats — from PSD, CorelDRAW and TIFF to PDF, Office documents, RAW photos and video.

Press **`Alt+X`** in any app to open it.

[![Latest release](https://img.shields.io/github/v/release/RockySharaf/rock-preview?label=download&color=0067c0)](https://github.com/RockySharaf/rock-preview/releases/latest)
[![Build](https://github.com/RockySharaf/rock-preview/actions/workflows/build.yml/badge.svg)](https://github.com/RockySharaf/rock-preview/actions/workflows/build.yml)
[![License: GPL v3](https://img.shields.io/badge/license-GPL--3.0-blue)](LICENSE)
![Windows 10 & 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078d4?logo=windows)
![.NET 10](https://img.shields.io/badge/.NET-10-512bd4)

**[⬇ Download Rock Preview](https://github.com/RockySharaf/rock-preview/releases/latest)** · [Features](#features) · [Search syntax](#search-syntax) · [Build from source](#build-from-source)

<img src="docs/images/explorer.png" alt="Rock Preview: the Windows 11 Explorer view with large thumbnails and the preview pane" width="900">

</div>

---

## Why Rock Preview?

File Explorer's search is slow, and opening a file just to see what's in it wastes time — especially for design files
like PSD, AI or CorelDRAW that take ages to launch. Rock Preview fixes both:

- **⚡ Instant search** — reads the NTFS Master File Table directly, so millions of files are indexed in seconds and
  every keystroke filters the whole disk immediately. The index stays live as files change.
- **🗂️ The real Explorer, built in** — not a look-alike: Rock Preview hosts Windows' own Explorer file view and
  navigation pane, so context menus, drag and drop, rename, thumbnails and shell extensions all just work.
- **👁️ Preview anything** — select a file and see it, without opening another app.
- **🖼️ Thumbnails of everything** — one click shows your search results, or *every* file in a folder and its
  subfolders at once, as thumbnails.

## Features

### Instant search
- Indexes NTFS volumes from the Master File Table and keeps up with changes through the USN change journal.
  Without administrator rights it falls back to fast folder scanning and file-system notifications.
- Results update as you type — whole disk, or just the open folder.
- Powerful query language: wildcards, regular expressions, AND / OR / NOT, and functions for size, dates, type,
  extension, attributes, parent folder and more ([syntax below](#search-syntax)).
- Sort by any column, highlight matches, bookmarks for saved searches, export results to CSV / TXT.
- `rockpreview-cli` for scripting searches from the command line.

### Windows 11 File Explorer, inside
- Hosts the genuine Explorer file view (`ExplorerBrowser`) and navigation pane (`NamespaceTreeControl`) —
  Home, Gallery, your pinned folders, This PC, Network.
- A pixel-faithful Windows 11 frame: breadcrumb address bar, command bar with Explorer's own icons
  (New, Cut, Copy, Paste, Rename, Share, Delete, Sort, View), search box, status bar and view switcher.
  Sizes, colours and behaviour were measured from Explorer itself, not guessed.
- The real shell **New** menu, the Windows **Share** sheet, Properties, Select all / none / invert.
- Light and dark themes.

### Preview pane — hundreds of formats
| Kind | Examples |
|---|---|
| Images & RAW | JPG, PNG, GIF, WebP, AVIF, HEIC, JXL, TIFF, BMP, TGA, DDS, ICO, SVG, CR2/CR3, NEF, ARW, DNG, RAF… |
| Design | **PSD**, **CorelDRAW (CDR)**, **Illustrator (AI)**, **InDesign**, Affinity, Krita, XCF, Figma, XD, EPS |
| Documents | **PDF** (multi-page), Word, Excel, PowerPoint, Visio, OpenDocument, RTF, XPS, EPUB, email (EML/MSG) |
| CAD & 3D | DWG, DXF, STL, OBJ, 3MF, FBX, Blender, SketchUp |
| Media | MP4, MKV, MOV, AVI, WebM, MP3, FLAC, WAV, OGG… |
| Code & text | 100+ languages and config formats, CSV/TSV tables, JSON, XML, Markdown, logs |
| Other | Archives (ZIP, 7z, RAR…), fonts, executables, certificates, and a hex view for anything else |

Office and other installed shell preview handlers are hosted in place; embedded thumbnails are used where a format has one.

### Thumbnails
Press **Thumbnails** (or `Ctrl+Shift+X`) to see files as pictures:

- **While searching**, your results switch to thumbnails, wherever on the disk they are.
- **In a folder**, every file in it *and all its subfolders* appears at once, straight from the index — perfect for
  "show me all the images in this project". Press it again to go back to the folder.

The arrow next to it shows the same files as a list instead (`Ctrl+Shift+Z`), or only the folder's own files.

<div align="center">
<img src="docs/images/search.png" alt="Instant search results with highlighted matches" width="900">
</div>

## Install

1. Download **`RockPreview-Setup.exe`** from the [latest release](https://github.com/RockySharaf/rock-preview/releases/latest)
   and run it. Choose *Just me* (no administrator needed) or *Everyone on this PC*, plus optional desktop shortcut
   and *Always run as administrator*. *Start with Windows* is on by default, so `Alt+X` opens Rock Preview at any time.
2. Prefer no install? Download **`RockPreview.exe`** — the portable version runs from any folder or USB stick.

Both are self-contained: nothing else (not even .NET) needs to be installed. Uninstall from **Settings › Apps**.

> **Windows SmartScreen** may say "Windows protected your PC" because the download isn't code-signed yet.
> Click **More info › Run anyway**.

> **Administrator rights** give the instant NTFS index (reading the Master File Table needs them). Without them,
> Rock Preview still works — it indexes by scanning folders, which is slower the first time.

## Keyboard shortcuts

| Keys | Action |
|---|---|
| `Alt+X` | Open or hide Rock Preview from any app (change it in **Tools › Options › Keyboard**) |
| `Ctrl+F` / `Ctrl+E` | Search |
| `Ctrl+L`, `Alt+D`, `F4` | Type a path in the address bar |
| `Alt+←` / `Alt+→` / `Alt+↑` | Back / Forward / Up |
| `Alt+P` | Show or hide the preview pane |
| `Ctrl+Shift+X` | Thumbnails: the search results, or every file in the folder and its subfolders |
| `Ctrl+Shift+Z` | Every file in the folder and its subfolders, as a list |
| `Ctrl+Shift+N` | New folder |
| `Alt+Enter` | Properties |
| `F5` | Refresh |
| `Ctrl+I` `Ctrl+B` `Ctrl+U` `Ctrl+R` | Match case / whole word / path / regex |
| `Ctrl+D` | Bookmark the current search |
| `Ctrl+N` | New window |
| `Esc` | Clear the search / back to the folder |
| `F1` | Search syntax help |

## Search syntax

| Query | Finds |
|---|---|
| `invoice 2024` | names containing both words |
| `report \| summary` | either word |
| `!tmp` | names *not* containing "tmp" |
| `*.psd` | wildcards (`*` any text, `?` one character) |
| `"annual report"` | an exact phrase |
| `ext:psd;ai;cdr` | design files |
| `size:>100mb` | files over 100 MB |
| `dm:today` · `dm:lastweek` | modified today / last week |
| `parent:"D:\Photos"` | items directly inside a folder |
| `path:projects logo` | match against the full path |
| `regex:^IMG_\d{4}\.jpg$` | a regular expression |
| `audio:` `video:` `pic:` `doc:` `zip:` `exe:` | built-in type filters |

Press `F1` in the app for the complete reference.

## Command line

```text
RockPreview.exe [search text]        open with a search
RockPreview.exe -path "D:\Projects"  open a folder
RockPreview.exe -admin               restart elevated (instant NTFS index)
RockPreview.exe -startup             start in the background
RockPreview.exe -exit                close the running instance
rockpreview-cli ext:pdf size:>10mb   search the saved index from a script (-h for options)
```

## Build from source

Requirements: Windows 10 or 11, the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
git clone https://github.com/RockySharaf/rock-preview.git
cd rock-preview
dotnet build -c Release
dotnet test
# Self-contained single-file builds: dist\RockPreview-Setup.exe and dist\RockPreview.exe
powershell -ExecutionPolicy Bypass -File build\Publish.ps1
```

### Project layout
| Project | What it is |
|---|---|
| `src/Anything.Core` | The index (compact in-memory file table), NTFS MFT reader, USN journal monitor, folder scanner, query parser and search engine |
| `src/Anything.Preview` | The preview pane and renderers for every supported format, plus the thumbnail provider |
| `src/Anything` | The Windows app: Explorer hosting, Windows 11 chrome, results list and thumbnails, setup |
| `src/Anything.Cli` | `rockpreview-cli` |
| `tests/Anything.Tests` | Unit tests for indexing and search |

## Privacy

Rock Preview runs entirely on your PC. It makes no network connections, has no telemetry and no account.
Its index and settings stay in `%LOCALAPPDATA%\Rock Preview` and `%APPDATA%\Rock Preview`.

## Roadmap

- Tabs, like Windows 11 Explorer
- A background service so the instant index doesn't need a UAC prompt at every start
- Code-signed releases and a Microsoft Store listing
- ARM64 builds

Ideas and bug reports are welcome in [Issues](https://github.com/RockySharaf/rock-preview/issues).

## License

Rock Preview is free software, released under the [GNU General Public License v3.0](LICENSE).
Copyright © 2026 Rocky Sharaf.

Rock Preview is an independent project. It is not affiliated with Microsoft or voidtools. Its search syntax is
compatible with the query language popularised by voidtools' *Everything*. Windows' own icons are loaded from your
Windows installation at run time and are not distributed with Rock Preview.
