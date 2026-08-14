using System.IO;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using UnturnedGodot.Player;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests.Player;

public class PhysicsMaterialFrictionTests
{
    private const string IceAsset = """
    Metadata
    {
    	GUID 16ca1781f893452ea0b460e7191006e2
    	Type SDG.Unturned.PhysicsMaterialAsset, Assembly-CSharp, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
    }
    Asset
    {
    	UnityNames
    	[
    		Ice
    	]
    	Fallback "33650ff924b34f8d9c5a0fd97418cd3e"
    	TireMotionEffect "3050f44b94b649a7b4cdc8123a90cd7a"
    	Character_Friction_Mode Custom
    	Character_Acceleration_Multiplier 1
    	Character_Deceleration_Multiplier 0.5
    	Character_Max_Speed_Multiplier 1.2
    }
    """;

    private const string GravelAsset = """
    Metadata
    {
    	GUID 33650ff924b34f8d9c5a0fd97418cd3e
    	Type SDG.Unturned.PhysicsMaterialAsset, Assembly-CSharp, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
    }
    Asset
    {
    	UnityNames
    	[
    		Gravel
    	]
    	IsArable true
    	HasOil true
    }
    """;

    private static PhysicsMaterialAsset Parse(string text)
    {
        Assert.True(PhysicsMaterialAsset.TryParse(DatParser.Parse(text), out PhysicsMaterialAsset? asset));
        return asset!;
    }

    [Fact]
    public void ReadsTheFrictionBlock()
    {
        PhysicsMaterialAsset ice = Parse(IceAsset);

        Assert.Equal(EPhysicsMaterialCharacterFrictionMode.Custom, ice.CharacterFrictionMode);
        Assert.Equal(1f, ice.CharacterAccelerationMultiplier);
        Assert.Equal(0.5f, ice.CharacterDecelerationMultiplier);
        Assert.Equal(1.2f, ice.CharacterMaxSpeedMultiplier);
        Assert.Equal(System.Guid.Parse("3050f44b94b649a7b4cdc8123a90cd7a"), ice.TireMotionEffect);
    }

    // "if (characterFrictionMode != ImmediatelyResponsive) { ... }" — an asset that spells out a
    // multiplier without switching mode has it IGNORED, so reading it would invent a value.
    [Fact]
    public void MultipliersAreIgnoredWithoutACustomMode()
    {
        PhysicsMaterialAsset asset = Parse(IceAsset.Replace(
            "Character_Friction_Mode Custom", "Character_Friction_Mode ImmediatelyResponsive",
            System.StringComparison.Ordinal));

        Assert.Equal(EPhysicsMaterialCharacterFrictionMode.ImmediatelyResponsive,
            asset.CharacterFrictionMode);
        Assert.Null(asset.CharacterAccelerationMultiplier);
        Assert.Null(asset.CharacterDecelerationMultiplier);
        Assert.Null(asset.CharacterMaxSpeedMultiplier);
    }

    [Fact]
    public void NoFrictionKeyAtAll_LeavesEverythingUnset()
    {
        PhysicsMaterialAsset gravel = Parse(GravelAsset);

        Assert.Equal(EPhysicsMaterialCharacterFrictionMode.ImmediatelyResponsive,
            gravel.CharacterFrictionMode);
        Assert.Null(gravel.CharacterAccelerationMultiplier);
        Assert.Null(gravel.CharacterMaxSpeedMultiplier);
    }

    // Nullability is load-bearing: a multiplier the asset does not name must stay null so the fallback
    // chain can supply it, rather than resetting to 1.
    [Fact]
    public void UnnamedMultipliersStayNull()
    {
        PhysicsMaterialAsset asset = Parse("""
        Metadata
        {
        	GUID 16ca1781f893452ea0b460e7191006e2
        	Type SDG.Unturned.PhysicsMaterialAsset
        }
        Asset
        {
        	UnityNames
        	[
        		Partial
        	]
        	Character_Friction_Mode Custom
        	Character_Deceleration_Multiplier 0.25
        }
        """);

        Assert.Equal(0.25f, asset.CharacterDecelerationMultiplier);
        Assert.Null(asset.CharacterAccelerationMultiplier);
        Assert.Null(asset.CharacterMaxSpeedMultiplier);
    }

    [Fact]
    public void IsArableAndHasOilAreRead()
    {
        PhysicsMaterialAsset gravel = Parse(GravelAsset);
        Assert.True(gravel.IsArable);
        Assert.True(gravel.HasOil);

        PhysicsMaterialAsset ice = Parse(IceAsset);
        Assert.Null(ice.IsArable); // unset, so the fallback chain answers
        Assert.Null(ice.HasOil);
    }

