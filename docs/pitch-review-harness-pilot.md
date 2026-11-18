# Director pitch-review harness pilot

The Director runs a Producer pitch review through the packed `CSweet.Agent.Testing` host using scripted model and platform responses. The repeat used Director `0.14.2` with the pilot applied, SDK and harness `5.42.0`, a standalone Git clone, and a fresh NuGet cache. Both restored packages matched the local feed byte-for-byte. The test project's only project reference was the Director.

| Adoption measure | Target | Original pilot | With shared operating state |
|---|---|---|---|
| Time from repository inspection to first passing cases | ≤30 min | 4 min 50 s | Existing test refitted. New-author time measured only on the original pilot. |
| Added or replaced nonblank setup lines | ≤30 | 45 at first green. Final fixture used 69 (30 scenario + 39 fixture). | 52 (30 scenario + 22 fixture), 17 fewer. One additional line updates a pre-existing test outside the original counting rule. |
| Warm edit through build and both verdicts | p95 ≤15 s | 6.81 s p95, 2.68 s median | 3.18 s p95, 2.90 s median |

The setup budget is still exceeded by 22 lines. The scenario still registers the named model response, captures the submitted coordination answer, and configures provider and model settings. The Director owns the pitch, project, evidence and decision assertions. [Pitch-review tests](../tests/CSweet.Agent.Director.VideoGame.Tests/PitchEscalationTests.cs)

## What the two cases prove

Given the exact Producer brief, the Director runs its scripted production-baseline review, persists the review under its session and turn key, and creates one pending owner decision about audio licensing. The decision is bound to the Producer, session, pitch digest, document revision and hash. The SDK submits a blocked reply referencing that decision with the expected turn revision and idempotency key. Changing only the submitted hash blocks the turn before inference, with no saved review or owner decision. Removing the hash check from a disposable checkout made only the stale case fail. Restoring it made both pass.

`OperatingStateFixture` replaces the Director's custom state handlers. The scenario seeds a project checkpoint and a creative review under their production keys. Existing cases can also seed a cached Director judgment. The Director reads and writes through the SDK platform client throughout.

## Reproduce the comparison

1. Use a standalone Director checkout and a NuGet configuration that resolves `CSweet.Agent.SDK` and `CSweet.Agent.Testing` `5.42.0` from the packed feed, with a source for the remaining dependencies. Use an empty package cache for the first restore.
2. Run the commands below from the repository root. Set `$config` to the NuGet configuration path and `$packages` to the cache directory.

```powershell
$flags = '-p:UseLocalCSweetAgentSdk=false', '-p:UseLocalCSweetMemory=false', '-p:UseLocalCSweetWorkManagementContracts=false'
dotnet restore CSweet.Agent.Director.VideoGame.slnx --configfile $config --packages $packages @flags
dotnet test -c Release --no-restore --filter Category=AgentHarness @flags
```

Both 20-run samples used .NET SDK 10.0.401 on `DESKTOP-1LDJV2J`. Each run alternated the wording of the stale-brief diagnostic, built incrementally and ran both filtered tests with a TRX logger. Timing includes the edit, build, test process and result file. The p95 is the 19th sorted observation of 20. A single before-and-after sample does not establish the cause of the timing difference.

The original authoring measurement used one Codex author already familiar with the harness, Director `aad24bc`, and the then-local prototype SDK. That prototype reused version `5.41.0` independently of the subsequently released SDK. The follow-up uses SDK commit `308b79e`, which includes the released SDK changes. New-maintainer onboarding time remains unmeasured.

For setup size, compare `PitchEscalationTests.cs` with the pre-pilot test on Director `0a155fc` (identical to `aad24bc` for this file). Count added or replaced nonblank lines from scenario arrangement through agent configuration, plus those inside the shared test fixture, including braces. The baseline supplied 53 existing fixture lines. Delivery, outcome assertions, package wiring and documentation fall outside the count. There are zero new seed files. The original sample's serialized Producer review was 545 bytes within a 1,891-byte coordination request.
