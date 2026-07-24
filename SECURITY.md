# Security Policy

## Supported versions

| Version | Supported |
|---------|-----------|
| 0.1.x   | ✅        |

Only the latest `0.1.x` release receives security fixes.

## Reporting a vulnerability

Please **do not** open a public issue for security reports.

Instead, use GitHub's private vulnerability reporting: go to the repository's
**Security** tab → **Report a vulnerability**. This keeps the details private
until a fix is available.

Include enough detail to reproduce the problem (affected version, steps, and any
relevant `[ArenaHelper]` log lines). We will acknowledge the report and keep you
updated on the fix.

## Scope notes

HDT Arena Helper runs **locally** as a plugin inside Hearthstone Deck Tracker
and uses **only free, public data** — it never scrapes paywalled services and
bundles no third-party credentials or proprietary data. Its only network access
is read-only fetches of public arena statistics, cached to disk with a browser
User-Agent. Keep this in mind when assessing impact.
