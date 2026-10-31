# Reference-inspired Unity resource instruments

The user reference is preserved as `reference.png`. `SOURCE.json` records the
built-in image_gen artwork prompt. Only the reusable metal housing is raster
art. `ResourceDial` renders its live shader, labels, values and proportional
segments independently; `ResourceGaugePreview` supplies sample values and sliders.

Open `Assets/FortyTwoAndBeyond/Previews/ResourceGauges.unity` and press Play.
The preview does not bootstrap SpaceClient or contact a server. The approved housing is now used in the installed HUD; the game combines shield
and hull in one blue/red dial, with a separate reactor and matching crew/target dials.

Regenerate the real Unity screenshots and animation frames with:

```sh
Unity -batchmode -projectPath client -executeMethod FortyTwoAndBeyond.Editor.ResourceGaugePreviewBuilder.Create -logFile /tmp/resource-dials.log
```

The desktop `gauges/` directory contains the rendered PNGs and the preview video.
Frames are in `client/Builds/verification/resource-gauge-frames/`.
