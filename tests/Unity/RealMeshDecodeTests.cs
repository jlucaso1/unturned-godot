using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

// The mesh reader run over what Unity actually produced, rather than over fixtures this repo generates.
//
// Everything else covering UnityMesh and CompressedMesh builds its own input, which proves the decoder
// agrees with this repo's idea of the format and nothing more — the README's "checked byte-for-byte
// against the game's own data" did not hold for either of them. These assert invariants no fixture can
// fake: that every index of every shipped mesh addresses a vertex that mesh has, that quantized normals
// come back on the unit sphere, and that the skinned rigs decode a full set of weights.
[Trait("Category", "RealData")]
public class RealMeshDecodeTests
{
    private const int MeshClassId = 43;

    private static IEnumerable<UnityMesh> Meshes(SerializedFile file)
    {
        foreach (SerializedObject o in file.Objects)
        {
            if (o.ClassId != MeshClassId)
                continue;
            yield return UnityMesh.Read(TypeTreeReader.Read(o.TypeTree, file.ReaderFor(o)));
        }
    }

    private static IEnumerable<(UnityMesh Mesh, Dictionary<string, object> Raw)> RawMeshes(SerializedFile file)
    {
        foreach (SerializedObject o in file.Objects)
        {
            if (o.ClassId != MeshClassId)
                continue;
            Dictionary<string, object> raw = TypeTreeReader.Read(o.TypeTree, file.ReaderFor(o));
            yield return (UnityMesh.Read(raw), raw);
        }
    }

    [RealDataFact(RequiresMasterBundle = true)]
    public void EveryShippedMesh_IndexesOnlyItsOwnVertices()
    {
        int checkedMeshes = 0;
        foreach (UnityMesh mesh in Meshes(GameData.Prefabs.File))
        {
            if (!mesh.Usable)
                continue;
            checkedMeshes++;

            foreach (int[] submesh in mesh.Submeshes)
            {
                Assert.True(submesh.Length % 3 == 0,
                    $"{mesh.Name}: a triangle-list submesh of {submesh.Length} indices");
                foreach (int index in submesh)
                {
                    Assert.True((uint)index < (uint)mesh.Vertices.Length,
                        $"{mesh.Name}: index {index} against {mesh.Vertices.Length} vertices");
                }
            }
        }

        // The core bundle carries thousands; a run that silently checked none would pass every assert.
        Assert.True(checkedMeshes > 1000, $"only {checkedMeshes} meshes decoded out of the masterbundle");
    }

    [RealDataFact(RequiresMasterBundle = true)]
    public void EveryShippedMesh_HasAttributeArraysMatchingItsVertexCount()
    {
        foreach (UnityMesh mesh in Meshes(GameData.Prefabs.File))
        {
            if (!mesh.Usable)
                continue;

            // A channel is either absent or complete: a partial one means the stride arithmetic drifted.
            Assert.True(mesh.Normals.Length is 0 || mesh.Normals.Length == mesh.Vertices.Length,
                $"{mesh.Name}: {mesh.Normals.Length} normals for {mesh.Vertices.Length} vertices");
            Assert.True(mesh.Uvs.Length is 0 || mesh.Uvs.Length == mesh.Vertices.Length,
                $"{mesh.Name}: {mesh.Uvs.Length} UVs for {mesh.Vertices.Length} vertices");

            foreach (Vector3 normal in mesh.Normals)
            {
                // Authored normals are unit length; anything else means the components were read at the
                // wrong offset or in the wrong format, which is exactly what the old fall-through to
                // BitConverter.ToSingle produced for the narrow formats.
                float length = normal.Length();
                Assert.True(length is 0f or > 0.9f and < 1.1f,
                    $"{mesh.Name}: a normal of length {length}");
            }
        }
    }

    // The quantized path, over the 28 compressed meshes the game's own bundle ships. Nothing checked
    // CompressedMesh against Unity's own output before: its whole test suite ran on vectors this repo
    // packs itself, so a decoder that agreed with the packer would have passed either way.
    [RealDataFact(RequiresMasterBundle = true)]
    public void ShippedCompressedMeshes_DecodeToGeometryOnTheUnitSphere()
    {
        int compressed = 0;
        foreach ((UnityMesh mesh, Dictionary<string, object> raw) in RawMeshes(GameData.Prefabs.File))
        {
            if (Convert.ToInt32(raw["m_MeshCompression"]) == 0)
                continue;
            compressed++;

            Assert.True(mesh.Usable, $"{mesh.Name}: a compressed mesh that decoded to nothing");
            Assert.NotEmpty(mesh.Vertices);
            Assert.True(mesh.Indices.Length % 3 == 0, $"{mesh.Name}: {mesh.Indices.Length} indices");
            foreach (int index in mesh.Indices)
            {
                Assert.True((uint)index < (uint)mesh.Vertices.Length,
                    $"{mesh.Name}: index {index} against {mesh.Vertices.Length} vertices");
            }

            // Normals are stored as two components plus a sign bit, so this is the assertion that the
            // third one was rebuilt correctly rather than merely plausibly.
            foreach (Vector3 normal in mesh.Normals)
                Assert.Equal(1f, normal.Length(), 2);
        }

        Assert.True(compressed > 0, "the masterbundle carries no compressed meshes to check");
    }

    // The regression guard for the first-person arms. Unturned's skinned rigs are not all authored at four
    // influences per vertex: the Viewmodel arms and one rig beside them declare BlendWeight/BlendIndices
    // channels of dimension 2, and a reader that insists on four decoded no weights for them at all —
    // which is what left that rig importing as an unposable bind-pose shell.
    [RealDataFact(RequiresMasterBundle = true)]
    public void EverySkinnedRig_DecodesAFullSetOfWeights()
    {
        SerializedFile? assets = GameData.PlayerAssets;
        Assert.NotNull(assets);

        int rigs = 0;
        foreach (UnityMesh mesh in Meshes(assets!))
        {
            if (mesh.BindPoses.Count == 0 || !mesh.Usable)
                continue;
            rigs++;

            Assert.Equal(mesh.Vertices.Length * UnityMesh.BonesPerVertex, mesh.BoneWeights.Length);
            Assert.Equal(mesh.Vertices.Length * UnityMesh.BonesPerVertex, mesh.BoneIndices.Length);

            for (int v = 0; v < mesh.Vertices.Length; v++)
            {
                float sum = 0f;
                for (int i = 0; i < UnityMesh.BonesPerVertex; i++)
                {
                    int at = (v * UnityMesh.BonesPerVertex) + i;
                    sum += mesh.BoneWeights[at];
                    Assert.InRange(mesh.BoneIndices[at], 0, mesh.BindPoses.Count - 1);
                }

                // Unity normalizes a vertex's weights across the influences it declares, however many
                // that is, so two that add up to one are as complete as four.
                Assert.Equal(1f, sum, 3);
            }
        }

        // The player body, its lower level, and the Viewmodel arms: fewer than this and the rig whose
        // weights this test exists for has dropped out of the file rather than out of the reader.
        Assert.True(rigs >= 5, $"only {rigs} skinned rigs decoded out of resources.assets");
    }
}
