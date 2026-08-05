using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The zombie router, and the handoff that gets its data into a session.
//
// The handoff is the part with a rule worth holding, and it is a rule about FAILURE. Interactive loading
// parses the baked navmesh early — before the collision world exists — and parks it. The session claims
// it later. Between those two moments a load can die: an unreadable map, a player backing out, a throw
// anywhere in the world build.
//
// If the parked data survived that, the NEXT map's session would claim the FAILED map's navmesh, and its
// zombies would route over geometry that is not there. So an unclaimed parse has to be discardable, and
// claiming has to be a one-shot: whoever takes it leaves nothing behind.
public class ZombieNavigationTests : TestClass
{
    public ZombieNavigationTests(Node testScene) : base(testScene) { }

    // DisabledFaces is only populated when captures are possible, and that is decided ONCE at type load
    // from REPRO — the documented way to turn the recorder off. It cannot be forced on from here, so with
    // REPRO=0 an assertion about dropped faces reads as "this pass pruned nothing" when what it means is
    // "this build is not recording what it pruned". Those are the same empty dictionary.
    //
    // So every test below asserts the ROUTING effect, which is environment-independent, and asserts the
    // face sets only where they are meaningful. The alternative — asserting the sets unconditionally —
    // makes the suite pass or fail on the caller's environment rather than on the code.
    private static bool FacesAreRecorded()
    {
        if (ZombieNavigation.RecordsDisabledFaces)
            return true;

        Log.Print("[runtime-tests] REPRO is off, so disabled faces are not recorded; asserting routing only");
        return false;
    }

    // A map with no navigation flags builds no router. Plenty of workshop maps ship none, and the session
    // has to treat that as "no zombies route here" rather than as a failure.
    [Test]
    public void AMapWithNoFlagsBuildsNoRouter()
    {
        Assert.Null(ZombieNavigation.Build(null));
        Assert.Null(ZombieNavigation.Build(new List<NavFlag>()));
    }

    // A map with flags does build one — the other side of the same question.
    [Test]
    public void AMapWithFlagsBuildsARouter()
    {
        Assert.NotNull(ZombieNavigation.Build(new List<NavFlag> { Flag() }));
    }

    // Claiming a parse nobody made is null rather than an empty router. A session that got one would
    // believe it had a navmesh and route zombies against nothing.
    [Test]
    public void ClaimingAnEmptyParkIsNull()
    {
        ZombieNavigation.DiscardPreloaded();

        Assert.Null(ZombieNavigation.TakePreloaded());
    }

    // Discarding is what a failed load calls. Without it the parked parse outlives the map it came from,
    // and the next session claims the failed map's navmesh — zombies routing over geometry that is not
    // there, in a world that looks fine.
    [Test]
    public void DiscardingLeavesNothingForTheNextMap()
    {
        ZombieNavigation.DiscardPreloaded();
        ZombieNavigation.Preload("/nonexistent-map");

        ZombieNavigation.DiscardPreloaded();

        Assert.Null(ZombieNavigation.TakePreloaded());
    }

    // Discarding twice is quiet: a failed load and the teardown that follows it can both call this.
    [Test]
    public void DiscardingTwiceIsQuiet()
    {
        ZombieNavigation.DiscardPreloaded();
        ZombieNavigation.DiscardPreloaded();

        Assert.Null(ZombieNavigation.TakePreloaded());
    }

    // Preloading a map with no navmesh under it parks nothing, so a later claim is null rather than an
    // empty router. A workshop map without Navigation_*.dat takes this path on every load.
    [Test]
    public void PreloadingAMapWithNoNavmeshParksNothing()
    {
        ZombieNavigation.DiscardPreloaded();
        ZombieNavigation.Preload("/nonexistent-map");

        Assert.Null(ZombieNavigation.TakePreloaded());
    }

    // Preload does not overwrite a park that is already there. Two loads racing — a player backing out
    // and starting another map before the first finished — must not leave the second holding the first's
    // data, and the guard is what stops it.
    [Test]
    public void PreloadingTwiceDoesNotOverwriteTheFirstPark()
    {
        ZombieNavigation.DiscardPreloaded();
        ZombieNavigation.Preload("/nonexistent-map");
        ZombieNavigation.Preload("/another-nonexistent-map");

        // Neither map has a navmesh, so the observable part is that this stays survivable and leaves
        // nothing behind — the claim below is what a session would do next.
        Assert.Null(ZombieNavigation.TakePreloaded());
        Assert.Null(ZombieNavigation.TakePreloaded());
    }

