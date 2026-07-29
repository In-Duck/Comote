# Comote commercial-readiness plan

Comote is developed as a staged release candidate. A successful build is not
enough to call a remote-control product production-ready; each stage below has
an explicit quality gate.

## Stage 1: scalable monitoring (Preview 24)

- Separate monitoring resolution, frame rate, bitrate, audio, and lifecycle
  from the active control session.
- Warm up to 150 online clients gradually instead of opening them in one burst.
- Use 160x90 H.264 at 15 FPS and about 80 Kbps per client for a 100-PC grid.
- Keep only the newest decoded thumbnail frame and discard stale work.
- Release inactive receivers and their decoder/audio resources.
- Reconfigure the active Host encoder to full control quality without creating
  a second connection.

Quality gate: Host and Manager release builds pass, malformed settings are
rejected, 100-client synthetic traffic stays bounded, and a real 10-PC soak
test runs for at least two hours without increasing memory or connection count.

## Stage 2: connection reliability

- Introduce one connection state machine with bounded timeouts and cancellation.
- Add jittered exponential reconnect, ICE restart, and network-change recovery.
- Make switching between already-warmed clients immediate.
- Expose actionable states: connecting, direct, relayed, degraded, reconnecting,
  authentication failed, and update required.

Quality gate: repeated network loss, sleep/resume, client restart, and TURN-only
tests recover without restarting Manager.

## Stage 3: security boundary

- Require authenticated account ownership before signaling or control.
- Bind each control session to a short-lived nonce and explicit permission set.
- Rate-limit input, clipboard, file, and administrative commands independently.
- Encrypt or disable the legacy raw TCP Manager Hub outside trusted LANs.
- Remove secrets from packages and logs, rotate service credentials, and record
  auditable control start/stop and privileged operations.

Quality gate: threat-model review, dependency scan, cross-account isolation
test, replay test, malformed-message fuzzing, and an external security review.

## Stage 4: update and recovery

- Sign Manager, Client, installer, and update manifests.
- Verify package signature and SHA-256 before staging.
- Use atomic replacement, health confirmation, automatic rollback, and update
  rings so a bad build cannot disable every client at once.

Quality gate: interrupted download, corrupt package, locked files, power loss,
and failed-start simulations all preserve or restore the previous version.

## Stage 5: operational release

- Add crash reporting with privacy controls, structured diagnostics, and a
  one-click support bundle that excludes credentials and clipboard contents.
- Define TURN bandwidth budgets, quotas, alerts, retention, and cost caps.
- Publish privacy, retention, third-party license, support, and incident plans.

Quality gate: 25/50/100/137-PC soak tests, signed installer verification on
supported Windows versions, disaster recovery drill, and release checklist
approval.

## Release rule

Preview builds may be tested by the team. “Production-ready” is used only after
all five stages pass their quality gates on real machines and the release is
code-signed. FakerInput compatibility and game anti-cheat acceptance are never
guaranteed by Comote.
