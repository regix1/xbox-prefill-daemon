# XboxPrefill

[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue?style=for-the-badge)](LICENSE)
[![Platform: Xbox](https://img.shields.io/badge/Xbox%20%2F%20Game%20Pass-107C10?style=for-the-badge&logo=xbox&logoColor=white)](https://www.xbox.com/)
[![Discord](https://img.shields.io/badge/Discord-Join-5865F2?style=for-the-badge&logo=discord&logoColor=white)](https://discord.com/invite/BKnBS4u)
[![LANCache Manager](https://img.shields.io/badge/LANCache-Manager-9af?style=for-the-badge)](https://github.com/regix1/lancache-manager)

Xbox / Microsoft Store / PC Game Pass prefill daemon for
[LANCache](https://lancache.net/) — a companion to
[**LANCache Manager**](https://github.com/regix1/lancache-manager), which
coordinates the prefill providers.

It downloads your owned and Game Pass game packages through your lancache *before*
you install them, so the real install — and every other machine on your LAN —
pulls from the cache at full LAN speed instead of the internet. Nothing is written
to disk: bytes stream through the cache and are discarded.

## Why use it

- **Cache warm before you install** — pre-download titles overnight, install instantly later.
- **LAN speed for every machine after the first** — the second install of the same version is served from cache.
- **No disk writes, no free space needed** — bytes stream through and are discarded, sparing your SSD.
- **Owned games and Game Pass** — prefill anything in your library or an active Game Pass sub.
- **Simple sign-in** — Microsoft device-code login; just enter a short code at microsoft.com/link. No app registration.
- **Headless daemon** — driven by LANCache Manager or any socket client.

## Quick start

**Recommended — run it through [LANCache Manager](https://github.com/regix1/lancache-manager).**
LANCache Manager installs, configures, and drives this daemon alongside the other
prefill providers, so you never touch the socket protocol by hand. This is the
supported path for almost everyone.

**Standalone (.NET 8 SDK)** — build and run from source:

```bash
dotnet build XboxPrefill/XboxPrefill.csproj -c Release
dotnet run  --project XboxPrefill/XboxPrefill.csproj -c Release
```

Sign in once before prefilling — every command runs after login.

> A prebuilt container image is published at `ghcr.io/regix1/xbox-prefill-daemon`
> for advanced/manual setups. It is a socket-driven daemon (volumes `/commands`,
> `/responses`, `/app/Config`, `/app/.cache`), so see
> [LANCache Manager](https://github.com/regix1/lancache-manager) and the repo docs
> for the full container configuration rather than running it ad hoc.

## How it works

1. **Authenticate** — Microsoft device-code sign-in; enter the code shown at microsoft.com/link.
2. **Select titles** — pick from your Xbox / Store library, or pass specific Product IDs (e.g. `9NBLGGH2JHXJ` for Minecraft for Windows).
3. **Resolve** — each title's package files and CDN URLs are looked up from Microsoft's content services.
4. **Prefill** — each package is fetched through the lancache and discarded, warming the cache.

## Requirements

- A running [LANCache](https://lancache.net/) with the **`xboxlive`** and
  **`windowsupdates`** cache-domain groups enabled
  (from [uklans/cache-domains](https://github.com/uklans/cache-domains)).
- A Microsoft account that owns the games, or an active Game Pass subscription.
- Docker, or the [.NET 8 SDK](https://dotnet.microsoft.com/) to build from source.

## Support

Questions or issues? [Open an issue](https://github.com/regix1/xbox-prefill-daemon/issues),
or find the LANCache community on the
[LanCache.NET Discord](https://discord.com/invite/BKnBS4u).

If XboxPrefill has been useful, you can
[buy me a coffee](https://www.buymeacoffee.com/regix). Thanks!

## License

Licensed under the **[GNU Affero General Public License v3.0](LICENSE)**. XboxPrefill
builds on the MIT-licensed lancache-prefill tools by Tim Pilius
([@tpill90](https://github.com/tpill90)); those upstream portions keep their MIT
copyright and are redistributed here under AGPL-3.0 as the MIT License permits.

> ⚠️ Unofficial tool. It uses Microsoft's public client IDs and undocumented
> endpoints and is not affiliated with or endorsed by Microsoft. Use your own
> account at your own risk.
