# SVS_Screenshot

A screenshot plugin for **SamabakeScramble** (ILLGames), similar to the screenshot manager for Koikatsu. It renders screenshots at a higher resolution than your game window, with an optional transparent background, and doesn't resize the window.

> **AI disclosure:** This plugin was written with AI assistance (Claude Opus 5.5, via Claude Code). I directed the work and tested it in-game, but the code was AI-generated.

This is a standalone plugin for SamabakeScramble only. You don't need any of the other versions installed. If you also play other ILLGames titles, they have their own separate versions: [HC_Screenshot](https://github.com/amiawrappsy/HC_Screenshot) (HoneyCome and DigitalCraft) and [AC_Screenshot](https://github.com/amiawrappsy/AC_Screenshot) (Aicomi).

## Features
<img width="1283" height="766" alt="image" src="https://github.com/user-attachments/assets/98140302-7521-4907-90ba-aa3fc136c6ca" />

- **High-resolution renders** at any size up to your GPU's limit (usually 16384 px), regardless of window size.
- **Supersampling** (1–4×) for smooth, anti-aliased edges.
- **Transparent background:** the scene is rendered over black and over white, and the transparency of each pixel is worked out from the difference. This works even though the game's shaders don't write a usable transparency channel.
- **Keeps post-processing** (bloom, color grading and so on) in transparent shots. You can turn this off.
- **Hides UI** in renders (buttons, sliders and text), while keeping backgrounds such as dialogue scene backdrops.
- **Dialogue (ADV) scenes:** transparent renders show just the characters in the conversation. Normal F11 screenshots capture the scene exactly as shown, painted background included, with the text box and other UI hidden.
- **Screen capture** of the window as it looks, UI included, optionally upscaled. Notification messages (from this or other plugins) are left out.
- **Blocks the game's built-in F11 screenshot**, so you don't get duplicate or black images. You can turn this off. Card photos in the maker are unaffected.

## Requirements

- SamabakeScramble with **BepInEx 6 (IL2CPP)**, for example from HF Patch
- **Configuration Manager** (included in HF Patch), which provides the hotkey support

## Installation

1. Download the latest `SVS_Screenshot_vX.X.X.zip` from the [Releases](../../releases) page.
2. Extract it into your SamabakeScramble game folder, the one containing `SamabakeScramble.exe`. The DLL will end up in:

```
SamabakeScramble\BepInEx\plugins\SVS_Screenshot\SVS_Screenshot.dll
```

## Usage

| Hotkey | Action |
|---|---|
| **F9** | Capture the screen as it looks, UI included |
| **F11** | Render a high-resolution screenshot, no UI |
| **Shift+F11** | Turn the transparent background on or off |

Screenshots are saved to `SamabakeScramble\UserData\cap` by default.

**Where F11 works:** the maker, dialogue scenes and H scenes. In free roam (the map and your room), the game draws its backgrounds in a way a re-render can't reproduce, and the characters there are low-detail anyway, so F11 shows a message instead. Use F9 there.

**F11 in dialogue scenes:** a normal (non-transparent) F11 is a screen capture with the UI hidden, not a re-render, because the painted background can only be captured from the screen. It's saved at up to 4× the window size, as close to **Resolution X** as possible. Supersampling doesn't apply to it.

All settings, including hotkeys, resolution, supersampling, transparency, output folder and format (PNG or JPG), can be changed in-game with the **Configuration Manager (F1)** under "Screenshot Manager".

### Tips

- The transparent background only removes empty space. Anything the camera actually sees, such as a map, floor or 3D backdrop, still appears in the image, so hide it first.
- Transparency is turned off automatically in H scenes, where the map is always in the way. To allow it there, turn off **Disable transparency in H scenes**.
- You may hear the game's camera shutter sound when pressing a hotkey, for example Shift+F11. That's harmless: the game plays the sound before trying its own screenshot, which the plugin blocks, so no extra file is saved.
- If semi-transparent edges like hair look wrong in transparent shots, turn off **Post-processing in transparent shots**.
- Large renders take a few seconds. At the default settings (3840×2160, 2× supersampling) it's about 4–6 seconds. Higher supersampling costs a lot more: 3× at 3840×2160 can take around 30 seconds and uses a lot of memory.
- If a scene renders wrong, turn on **Log camera details** (under Advanced), take the screenshot again, and include the end of `BepInExogoutput.log` when you report it.

## Building

Requires the .NET 6 SDK.

1. The project references the game's own assemblies. Edit `GameDir` in `SVS_Screenshot.csproj` to point to your SamabakeScramble folder. The game must have been launched with BepInEx at least once so the `BepInEx\SamabakeScramble\interop` folder exists.
2. Run:

   ```
   dotnet build -c Release
   ```

The build copies the DLL into the game's `BepInEx\plugins\SVS_Screenshot` folder automatically. Close the game first.

## Compatibility

Tested in SamabakeScramble (maker, dialogue and H scenes).

## Changelog

### v1.0.1
- Fixed rendered screenshots (F11) in dialogue (ADV) scenes. Transparent renders now show the conversation's characters instead of a black image, and normal ones capture the painted background with the UI hidden.
- Free-roam characters standing behind a dialogue scene are left out of dialogue renders.
- F11 in free roam now shows a message suggesting F9, instead of producing an empty sky.
- Fixed the "Screen captured" notification, and other plugins' notifications, showing up in F9 screenshots.
- "Hide camera-space UI" now hides only real UI (buttons, sliders and text), so backgrounds stay in the render.
- New setting, **Disable transparency in H scenes** (on by default).
- New setting, **Log camera details** (off by default), for reporting scenes that render wrong.

### v1.0.0
- First release.
