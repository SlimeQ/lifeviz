# Sharing Scenes

A `.lifevizlayers.json` export only stores *paths* to your media, so it breaks on someone else's PC. To send a whole scene to another person, use **Share...** in the Scene Editor. It writes one `.lifevizscene` file containing the scene and a copy of every local media file it uses.

## Sending a scene

1. Open the **Scene Editor** and press **Share...** (next to **Save...**). It packages the scene shown in the editor, including unapplied edits when Live Mode is off, as **Save...** does.
2. If the scene captures a **Window**, LifeViz lists those layers and asks whether to leave them out, since the windows only exist on your PC. **Yes** (recommended) removes them from the shared copy. **No** keeps them, and they appear as missing inputs on the other PC unless the same window is open there. **Webcam** layers are kept and switched to **Any camera** in the package, so they use the recipient's camera automatically ([Webcam Layers](Webcam-Layers.md)). Your own scene is not changed either way.
3. Choose where to save. The dialog title shows how many media files are included and their total size.
4. When the package is written, LifeViz lists anything it could not include and offers to show the file in File Explorer. Send that one file by any method: a USB stick, a cloud drive, or a file-transfer service.

### What goes in the package

| Bundled | Kept as a reference (not bundled) |
| --- | --- |
| Image, GIF and video layers | The bundled starter loop (every install has it) |
| Video Sequence and AutoClip files, including per-file AutoClip overrides | YouTube links (they stream online) |
| Movie Playlist movies and external `.srt` subtitles | Bundled MilkDrop presets (every install has them) |
| Custom MilkDrop presets (`.milk` files outside the bundled library) | Window layers, if you kept them. Webcam layers become **Any camera** |

A file referenced several times is stored once. Files with the same name from different folders get distinct names. A file that is missing when you share is reported and keeps its original path.

Media is stored without recompression, so the package is about as large as the media itself, and writing it takes about as long as copying the files. The **Share...** button shows progress, and the editor stays usable. The package is written to `<name>.lifevizscene.partial` and renamed when complete, so a failed export never leaves a half-written `.lifevizscene` file. If LifeViz closes mid-export, a leftover `.partial` file is safe to delete.

The scene keeps all its settings: layer stack, sim groups, project render settings, animations, AutoClip and playlist settings. It also keeps Movie Playlist resume bookmarks.

## Opening a shared scene

1. Open the **Scene Editor** and press **Load...**. The default filter shows both `.lifevizlayers.json` projects and `.lifevizscene` packages.
2. Choose the `.lifevizscene` file. The **Load...** button shows unpacking progress.
3. The scene loads like any other project: with Live Mode on it applies immediately, and with Live Mode off it becomes an unapplied draft for **Apply**. Your previous scene is checkpointed in **Recover...** first.
4. If the sender's scene used things that are not in the package, LifeViz lists them.

Media is extracted to `%LOCALAPPDATA%\lifeviz\shared-scenes\<package name>-<id>\media\`, and the loaded scene points at those files. Autosave and later **Save...** exports reference that folder. **Do not delete it while you use the scene.** Opening the same package again reuses the folder when the extracted files are intact. Extraction goes to a temporary folder that is renamed once complete, so an interrupted import is redone next time.

Packages are validated before anything is written. Entries that would extract outside their folder are rejected, as are packages from a newer package format. The scene inside must pass the same checks as a normal project load.

## Package format

A `.lifevizscene` file is a standard zip archive:

- `lifeviz-package.json`: format version, a package ID (used to name the extraction folder), the package name, the LifeViz version that wrote it, the list of media entries, and the "not included" notes.
- `scene.lifevizlayers.json`: an ordinary scene project, with bundled media referenced by relative `media/...` paths.
- `media/...`: the media files, stored uncompressed.

You can open it with any zip tool to inspect or extract the media manually.

## Validation

`--smoke-test scene-package` builds a scene that uses every kind of media reference and exports it. It deletes the original media, imports into an isolated folder, and checks that every path is relinked to the extracted copy. It also covers name collisions, duplicate references, the references that stay unbundled, missing/live-input reporting, leaving out nested live inputs, re-import reusing an in-use extraction, and rejecting a path-traversal package. See [Build & Install](Build-and-Install.md#validate-scene-persistence).
