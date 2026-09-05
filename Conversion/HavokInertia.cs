using NIFSharp;
using SECmd.Nif;

namespace SECmd.Conversion
{
    /// <summary>
    /// The inertia tensor of a Havok shape of a given mass.
    /// </summary>
    /// <remarks>
    /// ck-cmd hands the shape to Havok and lets <c>hkpInertiaTensorComputer</c> do
    /// this. There is no Havok here to ask, so the tensors are computed directly —
    /// which is possible because Havok's are the textbook ones for a solid body of
    /// uniform density, and they agree with the files ck-cmd generated to every digit
    /// those files store.
    ///
    /// The tensor is not decoration. It is what decides how a body resists being spun,
    /// so a wrong one is a crate that tumbles like a pencil, and a zero one is a body
    /// with no resistance at all.
    ///
    /// All of these are about the centre of mass and are diagonal, because the shapes
    /// are symmetric about their own axes. A convex hull is not, and is handled by
    /// integrating over its faces.
    /// </remarks>
    public static class HavokInertia
    {
        /// <summary>A solid box, from its half-extents.</summary>
        /// <remarks>
        /// <c>m/12 (a² + b²)</c> over the two full edge lengths perpendicular to each
        /// axis, which in half-extents is <c>m/3 (h² + h²)</c>.
        /// </remarks>
        public static NifMatrix33 Box(float mass, NifVector3 halfExtents)
        {
            float x = halfExtents.X * halfExtents.X;
            float y = halfExtents.Y * halfExtents.Y;
            float z = halfExtents.Z * halfExtents.Z;

            float scale = mass / 3f;

            return Diagonal(scale * (y + z), scale * (x + z), scale * (x + y));
        }

        /// <summary>A solid sphere: <c>2/5 m r²</c> about every axis.</summary>
        public static NifMatrix33 Sphere(float mass, float radius)
        {
            float i = 0.4f * mass * radius * radius;

            return Diagonal(i, i, i);
        }

        /// <summary>
        /// A solid capsule: a cylinder with a hemisphere on each end.
        /// </summary>
        /// <remarks>
        /// The two parts are integrated separately and added, with the hemispheres
        /// shifted out to their own centres by the parallel axis theorem. The axis is
        /// the line between the two points, so the result is rotated back into the
        /// body's frame rather than left along z.
        /// </remarks>
        public static NifMatrix33 Capsule(float mass, NifVector3 first, NifVector3 second, float radius)
        {
            float dx = second.X - first.X, dy = second.Y - first.Y, dz = second.Z - first.Z;
            float length = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

            if (length < 1e-9f)
                return Sphere(mass, radius);

            float r2 = radius * radius;

            // Split the mass between the parts by volume.
            float cylinder = MathF.PI * r2 * length;
            float caps = 4f / 3f * MathF.PI * r2 * radius;
            float total = cylinder + caps;

            if (total < 1e-12f)
                return Diagonal(0f, 0f, 0f);

            float cylinderMass = mass * (cylinder / total);
            float capMass = mass * (caps / total);

            // Along the axis, and across it.
            float along = 0.5f * cylinderMass * r2 + 0.4f * capMass * r2;

            float across = cylinderMass * (3f * r2 + length * length) / 12f
                           + capMass * (0.4f * r2 + 0.375f * radius * length + 0.25f * length * length);

            return AboutAxis(new NifVector3(dx / length, dy / length, dz / length), along, across);
        }

