# SSH key management: upload or generate a keypair

Status: **built** on `feature/identity-full`. TJ's framing; this note records what shipped and why,
including the three places the design changed once it met the code.

## What it does

One stored SSH keypair per site, used for Gateway SSH and Device SSH instead of a password or a key
file on the server.

- **Generate is the default and takes one click.** No name to choose (there is only one key) and no
  algorithm prompt: Ed25519, with RSA-4096 behind a small link for firmware too old for Ed25519.
  Upload is a text link, not a co-equal button - it is the fallback for a key you already have.
- The private half never crosses the wire. The UI only ever receives `SshKeyInfo`, which has no
  private-key member by construction.
- **Purely optional.** A site with no stored key authenticates exactly as it did before this existed,
  and the empty state offers rather than nags.

## Where it lives

A single **SSH Key** panel in Settings - Connection (`Settings.razor`, card `id="ssh-key"`, beside the
existing `gateway-ssh` and `device-ssh` cards). It is the only editor.

The places that configure SSH each get a read-only `SshKeyRow` stating which key is in effect and
linking to the panel - no picker, no per-form generate, and no way to replace the gateway's key from
the ONT form. With one key per site there is nothing to select: the key is attached wherever SSH
connects, additively, alongside whatever password or key file is already set.

With no key stored the row states the offer rather than the absence. Someone typing a password into an
SSH form is exactly the person who would rather not have one and is unlikely to know the app can make
one for them.

## Where SSH auth is configured

Four places, not the three the original note listed:

1. Settings - Connection, gateway SSH - `Settings.razor` ~448
2. Settings - Connection, Device SSH - `Settings.razor` ~617
3. LAN Speed Test, per-device override - `SpeedTest.razor` ~690
4. ONT configuration - `Settings.razor` ~1789, and it does reach SSH: `OntMonitorService.cs:407`
   passes it into the poll context

`ModemConfiguration.PrivateKeyPath` exists as a model field with validation, but nothing renders an
input for it, so there is nothing to lock down there.

## The lock-out

A Site Admin cannot see or set a path to a key file on the server. They can still *use* one a global
Admin configured - they just never learn what it is.

The reason: `SshClientService` opens whatever path the record names. Not arbitrary file read
(`PrivateKeyFile` throws unless the bytes parse as a key), but a Site Admin naming a path where
someone else's key lives would have the server authenticate as them.

**Enforced on a separate service surface, not by gating the existing ones.** `ISshSettingsAdminService`
is `[MutatingService(SiteScoped = true)]` and is what the edit forms resolve; it redacts
`PrivateKeyPath` on read and preserves the stored value on write for anyone below global Admin.

`IGatewaySshService` and `IUniFiSshService` were deliberately left ungated:

- They are on the connection path, and monitoring calls them with no caller established
  (`ProbeExecutorFactory`, `DeviceRebootProbe`). A gated call with an unset caller is a hard failure -
  `MethodSecurityInterceptor` calls `ICallerContext.Require()` - so marking them would have made every
  monitoring poll throw. Only four places in the app enter a system scope, and monitoring is not one.
- Redacting inside `GetSettingsAsync` fails for the same reason from the other direction: the
  connection genuinely needs the path in order to connect.

Every edit-form save routes through the admin surface, including the incidental ones (the
last-tested-at stamps and the TC monitor port). A Site Admin's loaded settings carry
`PrivateKeyPath = null`, so saving one of those through the raw service would wipe an admin's path.

## Encryption

`ICredentialProtectionService` - AES-256 with a machine key from DPAPI on Windows or a key file on
Linux, `ENC:` prefixed, with `IsEncrypted` so re-saving does not double-encrypt.

Not `IDataProtector`/`ClientSecretProtected`, which the original note called for. That is the identity
subsystem's mechanism; every SSH credential in the app already uses `ICredentialProtectionService`, and
following the note literally would have put a second encryption scheme on the same kind of secret in
the same card.

## Keys stay server-side of the agent tunnel

A stored key is decrypted into a `MemoryStream` and handed to `PrivateKeyFile(Stream)`. It is never
written to the filesystem, and it never reaches an agent.

**This is a constraint, not an accident - do not "optimize" it away.** `SiteTunnelRouting` rewrites
host:port to the tunnel proxy, so SSH.NET dials through the agent but the SSH session terminates at the
device and authentication happens on the server. `ProxyHandler` is a TCP relay with a dial policy, and
`AgentProtocol` carries no credential or key fields at all.

## Generation

BouncyCastle (`BouncyCastle.Cryptography`, the maintained package - not `Portable.BouncyCastle`).
net10.0 has no standalone Ed25519 primitive (it appears only inside Composite ML-DSA) and SSH.NET does
not generate keys.

The test that matters is the round trip: generate, parse with the same `PrivateKeyFile` the auth path
uses, and confirm the public half SSH.NET derives is the one the UI displayed. A key we cannot read
back would have the user install a public half on their gateway and then fail every connection.

## Multi-site

`SshKeyService` takes the **scoped** `NetworkOptimizerDbContext`, which routes to the site in context.
Not the singleton `IDbContextFactory`, which is pinned to the main database - with that, a Site Admin
on a secondary site would write their key into the main site's database and overwrite its key.

`HasCredentials` on both settings models folds in a `[NotMapped] HasStoredKey` flag that the loaders
set, so the two dozen call sites that gate on it are correct at once. Patching them individually is how
holes survive.

## Still open

- Whether full config export should exist in the hosted build, since stored keys land in the site DB
  and the export bundles every site DB plus the Data Protection keys. A SaaS-fork decision.
- Automatic key placement on console gateways via udm-boot - tabled, see `TODO.md`.
