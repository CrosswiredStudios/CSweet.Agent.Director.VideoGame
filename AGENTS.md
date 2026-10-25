# Video Game Director repository instructions

This repository contains one standalone C-Sweet protocol-v2 agent. Its purpose is:

> Lead the business-wide game portfolio, secure exact CEO pitch approval, supervise shared Creative Director and Producer roles, and deliver the agreed CEO reporting contract.

## Invariants

- Keep `com.csweet.video-game-director` and version `0.1.1` synchronized between agent code,
  `csweet-plugin.json`, tests, and releases.
- The root manifest is the reviewed authority request. Keep `provides`, `requires`, events,
  configuration, credentials, web access, and UI contributions synchronized with implementation
  and tests.
- Request the minimum authority needed. Manifest declarations never grant access.
- Use typed callbacks and `AgentRuntimeContext.Platform`. Do not implement MCP/JSON-RPC, access
  runtime/workload/lease tokens, connect directly to databases or Docker, or handle provider
  credentials.
- Agent work is delivered at least once. Honor cancellation and use stable domain idempotency keys
  for external mutations.
- Unknown capabilities and events must fail or be ignored safely without leaking sensitive data.

## Verification

Run from the repository root:

```powershell
dotnet test
dotnet run --project src/CSweet.Agent.Director.VideoGame -- --self-test
```

Any new capability, grant, event, configuration field, credential, or network rule requires a
manifest update, a README explanation, and tests.

## Releases

Bump and synchronize the manifest, implementation and project versions before writing releases/<version>.md. Verify tests, self-test and package version. Use published releases and Installed Agents; never patch installed runtime payloads.
