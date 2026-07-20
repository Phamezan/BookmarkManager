---
name: orchestrator
description: Orchestrate implementation work by delegating to subagents running cheaper models (Sonnet/Haiku) while the main session writes specs, picks the model, monitors and steers mid-run, and verifies the result. Use this skill at the START of any non-trivial implementation task in this repo — new features, multi-file changes, bug-fix-plus-tests, doc generation — before writing any code yourself. Also use when the user says "orchestrate", "delegate", "use a subagent", or asks for work to be built by a cheaper/worse model. Not for one-line edits or pure questions.
---

# Orchestrator

The main session is the architect, reviewer, and quality gate — it should not type the implementation. Subagents on cheaper models do the building. This works because **spec detail does the heavy lifting**: a Sonnet builder with an exact spec beats an unguided strong model, and costs less. Your job is to make the spec so precise the builder can't wander, then verify as if the builder were a stranger's PR.

## Workflow

1. **Investigate first, yourself.** Read the files the task touches before writing the spec. A spec written from memory produces a subagent that "discovers" the wrong architecture. Use `graphify query` / Grep / Read in the main session — investigation is cheap here and disastrous to delegate poorly.
2. **Write the spec** (see Spec contract below).
3. **Pick the model** (see Model selection).
4. **Spawn in background**, monitor, steer.
5. **Verify everything yourself.** Then optionally spawn a reviewer.
6. Run `graphify update .` after code changes land.

## Model selection

| Task shape | Model | Why |
|---|---|---|
| Mechanical, bounded, 1–2 files: renames, ctor-param threading, fake updates, formatting | `haiku` | Spec fully determines output; intelligence wasted |
| Feature build, new service/component, multi-file with tests | `sonnet` | Needs local judgment within the spec's rails |
| Review pass over a builder's diff | `haiku` (checklist review) or `sonnet` (semantic review) | Match to what the review must catch |
| Architecture decisions, spec writing, final verification | main session — never delegated | This is the orchestrator's actual job |

When the user asks to test orchestration explicitly, name the model in the `model` param; otherwise default builders to `sonnet`, mechanical chores to `haiku`.

## Spec contract

Every builder prompt must contain:

- **Exact file paths** to create/modify — never "find the service that…".
- **Code-level anchors**: existing signatures, the pattern file to imitate ("same pattern as `LibraryProviderRegistry`"), snippets for anything with a known right answer.
- **Repo constraints, restated** — subagents do not inherit session memory. Always include: never launch `BookmarkManager.Api` (user runs it); never run full `dotnet test BookmarkManager.sln` — scoped `--filter "FullyQualifiedName~…"` only; build/test with `-c Release` when the API may be running (Debug outputs are file-locked, MSB3027); no mocking libraries; partial-class `TypeName.Concern.cs` split for large types; no commits.
- **Test expectations**: which test file, which scenarios, and any hard-won rules ("seed real punctuated titles, not sanitized ones").
- **Definition of done**: build command + test filter that must pass, and what to report back (files touched, test output tail).
- **Relevant skill content inlined or pointed at**: if a domain skill exists (e.g. `autotagging`), paste its relevant invariants into the spec — the subagent won't load it on its own.

## Monitoring & steering

Spawn with `run_in_background: true`. Between spawn and completion:

- Check progress via TaskOutput when a run is long; completion notifications arrive automatically — don't poll on a timer.
- **Steer with SendMessage** the moment output drifts: wrong file, wrong pattern, scope creep, full-suite test run. One early correction is cheaper than a redo. Continue an existing agent via SendMessage rather than respawning — it keeps context.
- If a builder hard-fails twice on the same point, stop steering and fix that piece in the main session; don't burn a third round-trip.

## Verification — the non-negotiable part

Subagents confidently report false claims. Confirmed failure mode: a doc subagent declared two symbols dead because it only searched the directories named in its spec. Therefore:

- **Read the actual diff/files**, not the subagent's summary of them.
- **Repo-wide grep every "unused / no callers / dead code" claim** before it reaches the user or a doc.
- **Re-run the scoped tests yourself** — don't accept "9/9 passed" as text.
- Check the spec's definition-of-done point by point; anything skipped gets a SendMessage follow-up or a main-session fix.
- Report to the user what YOU verified, distinguishing it from what the subagent merely claimed.

## Review pass (optional, for larger diffs)

After verification, a second subagent can review the first's diff — use `caveman:cavecrew-reviewer` for terse severity-tagged findings or a `haiku` general agent walked through the matching `.agents/commands/review-*.md` checklist (`review-autotagging-change.md`, `review-sync-change.md`). Triage its findings yourself; reviewers also hallucinate.

## Parallelism

Independent workstreams (e.g. API change + doc write) go in one message as parallel Agent calls. Dependent work stays sequential — a builder must not start against files another builder is mid-rewrite on.
