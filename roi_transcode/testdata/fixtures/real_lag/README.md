# Real-lag fixture

Small real-clip fixture used by `integration/scripts/check.sh` for the optional
`roi lag acceptance on a real clip` step.

Files:

- `video.mp4` — short H.264 clip.
- `roi.json` — ROI samples for the same timeline in `samples[]` format.

Note: `video.mp4` matches `*.mp4` ignore rules in this repository and is kept as
local generated fixture (not tracked in git by default).

How it was generated:

```bash
bash integration/scripts/prepare-real-lag-fixture.sh \
  /path/to/source.mp4 \
  /path/to/roi.json \
  roi_transcode/testdata/fixtures/real_lag \
  0 6
```

You can regenerate with different source/start/duration if needed, but keep the
fixture short and deterministic so `check.sh` stays fast and stable.
