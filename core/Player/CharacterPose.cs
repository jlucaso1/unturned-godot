using Godot;

namespace UnturnedGodot.Player;

// A single bone's pose override sampled from an animation clip. Any of the three channels may be absent when
// the clip doesn't animate it (the bone then keeps its bind-rest value for that channel).
public readonly record struct BonePose(int Bone, Quaternion? Rotation, Vector3? Position, Vector3? Scale);
