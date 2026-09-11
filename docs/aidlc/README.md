# AIDLC Documentation

This folder holds every artifact produced for the Fantasy EPL League Manager through its AIDLC (AI-driven documentation/development lifecycle) pipeline — the sequence of documents that takes the product from a domain specification through to implementation-ready feature specs. Each subfolder is one stage of that pipeline, numbered so they sort in the order the pipeline actually runs.

## Pipeline stages

```
00-domain-specification → 01-requirements → 02-architecture → 03-epics-and-backlog → 04-user-stories → 05-api-specification → 06-database-migrations → 07-testing-strategy → 08-implementation-tasks
```

| Stage | Produces |
|---|---|
| `00` original spec | The seed domain/requirements specification |
| `01` requirements | BRD, `BR-###` business rules |
| `02` architecture | Domain model, APIs, aggregates |
| `03` epics & backlog | Features (`F-###.#`), `BR-###` traceability |
| `04` user stories | Acceptance criteria per feature, by phase |
| `05` API specification | OpenAPI: request/response DTOs, validation |
| `06` database migrations | Physical PostgreSQL DDL, per Architecture §8 |
| `07` testing strategy | `BR-243`–`BR-254`/`AP-###` → test type/layer map |
| `08` implementation tasks | 71 sequenced engineering tasks — last stop before code |

| Folder | Stage | Contents |
|---|---|---|
| `00-domain-specification/` | Origin | The original `EPL_Fantasy_AIDLC_Domain_Requirements_Specification` (.docx) — the seed document every later artifact traces back to. Single version; never reissued. |
| `01-requirements/` | Requirements | The **Business Requirements Document (BRD)** — the authoritative source of every `BR-###` business rule, the Decision Register (`DEC-###`), and the version-by-version rationale for every change. This is the document every other stage cites. |
| `02-architecture/` | Architecture & Domain Model | Bounded contexts, aggregates/entities, domain events, logical data model, and API surface — the technical design that implements the BRD's rules. |
| `03-epics-and-backlog/` | Epics & Backlog | The Domain Spec's epics (`EPIC-###`) refined into a prioritized, dependency-ordered feature list (`F-###.#`), each tracing to the `BR-###` rules it implements, plus a recommended build sequence. |
| `04-user-stories/` | User Stories / Feature Behavior Specs | Given/When/Then acceptance criteria for every feature in the backlog, grouped into the four build phases below. |
| `05-api-specification/` | API Specification | The **OpenAPI specification** — every REST endpoint, request/response schema, and validation rule implied by Architecture §9 and the Feature Behavior Specs. |
| `06-database-migrations/` | Database Migrations | Physical, runnable **PostgreSQL DDL** implementing Architecture §8's logical data design — a versioned strategy document plus a `migrations/` folder of ordered, immutable `V{NNN}__*.sql` files. |
| `07-testing-strategy/` | Testing Strategy | Maps every `BR-243`–`BR-254` coverage category and `AP-001`–`AP-010` principle to a test type and layer (Architecture §14's `UnitTests`/`IntegrationTests`/`ApiTests` projects), plus a genuinely executable `db-tests/` database-invariant suite and a live two-connection concurrency race — both already run and passing against a real PostgreSQL instance. |
| `08-implementation-tasks/` | Implementation Task Breakdown | Every one of the Backlog's 57 features, decomposed into 71 total engineering tasks (57 feature tasks + 14 foundational/cross-cutting tasks) and resequenced into a single dependency-correct build order, each citing its Architecture aggregate, migration file, OpenAPI operation(s), `BR-###`/`AP-###` traceability, and test coverage. This is the final AIDLC artifact — the last stop before actual application code. |

### `04-user-stories/` phase subfolders

The backlog's features are built in four phases (see `03-epics-and-backlog`'s recommended build sequence); each phase gets its own Feature Behavior Specifications document:

| Subfolder | Phase | Epics covered |
|---|---|---|
| `phase-1-foundation/` | Phase 1 — Foundation | Identity & Auth, User Profile & FantasyTeam, League & Season Management, EPL/FPL Data |
| `phase-2-draft-and-squad/` | Phase 2 — Draft & Squad | Initial Draft, Secondary & Replacement Draft |
| `phase-3-weekly-gameplay/` | Phase 3 — Weekly Gameplay | Gameweek Roster, Scoring Engine, H2H Competition, Standings & Tie-Breaks |
| `phase-4-operations/` | Phase 4 — Operations | Corrections & Administration, Notifications, Reporting & History |

## Versioning convention

Every artifact is **append-only**: a change never edits a prior version's file — it's saved as a new `v{N}.md` (or `v{N}.docx`) in the same folder, one integer higher than the previous version. The highest version number in a folder is always the current baseline for that stage. Each document's own "Version History" section (and the BRD's Decision Register) explains what changed and why between versions, so the full rationale trail is preserved rather than overwritten.

Because later stages are generated from earlier ones, a given version of a downstream document usually names which upstream version it was built against in its own **Inputs** section (e.g., an Architecture version cites the BRD version it reconciles against) — that's the fastest way to check whether a document is still current with the latest BRD.

## Current baseline (highest version in each folder)

| Stage | Latest version |
|---|---|
| Requirements (BRD) | v1.17 |
| Architecture and Domain Model | v1.15 |
| Epic and Feature Backlog | v1.10 |
| Feature Behavior Specs — Phase 1 | v1.3 |
| Feature Behavior Specs — Phase 2 | v1.3 |
| Feature Behavior Specs — Phase 3 | v1.3 |
| Feature Behavior Specs — Phase 4 | v1.6 |
| API Specification (OpenAPI) | v1.0 |
| Database Migration Strategy | v1.0 |
| Testing Strategy | v1.0 |
| Implementation Task Breakdown | v1.0 |

## Related, not in this folder

- **`../../mockup/`** — an illustrative HTML mock-up of the application. It isn't an AIDLC pipeline artifact itself, but several BRD versions in `01-requirements/` were driven by gaps a mock-up review surfaced (each such version's changelog entry says so explicitly), and the mock-up is kept versioned (`index.v{N}.html`) the same way these documents are.

## Not yet produced

`08-implementation-tasks/` completes every item on Architecture's "Recommended Next AIDLC Artifacts" list (§16). What comes after this pipeline is not another AIDLC document — it's `EplFantasy.*` application code itself, built task-by-task per `08-implementation-tasks/`'s sequence.
