using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// Carries a <c>BSMultiBoundNode</c>'s culling volume through FBX.
    /// </summary>
    /// <remarks>
    /// A multi-bound node carries its own bounding volume, which the engine culls
    /// against instead of working one out from the geometry. That is the whole point of
    /// the class: a room whose walls are drawn only when the player can see into it, or
    /// a chunk of landscape that is skipped whole.
    ///
    /// The class already survives the trip (§5.2), so what is left is the payload. It
    /// is three blocks deep — the node names a <c>BSMultiBound</c>, which names a
    /// <c>BSMultiBoundData</c>, which is an oriented box or a sphere — and none of it
    /// has an FBX equivalent, so it rides as properties on the node like the rest.
    ///
    /// Losing it leaves a multi-bound node with no bound. Nothing looks wrong; the
    /// engine culls against nothing, and the saving the node existed for is gone.
    /// </remarks>
    public static class FbxMultiBound
    {
        /// <summary>Names the data class, and marks that a volume travelled at all.</summary>
        public const string TypeProperty = "multi_bound_type";

        /// <summary>Prefix on the volume's own fields.</summary>
        public const string Prefix = "mb_";

        /// <summary>The culling mode, which sits on the node rather than on the volume.</summary>
        public const string CullingProperty = "multi_bound_culling";

        /// <summary>
        /// Suffix on the mesh that shows the volume.
        /// </summary>
        /// <remarks>
        /// The volume is written twice, as the collision material and the effect
        /// shader are: the properties above are exact and are what the import reads,
        /// and this is a mesh an artist can see and resize. A culling volume that
        /// exists only as six numbers is one nobody will ever notice is wrong.
        ///
        /// The import recognises the suffix and skips it, so the mesh never becomes
        /// geometry in the rebuilt file.
        /// </remarks>
        public const string MeshSuffix = "_multibound";

        /// <summary>Whether a node is the mesh drawn for a volume rather than real geometry.</summary>
        public static bool IsVolumeMesh(string name) =>
            name.EndsWith(MeshSuffix, StringComparison.Ordinal);

        /// <summary>Records the node's bound, if it has one.</summary>
        public static void Write(FbxObject node, NifModel model, NifItem block)
        {
            if (!model.BlockInherits(block, "BSMultiBoundNode"))
                return;

            if (model.FindItem(block, "Culling Mode") is { } culling)
            {
                node.Properties.SetUserString(
                    CullingProperty,
                    culling.Value.ToUInt().ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (model.GetRef(block, "Multi Bound") is not { } bound
                || model.GetRef(bound, "Data") is not { } data)
            {
                return;
            }

            node.Properties.SetUserString(TypeProperty, data.Name);

            NifFieldCodec.Write(
                model, data, Prefix,
                (name, value) => node.Properties.SetUserString(name, value));
        }


        /// <summary>Takes the volume's placement and size off the mesh standing for it.</summary>
        private static void RefitFromMesh(FbxScene scene, FbxObject node, NifModel model, NifItem data)
        {
            FbxObject? holder = scene.ChildrenOf(node.Id)
                .FirstOrDefault(o => o.Class == "Model" && IsVolumeMesh(o.Name));

            if (holder is null)
                return;

            FbxObject? geometry = scene.ChildrenOf(holder.Id)
                .FirstOrDefault(o => o.Class == "Geometry");

            if (geometry is null || FbxMeshReader.Read(geometry) is not { Vertices.Count: > 0 } mesh)
                return;

            // Where it sits and which way it faces, from the holder's own transform --
            // the same two numbers the export put there.
            (double tx, double ty, double tz) = holder.Properties.GetVector3("Lcl Translation");
            (double rx, double ry, double rz) = holder.Properties.GetVector3("Lcl Rotation");

            var centre = new NifVector3((float)tx, (float)ty, (float)tz);

            model.FindItem(data, "Center")?.Value.Set(centre);

            model.FindItem(data, "Rotation")?.Value.Set(
                NifTransform.RotationFromEulerDegrees((float)rx, (float)ry, (float)rz));

            // And how big it is, from the geometry in its own space. `Size` is the full
            // length of each side where the fit gives half of it, which is the same
            // doubling the export halves.
            (NifVector3 _, NifVector3 half) = SECmd.Conversion.ShapeFitter.FitBox(mesh.Vertices);

            model.FindItem(data, "Size")?.Value.Set(
                new NifVector3(half.X * 2f, half.Y * 2f, half.Z * 2f));

            // A sphere states one number instead, and the fit's largest half-extent is it.
            model.FindItem(data, "Radius")?.Value
                .SetFloat(MathF.Max(half.X, MathF.Max(half.Y, half.Z)));
        }

        /// <summary>Rebuilds the bound and hangs it back on the node.</summary>
        /// <remarks>
        /// The volume is taken from the mesh, the way a collision shape is. The `_multibound`
        /// mesh is exported at the volume's centre, turned by its rotation, and built to its
        /// size, so all three are readable back off it: the holder's transform gives the
        /// centre and the rotation and the geometry's own extents give the size. Editing the
        /// box in a DCC now moves the volume, which is what someone doing that expects, and
        /// it is what makes the visible half worth having rather than decoration.
        ///
        /// ck-cmd is no guide here -- `FBXWrangler.cpp` has no occurrence of `MultiBound` in
        /// either direction, and its only mention of the class anywhere is commented-out
        /// viewer code.
        ///
        /// The carried fields remain as a fallback for a scene with no such mesh, which is
        /// a data class this build tessellates nothing for. Where the mesh is there, it
        /// wins.
        /// </remarks>
        public static void Read(
            FbxScene scene, FbxObject node, NifModel model, NifItem block, List<string> warnings)
        {
            if (!model.BlockInherits(block, "BSMultiBoundNode"))
                return;

            if (node.Properties.GetString(CullingProperty) is { Length: > 0 } culling
                && uint.TryParse(culling, System.Globalization.NumberStyles.Integer,
                                 System.Globalization.CultureInfo.InvariantCulture, out uint mode))
            {
                model.FindItem(block, "Culling Mode")?.Value.SetCount(mode);
            }

            string type = node.Properties.GetString(TypeProperty);

            if (type.Length == 0)
                return;

            if (!model.KnowsBlock(type) || !model.Database.Inherits(type, "BSMultiBoundData"))
            {
                warnings.Add(
                    $"{model.GetName(block)}: \"{type}\" is not a multi-bound volume this build knows, "
                    + "the node keeps none");

                return;
            }

            NifItem data = model.InsertBlock(type);

            NifFieldCodec.Read(
                model, data, Prefix,
                name => node.Properties.GetString(name) is { Length: > 0 } value ? value : null);

            // ...and then the mesh, which overrides them where it exists.
            RefitFromMesh(scene, node, model, data);

            NifItem bound = model.InsertBlock("BSMultiBound");

            model.SetRef(bound, "Data", data);
            model.SetRef(block, "Multi Bound", bound);
        }
    }
}
