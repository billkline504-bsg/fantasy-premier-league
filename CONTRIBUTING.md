# Contributing

This repository is documentation- and mockup-only today, so "contributing" mostly means editing the AIDLC pipeline documents in `docs/aidlc/` and the HTML mock-up in `mockup/`. Both follow a strict append-only versioning discipline — never edit a previously-published version in place. This file explains that discipline so it stays consistent as more people (or sessions) touch it.

## AIDLC documents (`docs/aidlc/`)

See `docs/aidlc/README.md` first for the folder structure and pipeline stages (domain spec → requirements → architecture → epics/backlog → user stories).

### The core rule: never edit a published version

Every document is a series of immutable, numbered files: `Fantasy EPL League Manager — <Document Type> v{N}.md`. Once `vN` exists, it is never edited again. A change always starts by copying the current highest version to `v{N+1}` and editing the copy.

### Making a change to the BRD (Business Requirements Document)

1. Copy the highest-numbered BRD to the next version number.
2. Update the title block: the `##` heading, `**Version:**` line.
3. Add the new/changed business rule(s) as new `BR-###` entries (next sequential number after the highest existing one), inserted **inline in the most relevant existing section**. Never renumber or rewrite an existing `BR-###` — if an old rule needs correcting, add a new rule that supersedes or clarifies it and say so explicitly.
   - If the change is a genuinely new topic with no existing section to extend, give it its own new top-level section instead, placed at the end of the document (not renumbered into the middle) — see e.g. the EPL League Table section for a template.
4. Add a row to the **Decision Register** (`DEC-###`, next sequential number) summarizing the decision in one line.
5. Add an entry to **BRD Version History** describing what changed and, importantly, *why* — what gap or review surfaced it.
6. Append a new trailing section (`# {next section number}. Version {N} — {short title}`) that restates the new rule(s) with fuller rationale — what was missing before, what triggered the fix, what it resolves. This is the section a future reader opens to understand the *reasoning*, not just the rule text.
7. Update the **Final Baseline** line to point at the new version.

### Making a change to a downstream document (Architecture, Backlog, Feature Behavior Specs)

Same copy-then-edit rule applies. Additionally:

- Update the **Inputs** section to cite the new upstream version(s) it was built against, and describe in that same line what changed because of them (this project's convention is a single running sentence in the Inputs bullet, not a separate changelog block, for Architecture/Backlog — but Feature Behavior Specs use an explicit "Added in vN" paragraph appended at the end of the doc; follow whichever pattern the document you're editing already uses).
- **Prefer extending an existing feature/entity over creating a new one.** If a capability already has an owning `F-###.#` feature or an owning domain entity, extend its acceptance criteria / trace list / fields rather than inventing a new one. Only create a new `F-###.#` when nothing currently owns the capability at all (and say so explicitly — this is usually itself worth a note, since it means an existing feature's scope was incomplete).
- When a Feature Behavior Spec's acceptance criteria change, append an **"Added in vN (...)"** paragraph at the end of the document explaining what triggered the change — a mock-up review surfacing a gap, a BRD rule that was added but never propagated downstream, etc.
- If a change adds or re-keys a domain entity, update every place that entity is described: the module map, its own aggregate/entity definition, the API resource table if it has an endpoint, and the version history — not just the section you were originally looking at.

### Cascade discipline (the most commonly missed step)

A new or changed `BR-###` almost never stays confined to the BRD. Work outward and check each of these before considering the change done:

1. **BRD** — the rule itself, plus Decision Register and Version History.
2. **Architecture** — does this change a domain entity's shape, an API endpoint, or a bounded context's responsibilities?
3. **Epic and Feature Backlog** — does an existing feature's trace list (`Traces to`) or dependency list (`Depends On`) need a new `BR-###` citation?
4. **Feature Behavior Specs** — do that feature's Given/When/Then acceptance criteria still accurately describe the behavior?
5. **Mock-up** (`mockup/index.html`) — does the illustrative UI need to change to match, or did the mock-up already show the correct behavior and the docs just needed to catch up? (Both directions have happened in this project's history — check which one you're doing.)
6. **Top-level `README.md` doc badge** — if the change bumped the BRD's version, update the `docs-BRD%20vX.Y-blue` badge at the top of `README.md` to match, and update the current-baseline table in `docs/aidlc/README.md` for whichever document(s) you bumped.

When a change touches a shared entity or concept (e.g., re-keying an entity from `UserId` to `LeagueMembershipId`), **audit every sibling feature that reads or writes that entity**, not just the one you started with — it's easy to fix the feature that owns the concept and miss the features that consume it. When in doubt, grep the whole `docs/aidlc/` tree (latest version of each document) for the entity/rule name before calling a change complete.

### Folder structure for new artifact types

If the pipeline produces a new kind of artifact not yet represented (an OpenAPI spec, database migrations, a test strategy, etc. — see `docs/aidlc/README.md`'s "Not yet produced" section), give it the next sequential numbered folder (`05-...`, `06-...`) under `docs/aidlc/`, following the same naming and versioning conventions as the existing folders.

## Mock-up (`mockup/index.html`)

- `index.html` is always the live, current file — the one referenced when previewing the app.
- **Before making any edit to `index.html`, copy its current contents to `index.v{N}.html`**, where `N` is the next integer after the highest existing `index.vN.html` in that folder (check with a directory listing — don't assume). This snapshot captures the state immediately *before* the change.
- `index.vN.html` files are write-once historical snapshots and are never edited after creation.
- This convention exists because a past mock-up refactor (done without following it) silently dropped two previously-built features, which went unnoticed until a later review. Snapshotting before every edit is what makes that kind of regression detectable and recoverable.

## General

- Don't delete or renumber a previously-published version of anything in this repository. If something was wrong, the fix is a new version that says so, not a rewrite of history.
- Keep the "why," not just the "what," in every version-history entry or changelog note — the next person (or session) reconciling the mock-up against the docs, or one document against another, relies on that rationale trail to know what's still current and what's been superseded.
