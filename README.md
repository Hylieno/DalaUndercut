# DalaLeno Undercut

Dalamud plugin for Final Fantasy XIV that reprices a retainer's current market listings using live Market Board data.

Current stable baseline: **v0.3.5**.

## Branches

- `dev` — active development and testing.
- `release` — stable code intended for releases.
- `main` — bootstrap/default branch; development should happen on `dev` and stable promotions should target `release`.

## Current behavior

- Walks the current retainer's listed items automatically.
- Reads live FFXIV Market Board results rather than relying on delayed external pricing data.
- Matches HQ listings against HQ competitors and NQ listings against NQ competitors.
- Can ignore listings from your own retainers.
- Configurable undercut amount.
- Configurable action delay (1000 ms default).
- Never prices below 1 gil.
- Provides STOP while an automated run is active.

## Development flow

1. Make changes on `dev`.
2. Push/PR to `dev`; the CI workflow builds the plugin.
3. Test the resulting development build in game.
4. Open a PR from `dev` to `release` once the version is validated.
5. Merging into `release` builds and publishes the version declared in `DalaLenoUndercut.csproj` as a GitHub Release, provided that version does not already exist.

When preparing a new stable version, bump `<Version>` in `DalaLenoUndercut.csproj` before merging to `release`.

## Disclaimer

This is an unofficial third-party FFXIV plugin. Use of third-party tools is governed by Square Enix's rules and policies.
