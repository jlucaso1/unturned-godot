# screenshots

Rendered evidence for pull requests. Code never lands here — this branch exists so a PR body can
show what changed on screen without carrying binaries in the change itself.

Each folder is one PR. Every image is a headless render of the same map, camera and resolution on
either side of the change:

```sh
GODOT=…/Godot_v4.7-stable_mono_linux.x86_64
SCREENSHOT_PATH=out.png MAP="PEI" SHOT_CAM="<x,y,z,pitch,yaw>" "$GODOT" --resolution 1600x900
```

## pr-79 — [Port PlayerEquipment's punch damage, and animate the swing in first person](https://github.com/jlucaso1/unturned-godot/pull/79)

First person, 1152x648, no world build — `CHAR_ONLY=1 CHAR_FIRST=1` frames the character the way the
game's own view does, so a stance can be read off a render that takes seconds:

```sh
CHAR_ONLY=1 CHAR_FIRST=1 CHAR_STANCE=Crouch SCREENSHOT_PATH=after-crouch.png "$GODOT" --path .
```

`before` adds `CHAR_REST_ANCHOR=1`, which pins the rig to its BIND pose the way the branch did before
this fix. Unturned parents the first-person camera under the viewmodel skeleton's Skull
(`firstSkeleton/Spine/Skull/ViewmodelCamera`), so the eye rides the animated head; a bind-pose offset
only agrees with that while the character is standing.

| stance | before | after |
|---|---|---|
| stand | the frame the fix must not change | byte-identical to before |
| crouch | the screen filled edge to edge with skin — the camera inside the torso | clean, byte-identical to the standing frame |
| prone | the body a sliver at the bottom: the eye floating above a character lying down | inside the head, arms framing the view from both sides |

The `-moving` pair is the same three stances on their `Move_` clips rather than `Idle_`, which is where
the arms are actually in shot.

## pr-80 — [Leave a skinned part where its bones put it, not where its node sits](https://github.com/jlucaso1/unturned-godot/pull/80)

PEI, 1600x900, model cache cleared before each side.

| | camera (`SHOT_CAM`) | before | after |
|---|---|---|---|
| 1 | `-612.27,35.05,-205.96,-16.4,130.2` | the display case's glass lying flat in mid-air, outside the diner's wall | nothing outside the wall |
| 2 | `-615.89,35.31,-198.54,-5.6,1.4` | the case with its frame and shelves but no glass | the panes standing in the frame |
| 3 | `-623.22,35.31,-193.17,-17.8,175.6` | the counters' and ovens' doors as slabs jutting out of the bodies | doors on the fronts, hobs on the tops |

`before` is `main` at [`8d7bfb6`](https://github.com/jlucaso1/unturned-godot/commit/8d7bfb6c0cfd74cf6514d97026b6a92ab5b16ef8); `after` is the PR branch.
