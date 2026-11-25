# Database backups: storage, compression, encryption

Added in 4.11.0. Every backup is written as one artifact plus a `.meta.json` sidecar:

`shop_2026-11-25_02-00-00_schedule.sql.zst.enc`

## What happens to a dump

1. The dump's **stdout is copied as raw bytes** (no text decoding, no line-ending changes) through
   `IRawSshRunner` — direct or through the tailnet SOCKS proxy, whichever route the server uses.
2. Bytes are hashed (SHA-256) and compressed with **zstd** at the project's level (default 12; 0 = raw, 19 = smallest).
3. The compressed stream is sealed in 1 MiB chunks with **AES-256-GCM** using the server-managed backup key.
   Any change to the file fails authentication instead of restoring damaged data.
4. The sidecar records the original size, stored size, ratio, checksum, key id, source (manual/schedule),
   duration and creation time. Files written by older versions have no sidecar; they are recovered from the file
   name and size and still appear in the list.

SQL Server `.bak` files are first written inside the container, copied to the host with `docker cp`, and then pulled
over SFTP into the same pipeline. Restore copies the file back in before running `RESTORE`.

## Schedules

Schedules created on the Backups page are saved (`backup-schedules.json`, company-scoped) and re-registered at
startup, so a restart does not forget them. There is **no implicit daily backup** any more: a project is only backed
up when a schedule exists or someone presses **Backup now**. Verified by `JsonBackupScheduleStoreTests`.

## Retention

Each project has two limits, applied after every successful run:

- **Keep last N** (minimum 1) — the primary control.
- **Delete older than D days** (0 = no age cap).

A file is deleted when it is beyond the count **or** older than the age cap. The newest file is never deleted,
even if it is older than the age cap, so a forgotten schedule cannot leave a project with zero backups.

The Backups page shows per file: `#2 of 10 · ~5 Dec` — the rank, and an estimate computed from the observed
interval between backups. Count-based retention is an estimate; the age cap is exact.

## Where files live

`SHIPRIGHT__BACKUP_DIR` selects the root; default is `<app data>/backups`.

Both compose files mount a host folder so teardown does not remove backups:

```yaml
environment:
  SHIPRIGHT__BACKUP_DIR: /var/lib/shipright/backups
volumes:
  - ./backups:/var/lib/shipright/backups
```

`docker compose down` keeps named volumes; `down -v` removes them. The bind mount above survives `-v`, an image swap
and a reset of the app data volume. A host failure still loses both copies, so keep an offsite copy of the folder.

To migrate an existing deployment: create `./backups`, copy `<data>/backups/*` into it **before** starting the new
image, verify the file count and sizes, then remove the old folder after a successful run.

## Compression

zstd via `ZstdSharp.Port` (MIT, pure managed — no extra binary in the installer). Default level 12; level 19 is
available per project for smaller files at more CPU. `xz` is not used: on a 1 GB host its compression memory can
starve MariaDB and the app.

SQL Server backups also use `WITH COMPRESSION, CHECKSUM`; the zstd layer still applies for a single code path.

## Encryption and recovery keys

- The server generates a 256-bit key on first use and stores the keyring encrypted with the app's secure store.
- New artifacts use the **active** key. Rotating a key makes a new key active; older keys stay available so
  yesterday's backup still restores. A key can be retired only when no artifact references it.
- **Export** (`GET /api/backups/recovery-key/export`, Admin) downloads `shipright-backup-keys-*.json` containing
  **every** key. Keep it offline: it opens every backup. The JSON format is documented below.
- **Rotate** (`POST /api/backups/recovery-key/rotate`) returns the new active key id and the refreshed recovery file.
- Scope: this is encryption at rest. It protects a stolen disk, a copied volume or an offsite copy. It does not
  protect against a fully compromised host, which can read the database directly.

Recovery file shape:

```json
{
  "version": 1,
  "encryption": "aes256gcm",
  "container": "SRBK1",
  "activeKeyId": "…",
  "exportedAt": "…",
  "keys": [{ "id": "…", "key": "base64(32 bytes)", "createdAt": "…" }]
}
```

## Downloads

| Format | Endpoint | What the user gets |
|---|---|---|
| plain (default) | `…/backups/download?file=…&format=plain` | decrypted + decompressed `.sql` / `.bak`, ready for `mysql`/`sqlcmd` |
| zstd | `…&format=zstd` | decrypted but still compressed `.sql.zst`; use `zstd -d` |
| raw | not exposed | the stored `.enc` artifact; use the recovery key and a future decrypt tool |

Encryption never blocks a download: the server decrypts while streaming, so no key is needed outside ShipRight.
Downloads are Operator-only; the recovery-key export is Admin-only.

## Decrypting a file without the app

The Backups page has **Decrypt a backup file**: choose an artifact copied from the backup folder (for example
`shop_2026-11-25_02-00-00_schedule.sql.zst.enc`) and the recovery JSON. ShipRight validates the key, then streams
the decrypted (and decompressed) file back; the upload is temporary and removed when the download completes.

- Plain `.sql`/`.bak` files pass through unchanged.
- zstd-only files (`.sql.zst`, no `.enc`) are decompressed without a recovery file.
- The download is a second request with a one-hour, unguessable token, so large files go straight to disk instead of
  through browser memory.
- Encryption does not change the download formats on the project row: **Download** gives plain bytes,
  **Download .zst** gives decrypted-but-compressed bytes.

## API additions

| Method and path | Purpose |
|---|---|
| `GET /api/projects/{id}/db/backups` | files with sizes, ratio, checksum, rank, projected rotation |
| `PUT /api/projects/{id}/db/backup-policy` | keep count, age cap, compression level, encrypt |
| `GET /api/backups/storage` | root, free space, used bytes, count, oldest/newest |
| `GET /api/backups/recovery-key` | key ids, creation dates, active flag (no key material) |
| `POST /api/projects/{id}/db/restore` | `{ fileName }` — no absolute paths accepted |
| `GET /api/servers/{id}/db/backups/download` | same formats for standalone server panels |

## Tests

- `BackupArchiveTests` — round trip, checksum, tamper detection, legacy files, traversal refusal
- `BackupRetentionTests` — count + age, newest protection, projection
- `BackupKeyringTests` — encryption at rest, rotation, export, retire rules
- `JsonBackupScheduleStoreTests` — schedules survive a restart and stay company-scoped
- `SqlServerProviderCommandTests` — `docker cp` in/out, tools18 fallback, compression/checksum
- `DatabaseOrchestratorBackupEncodingTests` — byte-exact dumps, linked-server key, no-key failures
- `front-end/src/modules/Backups/__tests__/BackupsPage.test.tsx` — expand, sizes, retention save, storage warning
