# Cyclorama

A tiny, standalone **curved screen for your desktop**. Point it at an image, a video, or a live web
page and it renders that content onto a real concave 3D surface in a borderless, transparent,
draggable window — like a little sci-fi viewscreen sitting on your desktop. The surface leans toward
your cursor and drifts gently on its own.

![A line bends into a curved screen broadcasting James Webb imagery](media/preview.gif)

▶ full demo: [`media/demo.mp4`](media/demo.mp4) — and the demo itself is code: it's rendered from an
HTML composition in [`promo/`](promo/) with HyperFrames.

![Cyclorama showing a James Webb image on the curved surface](media/showcase.png)

Extracted and generalized from a native curved companion display: the app-specific parts are gone,
what's left is the pure curved-surface media carrier.

## Quick start

- **Double-click `Cyclorama.exe`** → it opens with a bundled James Webb space image on the curve.
- Or point it at your own content:

```
Cyclorama "C:\photo.jpg"
Cyclorama "C:\clip.mp4"
Cyclorama https://example.com
Cyclorama samples\cosmic-cliffs-live.mp4     # a bundled looping cosmic clip
```

`source` is auto-detected:

| source | shows as |
|--------|----------|
| `.png .jpg .gif .webp .bmp .tiff` | image |
| `.mp4 .webm .mov .mkv .avi .m4v`  | video — looped, GPU-smooth, with a play/seek/volume bar |
| `https://…` or a local `.html`    | live web page |

Force a kind with `--image` / `--video` / `--url`.

Drag the surface to move it · drag the bottom-right grip to resize (aspect-locked) · `Esc` closes.

### Options

| flag | meaning |
|------|---------|
| `--size WxH` | initial window width (height auto-locks to the content's aspect) |
| `--pos X,Y`  | window position (default: centered) |
| `--curve N`  | concavity, `0`–`0.8` (default `0.38`; `0` = flat) |
| `--flat`     | no curve |
| `--still`    | disable the idle drift (mouse-follow tilt stays) |
| `--top`      | always-on-top |
| `--mute`     | mute video audio |

## Bundled samples (`samples/`)

Real **James Webb Space Telescope** imagery to show off the curve — see `samples/CREDITS.md`.

- `cosmic-cliffs.jpg` — the Carina Nebula ("Cosmic Cliffs") — also the default image
- `pillars-of-creation.jpg`, `deep-field.jpg`
- `cosmic-cliffs-live.mp4` — a gently-looping animated version of the Cosmic Cliffs
- `web-demo.html` — a tiny live web page: `Cyclorama samples\web-demo.html`

## How it works

The content isn't warped as a 2D effect — it's painted onto a real **3D mesh** bent into a parabola
and viewed through a perspective camera. The concave-vs-convex shape is one sign in the mesh's `z`:

```
concave (wraps in)   :  z = +curveDepth * nx²    // edges nearer the camera   ← Cyclorama
convex  (bulges out) :  z = -curveDepth * nx²    // centre nearer the camera
```

where `nx` runs −1…+1 across the panel. Each source reaches the curve differently:

- **image** → an `ImageBrush` on the mesh material.
- **video** → `MediaPlayer` → a `VideoDrawing` brush (GPU, smooth, looped).
- **web** → an offscreen WebView2 (the shared Edge runtime — not bundled) renders the page and its
  frames are copied onto the material. Smooth for normal pages; capped for video-heavy ones.

The window auto-locks its aspect to the content so the curve is never stretched, polls the cursor to
lean toward it, and lights the surface with flat ambient white so media shows at full brightness.

## Build

```
dotnet build -c Release
# or a single-file exe:
dotnet publish -c Release -o dist --self-contained false -p:PublishSingleFile=true
```

Requirements: **.NET 8 SDK** (Windows) to build, the **.NET 8 Desktop runtime** to run, and the Edge
**WebView2 runtime** (preinstalled on current Windows 10/11) for the web source.

## Credits & license

- Code: **MIT** (see `LICENSE`).
- Bundled space imagery: **NASA, ESA, CSA, STScI** (James Webb Space Telescope), CC BY 4.0 — see
  `samples/CREDITS.md`.