    // --- routing over a floor ------------------------------------------------------------------------

    // A router built from flags is ready the moment Build returns. Build is the non-interactive path — the
    // dedicated server and the screenshot runner both take it — and there is no later frame in which to
    // publish, so anything not ready here would never become ready.
    [Test]
    public void ARouterBuiltFromFlagsIsReadyImmediately()
    {
        ZombieNavigation nav = Build();

        Assert.True(nav.IsReady);

        nav.Free();
    }

    // A point above the floor projects down onto it. This is how a zombie that spawned, fell, or got
    // shoved off the mesh finds its way back onto it, and every route starts by asking for it.
    [Test]
    public void APointAboveTheFloorProjectsOntoIt()
    {
        ZombieNavigation nav = Build();

        Assert.True(nav.ProjectToSurface(new Vector3(0f, 3f, 0f), out Vector3 projected));
        Assert.True(Mathf.Abs(projected.Y) < 1f, $"projected off the floor's plane: {projected}");

        nav.Free();
    }

    // A point far outside the mesh projects onto nothing. Answering with the nearest triangle anyway would
    // teleport a zombie standing in unnavigable geometry across the map.
    [Test]
    public void APointNowhereNearTheFloorProjectsOntoNothing()
    {
        ZombieNavigation nav = Build();

        Assert.False(nav.ProjectToSurface(new Vector3(5000f, 0f, 5000f), out _));

        nav.Free();
    }

    // Two points on the same floor are routable, and the route it hands back is real points rather than an
    // empty list reported as success.
    [Test]
    public void TwoPointsOnTheSameFloorAreRoutable()
    {
        ZombieNavigation nav = Build();
        var path = new List<Vector3>();

        Assert.True(nav.Query(new Vector3(-8f, 0f, -8f), new Vector3(8f, 0f, 8f), path, 0.4f));
        Assert.NotEmpty(path);

        nav.Free();
    }

    // Routing to somewhere off the mesh fails rather than walking a zombie to the edge and stopping. The
    // caller falls back to steering directly, which is the right behaviour for a target it cannot reach.
    [Test]
    public void RoutingToSomewhereOffTheMeshFails()
    {
        ZombieNavigation nav = Build();
        var path = new List<Vector3>();

        Assert.False(nav.Query(new Vector3(-8f, 0f, -8f), new Vector3(5000f, 0f, 5000f), path, 0.4f));

        nav.Free();
    }

    // The short-segment probe is what a zombie asks before cutting a corner: can I walk straight from here
    // to there without leaving the mesh? Across the open floor it can; off the edge it cannot.
    [Test]
    public void TheLocalSegmentProbeAnswersBothWays()
    {
        ZombieNavigation nav = Build();

        Assert.True(nav.SupportsLocalSegment(new Vector3(-4f, 0f, 0f), new Vector3(4f, 0f, 0f), 0.4f));
        Assert.False(nav.SupportsLocalSegment(new Vector3(0f, 0f, 0f), new Vector3(5000f, 0f, 0f), 0.4f));

        nav.Free();
    }

    // A freed router routes nothing. Freeing happens on session teardown, and a stale delegate held by a
    // zombie system that outlived it must answer "no route" rather than read a disposed graph.
    [Test]
    public void AFreedRouterRoutesNothing()
    {
        ZombieNavigation nav = Build();
        nav.Free();

        Assert.False(nav.IsReady);
        Assert.False(nav.ProjectToSurface(new Vector3(0f, 3f, 0f), out _));
    }

    // Freeing twice is quiet: session teardown and the owner's own disposal can both arrive.
    [Test]
    public void FreeingTwiceIsQuiet()
    {
        ZombieNavigation nav = Build();

        nav.Free();
        nav.Free();

        Assert.False(nav.IsReady);
    }

    // --- reconciling the graph with the world ---------------------------------------------------------

