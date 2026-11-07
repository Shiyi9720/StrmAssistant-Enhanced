# Strm Assistant · Enhanced build for Emby 4.10

An unofficial plugin build for **Emby Server 4.10.2**, continuing from the community Strm Assistant project and adding capabilities around Chinese-language libraries, STRM playback and multi-version management.

- Assembly version target: `4.10.2.0` (Emby 4.10.2 stable)
- Platforms: Linux x64 / arm64, macOS
- License: GPL-3.0

## What it adds

**Playback and scanning**
- Merge multiple versions in the same folder; configurable cap 2–64 (native cap is 8)
- Order versions by quality (resolution / bitrate); display name from media info or forced file name
- Separate watch progress and favourites per version, including across libraries
- STRM-friendly startup: persisted media info, thumbnails and BIF so rescans don't recompute

**Chinese experience**
- Search by full pinyin, pinyin initials, simplified/traditional variants, aliases and cast names
- Graded result ranking (exact > prefix > contains > fuzzy) with visibility filtering
- Douban supplementation for overviews, posters, cast, directors and season/episode data (optional, rate limited)
- Separate simplified/traditional posters and logos; optional "prefer traditional Chinese + convert"
- Chinese UI for settings pages and task names (simplified and traditional)

**Metadata**
- Local TMDB metadata source with `cache/` and `override/` layers, four modes, per-`tmdbid` partial overrides, `episodegroup.json` for local episode groups, `tags.json` for custom tags, unified content ratings and custom specials ordering
- Episode-group scraping, original-language posters, alternative TMDB endpoint, configurable fallback languages
- Optional metadata/image update notifications and box set change notifications

**Media info**
- ISO / BDMV extraction (UDF with ISO9660 fallback, mpls and DVD IFO chapters)
- Dolby Vision Profile 7 detection with MEL / FEL distinction in labels
- HDR Vivid detection, Atmos / DTS:X badges
- Screenshot post-processing (16:9 landscape, 2:3 portrait)
- Custom ffmpeg path/arguments (e.g. for DV P5 green frames)
- Extraction blacklists by tag or keyword (media info / fingerprints / BIF independently)

**Intro, outro and subtitles**
- Per-library fingerprint duration, parallel computation
- External intro/outro sources including a second provider
- External subtitle scan, optionally scanning external audio tracks
- STRM-optimised OpenSubtitles search and download

**Extras**
- Optional rffmpeg sidecar to offload `ffprobe` over SSH (see `docs/rffmpeg-deployment.md`)
- Distributed media info extraction with automatic local fallback and per-outcome statistics

## Install

1. Verify Emby 4.10.2 with assembly version `4.10.2.0` (otherwise the plugin is rejected with `LoaderException`).
2. Back up your existing plugin folder.
3. Copy the whole `plugins/` folder from the release package into the Emby plugins directory, keeping `StrmAssistantLite.deps/`, `zh/` and `zh-hant/` intact.
4. Restart Emby.

Settings live in `Strm Assistant.json`; upgrading from an older version keeps your configuration. Roll back by restoring the backed-up DLL and restarting.

## Known limitations

- The 8-version merge cap is hard-coded in Emby's scanner; only the task-side cap is configurable
- Native watch progress is shared per TMDB id; per-version separation is implemented by the plugin
- Emby's transcoding pipeline is not intercepted
- Dolby Vision P7 layer detection requires an external ffprobe ≥ 9
- Douban, IntroDB and OpenSubtitles are third-party services; rate limits or API changes may disable those features temporarily

## Notice

- Not affiliated with, endorsed by, or authorised by Emby LLC. For users with a legitimately installed Emby server only.
- Free of charge; personal, non-commercial use only.
- Contains no Emby proprietary components and does not modify or redistribute Emby itself.
- Does not bypass licensing or DRM, and does not unlock paid features.
- Provided as-is, without warranty.

## License and credits

- Released under **GPL-3.0**; see [LICENSE](LICENSE)
- Upstream project: [sjtuross/StrmAssistant](https://github.com/sjtuross/StrmAssistant); its license and copyright notices are retained
- Third-party dependencies: see [NOTICE.md](NOTICE.md)

## 中文

[中文说明](README.md)
