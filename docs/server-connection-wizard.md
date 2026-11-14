# Connect a server: guided wizard and connection test

Added in 4.9.0. The old New Server form is untouched and keeps working; the wizard sits beside it ("Guided setup" on the
Servers page, and "Test / fix connection" in each server's menu).

## For the user: five screens
1. **How do you log in?** Password, key file (.pem), Tailscale, or "none of these, I can open a terminal on the server".
2. **Where is the server?** Name, address, username, optional folder. The server is saved here, without a key.
3. **Is this the right server?** Shows the server's fingerprint and a read-only command to compare it with what the server
   reports about itself. Nothing is trusted until the user confirms. A fingerprint that differs from one trusted before
   needs an explicit tick.
4. **Give ShipRight access.** ShipRight makes its own key (once, never replaced silently), then, depending on the route:
   password (used once, never kept), a saved key file (used once), or one copy-paste line to run on the server. The line
   is repeatable: running it twice leaves one key.
5. **Test the connection.** A checklist with a symbol and words per line. Failures show what to do and, where useful, a
   command to copy and run on the server. Finish is only possible when nothing failed. Warnings (for example "Docker will be
   installed automatically on the first deploy") do not block.

Nothing the wizard shows is ever meant to be run on the user's own computer.

## Endpoints
| Method and path | Purpose |
|---|---|
| `POST /api/servers/{id}/connection-test` | staged checklist: reachable, login, sudo, Docker, Compose, git, disk, folder |
| `GET /api/servers/{id}/host-key` | the server's fingerprint and whether it is new, trusted, or changed |
| `POST /api/servers/{id}/host-key/trust` | `{sha256, acknowledgeChange}`; re-scans and trusts only the fingerprint that was shown |

The connection always comes from the stored server, never from the request. The test only reads from the server.

## Rules the test applies
- A missing git, Docker or Compose is a **warning** when the login user can install software (the first deploy does it),
  and a **failure** when it cannot (sudo needs a password). The failure line carries the right install command for apt or dnf.
- Docker without sudo is done through the `docker` group (`sudo usermod -aG docker "$USER"`, then log in again). The text
  says plainly that the group is effectively administrator access.
- Passwordless sudo is only ever offered as an explicit command with a warning, and only when something must be installed.
- Tailscale-style names (`*.ts.net`, `100.64.0.0/10`) only change the wording of the advice. Nothing depends on Tailscale.

## Known limits
- ShipRight's SSH runner always connects on port 22; custom ports are not supported anywhere yet.
- The SSH runner still trusts an unknown or changed host key on first contact if the wizard was not used. A strict mode
  would change existing behaviour, so it is a separate decision.
- Tailscale SSH (keyless, identity-based login) is not supported; a normal key-based `sshd` over Tailscale is.
- Not yet run against a real Linux host. Verified with a pretend server, real bash, and unit tests.
