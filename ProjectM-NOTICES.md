# projectM in LifeViz

LifeViz uses the projectM core as a separate, replaceable `projectM-4.dll`.
Copyright 2003-2024 projectM Team. Its source is licensed under LGPL 2.1 or later;
see LICENSE-projectM.txt and COPYING-projectM.txt. This bundle selects LGPL 3.0
under that "or later" permission, accommodating the Apache-2.0 portions of glad.
The LGPL 3.0 and incorporated GPL 3.0 texts are provided in `licenses.zip`.
No projectM engine source
modifications are made by LifeViz.

The bundled development version identifies as 4.2.0 and is built from commit
`1e7ef7803b69024d1e0656705670adda2ffac817` of
https://github.com/projectM-visualizer/projectm, including projectm-eval commit
`22fb0cfd8f2dfbcd2b68f2443e7f44e19b32c09a`.
The newer API is needed for fixed-frame-time video baking.

Complete corresponding source, vendored dependency notices and the build script
are provided in `projectM-source.zip`. Extract the archive and build the included
source with Visual Studio 2022 C++ tools and CMake:

```
cmake -S projectm -B build -G "Visual Studio 17 2022" -A x64 -DENABLE_SYSTEM_PROJECTM_EVAL=OFF -DENABLE_PLAYLIST=OFF -DBUILD_SHARED_LIBS=ON -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded
cmake --build build --config Release --parallel 6
```

The resulting `build/src/libprojectM/Release/projectM-4.dll` can replace the copy
in LifeViz's `projectm` folder. LifeViz does not prohibit modifying this library
or reverse engineering LifeViz for debugging such modifications. No signature
or hash check prevents loading an API-compatible replacement at runtime.

projectm-eval uses the MIT license; hlslparser includes an MIT license.
Their license texts accompany the DLL. GLM 1.0.3 is used under its MIT option.
The glad 2.0.8 generated loader uses its CC0-1.0 option with Apache-2.0 portions.
Additional GLM, glad, CC0 and Apache license texts are in `licenses.zip` and the
source archive. stb_image and Khronos header notices remain in the source archive.

## Presets and textures

The presets and textures in `assets.zip` are copied from the official Windows distributable:
https://github.com/projectM-visualizer/frontend-sdl-cpp/releases/tag/2.0.0-pre1

Archive: `projectMSDL-2.0.0-win64.zip`
SHA-256: `7129cae0757970bd4fb2bf96aa7d6153fe9872d414c88c82b04a6396bdfe235b`

The standalone frontend, its GPL code, its executable and its other DLLs are
not incorporated in LifeViz. Preset and texture filenames and category folders
are preserved, with only the redundant outer pack folders removed. On first use,
LifeViz extracts them under `%LOCALAPPDATA%/lifeviz/projectm/` in a versioned cache.
This uses no network connection and avoids Windows installer path-length limits.

Cream of the Crop was curated by ISOSCELES/Jason Fletcher. Preset authors retain
credit in their original filenames and source. Upstream:
https://github.com/projectM-visualizer/presets-cream-of-the-crop
https://github.com/projectM-visualizer/presets-milkdrop-texture-pack

The collection does not provide explicit licenses from most individual authors.
The upstream LICENSE.md says:

> Milkdrop presets were, in almost all cases, not released under any specific license. Theoretically, each preset author
> holds the full copyright on any released presets. Since the presets were freely released and have been used in so many
> packages and applications in the past two decades, it is safe to assume them to be in the public domain.
>
> If any preset author doesn't want their own creation in this repository, please contact the projectM team and we will
> remove the preset(s) from future releases.

Source: https://github.com/projectM-visualizer/presets-cream-of-the-crop/blob/master/LICENSE.md

LifeViz follows upstream's distribution practice. That assumption is not an
explicit public-domain dedication by each author. LifeViz's own license and the
engine's LGPL do not relicense these presets or textures. Imported presets and
their textures remain subject to their authors' terms.