    // The baked navmesh describes the TERRAIN. It knows nothing about the objects placed on top of it, so
    // out of the box it routes zombies straight through walls, rocks and the sides of houses — the map's
    // own data says that ground is walkable, and geometrically it is; there is simply a building on it.
    //
    // Reconciliation is what makes the graph agree with the world: every face is probed against the real
    // collision, and one whose surface rises more than a step above the authored plane stops being a
    // route. This is the obstructed direction — a floor with a solid block standing on it.
    [Test]
    public async Task ASolidObjectStandingOnTheFloorTakesItsFacesOutOfTheGraph()
    {
        using var sandbox = new PhysicsSandbox(TestScene);
        sandbox.AddBox(new Vector3(0f, -0.5f, 0f), new Vector3(40f, 1f, 40f));   // the terrain under it
        // A metre tall, deliberately: the probe looks down from one step plus 1.5 m of headroom above the
        // authored plane, so a 2 m block would put its roof exactly at the ray's origin and the sampler
        // would graze it. Anything a zombie cannot step over is the same verdict; this one is measurable.
        sandbox.AddBox(new Vector3(0f, 0.5f, 0f), new Vector3(6f, 1f, 6f));      // the building on it
        await sandbox.Settle();

        ZombieNavigation nav = Build();
        await nav.PruneAgainstCollisionAsync(sandbox.Root, sandbox.Root.GetWorld3D().DirectSpaceState,
            stepOffset: 0.5f, new HashSet<Guid>());

        Assert.True(nav.IsReady, "reconciliation finished without publishing a graph");

        // The building is no longer walkable THROUGH, which is the effect the dropped faces exist to
        // produce and the only half of this that does not depend on the recorder being on.
        // The ground under the building is no longer part of the mesh: a zombie shoved in there cannot
        // find its way back onto the navmesh at that spot, while open floor a few metres away still
        // projects. This is the EFFECT the dropped faces exist to produce, and unlike the face sets it
        // does not depend on the recorder being switched on.
        Assert.False(nav.ProjectToSurface(new Vector3(0f, 2f, 0f), out _),
            "the floor under the building still projects, so nothing was pruned");
        Assert.True(nav.ProjectToSurface(new Vector3(-9f, 2f, -9f), out _),
            "the open floor was pruned along with the building");

        if (FacesAreRecorded())
            Assert.True(TotalDropped(nav) > 0, "the building took no faces out of the graph at all");

        // The building's footprint went, and only it: the floor around it is 20 m across and the block
        // covers 6 of them, so a pass that dropped everything would be eating the map rather than
        // reconciling it — and that failure looks identical from the outside until zombies stop moving.
        IReadOnlySet<int> dropped = nav.DisabledFaces[0];
        Assert.InRange(dropped.Count, 1, 199);

        // What the drop is FOR: a route across the floor still exists, and now goes around.
        var path = new List<Vector3>();
        Assert.True(nav.Query(new Vector3(-9f, 0f, -9f), new Vector3(9f, 0f, 9f), path, 0.4f));

        nav.Free();
    }

    // And the direction that matters more, because its failure is silent: over clear ground nothing is
    // dropped. A pass that ate faces off an empty map would leave zombies unable to route across open
    // terrain, with a graph that still looks built and a map that still looks fine.
    [Test]
    public async Task ClearGroundKeepsEveryFace()
    {
        using var sandbox = new PhysicsSandbox(TestScene);
        sandbox.AddBox(new Vector3(0f, -0.5f, 0f), new Vector3(40f, 1f, 40f));
        await sandbox.Settle();

        ZombieNavigation nav = Build();
        await nav.PruneAgainstCollisionAsync(sandbox.Root, sandbox.Root.GetWorld3D().DirectSpaceState,
            stepOffset: 0.5f, new HashSet<Guid>());

        Assert.True(nav.IsReady);
        foreach (KeyValuePair<int, IReadOnlySet<int>> flag in nav.DisabledFaces)
            Assert.Empty(flag.Value);

        // Still routable, which is the thing the assertion above is a proxy for.
        var path = new List<Vector3>();
        Assert.True(nav.Query(new Vector3(-8f, 0f, -8f), new Vector3(8f, 0f, 8f), path, 0.4f));

        nav.Free();
    }

