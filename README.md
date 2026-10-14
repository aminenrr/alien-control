# Alien Control GUI

Goal: a modern, lightweight G-Helper-style GUI over T-Troll's AlienFX Tools (github.com/t-troll/alienfx-tools) for Amine's Alienware m15 R7 (i7-12700H, RTX 3070 Ti Laptop GPU, 32 GB RAM). It replaces Alienware Command Center, whose background services keep WMI Provider Host busy.

- GUI only; never touch the AlienFX core. Buttons call alienfan-cli:
  - setpower / getpower: performance modes; setgmode=1/0: G-Mode
  - setgpu=0..4: GPU power limit (0 none, 4 max limit). Whether the m15 R7 BIOS exposes it is unverified; run alienfan-cli as admin to check.
  - setperf=<ac>,<dc>: CPU boost 0..4 (disabled, enabled, aggressive, efficient, efficient aggressive), via the Windows power plan
  - rpm, temp, setfans for readouts and fan boost
- Stack: C# .NET 8 + WPF + WPF-UI (Fluent look, tray icon). Poll sensors only while the window is open; idle in the tray.
- AWCC services must not run alongside it (disable them in services.msc first).
- Mock: mock/mock.png and mock/index.html ("Alien Control", dark theme, cyan-to-purple accent).
- Status (2026-10-14): mock only, no real code yet.
