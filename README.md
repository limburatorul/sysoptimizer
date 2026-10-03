# Sysoptimizer

A free Windows 10/11 tune-up tool that shows what it is about to change, and can change it back.

- **Resources** — live CPU, memory, GPU, per-disk and network graphs, with a selectable window.
- **Tweaks** — telemetry, Start menu web search and ads, Game DVR, animations and transparency,
  hibernation, activity history, location, background apps, notification toasts, power
  throttling, and more. Each one shows whether it is applied now, and each one can be reverted.
  Create a restore point first from the same tab; export and import a selection.
- **Cleanup** — analyze first, then clean the folders you tick: user and Windows temp, Prefetch,
  the Windows Update download cache, Delivery Optimization, error reports, the thumbnail cache.
- **Services** — telemetry, SysMain, Windows Search, Fax, Remote Registry and a few others, each
  with a line on what it does. Stop and disable, or re-enable.
- **Startup** — enable or disable the programs that start with Windows.
- **Debloat** — remove pre-installed Store apps (Xbox overlays, Solitaire, Clipchamp, Teams…).
  They can be reinstalled from the Microsoft Store.
- **Apps** — install common apps and update everything through `winget`.
- **DNS** — switch between automatic, Cloudflare, Google and Quad9.
- **Memory** — trim every process's working set on demand.

Runs as administrator, because most of the above is machine-wide. No account, no telemetry of its
own, nothing it talks to online except `winget` when you ask it to.

## Building

Needs the .NET 8 SDK.

```powershell
dotnet publish Sysoptimizer -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

## Licence

MIT. Made by [Protagonist Labs](https://protagonistlabs.app/sysoptimizer/).

## More from Protagonist Labs

- [SpaceScan](https://protagonistlabs.app/spacescan/?utm_source=github&utm_medium=readme&utm_campaign=sysoptimizer): shows what takes the space on a drive, free.
- [Backup Labs](https://protagonistlabs.app/backuplabs/?utm_source=github&utm_medium=readme&utm_campaign=sysoptimizer): scheduled, versioned folder backups, free.
- [All apps](https://protagonistlabs.app/?utm_source=github&utm_medium=readme&utm_campaign=sysoptimizer): Windows apps that each do one job properly.