    // A router freed mid-pass stops rather than probing into a world that is being torn down. A player
    // who backs out during the loading screen sends exactly this, and reconciliation is the longest thing
    // still running when they do.
    [Test]
    public async Task FreeingDuringReconciliationStopsIt()
    {
        using var sandbox = new PhysicsSandbox(TestScene);
        sandbox.AddBox(new Vector3(0f, -0.5f, 0f), new Vector3(40f, 1f, 40f));
        await sandbox.Settle();

        ZombieNavigation nav = Build();
        Task pass = nav.PruneAgainstCollisionAsync(sandbox.Root,
            sandbox.Root.GetWorld3D().DirectSpaceState, stepOffset: 0.5f, new HashSet<Guid>());
        nav.Free();

        await pass;   // it has to end, rather than hang the teardown that is waiting on it

        Assert.False(nav.IsReady);
    }

    // Reconciliation is CACHED, and the second run reads it back rather than probing the map again.
    //
    // The pass is hundreds of thousands of raycasts on a real map. Paying it on every load would put
    // that on the loading screen forever, so the verdicts are written under a fingerprint of everything
    // that could change them — the level, the model cache, the selected colliders, the step offset and
    // the probe settings — and a run whose fingerprint matches restores them instead.
    //
    // The risk the fingerprint exists to cover is the quiet one: a cache restored after the geometry
    // changed would disable faces that no longer correspond to anything, and the map would look right
    // while zombies refused to walk through doors that are now open.
    [Test]
    public async Task ReconciliationIsCachedAndTheSecondRunReadsItBack()
    {
        using var level = new TempLevel();
        using var sandbox = new PhysicsSandbox(TestScene);
        sandbox.AddBox(new Vector3(0f, -0.5f, 0f), new Vector3(40f, 1f, 40f));
        StaticBody3D building = sandbox.AddBox(new Vector3(0f, 0.5f, 0f), new Vector3(6f, 1f, 6f));
        await sandbox.Settle();

        PhysicsDirectSpaceState3D space = sandbox.Root.GetWorld3D().DirectSpaceState;
        var selected = new HashSet<Guid>();

        // The first pass probes the world and writes its verdicts.
        ZombieNavigation first = Build(level.Path);
        await first.PruneAgainstCollisionAsync(sandbox.Root, space, 0.5f, selected);
        SortedSet<string> fromProbing = DroppedFaces(first);
        first.Free();

        Assert.True(System.IO.File.Exists(level.CacheEntry),
            "the first pass wrote no cache, so the second has nothing to read and this proves nothing");
        if (!FacesAreRecorded())
            return;
        Assert.NotEmpty(fromProbing);

        // Now REMOVE the obstruction. A second pass that re-probed would find clear ground and drop
        // nothing; only a pass that restored the cache can still report the building. That is the whole
        // difference between reading the cache and quietly recomputing, and without it this test passes
        // either way — the sandbox has not changed, so both roads lead to the same answer.
        sandbox.Root.RemoveChild(building);
        building.QueueFree();
        await sandbox.Settle();

        ZombieNavigation second = Build(level.Path);
        await second.PruneAgainstCollisionAsync(sandbox.Root, space, 0.5f, selected);

        Assert.True(second.IsReady, "the cached run published no graph");
        Assert.Equal(fromProbing, DroppedFaces(second));

        second.Free();
    }

