# Scene Saving and Recovery

LifeViz autosaves the applied scene to `%APPDATA%\lifeviz\config.json`. A graphics failure must not turn resource cleanup into an empty saved scene. Shutdown captures a complete scene snapshot before closing the editor, stopping recording, or disposing source sessions, then prevents further scene snapshots during teardown. The background writer drains that immutable snapshot after media cleanup.

## Autosave and failure handling

- Edits are coalesced in a 500 ms window. Continuous slider movement does not restart the timer indefinitely. Identical snapshots are skipped; at most one write runs at a time, with the newest pending revision retained separately.
- Writes use a unique temporary file in the destination directory, flush its contents to disk, preserve the previous revision, and atomically replace the destination. A failed replacement leaves the existing file intact. Scene Editor **Save...** uses the same storage implementation.
- Each destination keeps its immediate predecessor at `<filename>.bak` and up to 40 automatic revisions in `<filename>.history`. Autosave history therefore lives in `%APPDATA%\lifeviz\config.json.history`. Timestamps in filenames are UTC. Revisions contain scene settings and media references, not copies of videos or simulation frame buffers.
- Transient write failures retain the pending snapshot and retry every two seconds without requiring another edit. The first failure displays a warning. The main context menu shows whether autosave is up to date, pending, retrying, or paused.
- An exclusive file lock and comparison with the previously loaded/written contents prevent a second LifeViz session from silently overwriting another session's changes. On conflict, export your work with **Save...** before restarting. The lock file can remain on disk; only an open exclusive handle holds the lock.
- Shutdown waits up to five seconds for the writer after media teardown and makes a best-effort separate `unsaved-<session>.json` recovery copy if a revision remains uncommitted. This cannot guarantee saving to a full, disconnected, or stalled disk. Unapplied editor changes with **Live Mode** off are separately checkpointed as `editor-draft-<session>.json` during owner shutdown. These explicit recovery copies are not chosen automatically at startup or pruned with automatic history.

## Loading safely

Startup validates the primary file, then tries its `.bak` and newest valid automatic history if the primary is missing or corrupt. A deliberately empty `Sources` array is valid and does not trigger recovery. Damaged predecessor contents are retained as `.invalid` files when a repaired revision is committed. Files from a newer autosave schema pause loading rather than falling back and overwriting them with an older schema.

If no revision loads, or some saved inputs cannot be restored, autosave pauses and explains why. The original scene stays on disk while a default or partial scene is displayed. Reconnect the missing windows, cameras, or media and restart, or explicitly load/apply a complete replacement. Export new work to a separate file while autosave is paused. A startup recovery flag may reduce display settings for launch, but it does not eagerly rewrite the scene before its sources load.

## Recovering a previous scene

**New Project** checkpoints the current applied scene before replacing it with the bundled logo demo and one Life Sim. With Live Mode off it also preserves the editor draft at `editor-draft-<session>.json`. It resets immediately in either mode and does not overwrite named project exports or reset recording-device/folder preferences. Demo media references are portable across install versions and Windows accounts.

1. Open the **Scene Editor** and choose **Recover...**. This opens the autosave history directory. **Load...** can also open an autosave JSON or an exported `.lifevizlayers.json` project; choose the JSON/all-files filter to browse `.bak` files.
2. Choose a revision by its timestamp. With **Live Mode** off, inspect it in the editor before pressing **Apply**. With Live Mode on it applies immediately. The current complete scene is checkpointed before replacement.
3. If all sources apply successfully, autosave resumes (unless a separate session conflict still needs resolution). Export a named project with **Save...** for a lasting checkpoint.

Recovery through the editor restores source structure and project rendering controls. Machine preferences such as recording destination and audio device selection remain those of the running session. Keep the original autosave file if you also need those preferences. Export files, their history directories, `.invalid` evidence, unapplied drafts, and conflict recovery copies can be managed manually; they are not media caches.

Old releases did not maintain this revision history. Updating cannot reconstruct a scene that was already overwritten without a surviving backup or export.

## Validation

`--smoke-test scene-persistence` checks replacement/backups, deliberate empty saves, corrupted primary and backup recovery, stale-writer rejection, history retention, project import, locked-file retry without another edit, unavailable-input protection, failed-load protection, editor toolbar layout, and a dirty shutdown with an in-flight writer. `config-save-coalescing` separately covers duplicate saves and an `A -> B -> A` race. See [Build & Install](Build-and-Install.md) for commands. These use isolated smoke data; they do not induce a real graphics-driver fault or a physical power loss.
