# Lookout for Windows

**AI screen assistant for Windows.** Lookout sees your screen, knows what apps
are open, and helps you navigate your computer — like a knowledgeable friend
looking over your shoulder. It's a Windows rebuild of the macOS
[Lookout](https://github.com/AnthonyDavidAdams/Lookout), powered by the Claude
API.

It lives in your system tray. Press **Ctrl+Alt+L** (or click the tray icon) to
summon a small floating chat window. Ask a question and Lookout takes a
screenshot, sees what you see, and gives specific, grounded guidance — and can
take actions like opening apps, finding files, and pointing a highlight right at
the button you need to click.

---

## What it is (and isn't)

Lookout is a **native Windows desktop application**. It runs entirely on your PC.
There is **no server, no backend, and nothing to host** — it talks directly from
your machine to the Anthropic API over HTTPS. (See *Distribution* below if you
were thinking about Vercel/Railway — those host web apps; this isn't one.)

| | Feature |
|---|---|
| Screen vision | Captures all displays via GDI; auto-attaches on the first message and after inactivity |
| Context aware | Knows running apps and visible window titles |
| Takes action | `capture_screen`, `highlight_element`, `list_applications`, `search_files`, `open_item` |
| On-screen highlight | A pulsing, click-through overlay points at UI elements (located with Windows OCR) |
| Streaming | Replies appear token by token |
| Personalizable | `%USERPROFILE%\.lookout\context.md` is folded into every conversation |
| Memory | Lookout saves short notes about you across sessions (`%USERPROFILE%\.lookout\notes.md`) |

---

## Installing

Grab the latest **[Release](../../releases/latest)** and use either:

- **`Lookout-Setup-x.y.z-x64.exe`** — the installer (adds a Start Menu entry,
  optional "start at sign-in"). Recommended.
- **`Lookout-vx.y.z-x64.zip`** — a portable build; unzip anywhere and run
  `Lookout.exe`. No install.

Then click the tray icon (or press **Ctrl+Alt+L**) and paste your
[Anthropic API key](https://console.anthropic.com/) into the box at the top.

### ⚠️ Windows will warn you first — this is expected

These downloads are **not code-signed**, so Windows treats them as "unknown."
You'll see one or both of these, and how to get past them:

- **"Windows protected your PC" (SmartScreen):** click **More info** →
  **Run anyway**.
- **Smart App Control blocks it entirely:** on some Windows 11 PCs this can't be
  clicked through. Either turn Smart App Control off (Settings → Privacy &
  security → Windows Security → App & browser control → Smart App Control), or
  use a PC that doesn't have it enabled.

These warnings appear because the app isn't signed with a reputable certificate —
not because anything is wrong with it. Removing them requires code signing or
publishing through the Microsoft Store (see *Distribution* below).

---

## Requirements

- Windows 10 (1809+) or Windows 11, x64 or ARM64
- An [Anthropic API key](https://console.anthropic.com/)
- To **build from source**: the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- To **run a published build**: nothing — it's self-contained

---

## Build from source

```powershell
# Debug build
dotnet build Lookout.sln -p:Platform=x64

# Run (see the note on Smart App Control below)
.\src\Lookout\bin\x64\Debug\net8.0-windows10.0.19041.0\Lookout.exe
```

## Produce a portable build

```powershell
.\publish.ps1            # x64 (default)
.\publish.ps1 -Platform arm64
```

This writes a fully self-contained, no-install build to `dist\Lookout-<arch>\`.
Copy that folder anywhere and run `Lookout.exe` — no .NET or Windows App SDK
runtime needed on the target machine.

> **Smart App Control / WDAC:** if the target PC has Smart App Control enabled,
> it will block the unsigned `Lookout.dll` from loading. For real distribution
> the build needs to be code-signed with a reputable certificate. For personal
> use, run it on a machine where Smart App Control is off, or sign the binaries.

---

## Your API key — where it goes

You enter your Anthropic API key **in the app**, not anywhere else:

- the **API key bar** at the top of the chat window (shown when no key is set), or
- **tray icon → Settings → Anthropic API key**.

It is stored in **Windows Credential Manager** (per-user, encrypted by Windows)
under the target name `Lookout:AnthropicApiKey` — never in a plain file. From
there it's used only as the `x-api-key` header on HTTPS requests straight to
`api.anthropic.com`. It is never written to disk in the clear and never sent
anywhere else.

**Never paste your API key into a chat, an issue, or a commit.** If a key is
ever exposed, rotate it in the Anthropic Console.

---

## Personalization & memory

Both live in `%USERPROFILE%\.lookout\`:

- **`context.md`** — write anything you'd want Lookout to know about you (your
  name, what you use the PC for, your comfort level with tech). It's added to
  every conversation. Open it from Settings.
- **`notes.md`** — Lookout writes short notes here itself via its `save_note`
  tool, and recalls them with `read_notes`. Clear them anytime from Settings.

---

## Distribution (the "deployment" question)

Lookout is a desktop app, so it can't be "deployed" to a web host like **Vercel
or Railway** — those run web servers and serverless functions, and Lookout has
no server-side component at all. Every Lookout install talks to Anthropic
directly using that machine's own API key.

"Shipping" Lookout instead means getting the executable to users:

1. **Portable ZIP** — run `publish.ps1`, zip `dist\Lookout-<arch>\`, share it.
   Simplest; recipients just unzip and run `Lookout.exe`.
2. **Code-signed installer** — sign the binaries with an Authenticode
   certificate, then wrap them in an installer (MSIX, or Inno Setup / WiX).
   Required to avoid SmartScreen and Smart App Control friction on other
   people's machines.
3. **GitHub Releases** — attach the signed ZIP/installer to a release tag.

If you ever wanted a *web* version of this idea, that would be a separate,
ground-up project (browser-based screen capture has very different constraints)
— not a deployment of this codebase.

---

## Project layout

```
Lookout.sln
publish.ps1
src/Lookout/
  Program.cs                 entry point + single-instance redirect
  App.xaml(.cs)              host: tray, hotkey, window lifecycle
  app.manifest               per-monitor-v2 DPI awareness
  Platform/                  TrayIconManager, GlobalHotkey, RelayCommand, ObservableObject
  Models/                    Message
  ViewModels/                ConversationViewModel, MessageViewModel
  Services/
    ClaudeApiService         streaming + agentic tool loop
    ScreenCaptureService     GDI multi-display capture
    SystemContextService     running apps / window titles
    ActionService            tool implementations (IToolExecutor)
    OcrService               Windows.Media.Ocr text location
    OverlayService           Win32 layered click-through highlight window
    OverlayRenderer          highlight frame rendering
    SecureStore              API key in Windows Credential Manager
    CustomContextService     ~/.lookout/context.md
    MemoryService            ~/.lookout/notes.md
    LookoutPaths             ~/.lookout path helpers
  Views/                     ChatView, MessageView
  Windows/                   ChatWindow, SettingsWindow
```

## Tech

C# / .NET 8 · WinUI 3 (Windows App SDK 1.8, unpackaged) · H.NotifyIcon ·
System.Drawing · Windows.Media.Ocr · Win32 interop for the hotkey and overlay.