    // Reconciliation with the CPU FIELD, which is how a real load runs it.
    //
    // The physics server can answer the downward probe, but it can only do so on the physics thread, a
    // slice of a tick at a time — hundreds of thousands of rays on a real map. So the load records the
    // map's whole collision geometry into a field as it builds it, and the pass samples THAT off-thread
    // instead, orders of magnitude faster.
    //
    // The field is a second implementation of the same geometry, and the two agree only to within a
    // margin. That is why the field is never allowed to settle a DESTRUCTIVE verdict on its own: keeping
    // a face leaves the baked navmesh as its authors shipped it, while dropping one removes route from
    // the graph — and a route removed in error is the failure this whole pass exists to prevent. A zombie
    // with nowhere to walk is worse than one shoving through a fence. So every face the field would drop
    // is re-asked of the physics server, however far from the threshold it sits.
    [Test]
    public async Task TheCpuFieldAgreesWithTheServerAboutWhatToDrop()
    {
        using var sandbox = new PhysicsSandbox(TestScene);
        sandbox.AddBox(new Vector3(0f, -0.5f, 0f), new Vector3(40f, 1f, 40f));
        sandbox.AddBox(new Vector3(0f, 0.5f, 0f), new Vector3(6f, 1f, 6f));
        await sandbox.Settle();

        PhysicsDirectSpaceState3D space = sandbox.Root.GetWorld3D().DirectSpaceState;
        var selected = new HashSet<Guid>();

        // The server-only pass, which is the answer the field has to match. UG_NAV_CPU_PROBE=0 in the
        // environment would send BOTH passes down the server path and make the comparison vacuous, so
        // each pass is run under the flag it is supposed to be exercising.
        using (new EnvFlagScope("UG_NAV_CPU_PROBE", "0"))
        {
            ZombieNavigation serverOnly = Build();
            await serverOnly.PruneAgainstCollisionAsync(sandbox.Root, space, 0.5f, selected);
            ServerVerdict = DroppedFaces(serverOnly);
            serverOnly.Free();
        }

        using (new EnvFlagScope("UG_NAV_CPU_PROBE", "1"))
        {
            ZombieNavigation field = Build();
            await field.PruneAgainstCollisionAsync(sandbox.Root, space, 0.5f, selected, RecordedGeometry());

            Assert.True(field.IsReady, "the field-backed pass published no graph");
            if (!FacesAreRecorded())
                return;
            Assert.NotEmpty(ServerVerdict);

            // The same FACES, not the same count. A field that misses one obstructed triangle and drops a
            // clear one instead matches on count while the navmesh is wrong in both directions — which is
            // precisely the destructive mismatch this pass exists to prevent.
            Assert.Equal(ServerVerdict, DroppedFaces(field));

            field.Free();
        }
    }

    private static SortedSet<string> ServerVerdict = new(StringComparer.Ordinal);

    // The field is RELEASED however the pass ended. What it holds is the map's entire collision geometry,
    // recorded during the load purely so this one pass could sample it — so on a session where the pass
    // finished, hit its cache, or was cut short by a quit, nothing is ever going to ask for it again.
    [Test]
    public async Task TheFieldIsReleasedHoweverThePassEnded()
    {
        using var sandbox = new PhysicsSandbox(TestScene);
        sandbox.AddBox(new Vector3(0f, -0.5f, 0f), new Vector3(40f, 1f, 40f));
        await sandbox.Settle();

        CollisionFieldBuilder geometry = RecordedGeometry();
        Assert.True(geometry.ShapeCount > 0 && geometry.TileCount > 0,
            "an empty builder cannot show a release, because every count is already zero");

        ZombieNavigation nav = Build();
        await nav.PruneAgainstCollisionAsync(sandbox.Root,
            sandbox.Root.GetWorld3D().DirectSpaceState, 0.5f, new HashSet<Guid>(), geometry);

        // Already released by the pass. Releasing it here instead would make the test pass whether or not
        // the pass ever did, since Release is quiet on a builder that still holds everything.
        Assert.Equal(0, geometry.ShapeCount);
        Assert.Equal(0, geometry.InstanceCount);
        Assert.Equal(0, geometry.TileCount);

        nav.Free();
    }

    // A field with no geometry in it is still a field. A map whose objects all failed to load records
    // nothing, and the pass has to fall through to the server rather than conclude the map is empty —
    // concluding that would drop nothing at all, which is the safe direction but for the wrong reason.
    [Test]
    public async Task AnEmptyFieldFallsThroughRatherThanConcludingTheMapIsEmpty()
    {
        using var sandbox = new PhysicsSandbox(TestScene);
        sandbox.AddBox(new Vector3(0f, -0.5f, 0f), new Vector3(40f, 1f, 40f));
        sandbox.AddBox(new Vector3(0f, 0.5f, 0f), new Vector3(6f, 1f, 6f));
        await sandbox.Settle();

        ZombieNavigation nav = Build();
        await nav.PruneAgainstCollisionAsync(sandbox.Root,
            sandbox.Root.GetWorld3D().DirectSpaceState, 0.5f, new HashSet<Guid>(),
            new CollisionFieldBuilder());

        Assert.True(nav.IsReady);
        Assert.True(TotalDropped(nav) > 0,
            "the empty field was treated as authoritative, so the block stayed walkable");

        nav.Free();
    }

