# xbox-prefill-daemon

[![](https://dcbadge.vercel.app/api/server/BKnBS4u?style=for-the-badge)](https://discord.com/invite/BKnBS4u)
[![view - Documentation](https://img.shields.io/badge/view-Documentation-green?style=for-the-badge)](https://regix1.github.io/xbox-prefill-daemon/)
[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/Y8Y5DWGZN)

![GitHub all releases](https://img.shields.io/github/downloads/regix1/xbox-prefill-daemon/total?color=red&style=for-the-badge)
[![dockerhub](https://img.shields.io/docker/pulls/regix1/xbox-prefill-daemon?color=9af&style=for-the-badge)](https://hub.docker.com/r/regix1/xbox-prefill-daemon)
[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue?style=for-the-badge)](LICENSE)

Automatically fills a [Lancache](https://lancache.net/) with games from Xbox, so that subsequent downloads for the same content will be served from the Lancache, improving speeds and reducing load on your internet connection.

<img src="docs/img/svg/overview.svg" alt="Overview">

# Features

- Selecting which apps to prefill can be done through an interactive menu.
- No installation required! A completely self-contained, portable application.
- Multi-platform support (Windows, Linux, MacOS, Arm64, Docker, Unraid)
- High-performance! Downloads are significantly faster than using the Xbox app. Downloads can scale all the way up to 100gbit/s!
- Game downloads write no data to disk, so there is no need to have enough free space available. This also means no unnecessary wear-and-tear to SSDs!
- Includes a built in benchmark feature for diagnosing performance bottlenecks!

# Table of contents

- [Installation](#installation)
- [Getting Started](#getting-started)
- [Frequently Asked Questions](#frequently-asked-questions)
- [Detailed Command Usage](#detailed-command-usage)
- [Updating](#updating)
- [Need Help?](#need-help)
- [Support and License](#support-and-license)

# Installation

**XboxPrefill** is flexible and portable, and supports multiple platforms and configurations. It can be run directly on the Lancache server itself, or on your gaming machine as an alternative Xbox client. You should decide which one works better for your use case.

Detailed setup guides are available for the following platforms:

<a target="_blank" href="https://regix1.github.io/xbox-prefill-daemon/install-guides/Linux-Setup-Guide">
    <img src="/docs/img/badges/linux-setup-badge.svg" height="32px" title="Linux" alt="Linux" />
</a> &nbsp;
<a target="_blank" href="https://regix1.github.io/xbox-prefill-daemon/install-guides/Docker-Setup-Guide">
    <img src="/docs/img/badges/docker-setup-badge.svg" height="32px" title="Docker" alt="Docker" />
</a> &nbsp;
<a target="_blank" href="https://regix1.github.io/xbox-prefill-daemon/install-guides/Unraid-Setup-Guide">
    <img src="/docs/img/badges/unraid-setup-badge.svg" height="32px" title="unRAID" alt="unRAID" />
</a> &nbsp;
<a target="_blank" href="https://regix1.github.io/xbox-prefill-daemon/install-guides/Windows-Setup-Guide">
    <img src="/docs/img/badges/windows-setup-badge.svg" height="32px" title="Windows" alt="Windows" />
</a>

<br/>

# Getting Started

## Selecting what to prefill

> [!WARNING]
> This guide was written with Linux in mind. If you are running **XboxPrefill** on Windows you will need to substitute `./XboxPrefill` with `.\XboxPrefill.exe` instead.

Prior to prefilling for the first time, you will have to decide which apps should be prefilled. This will be done using an interactive menu, for selecting what to prefill from all of your currently owned apps. To display the interactive menu, run the following command

```powershell
./XboxPrefill select-apps
```

Once logged into Xbox, all of your currently owned apps will be displayed for selection. Navigating using the arrow keys, select any apps that you are interested in prefilling with **space**. Once you are satisfied with your selections, save them with **enter**.

<img src="docs/img/svg/Interactive-App-Selection.svg" alt="Interactive app selection">

These selections will be saved permanently, and can be freely updated at any time by simply rerunning `select-apps` again at any time.

## Initial prefill

Now that a prefill app list has been created, we can now move onto our initial prefill run by using

```powershell
./XboxPrefill prefill
```

The `prefill` command will automatically pickup the prefill app list, and begin downloading each app. During the initial run, it is likely that the Lancache is empty, so download speeds should be expected to be around your internet line speed (in the below example, a 300mbit/s connection was used). Once the prefill has completed, the Lancache should be fully ready to serve clients cached data.

<img src="docs/img/svg/Initial-Prefill.svg" alt="Initial Prefill">

## Updating previously prefilled apps

Updating any previously prefilled apps can be done by simply re-running the `prefill` command, which will use same prefill app list as before.

**XboxPrefill** keeps track of which version of each app was previously prefilled, and will only re-download if there is a newer version of the app available. Any apps that are currently up to date, will simply be skipped. The number of apps already up to date will be displayed in the end of run summary table:

<img src="docs/img/svg/Prefill-Up-To-Date.svg" alt="Prefilled app up to date">

However, if there is a newer version of an app that is available, then **XboxPrefill** will re-download the app. Due to how Lancache works, this subsequent run should complete much faster than the initial prefill (example below used a 10gbit connection).
Any data that was previously downloaded, will be retrieved from the Lancache, while any new data from the update will be retrieved from the internet. Any apps that have been updated will be counted towards the "Updated" column in the end of run summary.

<img src="docs/img/svg/Prefill-New-Version-Available.svg" alt="Prefill run when app has an update">

# Frequently Asked Questions

> [!NOTE]
> FAQs have been moved to the project wiki. A table of contents is provided here for convenience and visibility : [Frequently Asked Questions](https://regix1.github.io/xbox-prefill-daemon/faq/)

- [Can I run XboxPrefill on the Lancache server?](https://regix1.github.io/xbox-prefill-daemon/faq/#can-i-run-xboxprefill-on-the-lancache-server)
- [Can XboxPrefill be run on a schedule?](https://regix1.github.io/xbox-prefill-daemon/faq/#can-xboxprefill-be-run-on-a-schedule)
- [Can I fill my cache using previously installed Xbox games?](https://regix1.github.io/xbox-prefill-daemon/faq/#can-i-fill-my-cache-using-previously-installed-xbox-games)
- [Where does XboxPrefill store downloads?](https://regix1.github.io/xbox-prefill-daemon/faq/#where-does-xboxprefill-store-downloads)
- [How do I pause my running downloads?](https://regix1.github.io/xbox-prefill-daemon/faq/#how-do-i-pause-my-running-downloads)
- [Is it possible to prefill apps I don't own?](https://regix1.github.io/xbox-prefill-daemon/faq/#is-it-possible-to-prefill-apps-i-dont-own)
- [How can I limit download speeds?](https://regix1.github.io/xbox-prefill-daemon/faq/#how-can-i-limit-download-speeds)
- [My logs have weird characters that make it hard to read. Is there any way to remove them?](https://regix1.github.io/xbox-prefill-daemon/faq/#my-logs-have-weird-characters-that-make-it-hard-to-read-is-there-any-way-to-remove-them)
- [Can I use more than one Xbox account at the same time?](https://regix1.github.io/xbox-prefill-daemon/faq/#can-i-use-more-than-one-xbox-account-at-the-same-time)

# Detailed Command Usage

More in depth documentation on XboxPrefill's various commands can be found on the project wiki.

- Looking to see what other options can be used with `prefill`?  See [prefill](https://regix1.github.io/xbox-prefill-daemon/detailed-command-usage/Prefill/)

# Updating

**XboxPrefill** will automatically check for updates, and notify you when an update is available :

<img src="docs/img/svg/app-update-available.svg" alt="Update available message">

### Automatically updating

- **Windows**
  - Run the `.\update.ps1` script in the executable directory
- **Linux**
  - **First time only** : Grant executable permissions to the update script with `chmod +x ./update.sh`
  - Run the `./update.sh` script in the executable directory
- **Docker**
  - `docker pull regix1/xbox-prefill-daemon:latest`

### Manually updating:

1.  Download the latest version for your OS from the [Releases](https://github.com/regix1/xbox-prefill-daemon/releases) page.
2.  Unzip to the directory where **XboxPrefill** is currently installed, overwriting the previous executable.
3.  Thats it! You're all up to date!

# Need Help?

If you are running into any issues, feel free to open up a Github issue on this repository.

You can also find us at the [**LanCache.NET** Discord](https://discord.com/invite/BKnBS4u), in the `#xbox-prefill` channel.

# Want to Contribute?

There is additional documentation over on the project wiki that can help you get started!  Interested in modifying and compiling the project from source? See [Compiling From Source](https://regix1.github.io/xbox-prefill-daemon/dev-guides/Compiling-from-source/).  Noticed something in the documentation needs updating?  See [Working With Project Documentation](https://regix1.github.io/xbox-prefill-daemon/dev-guides/mkdocs-setup/)

# Acknowledgements

- [@dlrudie](https://github.com/dlrudie) for all your help with debugging and testing!
- Built on [`LancachePrefill.Common`](https://github.com/tpill90/lancache-prefill-common) and modeled on the lancache prefill tools by **Tim Pilius** ([@tpill90](https://github.com/tpill90)). Xbox package resolution was informed by [`LukeFZ/MsixvcPackageDownloader`](https://github.com/LukeFZ/MsixvcPackageDownloader) and the [`OpenXbox`](https://github.com/OpenXbox) ecosystem.

-----

<a id="support-and-license"></a>
## Support and License

Stuck on something? [Open an issue](https://github.com/regix1/xbox-prefill-daemon/issues) on GitHub, or find the LANCache community on the [LanCache.NET Discord](https://discord.com/invite/BKnBS4u).

If XboxPrefill has been useful to you and you'd like to support development, you can [buy me a coffee](https://ko-fi.com/Y8Y5DWGZN). Every bit helps keep the project alive.

### License

XboxPrefill is licensed under the [GNU Affero General Public License v3.0 (AGPL-3.0)](LICENSE).

In plain terms:

- **Self-host it, modify it, use it however you like** for yourself, your LAN, your business, or your gaming cafe.
- **If you run a modified version for other people** (including as a hosted service), you must make your modified source available to them under the same license, with the original copyright intact.
- This keeps the project open: anyone who builds on it has to share their changes back, so it can't be turned into a closed, proprietary reskin.

XboxPrefill builds on the MIT-licensed [lancache-prefill](https://github.com/tpill90) tools by Tim Pilius; those upstream portions retain their original MIT copyright notice and are redistributed here under AGPL-3.0 as the MIT License permits.
