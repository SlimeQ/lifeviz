# Webcam Layers

A webcam layer is a slot in the scene that shows a camera. The camera can be missing, swapped or replaced without losing the layer, its position, or its settings (blend, opacity, keying, mirror, animations).

## Choosing a camera

- **Add a webcam layer** with **Webcam** in the Scene Editor or **Sources > Add Webcam Source** in the right-click menu. Pick a specific camera or **Any camera (first available)**. With no camera connected, LifeViz offers to add an **Any camera** layer that starts showing video once a camera is plugged in.
- **Swap the camera** on an existing layer:
  - **Scene Editor:** select the layer and use the **Camera** list under its name. **Refresh** looks for newly connected cameras. The status line underneath says which camera is in use, or why the layer is waiting. With Live Mode off, the change applies when you press **Apply**.
  - **Right-click menu:** use the layer's **Camera** submenu, next to **Mirror Webcam**.
- **Any camera** uses the first camera that no other webcam layer is using. It is the most portable choice, because it works on any PC with a camera.
- A specific camera can only be used by one layer at a time. Choosing a camera that another layer uses is refused; pick another camera for one of them.

## When the camera isn't there

The layer stays in the scene and in the Scene Editor. It shows nothing (transparent) until its camera is available, and autosave keeps saving it normally. Webcam layers are never moved to the hidden **missing inputs** list.

- **Unplugged or not yet connected:** LifeViz checks for cameras every 3 seconds while a layer is waiting, and every 10 seconds otherwise to notice unplugged cameras. Enumeration runs off the render loop. The layer reconnects automatically when the camera returns.
- **Fails to open or never delivers frames:** the layer releases the camera and retries after 5, 10, 20, 40, then 60 seconds. **Refresh** in the Scene Editor retries immediately. Before this change, such a layer was removed from the scene.
- **Different PC or USB port:** camera IDs depend on the PC and port. A saved camera is matched by ID first, then by name. A name match adopts the new local ID, so the scene keeps working there.

## Saving and sharing

The scene saves the chosen camera's ID and name, or no ID for **Any camera**. **Share...** turns every webcam layer into an **Any camera** layer in the package, so it picks up the recipient's camera automatically; your own scene is unchanged. The recipient can choose another camera in the layer's **Camera** list. See [Sharing Scenes](Sharing-Scenes.md).

## Validation

`--smoke-test webcam-devices` uses a fake camera list and runs the real capture loop and Scene Editor. It checks:

- a layer whose camera is absent survives restore, the capture loop, and a camera that fails to open (backoff included)
- name matching adopts the local ID
- **Any camera** assignment, and unplug/replug
- live, draft and editor-picker swaps that keep layer settings, and refusal of a camera in use
- project export and restore with no cameras connected

It writes `smoke-webcam-picker.png` beside the executable. No physical camera is needed.

`--smoke-test webcam-devices-live` runs the same checks, then opens the first real camera. It requires frames from it, switches the layer to **Any camera**, and requires frames again. The camera turns on for a few seconds and no frames are saved. It is skipped when no camera is connected.
