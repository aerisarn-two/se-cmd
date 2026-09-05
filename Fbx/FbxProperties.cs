using LeanMeshIO.Formats.Fbx;

namespace SECmd.Fbx
{
    /// <summary>
    /// The <c>Properties70</c> block of an FBX object: a flat list of named,
    /// typed properties.
    /// </summary>
    /// <remarks>
    /// Each entry is a <c>P</c> record whose first four properties are the name, the
    /// type, a sub-type and a flags string, followed by however many values the type
    /// needs. This is where an FBX carries both the standard attributes (Lcl
    /// Translation, Lcl Rotation, Lcl Scaling) and arbitrary user-defined ones.
    ///
    /// The user-defined case matters here: it is how FBXWrangler smuggles Havok
    /// annotations and float tracks through a scene, so the conversion layer needs
    /// to both read and write them.
    /// </remarks>
    public sealed class FbxProperties
    {
        /// <summary>Flag string marking a property as user-defined rather than standard.</summary>
        public const string UserFlags = "U";

        /// <summary>Flag string for an animatable user-defined property.</summary>
        public const string AnimatableUserFlags = "A+U";

        private readonly FbxNode _node;

        internal FbxProperties(FbxNode node) => _node = node;

        /// <summary>The underlying <c>Properties70</c> record.</summary>
        public FbxNode Node => _node;

        /// <summary>Every property, in order.</summary>
        public IEnumerable<FbxProperty70> All => _node.Nodes.Where(n => n.Name == "P").Select(n => new FbxProperty70(n));

        /// <summary>Finds a property by name, or null.</summary>
        public FbxProperty70? Find(string name)
        {
            foreach (FbxNode child in _node.Nodes)
            {
                if (child.Name == "P" && child.Properties.Count > 0 && child.Properties[0] as string == name)
                    return new FbxProperty70(child);
            }

            return null;
        }

        public bool Contains(string name) => Find(name) is not null;

        /// <summary>The values of a property, or an empty list when it is absent.</summary>
        public IReadOnlyList<object?> ValuesOf(string name) => Find(name)?.Values ?? [];

        /// <summary>Reads a property as a double, falling back to <paramref name="fallback"/>.</summary>
        public double GetDouble(string name, double fallback = 0)
        {
            IReadOnlyList<object?> values = ValuesOf(name);
            return values.Count > 0 ? ToDouble(values[0], fallback) : fallback;
        }

        /// <summary>Reads a three-component property such as a translation or rotation.</summary>
        public (double X, double Y, double Z) GetVector3(string name, double fallback = 0)
        {
            IReadOnlyList<object?> values = ValuesOf(name);

            if (values.Count < 3)
                return (fallback, fallback, fallback);

            return (ToDouble(values[0], fallback), ToDouble(values[1], fallback), ToDouble(values[2], fallback));
        }

        public string GetString(string name, string fallback = "")
        {
            IReadOnlyList<object?> values = ValuesOf(name);
            return values.Count > 0 ? values[0] as string ?? fallback : fallback;
        }

        /// <summary>Whether a property is present, whatever it holds.</summary>
        /// <remarks>
        /// Not the same question as whether it holds anything. A carrier that means
        /// something by an empty string -- a node whose name really is empty -- needs
        /// to tell "absent" from "empty", and every Get above answers both with the
        /// fallback.
        /// </remarks>
        public bool Has(string name) => All.Any(p => p.Name == name);

        public int GetInt(string name, int fallback = 0) => (int)GetDouble(name, fallback);

        public bool GetBool(string name, bool fallback = false) => GetDouble(name, fallback ? 1 : 0) != 0;

        /// <summary>
        /// Adds or replaces a property. Replacing keeps the entry in place, so
        /// property order survives a round trip.
        /// </summary>
        public FbxProperty70 Set(string name, string type, string subType, string flags, params object[] values)
        {
            var record = new FbxNode("P");
            record.Properties.Add(name);
            record.Properties.Add(type);
            record.Properties.Add(subType);
            record.Properties.Add(flags);

            foreach (object value in values)
                record.Properties.Add(value);

            for (int i = 0; i < _node.Nodes.Count; i++)
            {
                FbxNode existing = _node.Nodes[i];

                if (existing.Name == "P" && existing.Properties.Count > 0 && existing.Properties[0] as string == name)
                {
                    _node.Nodes[i] = record;
                    return new FbxProperty70(record);
                }
            }

            _node.Nodes.Add(record);
            return new FbxProperty70(record);
        }

        /// <summary>Sets one of the standard transform channels.</summary>
        public FbxProperty70 SetVector3(string name, double x, double y, double z) =>
            Set(name, "Lcl Translation", "", "A", x, y, z);

        /// <summary>
        /// Adds a user-defined property, the mechanism FBXWrangler uses to carry
        /// Havok data through a scene.
        /// </summary>
        public FbxProperty70 SetUserString(string name, string value) =>
            Set(name, "KString", "", UserFlags, value);

        /// <summary>Adds an animatable user-defined float, used for float tracks.</summary>
        public FbxProperty70 SetUserFloat(string name, double value) =>
            Set(name, "Number", "", AnimatableUserFlags, value);

        public bool Remove(string name)
        {
            for (int i = 0; i < _node.Nodes.Count; i++)
            {
                FbxNode existing = _node.Nodes[i];

                if (existing.Name == "P" && existing.Properties.Count > 0 && existing.Properties[0] as string == name)
                {
                    _node.Nodes.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }

        internal static double ToDouble(object? value, double fallback = 0) => value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            short s => s,
            byte b => b,
            bool x => x ? 1 : 0,
            string t when double.TryParse(t, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double parsed) => parsed,
            _ => fallback
        };
    }

    /// <summary>A single entry of a <c>Properties70</c> block.</summary>
    public readonly struct FbxProperty70(FbxNode node)
    {
        private readonly FbxNode _node = node;

        public FbxNode Node => _node;

        public string Name => _node.Properties.Count > 0 ? _node.Properties[0] as string ?? string.Empty : string.Empty;

        public string Type => _node.Properties.Count > 1 ? _node.Properties[1] as string ?? string.Empty : string.Empty;

        public string SubType => _node.Properties.Count > 2 ? _node.Properties[2] as string ?? string.Empty : string.Empty;

        public string Flags => _node.Properties.Count > 3 ? _node.Properties[3] as string ?? string.Empty : string.Empty;

        /// <summary>The property's values, i.e. everything after the four-item preamble.</summary>
        public IReadOnlyList<object?> Values =>
            _node.Properties.Count > 4 ? _node.Properties.Skip(4).ToList() : [];

        /// <summary>True when the property was added by a tool rather than defined by FBX.</summary>
        public bool IsUserDefined => Flags.Contains('U', StringComparison.Ordinal);

        /// <summary>True when the property can carry animation curves.</summary>
        public bool IsAnimatable => Flags.Contains('A', StringComparison.Ordinal);

        public override string ToString() => $"{Name} : {Type} = {string.Join(", ", Values)}";
    }
}
