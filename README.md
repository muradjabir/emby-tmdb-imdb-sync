# Emby TMDb & IMDb Sync Plugin

A lightweight, native plugin for **Emby Server** (v4.8+) that automatically synchronizes movie watched history and favorites between Emby, **The Movie Database (TMDb)**, and **IMDb**.

---

## Features

- **Real-Time Event Sync**: Hooks directly into Emby's `UserDataSaved` events with an intelligent debounce queue. Marking a movie as watched or favorited on any client (TV, web, mobile) syncs automatically within seconds.
- **Dual TMDb Sync**:
  - Syncs movie favorites directly to your native TMDb profile (`themoviedb.org/u/{username}/favorites`).
  - Auto-provisions and syncs to dedicated TMDb lists (*"Emby Favorites"* and *"Emby Watched Movies"*).
  - Supports direct account credential login or optional v4 API Bearer tokens.
- **IMDb Synchronization**:
  - Syncs watched movies directly to your IMDb account watched status.
  - Auto-creates and populates a dedicated private IMDb Favorites list.
  - Uses polite pacing (~750ms+ delay) to respect server limits.
- **Automated Reconciliation**:
  - Includes a scheduled daily background task (default 03:00 AM) to reconcile any missing entries between Emby and remote platforms.
  - Manual *"Sync Now"* button in the configuration dashboard.
- **Multi-User Isolation**:
  - Supports multiple independent Emby users, each linked to their own TMDb and IMDb accounts.
  - Isolated HTTP request headers to prevent session data or cookies from crossing users.

---

## Installation

1. Download the latest `Emby.TmdbImdbSync.dll` from the Releases page.
2. Place the `.dll` into your Emby Server plugins folder:
   - **Docker / Linux**: `/config/plugins/`
   - **Windows**: `%ProgramData%\EmbyServer\plugins\`
3. Restart Emby Server.
4. Navigate to **Emby Dashboard -> Plugins -> TMDb & IMDb Sync** to configure your accounts.

---

## Configuration

### TMDb Configuration
- Enter your TMDb Username and Password in the Credential Login section, then click **Log In to TMDb**.
- The plugin will authenticate and automatically provision both your **Favorites List ID** and **Watched List ID**.
- Alternatively, you can supply your own TMDb API key or v4 Read Access Token from [themoviedb.org/settings/api](https://www.themoviedb.org/settings/api).

### IMDb Configuration
IMDb authentication uses standard web session cookies:
1. Log in to [imdb.com](https://www.imdb.com) in your web browser.
2. Open Developer Tools (F12) -> Application/Storage -> **Cookies**.
3. Copy the values for:
   - `at-main`
   - `ubid-main`
4. Paste each value into its respective field in the plugin settings.
5. Click **Create IMDb Favorites List** to generate your private favorites list.
6. Click **Test IMDb Cookie** to verify connectivity, then click **Save Settings**.

---

## Building from Source

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download) (or Docker)

### Build Command
```bash
./scripts/build.sh
```
The compiled single-assembly plugin will be output to `dist/Emby.TmdbImdbSync.dll`.

---

## Security & Privacy Notice

- **Cookie Safety**: IMDb `at-main` and `ubid-main` cookies act as active session credentials. Never commit your configuration files, share your cookies, or expose them in public backups.
- **Pacing**: IMDb GraphQL requests are paced politely to avoid triggering anti-abuse protections.

---

## Disclaimers & Attribution

- **TMDb Attribution**: This product uses the TMDB API but is not endorsed or certified by TMDB.
- **Disclaimer**: This is an independent open-source community project. It is not affiliated with, endorsed by, or associated with Emby LLC, The Movie Database (TMDb), or IMDb.com / Amazon.com, Inc.

---

## License

This project is open-source software licensed under the [MIT License](LICENSE).
