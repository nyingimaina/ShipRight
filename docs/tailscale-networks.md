# Tenant Tailscale networks

Added in 4.10.34. A server can live in a Tailscale tailnet instead of on a public address. Direct networking stays the
default: a server with no tailnet assigned behaves exactly as before.

## How it works

- ShipRight runs **one unprivileged `tailscaled` per tailnet** with `--tun=userspace-networking` and a loopback-only
  SOCKS5 listener. Nothing gets root networking, `NET_ADMIN`, or host networking in the container.
- Only transport details live in the providers. Everything shared — routing, host keys, SSH, SFTP, retries, errors —
  stays in one place:
  - `Modules/ServerNetwork/ServerNetwork.cs` — route contracts and the direct/tailnet providers
  - `Modules/ServerNetwork/Socks5SshRunner.cs` — SSH through the SOCKS5 route
  - `Modules/ServerNetwork/NetworkAwareSshRunner.cs` — decorator around `ISshRunner`, so every existing caller is routed
  - `Modules/ServerNetwork/RoutedSftp.cs` + `RoutedSftpRemoteFileClient` — Files tab, database transfers, file picker
  - `Modules/ServerNetwork/RoutedTcpProbe.cs` — the "can ShipRight reach it" check, through the proxy
  - `Modules/ServerNetwork/RoutedHostKeyScanner.cs` — the wizard's fingerprint check, through the proxy
  - `Modules/Tailscale/TailnetManager.cs` — `tailscaled` lifecycle, SOCKS port, OAuth key minting
  - `Modules/Tailscale/TailnetStore.cs` — encrypted, company-scoped tailnet records
  - `Modules/Tailscale/TailnetRouter.cs` — `/api/tailnets`
- A tailnet that is not running **fails closed**. There is no silent fallback to a direct connection.

## Endpoints

| Method and path | Purpose |
|---|---|
| `GET /api/tailnets` | list the company's networks (secrets never returned) |
| `POST /api/tailnets` | create or update a network; a blank secret keeps the stored one |
| `POST /api/tailnets/{id}/start` | start `tailscaled` and join; returns the SOCKS port |
| `POST /api/tailnets/{id}/stop` | stop the node |
| `POST /api/tailnets/{id}/test` | read the live online/port status |
| `DELETE /api/tailnets/{id}` | stop and remove |

Assigning a server: the server wizard and the server editor both offer a **Tailnet** selector. That writes
`TailnetId`; empty means direct.

## Credentials

Two options, both stored encrypted at rest and never returned by the API:

1. **Auth key** — `tskey-auth-…`. Simplest. Make it reusable if you want it to outlive a restart, and give it the tag
   ShipRight should use.
2. **Trust credential (OAuth client)** — client ID + secret with the **Auth keys** scope (read & write), created at
   `Settings → Trust credentials`. ShipRight exchanges the client credentials for an access token and mints a
   short-lived, ephemeral, preauthorized key per node. This is the closest thing to "sign in once": the credential is
   authorized once in Tailscale Admin and ShipRight never needs a new key.

If the **Tailnet** field is left as `-`, the API resolves the default tailnet from the credential. A custom name must be
the tailnet name from `Settings → General` (a domain such as `example.com`, or the signup email on a solo plan) — not a
device DNS name like `my-server.tail1234.ts.net`. If a custom name returns 404, ShipRight retries with `-` once, which
is usually the right tailnet for that credential.

### When joining fails

| Message | Meaning and fix |
|---|---|
| `could not find the tailnet '…'` (404) | Wrong tailnet name, or the credential has no `auth_keys` scope. Set Tailnet to `-`, or use the name from Settings → General; give the trust credential **Auth keys** write access. |
| `Tailscale rejected the trust credential` (401/403) | The credential is revoked, or its scope is not **Auth keys**. Create a new trust credential with that scope. |
| `invalid key` | The auth key is expired or already used. Generate a reusable key, or switch to a trust credential. |
| `requested tags … are not permitted` | Add the tag to `tagOwners`, or use a key that already carries it. |

The node's identity lives in the app's persistent volume (`/root/.shipright/tailscale/<company>/<tailnet>`), so a restart
reuses the saved login instead of spending another key.

### Google sign-in

ShipRight cannot offer "Sign in with Google to Tailscale" as a third-party consumer flow: Tailscale does not expose its
own identity provider for other apps to use. If the tailnet itself signs in with Google, do the authorization **in
Tailscale Admin**: create the OAuth client or auth key there while signed in with Google. Google identity stays inside
Tailscale; ShipRight only ever holds a Tailscale credential.

### Tailnet setup checklist

- Define the tag in the ACL `tagOwners` (default `tag:shipright`).
- Tag the auth key or have the OAuth client mint tagged keys; an untagged key plus `--advertise-tags` is rejected by
  Tailscale.
- Allow the ShipRight node to reach the servers on port 22 (and any other ports ShipRight uses) in the ACL `grants`.
- The servers must already be members of the same tailnet.
- If the server answers with **Tailscale SSH**, ShipRight's key is never used. Turn Tailscale SSH off on that server
  (`sudo tailscale set --ssh=false`) or set the SSH rule to `accept`; the connection test explains this when it happens.

## Tests

- `ShipRight.Tests/Modules/ServerNetwork/ServerNetworkProviderTests.cs` — and the network-aware SSH decorator
- `ShipRight.Tests/Modules/ServerNetwork/RoutedTcpProbeTests.cs` — direct vs SOCKS5 probing, proxy refusal, offline tailnet
- `ShipRight.Tests/Modules/ServerNetwork/RoutedHostKeyScannerTests.cs` — fingerprint reads stay on the route
- `ShipRight.Tests/Modules/Tailscale/JsonTailnetStoreTests.cs` — encryption at rest and company scoping
- `ShipRight.Tests/Modules/Tailscale/TailnetManagerCleanupTests.cs` — removing a network deletes only its state
- `ShipRight.Tests/Modules/Tailscale/TailscaleOAuthKeyMinterTests.cs` — token exchange and key request shape
- `front-end/src/modules/Tailscale/__tests__/TailnetsPage.test.tsx` — no secret rendered, save flow
