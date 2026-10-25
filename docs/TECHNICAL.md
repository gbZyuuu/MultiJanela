# Compatibility and behavior

- **Accounts:** official Roblox login in separate local WebView2 profiles. Sessions remain subject to Roblox permissions, moderation, verification and rate limits.
- **Multiple clients:** Windows synchronization objects are used to allow more than one Roblox process. This changes normal client behavior and is not endorsed by Roblox.
- **Versions:** downloads come from the Roblox CDN and require a valid Roblox signature. An older deployment may be rejected. Pinning may disable its updater by renaming it; returning to automatic version management restores it when possible.
- **CPU and audio:** Windows controls apply to identified processes. Lower CPU values may reduce game responsiveness. Background audio muting restores volume when applicable.
- **FPS and quality:** saved preferences are applied before launching. They use Roblox's shared settings file; they are not independent live controls.
- **Memory:** RAM and VRAM are usage readings reported by Windows, not guaranteed caps.
- **Avatar:** a generated 3D preview is downloaded and rendered locally. Trying items or colors does not equip them. Roblox can delay or reject rendering; the static fallback shows the current avatar, not pending edits.
- **Account actions:** friendships, outfits and communities require the account's permissions. Confirmations and read-back checks remain for changes to Roblox accounts. Not every Roblox administration feature is included.
- **Scripts:** JavaScript runs in the manager's limited runtime. Nexus requires a compatible authenticated external client. MultiJanela does not inject or provide an in-game Luau executor.
- **Recordings:** only supported TinyTask input events are accepted. Playback needs the selected window in the foreground and can be stopped.
- **Updates:** stable releases are downloaded from this repository and checked against GitHub's digest and the package manifest. Transfer may wait for active work or unsaved account changes. Roblox games remain open.
- **Recovery:** local session repair requires Roblox and Studio to be closed, backs up the native session file and may require another login. It is not a fix for every authentication error.

Browser-profile isolation does not create separate devices or prevent account association. Follow [Roblox's rules](https://www.roblox.com/termsofuse) and each experience's rules. This project offers no ban-prevention or compliance guarantee.

For storage and safe reporting, see [Security and privacy](../SECURITY.md).