        /// <summary>
        /// A solid convex hull, integrated over its triangles.
        /// </summary>
        /// <remarks>
        /// Each face triangle makes a tetrahedron with the origin, whose signed volume
        /// and second moments are known in closed form; summing them over a closed
        /// surface gives the whole body, with the tetrahedra outside it cancelling.
        /// The result is then shifted to the centre of mass.
        /// </remarks>
        public static NifMatrix33 Convex(float mass, MeshGeometry hull)
        {
            double volume = 0;
            double cx = 0, cy = 0, cz = 0;
            double xx = 0, yy = 0, zz = 0, xy = 0, xz = 0, yz = 0;

            foreach (NifTriangle t in hull.Triangles)
            {
                NifVector3 a = hull.Vertices[t.V1], b = hull.Vertices[t.V2], c = hull.Vertices[t.V3];

                double d = Determinant(a, b, c);

                volume += d;
                cx += d * (a.X + b.X + c.X);
                cy += d * (a.Y + b.Y + c.Y);
                cz += d * (a.Z + b.Z + c.Z);

                xx += d * Square(a.X, b.X, c.X);
                yy += d * Square(a.Y, b.Y, c.Y);
                zz += d * Square(a.Z, b.Z, c.Z);
                xy += d * Product(a.X, b.X, c.X, a.Y, b.Y, c.Y);
                xz += d * Product(a.X, b.X, c.X, a.Z, b.Z, c.Z);
                yz += d * Product(a.Y, b.Y, c.Y, a.Z, b.Z, c.Z);
            }

            if (Math.Abs(volume) < 1e-18)
                return Diagonal(0f, 0f, 0f);

            // The sums are proportional to the integrals, and the mass supplies the
            // density, so every constant depending only on volume divides out. The two
            // families do not share a constant: over a tetrahedron the squared terms
            // integrate with det/60 and the cross terms with det/120, which after
            // dividing by the volume leaves a factor of two between them.
            double centreX = cx / (4 * volume), centreY = cy / (4 * volume), centreZ = cz / (4 * volume);

            double squares = mass / (volume * 10);
            double products = mass / (volume * 20);

            xx *= squares; yy *= squares; zz *= squares;
            xy *= products; xz *= products; yz *= products;

            // Shift to the centre of mass.
            xx -= mass * (centreX * centreX);
            yy -= mass * (centreY * centreY);
            zz -= mass * (centreZ * centreZ);
            xy -= mass * (centreX * centreY);
            xz -= mass * (centreX * centreZ);
            yz -= mass * (centreY * centreZ);

            var tensor = new NifMatrix33
            {
                M11 = (float)(yy + zz),
                M22 = (float)(xx + zz),
                M33 = (float)(xx + yy),
                M12 = (float)-xy,
                M21 = (float)-xy,
                M13 = (float)-xz,
                M31 = (float)-xz,
                M23 = (float)-yz,
                M32 = (float)-yz
            };

            return tensor;
        }

        private static double Determinant(NifVector3 a, NifVector3 b, NifVector3 c) =>
            (double)a.X * (b.Y * c.Z - b.Z * c.Y)
            - (double)a.Y * (b.X * c.Z - b.Z * c.X)
            + (double)a.Z * (b.X * c.Y - b.Y * c.X);

        private static double Square(float a, float b, float c) =>
            (double)a * a + (double)b * b + (double)c * c + (double)a * b + (double)b * c + (double)c * a;

        private static double Product(float a1, float b1, float c1, float a2, float b2, float c2) =>
            2 * ((double)a1 * a2 + (double)b1 * b2 + (double)c1 * c2)
            + (double)a1 * b2 + (double)b1 * a2
            + (double)b1 * c2 + (double)c1 * b2
            + (double)c1 * a2 + (double)a1 * c2;

        private static NifMatrix33 Diagonal(float xx, float yy, float zz) =>
            new() { M11 = xx, M22 = yy, M33 = zz };

        /// <summary>
        /// A tensor that is <paramref name="along"/> about an axis and
        /// <paramref name="across"/> about everything perpendicular to it.
        /// </summary>
        private static NifMatrix33 AboutAxis(NifVector3 axis, float along, float across)
        {
            // across*I + (along - across) * (axis outer axis): diagonal in the axis's
            // own frame, and this is that expression written out in the body's.
            float d = along - across;

            return new NifMatrix33
            {
                M11 = across + d * axis.X * axis.X,
                M22 = across + d * axis.Y * axis.Y,
                M33 = across + d * axis.Z * axis.Z,
                M12 = d * axis.X * axis.Y,
                M21 = d * axis.X * axis.Y,
                M13 = d * axis.X * axis.Z,
                M31 = d * axis.X * axis.Z,
                M23 = d * axis.Y * axis.Z,
                M32 = d * axis.Y * axis.Z
            };
        }
    }
}
