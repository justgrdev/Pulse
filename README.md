# Pulse

A small Windows 11 tray app for two things: seeing what your network is doing, and cutting it off (and bringing it back) with one click.

I wrote it for gaming on my phone while my laptop sits on the same connection. Hit `Ctrl+Alt+N`, the laptop stops using Wi-Fi, Ethernet and Bluetooth, and the bandwidth is yours. Hit it again when you're done.

## What it does

- **Go offline / Go online**: one button or `Ctrl+Alt+N`. Turns off every connection you've included and remembers which ones were on, so going online restores exactly those.
- **Per-connection switches**: Wi-Fi, Ethernet and Bluetooth each have their own toggle and a gear menu. There you choose whether that connection takes part in Go offline. For Wi-Fi you can also choose between switching the radio (instant, the default) or disabling the whole adapter.
- **Live traffic**: download and upload speed, a 60-second graph, and session totals. Switch between MB/s and Mbps.
- **Per-app usage**: which programs are using the network right now, with icons, sorted by current speed.
- **Tray icon**: the same toggles from the tray menu. Closing the window hides it to the tray.

VPN and virtual adapters (WARP, Tailscale, Hyper-V and so on) are listed under a collapsed section and are left alone by Go offline unless you include them.

## Requirements

- Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build, or the .NET 10 Desktop Runtime to run a published build

## Build and run

```powershell
dotnet run -- --no-elevate     # quick dev run, limited mode (see below)
.\scripts\publish.ps1          # optimized build into .\dist
```

## Admin rights

Disabling Ethernet and tracking per-app traffic both need administrator rights, so Pulse asks for them on launch. If you decline the prompt it still runs, and Wi-Fi, Bluetooth and the speed graphs work. Ethernet control and the app list are disabled.

To skip the UAC prompt every time, run this once from an elevated PowerShell:

```powershell
.\scripts\install.ps1              # adds a Start menu shortcut backed by a scheduled task
.\scripts\install.ps1 -Autostart   # same, and also starts Pulse in the tray at login
```

Launch it from the "Pulse" Start menu entry afterwards. Pin that to your taskbar if you like.

## How it works

- Wi-Fi and Bluetooth are switched with the Windows radio API, which is instant and needs no admin rights.
- Ethernet (and anything else) is enabled or disabled through WMI `Win32_NetworkAdapter`.
- Per-app usage comes from the kernel network ETW provider via [TraceEvent](https://github.com/microsoft/perfview). Loopback traffic is ignored. Numbers are totals since Pulse started, and Reset clears them.
- Adapter speeds come from the standard .NET interface counters, sampled once a second.

## Notes

- Bluetooth is included in Go offline by default, which also drops a Bluetooth mouse or headset. Untick it in its gear menu if that's a problem.
- Per-app numbers include traffic that goes through a VPN adapter as well as the physical one, so with a VPN on they can read higher than the adapter total.
- Settings are saved to `%AppData%\Pulse\settings.json`.
- If Pulse crashes while tracing, Windows can leave a trace session behind. It's cleaned up on the next start.

## License

[MIT](LICENSE): use it, change it, ship it, no warranty and no liability. It disables network adapters, so know what you're clicking.

Dependencies ([CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet), [TraceEvent](https://github.com/microsoft/perfview), System.Management) are all MIT-licensed.