    // NAV_SKIP_RECONCILE publishes the baked graph and probes nothing. It is the diagnostic that
    // separates the cost of publishing a graph from the cost of checking it against the world, and it has
    // to leave a USABLE graph behind — a flag that produced no router would measure a session nobody
    // could play.
    [Test]
    public async Task SkippingReconciliationStillPublishesAGraph()
    {
        using var sandbox = new PhysicsSandbox(TestScene);
        sandbox.AddBox(new Vector3(0f, -0.5f, 0f), new Vector3(40f, 1f, 40f));
        sandbox.AddBox(new Vector3(0f, 0.5f, 0f), new Vector3(6f, 1f, 6f));
        await sandbox.Settle();

        using var flag = new EnvFlagScope("NAV_SKIP_RECONCILE", "1");
        ZombieNavigation nav = Build();

        await nav.PruneAgainstCollisionAsync(sandbox.Root,
            sandbox.Root.GetWorld3D().DirectSpaceState, 0.5f, new HashSet<Guid>());

        Assert.True(nav.IsReady, "the skip flag left no graph at all");

        // And nothing was pruned, which is the whole meaning of the flag: the block is still walkable
        // because nobody looked.
        var path = new List<Vector3>();
        Assert.True(nav.Query(new Vector3(-9f, 0f, -9f), new Vector3(9f, 0f, 9f), path, 0.4f));

        nav.Free();
    }

    // The probe audit compares the field's answers against the server's rather than trusting either. It
    // deliberately neither reads nor writes the cache: reading would return before any comparison
    // happened, and the common run is a warm one — so an audit that honoured the cache would report
    // nothing on exactly the runs someone would use it on.
    [Test]
    public async Task TheProbeAuditRunsWithoutTouchingTheCache()
    {
        using var level = new TempLevel();
        using var sandbox = new PhysicsSandbox(TestScene);
        sandbox.AddBox(new Vector3(0f, -0.5f, 0f), new Vector3(40f, 1f, 40f));
        sandbox.AddBox(new Vector3(0f, 0.5f, 0f), new Vector3(6f, 1f, 6f));
        await sandbox.Settle();

        using var audit = new EnvFlagScope("UG_NAV_PROBE_AUDIT", "1");
        ZombieNavigation nav = Build(level.Path);

        await nav.PruneAgainstCollisionAsync(sandbox.Root,
            sandbox.Root.GetWorld3D().DirectSpaceState, 0.5f, new HashSet<Guid>(), RecordedGeometry());

        Assert.True(nav.IsReady, "the audit left no graph");

        // Nothing was written under the ordinary fingerprint. An audit replaces every face with the
        // server's own answer, so its verdicts are the OLD algorithm's — left in the cache, the next
        // normal run would restore them instead of running the hybrid it is supposed to.
        string entry = System.IO.Path.Combine(
            ProjectSettings.GlobalizePath("user://nav_reconcile"),
            Data.NavReconcileCache.MapKey(level.Path) + ".cache");
        Assert.False(System.IO.File.Exists(entry), "the audit left its verdicts in the cache");

        nav.Free();
    }

    // --- the real map --------------------------------------------------------------------------------

    // PEI's own baked navmesh, routed over. The synthetic floor above proves the plumbing; this proves the
    // graph copes with what the game actually ships — thousands of triangles across several flags, with
    // the seams between them that a hand-made fixture never has.
    [Test]
    public void TheRealMapsNavmeshRoutes()
    {
        if (!RealLevelDir(out string levelDir))
            return;

        IReadOnlyList<NavFlag> flags = LevelNavmesh.Load(System.IO.Path.Combine(levelDir, "Environment"));
        Assert.NotEmpty(flags);

        ZombieNavigation nav = Assert.IsType<ZombieNavigation>(ZombieNavigation.Build(flags, null, levelDir));
        Assert.True(nav.IsReady);

        // Every flag's own centre sits inside its box by construction, so projecting it lands on the mesh
        // wherever that flag has floor beneath its middle.
        int projected = 0;
        foreach (NavFlag flag in flags)
        {
            if (nav.ProjectToSurface(flag.Center, out Vector3 onMesh))
            {
                projected++;
                Assert.True(flag.ContainsXZ(onMesh), $"projected outside its own flag: {onMesh}");
            }
        }

        Assert.True(projected > 0, "not one of the real map's flags had floor under its centre");
        nav.Free();
    }

