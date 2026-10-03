# Smart crop model: YOLOX-Nano

The bundled, unmodified `yolox_nano.onnx` comes from
[Megvii-BaseDetection/YOLOX release 0.1.1rc0](https://github.com/Megvii-BaseDetection/YOLOX/releases/tag/0.1.1rc0).
Upstream publishes YOLOX under Apache-2.0. The complete upstream license and
copyright notice are in `YOLOX-LICENSE.txt`; exact asset provenance is in
`model-provenance.json`.

- SHA-256: `c789161ed43c8269fcd4e67c67eeeb4e80c622da2eb296a20bc6007bd18a0b7d`
- Size: 3,659,407 bytes
- Input: float32 `[1, 3, 416, 416]`, **BGR 0–255**, CHW, aspect-preserving resize,
  top-left letterbox padded with 114. No division by 255.
- Output: `[1, 3549, 85]`, raw XY grid offsets and log WH at strides 8/16/32;
  confidence is objectness multiplied by the class probability.
- Classes: 80 COCO categories; the crop prioritizes people, applies NMS, then
  falls back to the existing text/saliency analysis if no suitable detection exists.

`Tools/Assert-ModelAssets.ps1` checks the allowlist, size, hash and license in CI
and again on the published installer payload. Shape, preprocessing and decoding
are covered by `OnnxModelInputTests` and `YoloxCropTests`. Model changes require a
new provenance review and corresponding preprocessing/decoder tests.

The Ultralytics YOLO11 model has been removed from the current source and
distribution. Git history and previously published installers can still contain
it and require a separate distribution review. Do not restore that asset without
documented authorization and a review of the applicable obligations.
