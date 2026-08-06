using System;
using Godot;
using UnturnedGodot.Damage;
using UnturnedGodot.Net;

namespace UnturnedGodot;

// The client half of DamageTool.ReceiveSpawnLegacyImpact: a hit landed over there, on that kind of
// surface, so make the sound it makes.
//
// Every client hears every hit, its own included. That is the original's shape rather than an oversight —
// the client raycasts for its hitmarker but does NOT spawn the impact locally, because only the server
// knows whether the swing it predicted actually reached anything. One authority, one effect, and no
// double-thump when the two agree.
public sealed partial class ImpactView : Node
{
    private NetClient? _client;
    private ImpactAudio? _audio;
    private ImpactDecals? _decals;

    // Datagrams whose bytes did not decode. Read by the diagnostics overlay, same as the zombie view's.
    public int MalformedPacketsDropped { get; private set; }

    // Impacts that arrived and resolved to no sound at all: a surface whose asset defines neither event
    // key, or one whose clips never made it into the audio cache. Silence is a legitimate outcome, so
    // this is a counter rather than a warning — but a session where it equals the total is a session
    // whose extraction did not run.
    public int SilentImpacts { get; private set; }

    public static ImpactView Create(NetClient client, ImpactAudio audio, ImpactDecals? decals = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(audio);
        var view = new ImpactView
        {
            Name = "ImpactView",
            _client = client,
            _audio = audio,
            _decals = decals,
        };
        client.OnUnhandledMessage += view.Handle;
        return view;
    }

    public override void _ExitTree()
    {
        if (_client != null)
            _client.OnUnhandledMessage -= Handle;
        _client = null;
    }

    private static readonly Func<byte[], PunchImpact> ReadImpact = ImpactNetMessages.ReadImpact;

    private void Handle(byte[] payload)
    {
        if (payload.Length == 0 || (ENetMessage)payload[0] != ENetMessage.Impact)
            return;
        if (!MalformedPacket.TryDecode(payload, ReadImpact, out PunchImpact impact))
        {
            MalformedPacketsDropped++;
            return;
        }

        if (!_audio!.Play(impact.Surface, impact.Point))
            SilentImpacts++;

        // The mark is independent of the sound. Several surfaces define one and not the other — Foliage
        // and Snow name a particles-only effect but do have impact audio — so neither gates the other.
        _decals?.Mark(impact.Surface, impact.Point, impact.Normal);
    }
}
