# screenshots

Rendered evidence for pull requests. Code never lands here — this branch exists so a PR body can
show what changed on screen without carrying binaries in the change itself.

Each folder is one PR. Every image is a headless render of the same map, camera and resolution on
either side of the change:

```sh
GODOT=…/Godot_v4.7-stable_mono_linux.x86_64
SCREENSHOT_PATH=out.png MAP="PEI" SHOT_CAM="<x,y,z,pitch,yaw>" "$GODOT" --resolution 1600x900
```

## pr-80 — [Leave a skinned part where its bones put it, not where its node sits](https://github.com/jlucaso1/unturned-godot/pull/80)

PEI, 1600x900, model cache cleared before each side.

| | camera (`SHOT_CAM`) | before | after |
|---|---|---|---|
| 1 | `-612.27,35.05,-205.96,-16.4,130.2` | the display case's glass lying flat in mid-air, outside the diner's wall | nothing outside the wall |
| 2 | `-615.89,35.31,-198.54,-5.6,1.4` | the case with its frame and shelves but no glass | the panes standing in the frame |
| 3 | `-623.22,35.31,-193.17,-17.8,175.6` | the counters' and ovens' doors as slabs jutting out of the bodies | doors on the fronts, hobs on the tops |

`before` is `main` at [`8d7bfb6`](https://github.com/jlucaso1/unturned-godot/commit/8d7bfb6c0cfd74cf6514d97026b6a92ab5b16ef8); `after` is the PR branch.