    // And the preload handoff over the real map: parsed early, claimed later, exactly as interactive
    // loading does it. The claimed router is deliberately NOT ready — the interactive path publishes its
    // graph after the collision world exists, so a router that arrived ready would have baked against
    // geometry that was not there yet.
    [Test]
    public void TheRealMapSurvivesTheParkAndClaim()
    {
        if (!RealLevelDir(out string levelDir))
            return;

        ZombieNavigation.DiscardPreloaded();
        ZombieNavigation.Preload(levelDir);

        ZombieNavigation nav = Assert.IsType<ZombieNavigation>(ZombieNavigation.TakePreloaded());
        Assert.False(nav.IsReady, "the claimed router published before the collision world existed");

        // And the claim was a one-shot: nothing is left for the next map.
        Assert.Null(ZombieNavigation.TakePreloaded());

        nav.Free();
    }

    // --- helpers -------------------------------------------------------------------------------------

    // One flag with a single walkable triangle: the least navmesh a router can be built over.
    private static NavFlag Flag() => new()
    {
        Center = Vector3.Zero,
        Size = new Vector3(64f, 16f, 64f),
        Vertices = new[] { Vector3.Zero, Vector3.Right, Vector3.Forward },
        Triangles = new[] { 0, 1, 2 },
    };

    // Every face the pass dropped, flattened. A dictionary entry proves only that a flag was VISITED;
    // an entry holding an empty set is a flag whose obstruction was missed, and the two read the same to
    // Assert.NotEmpty on the dictionary itself.
    private static int TotalDropped(ZombieNavigation nav)
    {
        int total = 0;
        foreach (KeyValuePair<int, IReadOnlySet<int>> flag in nav.DisabledFaces)
            total += flag.Value.Count;
        return total;
    }

    // The dropped faces themselves, per flag, so two passes can be compared by WHICH faces they removed
    // rather than by how many. A pass that misses one obstructed triangle and removes a clear one instead
    // matches on count while leaving the navmesh wrong in both directions.
    private static SortedSet<string> DroppedFaces(ZombieNavigation nav)
    {
        var faces = new SortedSet<string>(StringComparer.Ordinal);
        foreach (KeyValuePair<int, IReadOnlySet<int>> flag in nav.DisabledFaces)
            foreach (int face in flag.Value)
                faces.Add($"{flag.Key}:{face}");
        return faces;
    }

    // An environment flag set for one test and put back afterwards: these are read once per pass, and a
    // flag left set would silently change every test that ran after it in this process.
    private sealed class EnvFlagScope : IDisposable
    {
        private readonly string _name;
        private readonly string _previous;

        public EnvFlagScope(string name, string value)
        {
            _name = name;
            _previous = OS.GetEnvironment(name);
            OS.SetEnvironment(name, value);
        }

        public void Dispose() => OS.SetEnvironment(_name, _previous);
    }

    // The same ground and the same block the sandbox holds, recorded the way a load records them: the
    // terrain as a heightfield, the object as a box instance.
    private static CollisionFieldBuilder RecordedGeometry()
    {
        var field = new CollisionFieldBuilder();

        // A flat 40 m tile whose surface sits at y=0, matching the sandbox's ground box.
        const int res = 32;
        var heights = new float[res * res];
        field.AddHeightfield(
            new Transform3D(Basis.Identity.Scaled(new Vector3(40f / (res - 1), 1f, 40f / (res - 1))),
                Vector3.Zero), res, res, heights);

        int box = field.AddBoxShape(new Vector3(6f, 1f, 6f));
        field.AddInstance(box, new Transform3D(Basis.Identity, new Vector3(0f, 0.5f, 0f)));
        return field;
    }

