# Backup feature worklist

Goal: make backup/import/restore safe, predictable, and actually full-fidelity.

## In scope now

- [x] Fix overwrite-mode duplicate creation caused by recursive folder restore also restoring/creating descendants individually.
- [x] Restore manager-only metadata from app backups during import/restore.
- [x] Keep metadata-only backup changes server-side and avoid enqueueing unnecessary Brave sync commands.
- [x] Validate imported app-backup graphs before applying destructive changes.
- [x] Add API integration tests for overwrite dedupe, metadata restore, and invalid parent references.

## Next backup edge cases to implement

- [ ] Handle duplicate IDs inside imported app-backup JSON with user-facing diagnostics in the UI preview.
- [ ] Detect and explain cyclic parent relationships and other malformed trees before the user confirms import.
- [ ] Verify and normalize sibling ordering when imported positions are sparse, duplicated, or negative.
- [ ] Decide and implement the correct destination behavior for Chrome/Brave exports whose top-level nodes currently have no tracked-root parent.
- [ ] Add safe handling for partial imports that reference existing parents but omit ancestors.
- [ ] Decide how protected/root-like nodes should be treated when they appear in imported payloads.
- [ ] Add restore behavior tests for mixed folder/bookmark trees, deleted-node resurrection, and repeated imports of the same snapshot.

## Tree handling improvements

Why this matters: backup/import/restore must understand bookmark hierarchy, not just a flat list of rows. Most destructive bugs come from wrong parent mapping, replaying descendants twice, or treating browser-export roots like normal folders.

### Priority 1: server-computed dry run and tree diagnostics

- [ ] Add a server-side dry-run / preview endpoint that computes the final import plan before destructive apply.
- [ ] Return tree-aware counts in preview: creates, updates, restores, deletes, skips, metadata-only updates.
- [ ] Return validation diagnostics with node titles/IDs for duplicate IDs, missing parents, self-parenting, cycles, and unsupported type changes.
- [ ] Show whether each restored folder owns a recursive subtree so descendants are not also reported as separate creates/restores.

Use cases:
- User wants to confirm overwrite will replace one folder subtree instead of duplicating 300 child bookmarks.
- User imports a hand-edited JSON file and needs actionable tree errors before pressing confirm.

### Priority 2: top-level destination mapping for browser exports

- [ ] Detect external/browser-export payloads whose top-level nodes are not already attached to tracked roots.
- [ ] Define import behavior for top-level Bookmarks Bar / Other Bookmarks style roots.
- [ ] Let the preview show the chosen destination tracked root or wrapper folder before import runs.
- [ ] Decide whether root-like imported nodes are mapped, ignored, or flattened to children-only imports.

Use cases:
- User imports Brave/Chrome export JSON and expects the folders to land under a chosen tracked root, not create duplicate pseudo-roots.
- User migrates from browser-native bookmarks into the app and needs predictable root placement.

### Priority 3: partial subtree import rules

- [ ] Support importing a subtree whose direct parent already exists in the DB even if the payload omits older ancestors.
- [ ] Distinguish valid partial imports from invalid orphaned nodes.
- [ ] Surface when overwrite is unsafe because the payload is only a partial branch, not a complete tree.

Use cases:
- User restores only Anime/Seasonal/2026 into an existing Anime folder.
- User exports one folder for sharing and later re-imports that subtree without wanting the whole root replaced.

### Priority 4: stable sibling ordering and idempotency

- [ ] Normalize negative, duplicate, or sparse sibling positions per parent during import.
- [ ] Make repeated imports of the same snapshot converge to no-op behavior where possible.
- [ ] Add explicit tests for repeated import of the same snapshot and deleted-node resurrection under folders.

Use cases:
- User imports the same snapshot twice after a network hiccup and should not get duplicated folders/bookmarks.
- Imported children should appear in stable order instead of reshuffling every restore.

### Priority 5: protected/root-like node handling

- [ ] Decide whether protected folders can be overwritten, remapped, or only used as destination anchors.
- [ ] Prevent imported root-like nodes from generating invalid create/move behavior.
- [ ] Keep browser-managed roots structural while still restoring their descendants.

Use cases:
- Imported payload contains Bookmarks Bar as a normal folder node.
- Root nodes should anchor the import, not be duplicated as regular content.

## Backup QoL improvements

- [ ] Add richer import preview summaries: creates, updates, restores, deletes, skips, validation warnings.
- [ ] Show validation errors inline on the Backups page instead of only generic snackbar text.
- [ ] Add “dry run” / preview endpoint so the server, not the client, computes the final import diff.
- [ ] Add snapshot manifest details such as source format, item counts by type, and optional notes.
- [ ] Add duplicate-detection reporting for merge mode based on folder path + normalized URL/title heuristics.
- [ ] Add an option to restore metadata only without touching Brave-synced browser fields.
- [ ] Add backup retention / cleanup controls for local server snapshots.
- [ ] Add tests for large imports and file-size/error-limit behavior.

## Parallel workstreams

- [ ] Workstream A: design and implement server dry-run preview + tree diagnostics.
- [ ] Workstream B: define destination mapping rules for browser exports + partial subtree imports.
- [ ] Workstream C: define idempotency, sibling ordering normalization, protected-root handling, and test coverage.

## Validation checklist

- [ ] App backup restore in overwrite mode does not duplicate folders or bookmarks.
- [ ] Re-importing the same app backup is idempotent enough to avoid accidental duplication.
- [ ] Metadata round-trips through create-backup -> import-backup.
- [ ] Invalid imports fail before destructive apply.
- [ ] Browser-facing commands stay limited to title/url/move/create/delete/restore changes only.