    private static PhysicsMaterialBank Bank(params string[] assets)
    {
        var bank = new PhysicsMaterialBank();
        foreach (string text in assets)
            bank.Add(Parse(text));
        return bank;
    }

    [Fact]
    public void BankResolvesFrictionByMaterialName()
    {
        CharacterFrictionProperties friction = Bank(IceAsset, GravelAsset).FindCharacterFriction("Ice");

        Assert.Equal(EPhysicsMaterialCharacterFrictionMode.Custom, friction.Mode);
        Assert.Equal(1f, friction.AccelerationMultiplier);
        Assert.Equal(0.5f, friction.DecelerationMultiplier);
        Assert.Equal(1.2f, friction.MaxSpeedMultiplier);
    }

    // An unknown name returns the seeded defaults — the instant movement everything had before.
    [Fact]
    public void UnknownMaterial_IsTheDefault() =>
        Assert.Equal(CharacterFrictionProperties.Default,
            Bank(IceAsset).FindCharacterFriction("NoSuchSurface"));

    // Not the audio walk: each of the four properties independently takes the first asset along the
    // chain that carries it, so a surface setting only one inherits the rest from its fallback.
    [Fact]
    public void EachPropertyTakesTheFirstAnswerAlongTheChain()
    {
        const string partial = """
        Metadata
        {
        	GUID aaaaaaaaaaaa4f0a9c1d2e3f4a5b6c7d
        	Type SDG.Unturned.PhysicsMaterialAsset
        }
        Asset
        {
        	UnityNames
        	[
        		Slush
        	]
        	Fallback "16ca1781f893452ea0b460e7191006e2"
        	Character_Friction_Mode Custom
        	Character_Deceleration_Multiplier 0.9
        }
        """;

        CharacterFrictionProperties friction =
            Bank(partial, IceAsset, GravelAsset).FindCharacterFriction("Slush");

        Assert.Equal(0.9f, friction.DecelerationMultiplier);  // its own
        Assert.Equal(1.2f, friction.MaxSpeedMultiplier);      // inherited from Ice, not reset to 1
        Assert.Equal(1f, friction.AccelerationMultiplier);
    }

    // "if (!hasMode && info.characterFrictionMode != ImmediatelyResponsive)": the default mode does not
    // count as an answer, so a Custom fallback still reaches a surface that omitted the key.
    [Fact]
    public void DefaultModeDoesNotBlockACustomFallback()
    {
        const string quiet = """
        Metadata
        {
        	GUID bbbbbbbbbbbb4f0a9c1d2e3f4a5b6c7d
        	Type SDG.Unturned.PhysicsMaterialAsset
        }
        Asset
        {
        	UnityNames
        	[
        		Quiet
        	]
        	Fallback "16ca1781f893452ea0b460e7191006e2"
        }
        """;

        CharacterFrictionProperties friction = Bank(quiet, IceAsset).FindCharacterFriction("Quiet");

        Assert.Equal(EPhysicsMaterialCharacterFrictionMode.Custom, friction.Mode);
        Assert.Equal(1.2f, friction.MaxSpeedMultiplier);
    }

    [Fact]
    public void FlagsAndTireEffectWalkTheChainToo()
    {
        PhysicsMaterialBank bank = Bank(IceAsset, GravelAsset);

        Assert.True(bank.FindIsArable("Ice"));  // inherited from Gravel
        Assert.True(bank.FindHasOil("Ice"));
        Assert.False(bank.FindIsArable("NoSuchSurface"));
        Assert.False(bank.FindHasOil("NoSuchSurface"));
        Assert.Equal(System.Guid.Parse("3050f44b94b649a7b4cdc8123a90cd7a"),
            bank.FindTireMotionEffect("Ice"));
        Assert.Equal(System.Guid.Empty, bank.FindTireMotionEffect("Gravel"));
        Assert.Equal(System.Guid.Empty, bank.FindTireMotionEffect("NoSuchSurface"));
    }

