# Movie Playlists

Use a **Movie Playlist** source for a continuous run of full movies in a fixed order. It plays each file once, advances at the end, and loops from the last entry back to the first. It has no AutoClip random start points, clip durations, delays, fades, or takeover scheduling.

## Authoring and navigation

1. Open the Scene Editor and click **Movie Playlist** under Add Root or Add Child (inside a layer Group). You can also use **Sources → Add Movie Playlist** in the right-click menu, including inside groups.
2. Select the new layer and use **Add Movies...**. Files append in the order returned by the file picker; use **Move Up / Move Down** to author the exact order. **Remove** removes the selected entry, keeping the file on disk. Duplicate movies are allowed as independent entries with their own subtitle settings.
3. Select a movie, enter a timestamp under **Jump to**, and click **Play from here** or press Enter. Accepted formats are seconds, `MM:SS`, and `HH:MM:SS`, including fractional seconds. A jump keeps the layer's current play/pause state; if paused, press Play to continue. Times beyond the movie duration clamp near its end.
4. **Use Current Position** selects the playing movie and copies its time into the jump field. The status underneath identifies the playing entry and subtitle state. The ordinary Pause/Play, Restart Sequence, seek slider, audio volume, fit, scale, compositing, and animation controls also apply. The seek slider covers the current movie rather than the entire playlist. Restart Sequence returns to the first entry at zero.

Playlist edits work in both Live and draft mode. With Live Mode off, Apply commits the authored list and subtitle settings; playback navigation requires Live Mode. Reordering entries or editing an inactive entry preserves the active movie and playback clock. Changing the active movie's subtitle settings reopens its decoder at the current time. Removing the playing entry starts its successor at zero (or the new last entry if the old one was last). Empty playlists remain valid blank layers and can be populated later.

New movie playlists default to Normal compositing, Fit framing, and Play Audio enabled. Master source-audio controls still apply, including Video Stack (Silent) analysis. AutoClip and ordinary video sequence defaults are unchanged.

## Subtitles

Each selected movie offers **Embedded text track**, **External SRT**, or **Off**. Embedded track numbers are zero based subtitle-track ordinals: `0` is the first subtitle track, independent of video/audio stream numbers. Supported text formats include SubRip, ASS/SSA, MOV text, and WebVTT. If a matching `.srt` file sits beside a newly added movie, it is selected automatically; otherwise the default is embedded track 0. **Choose SRT...** attaches a different SRT and selects External SRT mode. Use UTF-8 SRT files.

Captions are rendered into the decoded movie image by FFmpeg/libass before scaling and compositing. They appear in CPU and GPU presentation, recordings, simulation inputs, and background bakes, and share the movie's transforms and opacity. ASS styling follows the embedded track; SRT uses libass defaults. There is no independent screen-space subtitle overlay or subtitle style editor.

Bitmap subtitles such as Blu-ray PGS and DVD subtitles require an external SRT. Missing subtitle files, unavailable tracks, and unsupported bitmap codecs display a status and leave the movie playing without captions. If a configured subtitle decoder fails, the player retries that movie without captions. Missing or unreadable movies are skipped in order; a playlist with no playable movies reports an error until edited, restarted, or navigated again.

## Resume and saved projects

**Resume playback when project reopens** restores the movie and timestamp recorded in the saved scene. It defaults off, which starts at the first movie at zero. Resume stores a stable entry ID, so list reordering and repeated file paths do not confuse the bookmark. The bookmark represents media time; time spent with LifeViz closed does not advance it. Playback opens playing rather than retaining a transient pause state.

Autosave snapshots and app shutdown capture the applied playlist's current position. Scene Editor **Save...** captures every live playlist's current position, including unselected layers. A named project export resumes from the position captured by its last Save; subsequent playback does not rewrite that exported file. This is not a periodic crash-recovery checkpoint. With Live Mode off, Save exports the draft and its last captured bookmark. A missing bookmarked entry falls back to the first movie at zero. Fixed-duration background bakes always start at the beginning of the playlist, regardless of the live resume option.

Projects contain movie and SRT paths, subtitle modes/track numbers, entry IDs, the resume option, and the bookmark. They reference media on disk rather than bundling it. Keep those files accessible when moving or reopening a project.

## Validation

After a Release build, run `dotnet bin/Release/net9.0-windows/lifeviz.dll --smoke-test movie-playlist`. The test generates its own four-second movie with audio, an embedded text track, and an SRT file. It exercises ordered EOF looping, duplicate entries, subtitle pixels and timing after seek, punctuation in paths, resume on/off, pause, editing continuity, persistence, missing subtitles, empty/repopulated playlists, and draft editor bindings. Its temporary artifact directory includes `playlist-editor.png`. See [Build & Install](Build-and-Install.md#validate-movie-playlists).
