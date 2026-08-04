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
CHAR_ONLY=1 CHAR_FIRST=1 CHAR_STANCE=Crouch                SCREENSHOT_PATH=after-crouch.png "$GODOT" --path .
CHAR_ONLY=1 CHAR_FIRST=1 CHAR_GESTURE=Punch_Left CHAR_ANIM_TIME=0.10 SCREENSHOT_PATH=after-punch.png "$GODOT" --path .
```

`before` adds `CHAR_REST_ANCHOR=1`, which pins the rig to its bind pose the way the branch did before
the framing was fixed.

The head BONE is a joint at the neck (1.3202 m in the prefab), and the shoulders sit at the identical
height. Anchoring first person on it put the camera inside the neck: the head hung over the top of the
screen and the arms swept through the near plane at eye level. The eye is `Skull/Spot`, 0.45 m further
along the head at 1.7702 m — the same height as the prefab's own `Aim/Fire`, and the point `Player.cs`
equates with the first-person camera.

| stance | before | after |
|---|---|---|
| stand | ![](https://raw.githubusercontent.com/jlucaso1/unturned-godot/screenshots/pr-79/before-stand.png) | ![](https://raw.githubusercontent.com/jlucaso1/unturned-godot/screenshots/pr-79/after-stand.png) |
| running | ![](https://raw.githubusercontent.com/jlucaso1/unturned-godot/screenshots/pr-79/before-stand-moving.png) | ![](https://raw.githubusercontent.com/jlucaso1/unturned-godot/screenshots/pr-79/after-stand-moving.png) |
| crouch | ![](https://raw.githubusercontent.com/jlucaso1/unturned-godot/screenshots/pr-79/before-crouch.png) | ![](https://raw.githubusercontent.com/jlucaso1/unturned-godot/screenshots/pr-79/after-crouch.png) |
| prone | ![](https://raw.githubusercontent.com/jlucaso1/unturned-godot/screenshots/pr-79/before-prone.png) | ![](https://raw.githubusercontent.com/jlucaso1/unturned-godot/screenshots/pr-79/after-prone.png) |

Stand, running, crouch and crouch-running all come out **byte-identical to an empty frame** after: no
head, no torso, nothing. Prone leaves the forearms resting in the bottom corners with the view down the
middle. The punch is the case the framing exists for:

![](https://raw.githubusercontent.com/jlucaso1/unturned-godot/screenshots/pr-79/after-punch.png)

A whole fist arriving from the lower left, with none of the sliced-open cross-section the near plane
used to cut through the arm.

## pr-80 — [Leave a skinned part where its bones put it, not where its node sits](https://github.com/jlucaso1/unturned-godot/pull/80)

PEI, 1600x900, model cache cleared before each side.

| | camera (`SHOT_CAM`) | before | after |
|---|---|---|---|
| 1 | `-612.27,35.05,-205.96,-16.4,130.2` | the display case's glass lying flat in mid-air, outside the diner's wall | nothing outside the wall |
| 2 | `-615.89,35.31,-198.54,-5.6,1.4` | the case with its frame and shelves but no glass | the panes standing in the frame |
| 3 | `-623.22,35.31,-193.17,-17.8,175.6` | the counters' and ovens' doors as slabs jutting out of the bodies | doors on the fronts, hobs on the tops |

`before` is `main` at [`8d7bfb6`](https://github.com/jlucaso1/unturned-godot/commit/8d7bfb6c0cfd74cf6514d97026b6a92ab5b16ef8); `after` is the PR branch.