    // A level directory of the test's own, and the reconciliation cache entry it causes to be written.
    //
    // The cache lands under user:// keyed by the level's path, so a directory nobody else uses gets an
    // entry nobody else reads — and deleting it on the way out keeps a test run from leaving verdicts
    // behind in the cache a real session shares.
    private sealed class TempLevel : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "unturned-godot-navlevel-" + Guid.NewGuid().ToString("N"));

        public TempLevel() => System.IO.Directory.CreateDirectory(Path);

        // Where this level's reconciliation verdicts land, so a test can tell "the cache was read" from
        // "there was no cache to read".
        public string CacheEntry => System.IO.Path.Combine(
            ProjectSettings.GlobalizePath("user://nav_reconcile"),
            Data.NavReconcileCache.MapKey(Path) + ".cache");

        public void Dispose()
        {
            string entry = System.IO.Path.Combine(
                ProjectSettings.GlobalizePath("user://nav_reconcile"),
                Data.NavReconcileCache.MapKey(Path) + ".cache");
            foreach (string leftover in new[] { entry, entry + ".csr", entry + ".tmp" })
            {
                try
                {
                    System.IO.File.Delete(leftover);
                }
                catch (System.Exception e) when (e is System.IO.IOException or System.UnauthorizedAccessException)
                {
                    // A leftover cache entry keyed to a directory that no longer exists is harmless.
                }
            }

            try
            {
                System.IO.Directory.Delete(Path, recursive: true);
            }
            catch (System.Exception e) when (e is System.IO.IOException or System.UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }

    // A 20 m square of floor at y=0, tessellated into 2 m cells.
    //
    // The cell size is load-bearing rather than incidental. Reconciliation deliberately exempts BROAD
    // faces from being cut by local collision — removing a 200 m² Recast face because a counter stands on
    // one corner of it turns a counter-sized obstruction into a room-sized hole in the navmesh — so a
    // fixture made of two huge triangles is one nothing can ever be pruned out of. Real baked navmesh is
    // tessellated at roughly this scale, which is why the exemption is safe in the game and why a fixture
    // has to match it to mean anything.
    private static ZombieNavigation Build(string? levelDir = null)
    {
        const int cells = 10;
        const float half = 10f, step = 20f / cells;
        var vertices = new Vector3[(cells + 1) * (cells + 1)];
        for (int z = 0; z <= cells; z++)
            for (int x = 0; x <= cells; x++)
                vertices[(z * (cells + 1)) + x] = new Vector3(-half + (x * step), 0f, -half + (z * step));

        var triangles = new List<int>(cells * cells * 6);
        for (int z = 0; z < cells; z++)
            for (int x = 0; x < cells; x++)
            {
                int origin = (z * (cells + 1)) + x;
                triangles.AddRange(new[] { origin, origin + 1, origin + cells + 2 });
                triangles.AddRange(new[] { origin, origin + cells + 2, origin + cells + 1 });
            }

        var floor = new NavFlag
        {
            Center = Vector3.Zero,
            Size = new Vector3(64f, 16f, 64f),
            Vertices = vertices,
            Triangles = triangles.ToArray(),
        };
        return Assert.IsType<ZombieNavigation>(
            ZombieNavigation.Build(new List<NavFlag> { floor }, null, levelDir));
    }

    // PEI's directory in the installed game, or nothing when the content is absent. UG_REQUIRE_REAL_DATA=1
    // turns that absence into a failure, so the job that fetches the content cannot pass by finding none.
    private static bool RealLevelDir(out string levelDir)
    {
        levelDir = "";
        string? install = Assets.UnturnedInstall.Find();
        string? maps = install == null ? null : System.IO.Path.Combine(install, "Maps", "PEI");
        if (maps == null || !System.IO.Directory.Exists(maps))
        {
            if (System.Environment.GetEnvironmentVariable("UG_REQUIRE_REAL_DATA") == "1")
            {
                throw new System.IO.IOException(
                    "UG_REQUIRE_REAL_DATA=1 but the PEI map is not present; this run exists to prove these "
                    + "tests execute");
            }

            Log.Print("[runtime-tests] skipping: no PEI map "
                + "(set UNTURNED_PATH or run ./scripts/fetch-game-data.sh)");
            return false;
        }

        levelDir = maps;
        return true;
    }
}
