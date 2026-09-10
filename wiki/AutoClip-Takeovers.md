# AutoClip takeovers and playlists

Use **Visibility Group** to alternate several visuals while a background and logo keep playing. A visibility group is a name shared by layers, not a flattened Layer Group: it preserves each layer's blend mode, transparency, and stack position.

## Movie, short loops, and long clips

Create this stack in back-to-front rendering order. Keep these five layers at the same scene-tree level; a normal Layer Group is unnecessary.

| Layer | Blend | Visibility Group | Takeover |
| --- | --- | --- | --- |
| Fractal zoom | Your existing setting | Leave blank | Off |
| Movie | Additive | `main visuals` | Off |
| Short AutoClip | Normal, with alpha or keying | `main visuals` | Off |
| Long AutoClip | Normal, with alpha or keying | `main visuals` | On |
| DJ logo loop | Normal, with alpha or keying | Leave blank | Off |

Select each source in **Scene Editor** to enter its Visibility Group. On the long AutoClip, enable **Take over this visibility group while playing**. The right-click Sources menu also exposes **Visibility Group...** and **Take Over Visibility Group**.

For the long AutoClip, start with:

- **Clip Time:** min `120`, max `180` seconds.
- **Loop selected file:** on to repeat the chosen file for that entire time window, starting at its beginning.
- **Delay Time:** min `60`, max `120` seconds, or whatever interval you want to spend with the movie and short loops between long clips.
- **Fade In / Out:** `2` seconds for a gradual switch; `0` for a cut.
- **Start with delay:** on to begin with the movie and short loops. This also applies at time zero of a fixed-duration render.

If each long video should play once from beginning to end, enable **Play whole file from beginning to end** instead. This uses each file's duration, overriding Clip Time and Loop selected file. For a two-minute source it produces a two-minute visible phase. Fade time is included in that duration, not added afterward. Whole-file playback requires readable duration metadata; files without it are skipped through the existing retry path.

Choose **Render Fixed Duration...**, enter `1:00:00`, and select the desired output FPS. The clip/gap schedule repeats throughout the hour without interrupting the fractal or logo. Fixed-duration output remains silent, as before.

## What happens during a takeover

The long clip fades in while the other group members fade out, and the reverse happens at its end. The takeover follows the AutoClip fade envelope, independently of the pixels' transparency: transparent holes reveal the fractal, not the hidden movie. Existing opacity and animation settings still affect the rendered layers. An AutoClip with zero layer opacity does not take over; a nonzero layer opacity does not weaken its takeover envelope.

Hidden layers **keep playing**, including their decoders, short-clip schedules, and animations. Returning to the movie shows wherever playback has reached. Visibility groups do not change audio routing or audio analysis; use Play Audio and the existing volume controls independently. This choice favors continuous playback and does not save decoding work while layers are hidden.

Names are trimmed and matched without case sensitivity. A blank name is independent. Names apply only among siblings at the same scene-tree level, so separate Layer Groups can reuse a name. A Layer Group can itself be a fallback member, hiding its composite output. Sim Group output does not expose this control; keep it independent or put it inside a fallback Layer Group when appropriate.

Takeover starts only with a published frame. Empty, disabled, failed, and delaying AutoClips release the fallback. A temporarily retained published frame follows the existing AutoClip handoff envelope. With no delay, consecutive clips can keep control continuously. Give the long AutoClip a nonzero delay to spend time in the fallback scene.

For predictable alternation, put all long videos in one takeover AutoClip. If multiple takeover AutoClips share a group and overlap, they remain visible together in their normal stack order; ordinary members follow the strongest active takeover fade. Takeover layers never hide each other, so there is no circular suppression.

## AutoClip as a playlist

AutoClip now includes the useful sequential playback workflow from Video Sequence while retaining fades, gaps, keying, per-file overrides, and takeovers:

- **Play files in list order:** cycles from the first file through the last, then returns to the first. Unchecked retains random selection with immediate repeats avoided when possible.
- **Multi-select:** Ctrl-click individual files or Shift-click a range. **Select All** and Ctrl+A select the entire playlist. The list shows a selected count and full paths on hover.
- **Move Up / Move Down:** move every selected run by one position. **Move to Top / Move to Bottom** gather all selected files at that end. Selection and relative file order are preserved, and per-file blend/keying overrides travel with their entries. Alt+Up/Down and Alt+Home/End provide the same moves while the list has focus.
- **Remove Selected / Delete:** remove all selected playlist entries in one edit; original media stays on disk. A surviving neighbor is selected afterward. Select all, then remove, to empty the playlist.
- Select exactly one file to edit its blend/keying overrides. Multiple selection hides those controls to avoid accidentally applying a change to just one file.
- **Play whole file:** plays each source once from its beginning to its actual duration. With list order enabled and delay/fade set to zero, this is a repeating ordered playlist.

Changing playback options, timing, or the file list restarts that AutoClip's schedule. Changing its visibility group, takeover checkbox, or fade does not restart its decoder. A fixed-duration render starts its ordered playlist at the first file and uses the export frame clock; live playback position and the next ordered file are restored afterward.

Existing Video Sequence layers remain supported. Existing scenes retain their previous AutoClip defaults: random timed clips, no initial delay, and no visibility group or takeover. Whole-file playback has no reserved trailing excerpt; at a zero-delay live seam the final source frame can be held briefly if the next decoder is not ready.
