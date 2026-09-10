# SotN Recomp Mods

A collection of my mods for Castlevania: Symphony of the Night (SotN) static recompilation.

## Mods Included

### 1. Input Overlay
* **Description:** Displays real-time controller inputs on screen using ImGui.
* **Features:**
  * Auto-resizes controller UI graphics using Lanczos3 scaling.
  * Customizable overlay width.
  * Configurable layout and positions via JSON settings (`mods/InputOverlay/imgs/joystick/positions.json`).

### 2. Split Tracker
* **File:** `SplitTracker.cs`
* **Description:** An in-game speedrun timer and boss split tracking overlay.
* **Features:**
  * Automatic split tracking for boss defeats.
  * Delta comparison against Personal Best (PB) runs.
  * Auto-saves best run times to local disk (`mods/SplitTracker/times`).
  * Resets upon returning to title/main menus.

## Installation

1. Copy the mod source files into your game's `mods/` directory.
2. Ensure the required assets folder structure exists:
   * `mods/InputOverlay/imgs/`
   * `mods/SplitTracker/times/`
3. Configure overlay positions and options directly in the in-game mod settings.

Here is little showcase video:
[![Watch the video](https://img.youtube.com/vi/koOvq3wdRD8/maxresdefault.jpg)](https://youtu.be/koOvq3wdRD8)
