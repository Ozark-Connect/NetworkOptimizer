# Contributing to Network Optimizer

Network Optimizer is source-available, not open source. The code is public under the [Business Source License 1.1](LICENSE), and the architecture, design, and roadmap are maintained by one person who is accountable for every line that ships.

## Issues are open to everyone

Bug reports and feature requests go in [GitHub Issues](https://github.com/Ozark-Connect/NetworkOptimizer/issues), and I read and reply to every one. Include your UniFi device models, your UniFi Network and UniFi OS versions, and how you run Network Optimizer (Docker, Windows, macOS, Proxmox). Sanitize credentials and IP addresses before attaching logs.

A good issue is the most useful thing you can send: logs, traces, and a clear repro have driven a lot of what's in this project, and they get credited in the release notes.

Report security issues privately by email to tj@ozarkconnect.net, never in a public issue. Patch releases ship often, so a valid security concern is typically fixed in a release within a few days.

## Pull requests start with an issue

Open an issue before you write any code, and describe the problem and the change you have in mind. That conversation tells you whether I already have it in the works, whether it fits the project, and what approach makes sense. Once we've agreed on it there, I'm happy to accept a PR from qualified software engineers and IT pros.

A first pull request that doesn't reference an issue is closed automatically. A PR that references an issue but skips the discussion gets closed by hand.

- **Agree on the approach in the issue first.** This applies to bug fixes as much as features, and doubly to new Security Audit rules, alert rules, monitoring data, dependencies, and UI or design changes.
- **Reference the issue in the PR description** (`Closes #123`).
- **One change per PR.**
- **Target the right line, never `main`.** A fix for a released version targets `dev`; a feature targets the active `release/X.Y` branch. Rebase onto the latest target before opening the PR.
- **Zero build warnings, and tests for the change** that pass against the current target branch.
- **Follow [CODING_STANDARDS.md](CODING_STANDARDS.md)** and the code around you. No dead code, no unrequested UI or copy changes, no new InfluxDB measurements, and no new project references or packages without agreement in the issue.

## AI tooling

Use whatever tools you like. You own every line you submit, and you should be able to explain why each change is there and what it was tested against.

## Licensing of contributions

By submitting a contribution, you agree that it is licensed to Ozark Connect under the same terms as the rest of the project, and that Ozark Connect may distribute it under the Business Source License, its Change License (Apache 2.0), and commercial licenses.

Commercial use, including by managed service providers (MSPs), network installers, and anyone using Network Optimizer to deliver paid services to clients, requires a commercial license. See [licensing and pricing](https://ozarkconnect.net/network-optimizer/licensing).
