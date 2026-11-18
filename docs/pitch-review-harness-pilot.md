# Director pitch-review harness pilot

The second agent repository ran a real Producer-to-Director pitch-review turn against the packed `CSweet.Agent.SDK` and `CSweet.Agent.Testing` 5.41.0. Both packages restored byte-for-byte from the local feed into a fresh cache. The only project reference resolved by the test project was the Director itself. Headquarters and model endpoints were unnecessary.

| Adoption measure | Target | Observed |
|---|---|---|
| Time from starting repository inspection to both cases passing | ≤30 min | 4 min 50 s, including worktree creation, package wiring, restore and first build |
| New or edited nonblank setup lines, including fixture helpers | ≤30 | 45 at first green (29 in the test, 16 in the fixture). Final version used 69 (30 and 39). |
| Warm edit through incremental build and both verdicts | p95 ≤15 s | p95 6.81 s, median 2.68 s across 20 edits |

The pilot reuses the Director’s existing audio-licensing scenario. Given the exact Producer brief, the Director runs its scripted production-baseline review, creates one pending owner decision bound to the session, Producer, pitch, document revision and hash, and submits a blocked turn referencing that decision. With only the submitted hash changed, it blocks before inference and creates zero decisions. Removing the revision-hash check in a scratch copy made only the stale case fail. Restoring it made both pass. [Pitch-review tests](../tests/CSweet.Agent.Director.VideoGame.Tests/PitchEscalationTests.cs)

## How the pilot was measured

- One Codex author familiar with the harness worked from Director commit `aad24bc` and the previously packed SDK. The original escalation fixture supplied 53 nonblank lines of existing test infrastructure. A first-time human maintainer's onboarding time remains unmeasured.
- The setup count uses added or replaced nonblank C# lines from the scenario arrangements through configuring the agent, plus all added or replaced nonblank lines in the shared fixture. It includes braces and fixture validation. The actual delivery, outcome assertions, package wiring and documentation are outside that count. The final fixture checks document identity, reads state by exact key, and persists the scripted review. There are zero new seed files. The serialized Producer review was 545 bytes inside a 1,891-byte coordination request in the measured sample.
- Warm runs used .NET SDK 10.0.401 on `DESKTOP-1LDJV2J`. In a standalone scratch copy, each run alternated the wording of the stale-brief diagnostic, built incrementally, then executed `dotnet test -c Release --no-restore --filter Category=AgentHarness`. Time includes the build, test process and result file. The p95 is the 19th sorted observation of 20.

The setup budget was already exceeded on the first passing run. The first shared fixture worth extracting is a store for keyed operating-state reads and writes. Scripting a named model response and collecting the SDK's submitted turn are the next places to reduce repeated setup. Keep the pitch, project and revision assertions in the Director test.
