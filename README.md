# Video Game Director 0.2.9

Approved-project requests retain the exact pitch and project context through SDK tracked delegation. Creative direction goes to the Creative Director; staffing, technical coordination and sprint delivery go to the Producer. Platform administration installs and certifies build capacity. Decision replies are reconciled with the recorded widget decision and never become creative assignments or new approvals.

Route post-pitch creative work through SDK tracked delegation. Require exact Naomi creative review before production-brief approval, resume durable coordination after review, and publish production profile 8 with Creative Director task eligibility. Retain exact CEO pitch approval before leadership hiring.

CEO communication and material business decisions belong to the Video Game Director. Specialists preserve independent discipline evidence; no role may manufacture another role's acceptance.


`CSweet.Agent.Director.VideoGame` / `com.csweet.video-game-director` version **0.2.9** is the business-wide video game leader, reporting to the CEO. The Chief of Staff recommends this role first for a game studio.

## Responsibilities and approval flow

1. Work with the CEO on the high-level concept, audience, platforms, technical constraints, budget, non-goals and success criteria.
2. Create an official game pitch and submit its exact revision for CEO approval. Delegated involvement never bypasses pitch approval.
3. Recommend one shared Creative Director and one shared Producer, both reporting to the Director. Reuse the studio leadership and team across approved games.
4. Review the Producer's project-foundation proposal against the accepted pitch. Request corrections or escalate material changes. The Producer owns delivery and boards; the Director remains the formal studio-team lead and resource approver.
5. Commission the Creative Director's GDD, review its exact revision, and request bounded revisions when necessary. Pass the accepted pitch and GDD to the Producer for production planning.
6. Review subordinate staffing, planning, milestones, risks and decisions throughout the portfolio. Publication and launch retain their separate human approval gates.

Each concept has independent durable state. Project-scoped work uses its explicit workstream; a conversation with multiple games asks for a game selection when ambiguous. Background tasks retain their original concept or project context. Coordination, document creation and decisions use persisted evidence and stable idempotency keys.

## CEO reporting

After the first official pitch is approved, attention review asks for cadence and format, one question at a time. Reporting setup never replaces the initial game-discovery question. Supported cadences are daily, weekdays, weekly (Friday), milestones, and on request. Timed reports require a local time and timezone, handle DST, and coalesce missed occurrences. Formats are messages, official report documents, or both. Enter `change reporting preferences` to update the contract or `report now` for an immediate update. Reports distinguish missing or old subordinate evidence from current verified progress.

## Contract and authority

The manifest provides `game-direction.game-vision.v1` and `management.check-in.v1`, subscribes to lifecycle, communication, coordination, artifact, workforce and management events, and ships immutable `video-game-production.v2` profile version 7. It requests brokered model, state, documents, personal agenda, project, team, resource-change and management capabilities. These declarations request authority; the host still validates grants and exact approval bindings. No credentials or direct web access are requested.

## Verification and release

```powershell
dotnet test -c Release -p:UseLocalCSweetAgentSdk=false -p:UseLocalCSweetWorkManagementContracts=false -p:UseLocalCSweetMemory=false
dotnet run --project src/CSweet.Agent.Director.VideoGame -c Release --no-build -- --self-test
dotnet pack src/CSweet.Agent.Director.VideoGame -c Release --no-build --no-restore -o .artifacts/packages
```

CI verifies extension hashes, public package dependencies, tests and identity. `agent-release.yml` builds the reviewed `.csab` release through `scripts/pack-csab.py`. Deploy through published releases and Installed Agents. This is a fresh-install role split; no persisted Creative Director state is migrated and no installed payload is patched.

## Initial pitch recovery (0.1.1)

The Director saves the CEO source and pitch task before generating the official pitch. The task
can resume after a provider/document interruption without asking the CEO to repeat direction;
cached drafts and stable document keys prevent duplicate documents. A current authorized source
read is required. Exact CEO document approval still gates the shared Creative Director and Producer
recommendations. Attention review wakes legacy 0.1.0 tasks that waited for another chat message.
Reporting questions carry real conversation-message anchors, and portfolio review tasks retain
both source conversation and message references.

## Sequential onboarding and upgrade recovery (0.1.3)

`HandleOnboardedAsync` asks only about collaboration. Its answer leads to
`InitialGameDirectionPrompt`: the kind of game, player experience, deployment platforms,
technical choices, budget and other constraints. `PersistPitchAsync` creates and submits
the Official Game Pitch to the CEO; an accepted exact revision gates `EnsureStaffingTodoAsync`
and the shared Creative Director/Producer proposal. Specialist staffing follows the approved
requirements and the Producer's planning, rather than an unconditional catalog roster.

The platform's `ExecutiveDecisionService.CreateAsync` supersedes an agent's previous ordinary
question in the same conversation. Releases 0.1.0–0.1.2 asked reporting immediately after
collaboration, superseding discovery before the CEO could answer. `ContinueDirectorOnboardingAsync`
repairs that saved state once during attention review or after the last reporting answer.
It preserves completed reporting preferences, lets an unfinished reporting choice complete,
and restores collaboration or game discovery according to the saved involvement preference.
`OnboardingFlowVersion` and stable message/question keys prevent repeated upgrade prompts.
The same method starts reporting only after pitch acceptance. No new capabilities are needed.

`PitchContinuationTests` models the platform's question supersession and verifies fresh
onboarding in all involvement modes, existing-install recovery, exact CEO document approval,
and the subsequent two-role hiring proposal. Update through Installed Agents; no rehire or
manual database repair is needed.

## Identity

Marcus Vale is the Video Game Director. His portrait uses the roster’s softly sculpted clay-like style, with a midnight-teal blazer and teal-gray background. Branding assets live in `assets/branding/`; the manifest links to published repository assets and the app includes an offline copy.

Director follow-through resolves business scope, resources and portfolio priorities. Authenticated project leads use SDK interactions; unsupported raw coordination cannot create a CEO decision. SDK delegation cancellation stops tracking only and does not withdraw another owner's executable work.
