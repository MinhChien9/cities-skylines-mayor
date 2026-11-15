# Security policy

The bridge mod opens an HTTP listener inside Cities: Skylines. It binds to **localhost
only** (`127.0.0.1:32123`) and has no authentication: anything running on the same machine
can read the city and build in it. Do not expose that port to a network.

## Reporting a vulnerability

Please do **not** open a public issue. Use GitHub's private reporting:
**Security → Report a vulnerability** on this repository. Include the version (release tag
or commit), what an attacker can do, and the steps. You will get an answer within a week.

## Supported versions

Only the latest release and `main` get fixes.
