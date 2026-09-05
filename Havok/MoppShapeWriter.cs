using NIFSharp;
using System.Globalization;
using System.Text;
using SECmd.Nif;

namespace SECmd.Havok
{
    /// <summary>
    /// Describes a Havok shape tree in the grammar mopper's <c>-clm</c> reads.
    /// </summary>
    /// <remarks>
    /// A MOPP tree indexes a shape collection, and Havok will build one over any
    /// container — a `bhkListShape` of primitives as readily as a mesh. What it cannot
    /// do is guess the primitives, so the backend builds them, and this is what tells
    /// it what to build. ck-cmd reaches the same place by holding the real Havok shapes
    /// in memory; there are none here, so they are described instead.
    ///
    /// Every length is already in Havok units: that is what a `bhk` block stores.
    ///
    /// A shape this does not know makes the whole description null rather than a
    /// partial one. A tree built over some of a collection would index children by a
    /// number that no longer means what it did, which is worse than no tree.
    /// </remarks>
    public static class MoppShapeWriter
    {
        /// <summary>How deep a shape tree is followed before it is called a loop.</summary>
        private const int MaxDepth = 16;

        /// <summary>
        /// The description of a shape tree, or null when it holds something unknown.
        /// </summary>
        public static string? Describe(NifModel model, NifItem shape)
        {
            var text = new StringBuilder();

            return Write(model, shape, text, 0) ? text.ToString() : null;
        }

        private static bool Write(NifModel model, NifItem shape, StringBuilder text, int depth)
        {
            if (depth > MaxDepth)
                return false;

            switch (shape.Name)
            {
                case "bhkSphereShape":
                    text.Append("sphere ").Append(Number(Radius(model, shape))).Append('\n');
                    return true;

                case "bhkBoxShape":
                {
                    NifVector3 half = model.FindItem(shape, "Dimensions")?.Value.Get<NifVector3>()
                                      ?? new NifVector3();

                    text.Append("box ")
                        .Append(Number(half.X)).Append(' ')
                        .Append(Number(half.Y)).Append(' ')
                        .Append(Number(half.Z)).Append(' ')
                        .Append(Number(Radius(model, shape))).Append('\n');

                    return true;
                }

                case "bhkCapsuleShape":
                    return WriteSegment(
                        model, shape, text, "capsule", "First Point", "Second Point", "Radius");

                case "bhkCylinderShape":
                    return WriteSegment(
                        model, shape, text, "cylinder", "Vertex A", "Vertex B", "Cylinder Radius");

                case "bhkConvexVerticesShape":
                {
                    if (model.FindItem(shape, "Vertices") is not { } vertices)
                        return false;

                    text.Append("convex ").Append(vertices.Children.Count).Append('\n');

                    foreach (NifItem vertex in vertices.Children)
                    {
                        NifVector4 v = vertex.Value.Get<NifVector4>();

                        text.Append(Number(v.X)).Append(' ')
                            .Append(Number(v.Y)).Append(' ')
                            .Append(Number(v.Z)).Append('\n');
                    }

                    text.Append(Number(Radius(model, shape))).Append('\n');

                    return true;
                }

                case "bhkListShape":
                {
                    var children = model.GetRefArray(shape, "Sub Shapes").ToList();

                    if (children.Count == 0)
                        return false;

                    text.Append("list ").Append(children.Count).Append('\n');

                    foreach (NifItem child in children)
                    {
                        if (!Write(model, child, text, depth + 1))
                            return false;
                    }

                    return true;
                }

                case "bhkTransformShape":
                case "bhkConvexTransformShape":
                {
                    if (model.GetRef(shape, "Shape") is not { } inner)
                        return false;

                    text.Append("transform");

                    // Column major, which is what hkTransform::set4x4ColumnMajor wants.
                    foreach (float value in ColumnMajor(model, shape))
                        text.Append(' ').Append(Number(value));

                    text.Append('\n');

                    return Write(model, inner, text, depth + 1);
                }

                default:
                    return false;
            }
        }

        private static bool WriteSegment(
            NifModel model, NifItem shape, StringBuilder text,
            string keyword, string firstField, string secondField, string radiusField)
        {
            NifVector3 first = PointOf(model, shape, firstField);
            NifVector3 second = PointOf(model, shape, secondField);

            text.Append(keyword).Append(' ')
                .Append(Number(first.X)).Append(' ')
                .Append(Number(first.Y)).Append(' ')
                .Append(Number(first.Z)).Append(' ')
                .Append(Number(second.X)).Append(' ')
                .Append(Number(second.Y)).Append(' ')
                .Append(Number(second.Z)).Append(' ')
                .Append(Number(model.FindItem(shape, radiusField)?.Value.ToFloat() ?? 0f))
                .Append('\n');

            return true;
        }

        /// <summary>
        /// A point, whether the field holds three components or four.
        /// </summary>
        /// <remarks>
        /// A capsule stores its ends as `Vector3`, a cylinder as `Vector4` whose fourth
        /// component repeats the radius. Same point, two spellings.
        /// </remarks>
        private static NifVector3 PointOf(NifModel model, NifItem shape, string field)
        {
            if (model.FindItem(shape, field) is not { } item)
                return new NifVector3();

            if (item.Children.Count >= 4)
            {
                NifVector4 v = item.Value.Get<NifVector4>();

                return new NifVector3(v.X, v.Y, v.Z);
            }

            return item.Value.Get<NifVector3>();
        }

        private static float Radius(NifModel model, NifItem shape) =>
            model.FindItem(shape, "Radius")?.Value.ToFloat() ?? 0f;

        /// <summary>
        /// The shape's transform as sixteen floats, column major.
        /// </summary>
        /// <remarks>
        /// A NIF stores it row by row; `hkTransform::set4x4ColumnMajor` wants it the
        /// other way. Transposing here rather than in mopper keeps the grammar saying
        /// what it means — "column major" — instead of "whatever a NIF happens to
        /// hold".
        /// </remarks>
        private static IEnumerable<float> ColumnMajor(NifModel model, NifItem shape)
        {
            NifMatrix44 m = model.FindItem(shape, "Transform")?.Value.Get<NifMatrix44>()
                            ?? NifMatrix44.Identity;

            return
            [
                m.M11, m.M21, m.M31, m.M41,
                m.M12, m.M22, m.M32, m.M42,
                m.M13, m.M23, m.M33, m.M43,
                m.M14, m.M24, m.M34, m.M44
            ];
        }

        private static string Number(float value) =>
            value.ToString("R", CultureInfo.InvariantCulture);
    }
}
