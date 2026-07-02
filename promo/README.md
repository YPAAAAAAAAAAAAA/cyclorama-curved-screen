# The promo video is code

`media/demo.mp4` isn't edited in a video editor — it's rendered from `index.html` in this folder
with [HyperFrames](https://github.com/heygen-com/hyperframes) ("write HTML, render video").

The composition rebuilds the Cyclorama surface natively in three.js — the **same parabolic panel
the app uses** (`z = depth · nx²`, perspective camera at `z = 4.2`, fov 46) — then tells the story:
a white line draws in, bends and grows into the curved screen, and the screen broadcasts three James
Webb images with the app's parallax tilt.

Rebuild it:

```
npx hyperframes render --output ../media/demo.mp4
```

(Requires Node 22+, FFmpeg, and a headless Chrome — `npx hyperframes doctor` checks all three.)

`gsap.min.js` (GreenSock standard license) and `three.min.js` (MIT) are vendored so the composition
renders offline. Webb imagery credits: see `../samples/CREDITS.md`.
