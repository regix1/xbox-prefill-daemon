# XboxPrefill

[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue?style=for-the-badge)](LICENSE)
[![Discord](https://img.shields.io/badge/Discord-Join-5865F2?style=for-the-badge&logo=discord&logoColor=white)](https://discord.com/invite/BKnBS4u)

The **Xbox / Microsoft Store / PC Game Pass** prefill daemon for [LANCache](https://lancache.net/) — a companion to [**LANCache Manager**](https://github.com/regix1/lancache-manager), which already coordinates other prefill providers. XboxPrefill adds Xbox to that lineup.

It pre-downloads your owned (and Game Pass) game packages **through your lancache** so the cache is warm *before* you install — the real install then comes from your LAN at full speed instead of the internet. No game data is written to disk; bytes are streamed through the cache and discarded.

## How it works

Login-first, mirroring the real Microsoft Store flow:

1. **Authenticate** — Microsoft device-code sign-in → XBL user token → a proof-of-possession device token (ECDSA P-256 request signing) → XSTS. No app registration needed; you enter a short code at `https://www.microsoft.com/link`.
2. **Enumerate** — your Xbox / Store titles are read from the titlehub library (filtered to installable titles), or you can prefill specific **Product IDs** (e.g. `9NBLGGH2JHXJ` for Minecraft for Windows).
3. **Resolve** — each Product ID → ContentId (DisplayCatalog) → package files + CDN URLs (`GetBasePackage`).
4. **Prefill** — each package file is fetched through the lancache and discarded, warming the cache.

The package bytes are static and shared across users for a given version, so they cache cleanly.

## Requirements

- A running [LANCache](https://lancache.net/) with the **`xboxlive`** and **`windowsupdates`** cache-domain groups enabled (from [uklans/cache-domains](https://github.com/uklans/cache-domains)).
- A Microsoft account that owns the games, or an active Game Pass subscription.
- Docker, or the [.NET 8 SDK](https://dotnet.microsoft.com/) to build from source.

## Running it

XboxPrefill runs as a **daemon** with a socket command interface (login, select titles, prefill, status); **login is required before any other command**. It is built to be driven by [**LANCache Manager**](https://github.com/regix1/lancache-manager) — set it up there alongside the other prefill providers.

To build and run standalone:

```bash
dotnet build XboxPrefill/XboxPrefill.csproj -c Release
dotnet run  --project XboxPrefill/XboxPrefill.csproj -c Release
```

## Support & License

Questions or issues? [Open an issue](https://github.com/regix1/xbox-prefill-daemon/issues), or find the LANCache community on the [LanCache.NET Discord](https://discord.com/invite/BKnBS4u). If XboxPrefill has been useful and you'd like to support development, you can [buy me a coffee](https://www.buymeacoffee.com/regix).

XboxPrefill is licensed under the **[GNU Affero General Public License v3.0](LICENSE)**. It builds on the MIT-licensed lancache-prefill tools by Tim Pilius ([@tpill90](https://github.com/tpill90)) and the shared [`LancachePrefill.Common`](https://github.com/tpill90/lancache-prefill-common); those upstream portions retain their original MIT copyright and are redistributed here under AGPL-3.0 as the MIT License permits. Xbox package resolution was informed by [`LukeFZ/MsixvcPackageDownloader`](https://github.com/LukeFZ/MsixvcPackageDownloader) and the [`OpenXbox`](https://github.com/OpenXbox) ecosystem.

> ⚠️ Unofficial tool — it uses Microsoft's public client IDs and undocumented endpoints, and is not affiliated with or endorsed by Microsoft. Use your own account at your own risk.
