# Branches

| Branch | What it is | Tags |
|---|---|---|
| `main` | the current stable line (2.0.x) | `v2.0.*` |
| `v2.0` | where 2.0 work happens; fast-forwarded into `main` at each stable | `v2.0.*-beta*` |
| `v1.3` | the last 1.x (1.3.5), kept for reference only | `v1.3.*` |

- Work on `v2.0` (or a short `fix/...` branch off it), build green, then tag.
- A stable release fast-forwards `main` to `v2.0` (`main` only ever holds tested builds).
- Delete a `fix/...` branch once it's merged. Old betas live on as tags and GitHub releases.
- Dependabot PRs go into `main`; merge them into `v2.0` too.