    // A fallback cycle must terminate rather than spin, the same hop cap the audio walk uses.
    [Fact]
    public void FallbackCycleTerminates()
    {
        const string a = """
        Metadata
        {
        	GUID aaaaaaaaaaaa4f0a9c1d2e3f4a5b6c7d
        	Type SDG.Unturned.PhysicsMaterialAsset
        }
        Asset
        {
        	UnityNames
        	[
        		A
        	]
        	Fallback "bbbbbbbbbbbb4f0a9c1d2e3f4a5b6c7d"
        }
        """;
        const string b = """
        Metadata
        {
        	GUID bbbbbbbbbbbb4f0a9c1d2e3f4a5b6c7d
        	Type SDG.Unturned.PhysicsMaterialAsset
        }
        Asset
        {
        	UnityNames
        	[
        		B
        	]
        	Fallback "aaaaaaaaaaaa4f0a9c1d2e3f4a5b6c7d"
        }
        """;
        PhysicsMaterialBank bank = Bank(a, b);

        Assert.Equal(CharacterFrictionProperties.Default, bank.FindCharacterFriction("A"));
        Assert.False(bank.FindIsArable("A"));
        Assert.Equal(System.Guid.Empty, bank.FindTireMotionEffect("A"));
    }

    // The real Ice.asset. Ice you slip on and walk 20% faster over is the whole example.
    [RealDataFact]
    public void RealIceAsset_SlipsAndIsFaster()
    {
        PhysicsMaterialBank bank = PhysicsMaterialBank.ScanDirectory(
            Path.Combine(GameData.Install!, "Bundles", "Assets", "PhysicsMaterials"));

        CharacterFrictionProperties ice = bank.FindCharacterFriction("Ice");
        Assert.Equal(EPhysicsMaterialCharacterFrictionMode.Custom, ice.Mode);
        Assert.Equal(1f, ice.AccelerationMultiplier);
        Assert.Equal(0.5f, ice.DecelerationMultiplier);
        Assert.Equal(1.2f, ice.MaxSpeedMultiplier);
    }

    // And concrete, which is what "ice walks like concrete" was comparing against, still does not ramp.
    [RealDataFact]
    public void RealConcrete_StaysImmediatelyResponsive()
    {
        PhysicsMaterialBank bank = PhysicsMaterialBank.ScanDirectory(
            Path.Combine(GameData.Install!, "Bundles", "Assets", "PhysicsMaterials"));

        Assert.Equal(EPhysicsMaterialCharacterFrictionMode.ImmediatelyResponsive,
            bank.FindCharacterFriction("Concrete").Mode);
    }
}

public class GroundFrictionTests
{
    private static readonly CharacterFrictionProperties Instant = CharacterFrictionProperties.Default;

    private static readonly CharacterFrictionProperties Ice =
        new(EPhysicsMaterialCharacterFrictionMode.Custom, 1f, 0.5f, 1.2f);

    private static readonly Vector3 FlatGround = Vector3.Up;

    [Fact]
    public void IsInstant_OnlyForTheDefaultMode()
    {
        Assert.True(GroundFriction.IsInstant(Instant));
        Assert.False(GroundFriction.IsInstant(Ice));
    }

    // The branch the port already had: velocity is set outright to the walk direction along the floor.
    [Fact]
    public void ImmediatelyResponsive_SetsVelocityOutright()
    {
        Vector3 desired = new Vector3(1, 0, 0) * PlayerConfig.SpeedStand;

        Vector3 result = GroundFriction.Apply(Vector3.Zero, desired, FlatGround,
            PlayerConfig.SpeedStand, Instant, 0.02f);

        Assert.Equal(PlayerConfig.SpeedStand, result.Length(), 4);
        Assert.Equal(PlayerConfig.SpeedStand, result.X, 4);
    }

    // "We do not allow an upward velocity here because it would bounce us over the top of the ramp."
    [Fact]
    public void ImmediatelyResponsive_ClampsUpwardVelocityOnASlope()
    {
        Vector3 slope = new Vector3(-0.4f, 1f, 0f).Normalized(); // walking uphill along +X
        Vector3 desired = new Vector3(1, 0, 0) * PlayerConfig.SpeedStand;

        Vector3 result = GroundFriction.Apply(Vector3.Zero, desired, slope,
            PlayerConfig.SpeedStand, Instant, 0.02f);

        Assert.True(result.Y <= 0f, $"the step tried to climb: {result}");
    }

    // Custom mode ramps: one 20 ms step from rest cannot reach walking speed.
    [Fact]
    public void Custom_AcceleratesRatherThanSnapping()
    {
        Vector3 desired = new Vector3(1, 0, 0) * PlayerConfig.SpeedStand;

        Vector3 result = GroundFriction.Apply(Vector3.Zero, desired, FlatGround,
            PlayerConfig.SpeedStand, Ice, 0.02f);

        // acceleration = desired * maxSpeed(1.2) * accel(1.0) = 5.4 m/s^2, over 0.02 s = 0.108 m/s.
        Assert.Equal(0.108f, result.Length(), 3);
    }

