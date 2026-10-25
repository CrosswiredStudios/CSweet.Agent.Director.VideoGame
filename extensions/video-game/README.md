# Video-game agent extension

This is agent-owned implementation, not a C-Sweet platform contract or NuGet package. The authoritative source is in the Video Game Director repository. Each agent carries a versioned source snapshot so an isolated build needs no unpublished domain dependency or sibling checkout.

C-Sweet owns generic work assignments, coordination envelopes, evidence, permissions, and profile interpretation. This extension owns game terminology, typed payload helpers, role-specific planning, prompts, and the game-production profile. Wire type keys remain stable for existing workstreams and stored coordination artifacts. Typed helpers are compiled into each agent; they are not referenced by headquarters.

Reconcile source updates individually; tailored adapters must not be overwritten wholesale. Use `scripts/Sync-VideoGameExtension.ps1` in the owner repository to refresh local provenance or verify each snapshot with `-VerifyOnly`. The provenance file records the source files and their SHA-256 hashes. Updating extension source requires synchronizing consumers, updating affected agent versions, and testing clean package-only restores.
