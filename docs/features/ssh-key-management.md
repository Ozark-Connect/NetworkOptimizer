# SSH key management: stored keypairs, and retiring the path field for tenants

Status: **design note, not yet built.** Written up so a fresh session can pick it up. Background is
TJ's, verified against the code on `feature/identity-full` 2026-07-27.

## Why this exists

Today an SSH private key is configured by typing a **filesystem path** into Settings, and the server
opens whatever that path names:

- `src/NetworkOptimizer.Web/Services/Ssh/SshClientService.cs:287-293` -
  `new PrivateKeyFile(connection.PrivateKeyPath[, passphrase])`, straight from the record.
- The path is stored on `GatewaySshSettings.PrivateKeyPath` and
  `DeviceSshConfiguration.SshPrivateKeyPath`, and typed into
  `Settings.razor` (gateway field ~line 447, device field ~line 616,
  placeholders `/app/ssh-keys/gateway_key` and `/app/ssh-keys/id_rsa`).

For a self-hosted operator that is fine and even desirable - you mount a key into the container and
point at it. In a **hosted / multi-tenant** context it is not.

### What the exposure actually is

Worth stating precisely, because the obvious reading is wrong:

- It is **not** arbitrary file read. `PrivateKeyFile` throws unless the bytes parse as a key, so a
  tenant cannot use it to print `/etc/passwd`.
- It **is arbitrary key USE**. A tenant admin who names a path where one of the operator's keys lives
  gets the server to authenticate to devices **as the operator**, with a key they never possessed and
  cannot see. They do not need to read it; they need it to load.
- Secondary: missing-vs-unparseable produce different errors, which makes it a **filesystem existence
  oracle**.

This is the same species as the tenancy review's T2/T3 findings - holding a role treated as proof of
authority over a resource the role never granted.

## Shape

Keep the path field. Do not remove it; self-hosted operators legitimately mount a key. Split the work:

**Source branch (public repo) - build the feature**
- Upload **or generate** an SSH keypair, stored per site, encrypted with Data Protection exactly as
  `ClientSecretProtected` already is.
- This closes a genuine gap for every user, not just tenants: today a self-hosted user has to shell
  into the container to place a key at all.

**Private fork (NetworkOptimizer-SaaS) - gate it**
- `PrivateKeyPath` becomes instance-admin-only. A tenant site admin must use a stored key, and never
  sees the filesystem path field.
- A global/instance admin can still point a site at a filesystem key; that path stays invisible in the
  tenant's UI.

Building on source **before the big rebase** is the right order: the fork inherits the feature and the
gate stays a small private delta.

## Design decisions to make early

1. **Generate is the default; upload is the fallback.** A generated key means the private half never
   crosses the wire - the UI hands back only the public key to install on devices. Upload exists for
   people with an existing key, but it should not be the primary path.

2. **It raises the stakes on config export.** Stored keys land in the site database, and the export
   already bundles every site DB plus the Data Protection keys - which is what made T2 critical. The
   export already claims to include SSH keys, so this is not new in kind, but the volume and value of
   what a single export yields goes up. Worth deciding whether full export should exist in the hosted
   build at all.

## Related work already on this branch

The RBAC pass gated who may *reach* the SSH settings (Settings is Site Admin or global Admin), but it
did not change what the path field does once reached. That is this feature's job.

## TODO

TJ has an iOS Notes item to paste in here - a fuller write-up of the intended UX. Ask for it before
building; do not infer the UX from this note alone.
