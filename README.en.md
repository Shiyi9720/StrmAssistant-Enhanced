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
- rffmpeg sidecar (SSH `ffprobe` offload) is **deprecated**: use the built-in distributed extraction below; no sidecar needed (archive: `docs/rffmpeg-deployment.md`)
- Distributed media info extraction with automatic local fallback and per-outcome statistics

## Install

1. Verify Emby 4.10.2 with assembly version `4.10.2.0` (otherwise the plugin is rejected with `LoaderException`).
2. Back up your existing plugin folder.
3. Copy the whole `plugins/` folder from the release package into the Emby plugins directory, keeping `StrmAssistantLite.deps/`, `zh/` and `zh-hant/` intact.
4. Restart Emby.

Settings live in `Strm Assistant.json`; upgrading from an older version keeps your configuration. Roll back by restoring the backed-up DLL and restarting.

## Scope and known limitations

### By design (not defects)

- Emby's transcoding pipeline is not intercepted: the plugin only does media info extraction, playback-chain enhancements and intro/credit marking
- Cover capture only fills gaps: a frame is extracted only when an item still has no primary image, and existing posters are never overwritten; off by default
- Every enhancement is off by default: an upgrade changes nothing until you enable it

### Known limitations

- Native watch progress is shared per TMDB id; per-version and cross-library resume is tracked by the plugin, and may drift on some clients. With per-version resume enabled, positions are stored per (item, version) and converted once by runtime ratio when switching; the source may be a split item or another media source of the same merged item, and the finished state is shared only by versions without their own record
- Dolby Vision P7 layer detection needs a static ffprobe 9 (Emby's bundled 5.1 lacks `-show_stream_groups`): the plugin prefers `bin/ffprobe` in its data folder, `/config/bin/ffprobe`, or the path configured in settings, and only then tries to download (~15 MB zip / 36 MB binary); unavailable in fully offline setups without a manually placed binary; a non-zero ffprobe exit code with no stream data in the output is treated as a probe failure rather than "not P7"; the check only applies to disc sources (ISO/BDMV dual-layer streams), single-track P7 MKV/REMUX files are reported as Profile 7 by Emby itself
- Douban, IntroDB and OpenSubtitles are third-party services; rate limits or API changes may disable those features temporarily. Consecutive failures back off progressively (10 min → 30 min → 2 h → 6 h) and reset on success

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
