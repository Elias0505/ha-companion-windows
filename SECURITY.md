# Security Policy

## Supported versions

Only the latest release receives security fixes. There is no backporting —
update to the newest version before reporting an issue you can still reproduce.

## Reporting a vulnerability

Please use GitHub's private vulnerability reporting:
**Security tab → Report a vulnerability** on this repository.
Do not open a public issue for anything security-sensitive.

You can expect a first response within 7 days. Confirmed issues are fixed in
the next release; you will be credited in the release notes unless you prefer
not to be.

## Scope notes

Areas worth extra scrutiny:

- Handling of the Home Assistant access token (DPAPI storage, WebView injection)
- The PowerShell installer / uninstaller chain (`install.ps1`, release checksums)
- Remote PC commands received via the mobile_app push channel
- The WebSocket / REST connection layer, including the
  "ignore certificate errors" opt-out
- **Config backup import** — a backup is a file a user may receive from someone
  else. By design an import cannot change any *security decision*: it never
  toggles certificate handling, never enables an HA→PC command, and never seeds
  the launch whitelist; and if it changes the HA URL, the stored token and
  webhook id are dropped so they can never be sent to a different host.
- **The token is bound to one origin.** The stored token is only ever sent to
  the scheme/host/port it was saved for (an http→https upgrade of the same host
  is the one allowed exception). Pointing the URL at a different origin —
  typed by hand, imported, or filled in by the network search — never carries
  it along: connecting is refused until the user enters a different token for
  the new instance, and only then are the old host's webhook id and device id
  dropped. An imported backup that changes the URL discards the stored
  credentials outright. Network discovery (mDNS) answers come from whoever is
  on the LAN, so a response only ever pre-fills the URL field (and not even
  that while the token field holds a secret and the found instance is a
  different host); it never carries credentials with it and never connects on
  its own.

Out of scope: issues that require the attacker to already control the local
Windows user account (the app runs unprivileged and per-user by design), and
reports against non-default configurations explicitly labelled as unsafe in
the settings UI.
