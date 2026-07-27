# SSH key management: upload or generate a keypair

Status: **not built.** Design note so a fresh session can pick it up. TJ's framing; verified against
the code on `feature/identity-full` 2026-07-27.

Critical before multi-user ships.

## What to build

Let admins **and site admins** upload or generate their own SSH private/public keypair, instead of
only being able to name a file already sitting on the server.

- **Generate is the default.** The private half never crosses the wire - the UI returns only the
  public key to install on UniFi and other devices. Upload is the fallback for an existing key.
- Stored per site, encrypted with Data Protection exactly as `ClientSecretProtected` already is.
- This is a gap for self-hosted users too, not just tenants: today placing a key at all means
  shelling into the container.

## The lock-out

**Site admins cannot specify the filesystem path option** - they use a stored key. A global admin can
still override and facilitate, and keeps the path field, since self-hosted operators legitimately
mount a key and point at it.

The nuance behind the lock-out: `SshClientService.cs:287-293` opens whatever path the record names.
Not arbitrary file read - `PrivateKeyFile` throws unless the bytes parse as a key - but a site admin
naming a path where someone else's key lives would have the server authenticate as them. Enough
reason to take the field away from them; not a reason to remove it for everyone.

## The three places SSH auth is configured

1. **Settings - UniFi Console Connection**, gateway SSH - `Settings.razor` ~447,
   `GatewaySshSettings.PrivateKeyPath`.
2. **Settings - Device SSH**, the global device credential - `Settings.razor` ~616,
   the same field on the device SSH settings.
3. **LAN Speed Test**, per-device override - `SpeedTest.razor` ~691,
   `DeviceSshConfiguration.SshPrivateKeyPath`.

All three need the keypair option and the same site-admin lock-out.

## Order of work

Build it on the **source branch before the big SaaS rebase**, so the fork inherits the feature and the
tenant gate stays a small private delta.

## Worth deciding alongside

Stored keys land in the site database, and the config export already bundles every site DB plus the
Data Protection keys. The export already claims to include SSH keys, so this is not new in kind, but
it is worth deciding whether full export should exist in the hosted build.
