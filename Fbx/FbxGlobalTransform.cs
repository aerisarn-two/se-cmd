using NIFSharp;
using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// A node's placement in the world, and the local transform that would put it there.
    /// </summary>
    /// <remarks>
    /// The FBX SDK calls this <c>EvaluateGlobalTransform</c>, and ck-cmd leans on it in
    /// both directions: it writes a blend collision object relative to its parent's
    /// global transform (§4.9) and reads the global one back for a rig (L4884). There
    /// is no SDK here, so the chain is walked directly.
    ///
    /// It matters for collision. A NIF rigid body's transform is a *world* transform
    /// even when the body hangs off a bone several levels down, which is exactly what a
    /// skeleton does — so a body written as a node's local transform ends up displaced
    /// by everything above it.
    /// </remarks>
    public static class FbxGlobalTransform
    {
        /// <summary>How deep an ancestor chain is followed before giving up.</summary>
        /// <remarks>A cycle in the connections would otherwise not terminate.</remarks>
        private const int MaxDepth = 64;

        /// <summary>The transform that places a node in the world.</summary>
        public static NifTransform Of(FbxScene scene, FbxObject node)
        {
            NifTransform transform = LocalOf(node);
            FbxObject current = node;

            for (int depth = 0; depth < MaxDepth; depth++)
            {
                if (ParentModelOf(scene, current) is not { } parent)
                    break;

                transform = transform.ComposedWith(LocalOf(parent));
                current = parent;
            }

            return transform;
        }

        /// <summary>
        /// The local transform that would place a node at <paramref name="world"/>
        /// under <paramref name="parent"/>.
        /// </summary>
        public static NifTransform Under(FbxScene scene, FbxObject? parent, NifTransform world)
        {
            if (parent is null)
                return world;

            NifTransform above = Of(scene, parent);

            return System.Numerics.Matrix4x4.Invert(above.ToMatrix(), out var inverted)
                ? NifTransform.FromMatrix(world.ToMatrix() * inverted)
                : world;
        }

        /// <summary>
        /// A node's own transform, as FBX stores it.
        /// </summary>
        /// <remarks>
        /// Rotation is Euler XYZ in degrees, and a non-uniform scale is averaged since
        /// NIF cannot hold one. The importer reports that averaging where it matters;
        /// here it only feeds a composition.
        /// </remarks>
        public static NifTransform LocalOf(FbxObject node)
        {
            (double tx, double ty, double tz) = node.Properties.GetVector3("Lcl Translation");
            (double rx, double ry, double rz) = node.Properties.GetVector3("Lcl Rotation");
            (double sx, double sy, double sz) = node.Properties.GetVector3("Lcl Scaling", 1.0);

            return new NifTransform(
                new NifVector3((float)tx, (float)ty, (float)tz),
                NifTransform.RotationFromEulerDegrees((float)rx, (float)ry, (float)rz),
                (float)((sx + sy + sz) / 3.0));
        }

        /// <summary>
        /// The node a node hangs from, or null at the scene root.
        /// </summary>
        /// <remarks>
        /// A node is connected to exactly one Model, its parent. Everything else that
        /// joins nodes — a constraint's attachment point, a mesh, a deformer — hangs
        /// the other way round, as a child of the node rather than a destination of it.
        /// </remarks>
        private static FbxObject? ParentModelOf(FbxScene scene, FbxObject node) =>
            scene.ParentsOf(node.Id).FirstOrDefault(o => o.Class == "Model");
    }
}
