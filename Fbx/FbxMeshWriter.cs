using MeshIO.Formats.Fbx;
using SECmd.Conversion;
using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// Writes a mesh into an FBX scene as a <c>Geometry</c> object, and the
    /// <c>Model</c> nodes that carry them.
    /// </summary>
    /// <remarks>
    /// Emits the layout FBXWrangler produces: attributes mapped
    /// <c>ByControlPoint</c> / <c>Direct</c>, one triangle per polygon, and the UV
    /// element named <c>"UV Map"</c>. That name is not cosmetic — Blender will not
    /// merge UV maps across meshes unless they share a name.
    /// </remarks>
    public static class FbxMeshWriter
    {
        /// <summary>The UV element name that lets Blender merge UV maps across meshes.</summary>
        public const string UvElementName = "UV Map";

        /// <summary>
        /// The name on the tangent and binormal elements, which names the UV set they
        /// were derived from — a tangent frame only means anything with respect to one.
        /// </summary>
        public const string TangentElementName = UvElementName;

        private const int GeometryVersion = 124;
        private const int LayerElementVersion = 101;
        private const int LayerVersion = 100;
        private const int ModelVersion = 232;

        /// <summary>
        /// Adds a mesh as a <c>Geometry</c> object. UVs are expected already in FBX
        /// convention, i.e. with V flipped relative to NIF.
        /// </summary>
        public static FbxObject AddGeometry(FbxScene scene, string name, MeshGeometry mesh)
        {
            FbxObject geometry = scene.AddObject("Geometry", name, "Mesh");
            FbxNode node = geometry.Node;

            node.Nodes.Add(new FbxNode("GeometryVersion", GeometryVersion));

            // Control points, flattened.
            var vertices = new double[mesh.Vertices.Count * 3];

            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                NifVector3 v = mesh.Vertices[i];
                vertices[i * 3] = v.X;
                vertices[i * 3 + 1] = v.Y;
                vertices[i * 3 + 2] = v.Z;
            }

            node.Nodes.Add(new FbxNode("Vertices", vertices));

            // Polygons. FBX marks the last corner of each polygon by storing its
            // bitwise complement, which is how a flat index list encodes polygon
            // boundaries without a separate size array.
            var indices = new int[mesh.Triangles.Count * 3];

            for (int i = 0; i < mesh.Triangles.Count; i++)
            {
                NifTriangle t = mesh.Triangles[i];
                indices[i * 3] = t.V1;
                indices[i * 3 + 1] = t.V2;
                indices[i * 3 + 2] = ~t.V3;
            }

            node.Nodes.Add(new FbxNode("PolygonVertexIndex", indices));

            var layerElements = new List<string>();

            if (mesh.HasNormals)
            {
                node.Nodes.Add(BuildVector3Element("LayerElementNormal", "Normals", string.Empty, mesh.Normals));
                layerElements.Add("LayerElementNormal");
            }

            // The tangent frame, which is a property of the vertex and not of the
            // surface: two vertices in the same place, facing the same way, with the
            // same texture coordinate can still have different tangents, and in the
            // game's meshes they routinely do.
            //
            // Leaving these out did not merely lose the frame — the reader tells two
            // vertices apart by comparing all eighteen numbers a vertex holds, and six
            // of them were always zero because nothing had written them. So vertices
            // the file said were different looked identical and were merged:
            // `norpullchainanim01`'s gear catch has 78 vertices, of which 46 are
            // distinct in position, normal and texture coordinate and all 78 are
            // distinct once the tangents are counted. It came back as 46.
            //
            // FBX was never the constraint. Every element here is ByControlPoint, one
            // value per vertex, so the file can hold exactly what the NIF holds.
            if (mesh.HasTangents)
            {
                node.Nodes.Add(BuildVector3Element(
                    "LayerElementTangent", "Tangents", TangentElementName, mesh.Tangents));

                node.Nodes.Add(BuildVector3Element(
                    "LayerElementBinormal", "Binormals", TangentElementName, mesh.Bitangents));

                layerElements.Add("LayerElementTangent");
                layerElements.Add("LayerElementBinormal");
            }

            if (mesh.HasUvs)
            {
                var uv = new double[mesh.Uvs.Count * 2];

                for (int i = 0; i < mesh.Uvs.Count; i++)
                {
                    uv[i * 2] = mesh.Uvs[i].X;
                    uv[i * 2 + 1] = mesh.Uvs[i].Y;
                }

                var element = new FbxNode("LayerElementUV", 0);
                element.Nodes.Add(new FbxNode("Version", LayerElementVersion));
                element.Nodes.Add(new FbxNode("Name", UvElementName));
                element.Nodes.Add(new FbxNode("MappingInformationType", "ByControlPoint"));
                element.Nodes.Add(new FbxNode("ReferenceInformationType", "Direct"));
                element.Nodes.Add(new FbxNode("UV", uv));

                node.Nodes.Add(element);
                layerElements.Add("LayerElementUV");
            }

            // The two per-vertex words nothing else has a channel for: the fourth word
            // of an SSE vertex, and the eye marker. A second UV set carries them,
            // because FBX has no per-vertex scalar of its own and a UV set is the one
            // channel every DCC tool keeps. Named, so the reader can tell it from the
            // real one -- LayerElement.Find takes the first element of a kind, and the
            // real UVs are written above.
            //
            // A uint in a double is exact below 2^53, so the fourth word survives
            // whole rather than as a float that happens to look like it.
            if (mesh.HasUnusedW || mesh.HasEyeData)
            {
                int count = Math.Max(mesh.UnusedW.Count, mesh.EyeData.Count);
                var extra = new double[count * 2];

                for (int i = 0; i < count; i++)
                {
                    extra[i * 2] = i < mesh.UnusedW.Count ? mesh.UnusedW[i] : 0d;
                    extra[i * 2 + 1] = i < mesh.EyeData.Count ? mesh.EyeData[i] : 0d;
                }

                var element = new FbxNode("LayerElementUV", 1);
                element.Nodes.Add(new FbxNode("Version", LayerElementVersion));
                element.Nodes.Add(new FbxNode("Name", VertexExtraElementName));
                element.Nodes.Add(new FbxNode("MappingInformationType", "ByControlPoint"));
                element.Nodes.Add(new FbxNode("ReferenceInformationType", "Direct"));
                element.Nodes.Add(new FbxNode("UV", extra));

                node.Nodes.Add(element);
            }

            if (mesh.HasPartitionMask)
            {
                // The mask in U, nothing in V. A double carries a 32-bit mask exactly,
                // so it comes back the integer it went out as.
                var membership = new double[mesh.PartitionMask.Count * 2];

                for (int i = 0; i < mesh.PartitionMask.Count; i++)
                    membership[i * 2] = mesh.PartitionMask[i];

                var element = new FbxNode("LayerElementUV", 2);
                element.Nodes.Add(new FbxNode("Version", LayerElementVersion));
                element.Nodes.Add(new FbxNode("Name", VertexPartitionElementName));
                element.Nodes.Add(new FbxNode("MappingInformationType", "ByControlPoint"));
                element.Nodes.Add(new FbxNode("ReferenceInformationType", "Direct"));
                element.Nodes.Add(new FbxNode("UV", membership));

                node.Nodes.Add(element);
            }

            if (mesh.HasColors)
            {
                var colors = new double[mesh.Colors.Count * 4];

                for (int i = 0; i < mesh.Colors.Count; i++)
                {
                    NifColor4 c = mesh.Colors[i];
                    colors[i * 4] = c.R;
                    colors[i * 4 + 1] = c.G;
                    colors[i * 4 + 2] = c.B;
                    colors[i * 4 + 3] = c.A;
                }

                var element = new FbxNode("LayerElementColor", 0);
                element.Nodes.Add(new FbxNode("Version", LayerElementVersion));
                element.Nodes.Add(new FbxNode("Name", "VertexColor"));
                element.Nodes.Add(new FbxNode("MappingInformationType", "ByControlPoint"));
                element.Nodes.Add(new FbxNode("ReferenceInformationType", "Direct"));
                element.Nodes.Add(new FbxNode("Colors", colors));

                node.Nodes.Add(element);
                layerElements.Add("LayerElementColor");
            }

            node.Nodes.Add(BuildLayer(layerElements));

            return geometry;
        }

        /// <summary>
        /// Adds the material element that assigns a single material to the whole
        /// mesh, which is the only case NIF has: one shape, one material.
        /// </summary>
        /// <summary>
        /// Adds a material element that assigns a material per polygon.
        /// </summary>
        /// <remarks>
        /// The one place NIF needs this is a `BSLODTriShape`, whose triangles are
        /// partitioned by level of detail. FBX has no notion of a LOD group, and a
        /// material per face is the one per-polygon channel every DCC tool exposes and
        /// lets an artist reassign — which is what makes the levels authorable rather
        /// than merely reproducible.
        /// </remarks>
        /// <summary>The name of the UV set carrying the two unchannelled vertex words.</summary>
        public const string VertexExtraElementName = "nif_vertex_extra";

        /// <summary>The name of the UV set carrying skin partition membership.</summary>
        /// <remarks>
        /// Its own set rather than another lane of the extra one, because the two are
        /// live under different conditions: the extra words belong to a shape without
        /// tangents or with eyes, and this belongs to a shape with several partitions,
        /// and a shape can be either without being both.
        /// </remarks>
        public const string VertexPartitionElementName = "nif_vertex_partition";

        public static void AddPerPolygonMaterialElement(FbxObject geometry, IReadOnlyList<int> perPolygon)
        {
            // The shape already has the one-material element every mesh gets; a mesh
            // has one material layer, so this replaces it rather than joining it.
            geometry.Node.Nodes.RemoveAll(n => n.Name == "LayerElementMaterial");

            var element = new FbxNode("LayerElementMaterial", 0);
            element.Nodes.Add(new FbxNode("Version", LayerElementVersion));
            element.Nodes.Add(new FbxNode("Name", string.Empty));
            element.Nodes.Add(new FbxNode("MappingInformationType", "ByPolygon"));
            element.Nodes.Add(new FbxNode("ReferenceInformationType", "IndexToDirect"));
            element.Nodes.Add(new FbxNode("Materials", perPolygon.ToArray()));

            FbxNode? layer = geometry.Node.Nodes.FirstOrDefault(n => n.Name == "Layer");
            int at = layer is null ? geometry.Node.Nodes.Count : geometry.Node.Nodes.IndexOf(layer);
            geometry.Node.Nodes.Insert(at, element);
        }

        public static void AddSingleMaterialElement(FbxObject geometry)
        {
            var element = new FbxNode("LayerElementMaterial", 0);
            element.Nodes.Add(new FbxNode("Version", LayerElementVersion));
            element.Nodes.Add(new FbxNode("Name", string.Empty));
            element.Nodes.Add(new FbxNode("MappingInformationType", "AllSame"));
            element.Nodes.Add(new FbxNode("ReferenceInformationType", "IndexToDirect"));
            element.Nodes.Add(new FbxNode("Materials", new[] { 0 }));

            // Insert before the Layer record so the layer can reference it.
            FbxNode? layer = geometry.Node.Nodes.FirstOrDefault(n => n.Name == "Layer");
            int at = layer is null ? geometry.Node.Nodes.Count : geometry.Node.Nodes.IndexOf(layer);
            geometry.Node.Nodes.Insert(at, element);

            if (layer is not null)
                AddLayerElement(layer, "LayerElementMaterial");
        }

        private static FbxNode BuildVector3Element(
            string elementName, string arrayName, string name, IReadOnlyList<NifVector3> values)
        {
            var data = new double[values.Count * 3];

            for (int i = 0; i < values.Count; i++)
            {
                data[i * 3] = values[i].X;
                data[i * 3 + 1] = values[i].Y;
                data[i * 3 + 2] = values[i].Z;
            }

            var element = new FbxNode(elementName, 0);
            element.Nodes.Add(new FbxNode("Version", LayerElementVersion));
            element.Nodes.Add(new FbxNode("Name", name));
            element.Nodes.Add(new FbxNode("MappingInformationType", "ByControlPoint"));
            element.Nodes.Add(new FbxNode("ReferenceInformationType", "Direct"));
            element.Nodes.Add(new FbxNode(arrayName, data));

            return element;
        }

        private static FbxNode BuildLayer(IEnumerable<string> elementTypes)
        {
            var layer = new FbxNode("Layer", 0);
            layer.Nodes.Add(new FbxNode("Version", LayerVersion));

            foreach (string type in elementTypes)
                AddLayerElement(layer, type);

            return layer;
        }

        private static void AddLayerElement(FbxNode layer, string type)
        {
            var entry = new FbxNode("LayerElement");
            entry.Nodes.Add(new FbxNode("Type", type));
            entry.Nodes.Add(new FbxNode("TypedIndex", 0));
            layer.Nodes.Add(entry);
        }

        /// <summary>
        /// Adds a <c>Model</c> node with a transform.
        /// </summary>
        /// <param name="subClass">
        /// "Mesh" for a node carrying geometry, "Null" for a plain transform,
        /// "LimbNode" for a skeleton joint.
        /// </param>
        public static FbxObject AddModel(FbxScene scene, string name, string subClass, NifTransform transform)
        {
            FbxObject model = scene.AddObject("Model", name, subClass);
            FbxNode node = model.Node;

            node.Nodes.Add(new FbxNode("Version", ModelVersion));

            NifVector3 t = transform.Translation;
            NifVector3 r = transform.ToEulerDegrees();
            float s = transform.Scale;

            // Only write channels that differ from the default, keeping files close
            // to what other exporters produce.
            if (t.X != 0 || t.Y != 0 || t.Z != 0)
                model.Properties.Set("Lcl Translation", "Lcl Translation", "", "A", (double)t.X, (double)t.Y, (double)t.Z);

            if (r.X != 0 || r.Y != 0 || r.Z != 0)
                model.Properties.Set("Lcl Rotation", "Lcl Rotation", "", "A", (double)r.X, (double)r.Y, (double)r.Z);

            if (Math.Abs(s - 1f) > 1e-6f)
                model.Properties.Set("Lcl Scaling", "Lcl Scaling", "", "A", (double)s, (double)s, (double)s);

            // Scale is inherited normally; NIF has no other mode.
            model.Properties.Set("InheritType", "enum", "", "", 1);

            node.Nodes.Add(new FbxNode("MultiLayer", 0));
            node.Nodes.Add(new FbxNode("MultiTake", 0));

            // FBX's one-byte boolean is property type 'C', which MeshIO models as a
            // char in both directions. Passing a bool here writes nothing MeshIO can
            // serialise and the save fails.
            node.Nodes.Add(new FbxNode("Shading", (char)1));

            node.Nodes.Add(new FbxNode("Culling", "CullingOff"));

            return model;
        }
    }
}
