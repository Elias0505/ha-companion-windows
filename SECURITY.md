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

Out of scope: issues that require the attacker to already control the local
Windows user account (the app runs unprivileged and per-user by design), and
reports against non-default configurations explicitly labelled as unsafe in
the settings UI.
