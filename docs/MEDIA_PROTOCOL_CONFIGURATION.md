# Media compatibility data

Users receive media compatibility data with normal V-Notch updates. There is no
configuration step in the application. Existing Spotify query-hash discovery and
YouTube watch-page key refresh continue to work automatically, subject to network
privacy settings and the providers' behavior.

Maintainers can change `Assets/MediaProtocols.json` without changing C# source.
The file carries the Spotify TOTP version/secret pair, initial GraphQL hashes,
YouTube Innertube key/client version and Piped/Invidious fallback host lists.
It is copied into build/publish output and embedded as a recovery default.
Startup reads the sidecar once as a complete snapshot. Missing, unreadable,
oversized or invalid files use the embedded defaults and log a generic diagnostic.
An empty fallback host list disables that fallback. No configuration server is
contacted. Installing a new application version updates the shipped data.

Schema version 1 requires every field. Hashes are 64 hexadecimal characters;
the TOTP secret is base64-encoded protocol data, not a user's session cookie.
Hosts must be plain DNS names, without URLs, ports or paths. Credentials and
user sessions belong in the encrypted settings store, never in this file.

These controls make compatibility data maintainable; they cannot guarantee that
an undocumented third-party protocol keeps working. Provider failures still fall
back to the existing artwork/subtitle behavior. This file does not establish
permission to access a provider or redistribute its content.