    // "Questionable units-wise, but pretend base acceleration is proportional to desired speed." Held
    // long enough the body reaches the max-speed multiple of the stance speed, not the stance speed.
    [Fact]
    public void Custom_TopsOutAtTheMaxSpeedMultiple()
    {
        Vector3 desired = new Vector3(1, 0, 0) * PlayerConfig.SpeedStand;
        Vector3 velocity = Vector3.Zero;
        for (int step = 0; step < 400; step++)
            velocity = GroundFriction.Apply(velocity, desired, FlatGround,
                PlayerConfig.SpeedStand, Ice, 0.02f);

        Assert.Equal(PlayerConfig.SpeedStand * 1.2f, velocity.Length(), 3);
    }

    // "Base deceleration is 2.0 m/s²", halved by Ice's 0.5 — so releasing the stick on ice bleeds speed
    // at 1 m/s² and the body slides.
    [Fact]
    public void Custom_DeceleratesAtTheBaseRateTimesTheMultiplier()
    {
        Vector3 moving = new(6f, 0, 0);
        Vector3 desired = Vector3.Zero; // no input: desired speed is zero

        Vector3 result = GroundFriction.Apply(moving, desired, FlatGround, 0f, Ice, 1f);

        // 6 - (2.0 * 0.5 * 1s) = 5
        Assert.Equal(5f, result.Length(), 3);
    }

    // The same release on a surface with the base deceleration loses twice as much.
    [Fact]
    public void Custom_FullDecelerationIsTwiceAsFast()
    {
        var grippy = new CharacterFrictionProperties(
            EPhysicsMaterialCharacterFrictionMode.Custom, 1f, 1f, 1f);

        Vector3 result = GroundFriction.Apply(new Vector3(6f, 0, 0), Vector3.Zero, FlatGround, 0f,
            grippy, 1f);

        Assert.Equal(4f, result.Length(), 3);
    }

    // "Mathf.Max(desiredSpeed, ...)" — deceleration never undershoots the speed being aimed for.
    [Fact]
    public void Custom_DecelerationStopsAtTheDesiredSpeed()
    {
        Vector3 desired = new Vector3(1, 0, 0) * 4f;

        Vector3 result = GroundFriction.Apply(new Vector3(4.2f, 0, 0), desired, FlatGround, 4f,
            Ice, 10f); // a huge step: without the floor this would reverse

        Assert.Equal(4f * 1.2f, result.Length(), 3);
    }

    // ---- the two Unity helpers, whose edge cases the step relies on ------------------------------

    [Fact]
    public void ClampMagnitude_ShortensOnlyWhenLonger()
    {
        Assert.Equal(new Vector3(3, 0, 0), GroundFriction.ClampMagnitude(new Vector3(3, 0, 0), 5f));
        Assert.Equal(5f, GroundFriction.ClampMagnitude(new Vector3(10, 0, 0), 5f).Length(), 4);
    }

    // A non-positive max collapses the vector rather than flipping it.
    [Fact]
    public void ClampMagnitude_NonPositiveMaxIsZero()
    {
        Assert.Equal(Vector3.Zero, GroundFriction.ClampMagnitude(new Vector3(3, 0, 0), 0f));
        Assert.Equal(Vector3.Zero, GroundFriction.ClampMagnitude(new Vector3(3, 0, 0), -1f));
    }

    [Fact]
    public void ProjectOnPlane_RemovesTheNormalComponent()
    {
        Vector3 projected = GroundFriction.ProjectOnPlane(new Vector3(1, 5, 0), Vector3.Up);
        Assert.Equal(0f, projected.Y, 5);
        Assert.Equal(1f, projected.X, 5);
    }

    // A degenerate normal must not divide by zero: a raycast normal is only approximately unit-length,
    // and a zero one has no plane to project onto.
    [Fact]
    public void ProjectOnPlane_ZeroNormalIsTheIdentity() =>
        Assert.Equal(new Vector3(1, 5, 0), GroundFriction.ProjectOnPlane(new Vector3(1, 5, 0), Vector3.Zero));

    // A non-unit normal still projects correctly, because the divide is by its squared length.
    [Fact]
    public void ProjectOnPlane_HandlesANonUnitNormal()
    {
        Vector3 projected = GroundFriction.ProjectOnPlane(new Vector3(1, 5, 0), new Vector3(0, 3, 0));
        Assert.Equal(0f, projected.Y, 5);
        Assert.Equal(1f, projected.X, 5);
    }
}
