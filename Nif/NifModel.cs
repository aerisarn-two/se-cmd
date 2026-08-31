namespace SECmd.Nif
{
    /// <summary>
    /// A loaded NIF: a header, a flat list of blocks, and a footer, each decoded
    /// into a tree of <see cref="NifItem"/> according to nif.xml.
    /// </summary>
    /// <remarks>
    /// A port of the parts of NifSkope's NifModel that actually move bytes, without
    /// the Qt model/view machinery. The shape of the algorithm is unchanged:
    /// <see cref="LoadItem"/> walks a subtree evaluating conditions and recursing,
    /// and bottoms out in a single <see cref="NifIStream.Read"/> per leaf.
    /// </remarks>
    public sealed class NifModel : INifStreamContext
    {
        /// <summary>Refuses arrays larger than this, as a guard against a corrupt length.</summary>
        private const int MaxArraySize = 1024 * 1024 * 8;

        private readonly NifXmlDatabase _db;
        private NifItem _root;
        private NifItem _header;
        private NifItem _footer;
        private bool _bigEndian;

        /// <summary>The packed file version, e.g. 0x14020007 for 20.2.0.7.</summary>
        public uint Version { get; private set; }

        /// <summary>The Bethesda stream version, cached from the header after loading.</summary>
        public uint BSVersion { get; private set; }

        /// <summary>The user version from the header.</summary>
        public uint UserVersion { get; private set; }

        public NifXmlDatabase Database => _db;

        public NifItem Root => _root;

        public NifItem Header => _header;

        public NifItem Footer => _footer;

        /// <summary>The blocks, in file order.</summary>
        public IReadOnlyList<NifItem> Blocks => _blocks;

        private readonly List<NifItem> _blocks = [];

        /// <summary>Diagnostics collected while loading; loading continues past most of them.</summary>
        public List<string> Warnings { get; } = [];

        public NifModel(NifXmlDatabase database)
        {
            _db = database;
            _root = CreateRoot();
            _header = _root.Children[0];
            _footer = _root.Children[1];
        }

        // --- construction -----------------------------------------------------

        private NifItem CreateRoot()
        {
            var root = new NifItem(
                new NifFieldDef { Name = "Root", Type = "Root", Flags = NifFieldFlags.Conditionless },
                null);

            root.AddChild(BuildCompound("NiHeader", "Header", root));
            root.AddChild(BuildCompound("NiFooter", "Footer", root));

            return root;
        }

        private NifItem BuildCompound(string name, string type, NifItem parent)
        {
            var def = new NifFieldDef
            {
                Name = name,
                Type = type,
                Flags = NifFieldFlags.Compound | NifFieldFlags.Conditionless
            };

            var item = new NifItem(def, parent);

            NifBlockDef compound = _db.GetCompound(type)
                ?? throw new NifFormatException($"nif.xml has no struct named {type}");

            foreach (NifFieldDef field in compound.Fields)
                InsertType(item, field);

            return item;
        }

        /// <summary>
        /// Creates the item, or items, that a field definition contributes to
        /// <paramref name="parent"/>.
        /// </summary>
        /// <remarks>
        /// Five shapes, in the order NifSkope tests them: arrays become an empty
        /// branch that <see cref="UpdateArraySize"/> fills later; compounds become a
        /// branch holding the struct's fields; mixins splice the struct's fields
        /// straight into the parent; templated fields resolve <c>#T#</c> against the
        /// nearest enclosing template argument; everything else is a leaf.
        /// </remarks>
        private void InsertType(NifItem parent, NifFieldDef def)
        {
            if (def.IsArray)
            {
                parent.AddChild(new NifItem(def, parent));
                return;
            }

            if (def.IsCompound)
            {
                NifBlockDef? compound = _db.GetCompound(def.Type);

                if (compound is null)
                    return;

                var branch = new NifItem(def, parent);
                parent.AddChild(branch);

                foreach (NifFieldDef field in compound.Fields)
                    InsertType(branch, field);

                return;
            }

            if (def.IsMixin)
            {
                NifBlockDef? compound = _db.GetCompound(def.Type);

                if (compound is null)
                    return;

                // Spliced in: the struct's fields become the parent's own.
                foreach (NifFieldDef field in compound.Fields)
                    InsertType(parent, field);

                return;
            }

            if (def.IsTemplated)
            {
                InsertType(parent, ResolveTemplate(parent, def));
                return;
            }

            var leaf = new NifItem(def, parent) { Value = new NifValue(def.ValueType) };

            // A link that has not been set points at nothing, and nothing is -1 here.
            // Left at zero it would point at the first block in the file, which for a
            // model built from scratch is the root -- so every ref nobody assigned
            // would quietly claim the root as its target.
            if (leaf.Value.IsLink)
                leaf.Value.SetLink(-1);

            // And a string field nobody sets names no string, which is also -1. Left at
            // zero it names the *first* string in the table, and since the root's name
            // is usually the first thing written, that is the name it takes.
            //
            // `NiTextKeyExtraData` is where this showed: `WriteTextKeys` never sets its
            // `Name`, so every animated file this wrote had an extra-data block claiming
            // to be called "Scene Root". All 22 in the fixtures carry -1. Nothing could
            // see it -- the corpus sweeps rebuild from a file whose string fields are
            // already indices, so the authoring default is never exercised by them.
            //
            // Only where the version puts strings in a table; before that they are
            // written inline and an empty one is already right. Same rule as SetString.
            if (leaf.Value.Type is NifValueType.String or NifValueType.FilePath
                && Version >= 0x14010003)
            {
                leaf.Value.ChangeType(NifValueType.StringIndex);
                leaf.Value.SetCount(uint.MaxValue);
            }

            ApplyDefault(leaf);
            parent.AddChild(leaf);
        }

        /// <summary>
        /// Substitutes the enclosing template argument for <c>#T#</c>, producing a
        /// definition with a concrete type.
        /// </summary>
        private NifFieldDef ResolveTemplate(NifItem parent, NifFieldDef def)
        {
            // The template argument may itself be #T#, in which case it comes from
            // further up the tree.
            string argument = parent.Template;
            NifItem? scope = parent;

            while (argument == NifXmlDatabase.TemplatePlaceholder && scope?.Parent is not null)
            {
                scope = scope.Parent;
                argument = scope.Template;
            }

            string type = def.Type == NifXmlDatabase.TemplatePlaceholder ? argument : def.Type;
            string template = def.Template == NifXmlDatabase.TemplatePlaceholder ? argument : def.Template;

            var flags = def.Flags & ~NifFieldFlags.Templated;

            if (_db.IsCompound(type))
                flags |= NifFieldFlags.Compound;

            return new NifFieldDef
            {
                Name = def.Name,
                Type = type,
                Template = template,
                Arg = def.Arg,
                Arr1 = def.Arr1,
                Arr2 = def.Arr2,
                Cond = def.Cond,
                VerCond = def.VerCond,
                Ver1 = def.Ver1,
                Ver2 = def.Ver2,
                Flags = flags,
                ValueType = _db.ResolveType(type),
                Default = def.Default,
                Text = def.Text
            };
        }

        private void ApplyDefault(NifItem item)
        {
            if (item.Def.Default is not { Length: > 0 } text)
                return;

            // A default may name an enum option rather than spell out a number.
            if (_db.TryGetEnumOptionValue(item.Def.Type, text, out uint option))
            {
                item.Value.SetCount(option);
                return;
            }

            SetValueFromString(ref item.Value, text);
        }

        private static void SetValueFromString(ref NifValue value, string text)
        {
            if (value.IsString)
            {
                value.Set(text);
                return;
            }

            if (value.Type == NifValueType.Float)
            {
                if (float.TryParse(text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float f))
                    value.SetFloat(f);

                return;
            }

            if (value.IsCount || value.IsLink)
            {
                string s = text.Trim();

                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    if (ulong.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out ulong hex))
                        value.SetCount(hex);
                }
                else if (long.TryParse(s, System.Globalization.NumberStyles.Integer,
                             System.Globalization.CultureInfo.InvariantCulture, out long signed))
                {
                    value.SetCount(unchecked((ulong)signed));
                }
            }

            // Compound defaults -- vectors and colours -- are parsed too, as a
            // comma-separated list. They used to be left alone on the reasoning that
            // the file overwrites them, which is true of a block being read and false
            // of one being built: the import creates blocks from the schema, and a
            // default nobody applies is a zero.
            //
            // nif.xml writes these as tokens, `#VEC4_1110#` and friends, which the
            // loader has already expanded into "1.0, 1.0, 1.0, 0.0" by the time this
            // sees them.
            string[] parts = text.Split(',');

            if (parts.Length is < 2 or > 4)
                return;

            var numbers = new float[parts.Length];

            for (int i = 0; i < parts.Length; i++)
            {
                if (!float.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out numbers[i]))
                {
                    return;
                }
            }

            // Set by what the field already holds, since that is what the schema said
            // its type was.
            switch (value.Get<object>())
            {
                case NifVector2 when parts.Length == 2:
                    value.Set(new NifVector2(numbers[0], numbers[1]));
                    break;

                case NifVector3 when parts.Length == 3:
                    value.Set(new NifVector3(numbers[0], numbers[1], numbers[2]));
                    break;

                case NifVector4 when parts.Length == 4:
                    value.Set(new NifVector4(numbers[0], numbers[1], numbers[2], numbers[3]));
                    break;

                case NifColor3 when parts.Length == 3:
                    value.Set(new NifColor3(numbers[0], numbers[1], numbers[2]));
                    break;

                case NifColor4 when parts.Length == 4:
                    value.Set(new NifColor4(numbers[0], numbers[1], numbers[2], numbers[3]));
                    break;
            }
        }

        /// <summary>Appends a block of the named type, built from its full inherited field list.</summary>
        public NifItem InsertBlock(string blockType)
        {
            NifBlockDef def = _db.GetBlock(blockType)
                ?? throw new NifFormatException($"unknown block type {blockType}");

            var item = new NifItem(
                new NifFieldDef
                {
                    Name = blockType,
                    Type = "NiBlock",
                    Flags = NifFieldFlags.Conditionless
                },
                _root);

            foreach (NifFieldDef field in _db.GetInheritedFields(def.Id))
                InsertType(item, field);

            // Blocks sit between the header and the footer.
            _root.Children.Insert(_root.Children.Count - 1, item);
            Renumber(_root);
            _blocks.Add(item);

            InitialiseArrays(item);

            // Sizing had to evaluate conditions, and did so against fields that are
            // all still at their defaults. Those answers must not be cached, or a
            // field set afterwards -- "Has Vertex Colors", say -- would be written
            // while the array it guards stayed invisible, producing a block that is
            // short by exactly that array.
            item.InvalidateConditionsRecursive();

            return item;
        }

        /// <summary>
        /// Sizes every array in a freshly built block from its length expression.
        /// </summary>
        /// <remarks>
        /// Writing sizes arrays for itself (see <see cref="PrepareForOutput"/>), so
        /// this is not what keeps a block's length honest. It is here so that code
        /// building a block can reach into an array straight away — asking for
        /// <c>Vertex Weights\[0]\Weight</c> without having to size anything first.
        ///
        /// Arrays whose length reads a count field simply come out empty, which is
        /// correct until the count is set.
        /// </remarks>
        private void InitialiseArrays(NifItem item)
        {
            foreach (NifItem child in item.Children)
            {
                if (child.IsAbstract || !EvalCondition(child))
                    continue;

                if (child.IsArray)
                    UpdateArraySize(child);

                InitialiseArrays(child);
            }
        }

        private static void Renumber(NifItem parent)
        {
            for (int i = 0; i < parent.Children.Count; i++)
                parent.Children[i].Row = i;
        }

        // --- conditions -------------------------------------------------------

        /// <summary>
        /// True when a field is present in the file, taking version bounds, vercond
        /// and cond into account. Results are cached per item.
        /// </summary>
        public bool EvalCondition(NifItem? item)
        {
            if (item is null)
                return false;

            if (!EvalVersion(item))
                return false;

            if (item.CachedCondition is { } cached)
                return cached;

            // Inside an array of fixed compounds, only the first element decides.
            NifItem reference = ConditionCacheItem(item);

            if (!ReferenceEquals(reference, item))
            {
                bool shared = EvalCondition(reference);
                item.SetCondition(shared);
                return shared;
            }

            bool result;

            if (item.Parent is not null && item.Parent != _root && !EvalCondition(item.Parent))
                result = false;
            else if (item.IsConditionless)
                result = true;
            else
                result = item.Def.Cond.Length == 0 || item.Def.CondExpr.EvaluateBool(MakeConditionResolver(item));

            item.SetCondition(result);
            return result;
        }

        /// <summary>True when the file version falls inside the field's declared range.</summary>
        public bool EvalVersion(NifItem item)
        {
            if (item.CachedVersionCondition is { } cached)
                return cached;

            bool result = true;

            if (item.Def.Ver1 != 0 && Version < item.Def.Ver1)
            {
                result = false;
            }
            else if (item.Def.Ver2 != 0 && Version > item.Def.Ver2)
            {
                result = false;
            }
            else if (item.Def.VerCond.Length > 0)
            {
                NifItem reference = ConditionCacheItem(item);

                result = ReferenceEquals(reference, item)
                    ? item.Def.VerExpr.EvaluateBool(VersionResolver)
                    : EvalVersion(reference);
            }

            item.SetVersionCondition(result);
            return result;
        }

        /// <summary>
        /// For a field inside a later element of an array of "fixed compounds",
        /// returns the matching field in the first element, whose condition stands
        /// for the whole array.
        /// </summary>
        /// <remarks>
        /// The Bethesda vertex formats are why this exists: every element of a
        /// Vertex Data array shares one layout, decided once, so evaluating each
        /// element's conditions against itself would be both wrong and quadratic.
        /// </remarks>
        private NifItem ConditionCacheItem(NifItem item)
        {
            NifItem? element = item.Parent;
            NifItem? array = element?.Parent;

            if (element is null || array is null)
                return item;

            if (array.IsArray && element.Row > 0 && _db.IsFixedCompound(element.Type))
                return array.Child(0)?.Child(item.Row) ?? item;

            return item;
        }

        /// <summary>
        /// Resolves names in a <c>vercond</c>. These always talk about the header —
        /// "BS Header\BS Version", "User Version" — never about the field's own
        /// siblings, so they resolve from the header item regardless of where the
        /// field sits.
        /// </summary>
        private Func<object?, object?> VersionResolver => operand =>
        {
            if (operand is not string name)
                return operand;

            NifItem? target = FindItem(_header, name);

            if (target is null)
                return 0;

            if (target.IsCount)
                return target.CountValue;

            if (target.IsFileVersion)
                return target.Value.ToUInt();

            return 0;
        };

        /// <summary>
        /// Builds the callback that turns a name inside a <c>cond</c> into a value.
        /// </summary>
        /// <remarks>
        /// Unlike a vercond, a cond talks about the field's <em>siblings</em>, so
        /// names resolve against the item's parent. Three forms are special:
        /// <c>#ARG#</c> walks up to the nearest enclosing field that supplied an
        /// argument, a leading <c>..\</c> steps out one level, and a
        /// backslash-separated path descends.
        /// </remarks>
        private Func<object?, object?> MakeConditionResolver(NifItem item) => operand =>
        {
            if (operand is not string name)
                return operand;

            NifItem? scope = item;
            bool argIsExpression = false;

            // #ARG# is whatever the enclosing field passed down, which may itself be
            // #ARG# from one level further out.
            while (name == NifXmlDatabase.ArgPlaceholder)
            {
                scope = scope?.Parent;

                if (scope is null)
                    return 0;

                name = scope.Arg;
                argIsExpression = !scope.Def.ArgExpr.IsNop;
            }

            if (argIsExpression && scope is not null)
                return scope.Def.ArgExpr.EvaluateUInt64(MakeConditionResolver(scope));

            // A bare number in a condition is a literal, not a field name.
            if (int.TryParse(name, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int literal))
                return literal;

            NifItem? target = FindItem(scope?.Parent, name);

            if (target is not null)
            {
                // A sibling *array* means the element lining up with this one: the
                // n-th strip's length is the n-th entry of Strip Lengths. This has to
                // be tested before the value below, because an array item carries no
                // value of its own and an unset value reads as a count of zero --
                // which silently made every ragged row empty.
                if (target.IsArray)
                {
                    if (scope is not null && target.Child(scope.Row) is { IsCount: true } peer)
                        return peer.Value.ToUInt64();

                    return 0;
                }

                if (target.IsCount || target.IsFloat)
                    return target.Value.ToUInt64();

                if (target.IsFileVersion)
                    return target.Value.ToUInt();

                return 0;
            }

            // Not a field: nif.xml also lets a condition name a block type, which
            // is how onlyT and excludeT are expressed. The test is against the
            // enclosing block, and it follows inheritance.
            if (_db.IsBlock(name) && scope is not null)
            {
                NifItem? block = TopLevelBlockOf(scope);

                if (block is not null)
                    return _db.Inherits(block.Name, name);
            }

            return 0;
        };

        /// <summary>The block an item belongs to, i.e. its highest non-root ancestor.</summary>
        private NifItem? TopLevelBlockOf(NifItem item)
        {
            NifItem? current = item;

            while (current is not null && current.Parent is not null && current.Parent != _root)
                current = current.Parent;

            return current;
        }

        /// <summary>
        /// Resolves a possibly backslash-separated path against a starting item.
        /// </summary>
        public NifItem? FindItem(NifItem? parent, string path)
        {
            if (parent is null)
                return null;

            int slash = path.IndexOf('\\');

            if (slash > 0)
            {
                string left = path[..slash];
                string right = path[(slash + 1)..];

                NifItem? next = left == ".." ? parent.Parent : FindChild(parent, left);
                return FindItem(next, right);
            }

            return FindChild(parent, path);
        }

        private NifItem? FindChild(NifItem parent, string name)
        {
            foreach (NifItem child in parent.Children)
            {
                if (string.Equals(child.Name, name, StringComparison.Ordinal) && EvalCondition(child))
                    return child;
            }

            return null;
        }

        // --- arrays -----------------------------------------------------------

        /// <summary>Evaluates an array's length expression.</summary>
        public int EvalArraySize(NifItem array)
        {
            if (!array.IsArray)
                return 0;

            return unchecked((int)array.Def.Arr1Expr.EvaluateUInt(MakeConditionResolver(array)));
        }

        /// <summary>
        /// Grows or shrinks an array to the length its expression currently gives.
        /// </summary>
        public bool UpdateArraySize(NifItem array)
        {
            if (!array.IsArray)
                return false;

            if (array.IsBinary)
                return UpdateByteArraySize(array);

            int newSize = EvalArraySize(array);

            if (newSize < 0)
            {
                Warnings.Add($"array {array.Name} has invalid size {newSize}");
                return false;
            }

            if (newSize > MaxArraySize)
            {
                Warnings.Add($"array {array.Name} has implausible size {newSize}");
                return false;
            }

            int oldSize = array.Children.Count;

            if (newSize < oldSize)
            {
                array.RemoveChildrenFrom(newSize);
                return true;
            }

            if (newSize == oldSize)
                return true;

            // One derived definition, shared by every element. An element is
            // unconditional (the array's own condition already decided that), and
            // its arg and inner length have to reach back out of the element to
            // find the fields they name.
            NifFieldDef elementDef = GetElementDef(array);

            for (int i = oldSize; i < newSize; i++)
                InsertType(array, elementDef);

            Renumber(array);
            return true;
        }

        private readonly Dictionary<NifFieldDef, NifFieldDef> _elementDefs = [];

        private NifFieldDef GetElementDef(NifItem array)
        {
            if (_elementDefs.TryGetValue(array.Def, out var cached))
                return cached;

            var def = new NifFieldDef
            {
                Name = array.Def.Name,
                Type = array.Def.Type,
                Template = array.Def.Template,
                Arg = AddParentPrefix(array.Def.Arg),

                // The inner length of a two-dimensional array becomes the element's
                // own length.
                Arr1 = AddParentPrefix(array.Def.Arr2),
                Arr2 = string.Empty,
                Flags = NifFieldFlags.Conditionless
                        | (array.Def.IsCompound ? NifFieldFlags.Compound : NifFieldFlags.None)
                        | (array.Def.IsMultiArray ? NifFieldFlags.Array : NifFieldFlags.None),
                ValueType = array.Def.ValueType,
                Text = array.Def.Text
            };

            _elementDefs[array.Def] = def;
            return def;
        }

        /// <summary>
        /// Rewrites a reference so that it resolves from inside an array element.
        /// A name referring to a sibling of the array has to step out one level;
        /// a plain number is a literal and is left alone.
        /// </summary>
        private static string AddParentPrefix(string reference)
        {
            if (reference.Length == 0)
                return reference;

            foreach (char c in reference)
            {
                if (!char.IsAsciiDigit(c))
                    return @"..\" + reference;
            }

            return reference;
        }

        /// <summary>
        /// A binary array is read as one opaque blob rather than element by element.
        /// </summary>
        private bool UpdateByteArraySize(NifItem array)
        {
            int newSize = EvalArraySize(array);

            // A size of zero is an empty array, not a failure. Only a *negative* one is
            // a size that could not be worked out.
            //
            // This said `<= 0`, and every caller reads false as a hard error: reading
            // one turns into a NifFormatException that rejects the whole file, and
            // writing one throws before a byte is emitted. So a `bhkMoppBvTreeShape`
            // whose `MOPP Code\Data Size` is still at its default of zero could not be
            // written at all, and a file carrying a legitimately empty blob could not
            // be read — the whole file lost, over a field with nothing in it. The
            // ordinary array path two hundred lines up has always treated zero as
            // zero; this is the same rule, applied to the binary case as well.
            if (newSize < 0)
                return false;

            if (array.Children.Count == 0)
            {
                var def = new NifFieldDef
                {
                    Name = array.Name,
                    Type = array.Type,
                    Template = array.Template,
                    Arg = AddParentPrefix(array.Def.Arg),
                    Flags = NifFieldFlags.Binary | NifFieldFlags.Conditionless,
                    ValueType = NifValueType.Blob
                };

                var blob = new NifItem(def, array) { Value = new NifValue(NifValueType.Blob) };
                array.AddChild(blob);
            }

            NifItem child = array.Children[0];

            if (child.Value.AsByteArray().Length != newSize)
                child.Value.Set(new byte[newSize]);

            return true;
        }

        // --- loading ----------------------------------------------------------

        bool INifStreamContext.SetHeaderString(string value, uint peekedVersion) =>
            SetHeaderString(value, peekedVersion);

        private bool SetHeaderString(string value, uint peekedVersion)
        {
            bool recognised = value.StartsWith("NetImmerse File Format", StringComparison.Ordinal)
                || value.StartsWith("Gamebryo", StringComparison.Ordinal)
                || value.StartsWith("NDSNIF", StringComparison.Ordinal)
                || value.StartsWith("NS", StringComparison.Ordinal)
                || value.StartsWith("Joymaster HS1 Object Format - (JMI)", StringComparison.Ordinal);

            if (!recognised)
                throw new NifFormatException($"not a NIF file: unrecognised header \"{value}\"");

            // The version that follows the banner is authoritative when we know it.
            if (peekedVersion != 0 && _db.SupportedVersions.Contains(peekedVersion))
            {
                Version = peekedVersion;
                return true;
            }

            // Otherwise take it from the banner text, which spells it out in full.
            int p = value.IndexOf("Version", StringComparison.OrdinalIgnoreCase);

            if (p < 0)
                throw new NifFormatException($"NIF header names no version: \"{value}\"");

            string tail = value[(p + 8)..];
            int end = 0;

            while (end < tail.Length && (char.IsAsciiDigit(tail[end]) || tail[end] == '.'))
                end++;

            Version = NifVersion.FromString(tail[..end]);

            if (Version == 0)
                throw new NifFormatException($"could not read a version out of \"{value}\"");

            if (!_db.SupportedVersions.Contains(Version))
                Warnings.Add($"version {NifVersion.ToVersionString(Version)} is not listed as supported");

            return true;
        }

        /// <summary>Loads a NIF from a seekable stream.</summary>
        public static NifModel Load(Stream stream, NifXmlDatabase database)
        {
            var model = new NifModel(database);
            model.ReadFrom(stream);
            return model;
        }

        /// <summary>Loads a NIF from a file.</summary>
        public static NifModel Load(string path, NifXmlDatabase database)
        {
            using FileStream stream = File.OpenRead(path);
            return Load(stream, database);
        }

        private void ReadFrom(Stream stream)
        {
            if (!stream.CanSeek)
                throw new NifFormatException("loading a NIF needs a seekable stream");

            var input = new NifIStream(this, stream);

            LoadHeader(input);

            int blockCount = (int)GetUInt(_header, "Num Blocks");

            for (int i = 0; i < blockCount; i++)
            {
                if (stream.Position >= stream.Length)
                    throw new NifFormatException($"unexpected end of file at block {i} of {blockCount}");

                string blockType = ReadBlockType(stream, input, i);

                if (!_db.IsBlock(blockType))
                    throw new NifFormatException($"block {i} has unknown type \"{blockType}\"");

                NifItem block = InsertBlock(blockType);

                if (!LoadItem(block, input))
                {
                    string detail = Warnings.Count > 0 ? $": {Warnings[^1]}" : string.Empty;
                    throw new NifFormatException($"failed to read block {i} ({blockType}){detail}");
                }
            }

            _bigEndian = input.IsBigEndian;

            // NifSkope deliberately ignores a footer that fails to read, since a bad
            // footer should not cost you the blocks that already loaded.
            LoadItem(_footer, input);
        }

        private void LoadHeader(NifIStream input)
        {
            // Read the banner alone first, so that the version is known before any
            // version-conditional header field is evaluated.
            var banner = new NifValue(NifValueType.HeaderString);

            if (!input.Read(ref banner))
                throw new NifFormatException("could not read the NIF header string");

            input.Reset();

            BSVersion = 0;
            UserVersion = 0;

            // Clear the two fields that the header's own conditions depend on, so a
            // stale value cannot decide the layout used to read them.
            SetIfPresent(_header, "User Version", 0);
            SetIfPresent(_header, @"BS Header\BS Version", 0);

            _header.InvalidateConditionsRecursive();

            if (!LoadItem(_header, input))
                throw new NifFormatException(
                    $"failed to read the header (version {NifVersion.ToVersionString(Version)})");

            UserVersion = GetUInt(_header, "User Version");
            BSVersion = GetUInt(_header, @"BS Header\BS Version");

            // Take over the file's string table, so a model that is loaded, edited
            // and saved again keeps its existing indices valid.
            _strings.Clear();

            if (FindItem(_header, "Strings") is { } strings)
            {
                foreach (NifItem entry in strings.Children)
                    _strings.Add(entry.Value.AsString());
            }
        }

        private void SetIfPresent(NifItem parent, string path, uint value)
        {
            NifItem? item = FindItemIgnoringConditions(parent, path);
            item?.Value.SetCount(value);
        }

        /// <summary>
        /// Path lookup that ignores conditions, for the bootstrap case where the
        /// conditions are exactly what we are trying to establish.
        /// </summary>
        private static NifItem? FindItemIgnoringConditions(NifItem? parent, string path)
        {
            if (parent is null)
                return null;

            int slash = path.IndexOf('\\');

            if (slash > 0)
            {
                NifItem? next = FindItemIgnoringConditions(parent, path[..slash]);
                return FindItemIgnoringConditions(next, path[(slash + 1)..]);
            }

            return parent.ChildByName(path);
        }

        /// <summary>
        /// Works out the type of block <paramref name="index"/>, which from 10.0.0.0
        /// onward is named by the header rather than written before the block.
        /// </summary>
        private string ReadBlockType(Stream stream, NifIStream input, int index)
        {
            if (Version < 0x0A000000)
            {
                var length = new NifValue(NifValueType.Int);

                if (!input.Read(ref length))
                    throw new NifFormatException($"could not read the type of block {index}");

                int n = length.ToInt();

                if (n is < 2 or > 80)
                    throw new NifFormatException($"block {index} does not start with a type name");

                byte[] bytes = new byte[n];
                stream.ReadExactly(bytes);
                return System.Text.Encoding.Latin1.GetString(bytes);
            }

            NifItem typeIndices = FindItem(_header, "Block Type Index")
                ?? throw new NifFormatException("the header has no Block Type Index");

            NifItem types = FindItem(_header, "Block Types")
                ?? throw new NifFormatException("the header has no Block Types");

            NifItem entry = typeIndices.Child(index)
                ?? throw new NifFormatException($"the header has no type index for block {index}");

            // The top bit is used as a marker and is not part of the index.
            int typeIndex = (int)(entry.Value.ToUInt() & 0x7FFF);

            string blockType = types.Child(typeIndex)?.Value.AsString()
                ?? throw new NifFormatException($"block {index} names type {typeIndex}, which the header does not list");

            // Some 10.0.1.0 files put four zero bytes before each non-Havok block.
            if (Version < 0x0A020000 && !blockType.StartsWith("bhk", StringComparison.Ordinal))
            {
                var separator = new NifValue(NifValueType.Int);
                input.Read(ref separator);

                if (separator.ToInt() != 0)
                    Warnings.Add($"non-zero separator {separator.ToInt()} before block {index} ({blockType})");
            }

            return blockType;
        }

        /// <summary>
        /// Reads every present field under <paramref name="parent"/>, recursing into
        /// arrays and structs.
        /// </summary>
        /// <remarks>
        /// The condition of each child is invalidated immediately before it is
        /// tested, because a field read a moment ago may be the one its condition
        /// names. That is the whole reason the walk is ordered rather than a
        /// precomputed layout.
        /// </remarks>
        private bool LoadItem(NifItem parent, NifIStream input)
        {
            foreach (NifItem child in parent.Children)
            {
                child.InvalidateCondition();

                // Abstract fields are declared for documentation and never stored.
                if (child.IsAbstract)
                    continue;

                if (!EvalCondition(child))
                    continue;

                if (child.IsArray)
                {
                    if (!UpdateArraySize(child))
                        return false;

                    if (!LoadItem(child, input))
                        return false;
                }
                else if (child.HasChildren)
                {
                    if (!LoadItem(child, input))
                        return false;
                }
                else if (!input.Read(ref child.Value))
                {
                    throw new NifFormatException(
                        $"ran out of data reading {PathOf(child)} as {child.Value.Type}");
                }
            }

            return true;
        }

        // --- saving -----------------------------------------------------------

        /// <summary>Writes the model back out.</summary>
        public void Save(Stream stream)
        {
            var output = new NifOStream(this, stream)
            {
                HeaderString = FindItem(_header, "Header String")?.Value.AsString() ?? string.Empty
            };

            output.SetBigEndian(_bigEndian);

            SaveItem(_header, output);

            foreach (NifItem block in _blocks)
            {
                // Before 10.0.0.0 each block is preceded by its type name.
                if (Version < 0x0A000000)
                {
                    byte[] name = System.Text.Encoding.Latin1.GetBytes(block.Name);
                    var length = new NifValue(NifValueType.Int);
                    length.SetCount((uint)name.Length);
                    output.Write(length);
                    stream.Write(name, 0, name.Length);
                }
                else if (Version < 0x0A020000 && !block.Name.StartsWith("bhk", StringComparison.Ordinal))
                {
                    var separator = new NifValue(NifValueType.Int);
                    output.Write(separator);
                }

                SaveItem(block, output);
            }

            SaveItem(_footer, output);
        }

        /// <summary>Writes to a file.</summary>
        public void Save(string path)
        {
            using FileStream stream = File.Create(path);
            Save(stream);
        }

        /// <summary>
        /// Brings a field up to date with whatever its condition and length depend
        /// on, and reports whether it is stored at all.
        /// </summary>
        /// <remarks>
        /// Reading and writing have to agree on the layout byte for byte, so writing
        /// walks the tree the same way <see cref="LoadItem"/> does: each field's
        /// condition is invalidated immediately before it is tested, since a field
        /// written a moment ago may be the one that condition names, and every array
        /// is resized from its length expression before it is descended into.
        ///
        /// Writing used to skip both steps and emit whatever children happened to
        /// exist. An array whose count had been set but whose elements were never
        /// created then wrote nothing, while the reader — which believes the count —
        /// went looking for elements that were not there. Because reading is
        /// sequential the damage is not local: every block after the short one is
        /// misread, and the error surfaces somewhere unrelated.
        /// </remarks>
        private bool PrepareForOutput(NifItem child)
        {
            child.InvalidateCondition();

            // Abstract fields are declared for documentation and never stored.
            if (child.IsAbstract || !EvalCondition(child))
                return false;

            if (child.IsArray && !UpdateArraySize(child))
            {
                string detail = Warnings.Count > 0 ? $": {Warnings[^1]}" : string.Empty;

                throw new NifFormatException($"cannot size {PathOf(child)} for writing{detail}");
            }

            return true;
        }

        private void SaveItem(NifItem parent, NifOStream output)
        {
            foreach (NifItem child in parent.Children)
            {
                if (!PrepareForOutput(child))
                    continue;

                if (child.IsArray || child.HasChildren)
                    SaveItem(child, output);
                else
                    output.Write(child.Value);
            }
        }

        /// <summary>
        /// A readable path to an item, for diagnostics: block names, field names,
        /// and array indices in brackets.
        /// </summary>
        public static string PathOf(NifItem item)
        {
            var parts = new List<string>();

            for (NifItem? i = item; i?.Parent is not null; i = i.Parent)
                parts.Add(i.Parent.IsArray ? $"[{i.Row}]" : i.Name);

            parts.Reverse();

            var text = new System.Text.StringBuilder();

            foreach (string part in parts)
            {
                if (part.StartsWith('[') || text.Length == 0)
                    text.Append(part);
                else
                    text.Append('\\').Append(part);
            }

            return text.ToString();
        }

        // --- authoring --------------------------------------------------------

        /// <summary>
        /// Creates an empty model with a usable header, ready for blocks.
        /// </summary>
        /// <remarks>
        /// Defaults to Skyrim LE: file version 20.2.0.7, user version 12, Bethesda
        /// stream version 83, which is what FBXWrangler writes (spec §5.8).
        /// </remarks>
        public static NifModel CreateNew(
            NifXmlDatabase database,
            uint version = 0x14020007,
            uint userVersion = 12,
            uint bsVersion = 83)
        {
            var model = new NifModel(database)
            {
                Version = version,
                UserVersion = userVersion,
                BSVersion = bsVersion
            };

            // The header's own conditions depend on these, so they have to be set
            // before anything reads the header back.
            model.SetIfPresent(model._header, "User Version", userVersion);
            model.SetIfPresent(model._header, @"BS Header\BS Version", bsVersion);

            NifItem? versionItem = FindItemIgnoringConditions(model._header, "Version");
            versionItem?.Value.SetCount(version);

            NifItem? banner = FindItemIgnoringConditions(model._header, "Header String");
            banner?.Value.Set(version <= 0x0A000100
                ? $"NetImmerse File Format, Version {NifVersion.ToVersionString(version)}"
                : $"Gamebryo File Format, Version {NifVersion.ToVersionString(version)}");

            // 1 is little-endian; every PC-era NIF is.
            NifItem? endian = FindItemIgnoringConditions(model._header, "Endian Type");
            endian?.Value.SetCount(1);

            model._header.InvalidateConditionsRecursive();
            return model;
        }

        private readonly List<string> _strings = [];

        /// <summary>
        /// Interns a string in the header table and returns its index, which is what
        /// a <c>string</c> field stores from 20.1.0.3 onward.
        /// </summary>
        public int AddString(string value)
        {
            if (value.Length == 0)
                return -1;

            int existing = _strings.IndexOf(value);

            if (existing >= 0)
                return existing;

            _strings.Add(value);
            return _strings.Count - 1;
        }

        /// <summary>
        /// Replaces the string table wholesale, keeping the given order.
        /// </summary>
        /// <remarks>
        /// For building a model that has to agree with an existing file, where the
        /// indices are already written into the blocks and the table has to line up
        /// with them exactly. <see cref="AddString"/> cannot do that job: it interns,
        /// so it folds duplicates together and refuses empty strings, and Bethesda's
        /// files contain both. One empty entry a third of the way down a table shifts
        /// every name after it onto the wrong index.
        /// </remarks>
        public void SetStringTable(IEnumerable<string> strings)
        {
            _strings.Clear();
            _strings.AddRange(strings);
        }

        /// <summary>
        /// Sets a string field, interning the text when the file version stores
        /// strings as indices into the header table.
        /// </summary>
        /// <remarks>
        /// The decision is made from the file version, not from the item's current
        /// type. A field declared <c>string</c> only becomes a
        /// <see cref="NifValueType.StringIndex"/> when the *reader* converts it, so
        /// on a model built from scratch it is still
        /// <see cref="NifValueType.String"/> — and the writer would then emit its
        /// numeric value, silently dropping the text.
        /// </remarks>
        public void SetString(NifItem block, string field, string value)
        {
            if (FindItem(block, field) is { } item)
                SetString(item, value);
        }

        /// <summary>Sets a string field directly, for a caller that already has it.</summary>
        public void SetString(NifItem item, string value)
        {
            bool usesStringTable = Version >= 0x14010003
                && item.Value.Type is NifValueType.String or NifValueType.FilePath or NifValueType.StringIndex;

            if (usesStringTable)
            {
                item.Value.ChangeType(NifValueType.StringIndex);
                item.Value.SetCount(unchecked((uint)AddString(value)));
            }
            else
            {
                item.Value.Set(value);
            }
        }

        /// <summary>
        /// Resizes an array field and its count together, which is the only safe way
        /// to grow one: the length expression reads the count.
        /// </summary>
        public NifItem? SetArraySize(NifItem block, string countField, string arrayField, int size)
        {
            NifItem? count = FindItem(block, countField);
            count?.Value.SetCount((uint)size);

            NifItem? array = FindItem(block, arrayField);

            if (array is null)
                return null;

            // The cached condition may have been decided before the count changed.
            array.InvalidateConditionsRecursive();
            UpdateArraySize(array);
            return array;
        }

        /// <summary>
        /// Recomputes every header field derived from the block list: the block
        /// count, the type table, the per-block type indices and sizes, and the
        /// string table.
        /// </summary>
        /// <remarks>
        /// Must run before <see cref="Save(Stream)"/>. A header disagreeing with the
        /// blocks is how a NIF ends up unreadable, and nothing else keeps them in
        /// step.
        /// </remarks>
        public void UpdateHeader()
        {
            SetIfPresent(_header, "User Version", UserVersion);
            SetIfPresent(_header, @"BS Header\BS Version", BSVersion);

            NifItem? numBlocks = FindItem(_header, "Num Blocks");
            numBlocks?.Value.SetCount((uint)_blocks.Count);

            // Distinct block types. Any order the header already has is kept, and
            // only the types it does not name are appended, in first-use order.
            //
            // First-use is what Bethesda's exporter produces -- of 2,500 vanilla
            // Skyrim meshes, all 2,500 are ordered that way -- so a model built from
            // scratch comes out the way the game's own files do. But a file written
            // by some other tool may order its table differently, and rewriting it
            // would change bytes that carry no meaning. Re-saving a file should
            // change what was edited and nothing else.
            var types = new List<string>();
            var typeIndices = new List<int>();

            var present = new HashSet<string>(_blocks.Select(b => b.Name), StringComparer.Ordinal);

            if (FindItem(_header, "Block Types") is { } existing)
            {
                foreach (NifItem entry in existing.Children)
                {
                    string name = entry.Value.AsString();

                    // A type the file names but no longer uses is dropped: the table
                    // describes the blocks, and a stale entry would outlive them.
                    if (name.Length > 0 && present.Contains(name) && !types.Contains(name))
                        types.Add(name);
                }
            }

            foreach (NifItem block in _blocks)
            {
                int at = types.IndexOf(block.Name);

                if (at < 0)
                {
                    at = types.Count;
                    types.Add(block.Name);
                }

                typeIndices.Add(at);
            }

            SetIfPresent(_header, "Num Block Types", (uint)types.Count);

            if (FindItem(_header, "Block Types") is { } blockTypes)
            {
                blockTypes.InvalidateConditionsRecursive();
                UpdateArraySize(blockTypes);

                for (int i = 0; i < types.Count && i < blockTypes.Children.Count; i++)
                    blockTypes.Children[i].Value.Set(types[i]);
            }

            if (FindItem(_header, "Block Type Index") is { } indices)
            {
                indices.InvalidateConditionsRecursive();
                UpdateArraySize(indices);

                for (int i = 0; i < typeIndices.Count && i < indices.Children.Count; i++)
                    indices.Children[i].Value.SetCount((uint)typeIndices[i]);
            }

            // Block sizes, which 20.2.0.0+ stores so a reader can skip a block it
            // does not understand.
            if (FindItem(_header, "Block Size") is { } sizes)
            {
                sizes.InvalidateConditionsRecursive();
                UpdateArraySize(sizes);

                var sizer = new NifOStream(this, Stream.Null);

                for (int i = 0; i < _blocks.Count && i < sizes.Children.Count; i++)
                    sizes.Children[i].Value.SetCount((uint)MeasureItem(_blocks[i], sizer));
            }

            UpdateStringTable();
        }

        private void UpdateStringTable()
        {
            SetIfPresent(_header, "Num Strings", (uint)_strings.Count);

            int longest = 0;

            foreach (string s in _strings)
                longest = Math.Max(longest, s.Length);

            SetIfPresent(_header, "Max String Length", (uint)longest);

            if (FindItem(_header, "Strings") is not { } strings)
                return;

            strings.InvalidateConditionsRecursive();
            UpdateArraySize(strings);

            for (int i = 0; i < _strings.Count && i < strings.Children.Count; i++)
                strings.Children[i].Value.Set(_strings[i]);
        }

        /// <summary>The number of bytes an item and everything under it will occupy.</summary>
        /// <remarks>
        /// Measuring prepares each field exactly as writing does, so that the size
        /// recorded in the header is the size that will actually be written. A
        /// measurement taken over a tree writing is about to resize would disagree
        /// with the bytes by however much the resize adds.
        /// </remarks>
        private int MeasureItem(NifItem parent, NifOStream sizer)
        {
            int total = 0;

            foreach (NifItem child in parent.Children)
            {
                if (!PrepareForOutput(child))
                    continue;

                total += child.IsArray || child.HasChildren
                    ? MeasureItem(child, sizer)
                    : sizer.SizeOf(child.Value);
            }

            return total;
        }

        /// <summary>Points the footer at the given root blocks.</summary>
        public void SetRoots(IReadOnlyList<NifItem> roots)
        {
            SetIfPresent(_footer, "Num Roots", (uint)roots.Count);

            if (FindItem(_footer, "Roots") is not { } array)
                return;

            array.InvalidateConditionsRecursive();
            UpdateArraySize(array);

            for (int i = 0; i < roots.Count && i < array.Children.Count; i++)
                array.Children[i].Value.SetLink(_blocks.IndexOf(roots[i]));
        }

        /// <summary>The index a link should carry to point at a block.</summary>
        public int IndexOf(NifItem block) => _blocks.IndexOf(block);

        /// <summary>
        /// Rewrites the block list in a new order, remapping every link.
        /// </summary>
        /// <remarks>
        /// A link is a block *number*, so moving a block renumbers every reference to
        /// it. The whole file's links are rewritten in one pass, the footer's roots
        /// included, which is why this takes the complete order rather than a move.
        ///
        /// The order has to be a permutation of what is already here. Anything else —
        /// a block dropped, a block twice — would leave links pointing at the wrong
        /// thing rather than at nothing, so it is refused.
        /// </remarks>
        public void ReorderBlocks(IReadOnlyList<NifItem> order)
        {
            if (order.Count != _blocks.Count || !order.All(_blocks.Contains) || order.Distinct().Count() != order.Count)
                throw new ArgumentException("the new order must be a permutation of the block list", nameof(order));

            var moved = new Dictionary<NifItem, int>(order.Count);

            for (int i = 0; i < order.Count; i++)
                moved[order[i]] = i;

            // Read every link before any of them move, so a link is never resolved
            // through a half-renumbered list.
            var links = new List<(NifItem Link, NifItem? Target)>();

            foreach (NifItem block in _blocks)
                CollectLinks(block, links);

            CollectLinks(_footer, links);

            _blocks.Clear();
            _blocks.AddRange(order);

            foreach ((NifItem link, NifItem? target) in links)
                link.Value.SetLink(target is null ? -1 : moved[target]);
        }

        private void CollectLinks(NifItem item, List<(NifItem, NifItem?)> links)
        {
            foreach (NifItem child in item.Children)
            {
                if (child.Value.IsLink)
                    links.Add((child, GetBlock(child)));
                else
                    CollectLinks(child, links);
            }
        }

        /// <summary>Points a reference field at a block, or at nothing when null.</summary>
        public void SetRef(NifItem block, string field, NifItem? target)
        {
            NifItem? link = FindItem(block, field);
            link?.Value.SetLink(target is null ? -1 : IndexOf(target));
        }

        // --- convenience ------------------------------------------------------

        /// <summary>Reads a count-like field by path, or 0 when it is absent.</summary>
        public uint GetUInt(NifItem parent, string path) =>
            FindItem(parent, path)?.Value.ToUInt() ?? 0;

        /// <summary>Reads a string field by path, resolving header string indices.</summary>
        public string GetString(NifItem parent, string path)
        {
            NifItem? item = FindItem(parent, path);

            if (item is null)
                return string.Empty;

            return ResolveString(item);
        }

        /// <summary>
        /// The text of a string field, following the header string table when the
        /// field is stored as an index.
        /// </summary>
        /// <remarks>
        /// The table consulted is the model's own, not the copy in the header. Both
        /// hold the same strings for a file that was loaded, but the header's is only
        /// written out by <see cref="UpdateHeader"/> — so on a model built from
        /// scratch it is empty, and reading back a name that was just set would
        /// return nothing at all.
        /// </remarks>
        public string ResolveString(NifItem item)
        {
            if (item.Value.Type != NifValueType.StringIndex)
                return item.Value.AsString();

            int index = (int)item.Value.ToUInt();

            return index >= 0 && index < _strings.Count ? _strings[index] : string.Empty;
        }

        /// <summary>The block a link points at, or null for a null link.</summary>
        public NifItem? GetBlock(NifItem link)
        {
            int index = link.Value.ToLink();
            return index >= 0 && index < _blocks.Count ? _blocks[index] : null;
        }

        /// <summary>True when a block is, or descends from, the named type.</summary>
        public bool BlockInherits(NifItem block, string ancestor) => _db.Inherits(block.Name, ancestor);

        /// <summary>Whether nif.xml declares a block of this name.</summary>
        /// <remarks>
        /// For rebuilding a block whose type came from outside the file — an FBX
        /// property, say. Inserting an unknown one throws, and a name that arrived as
        /// text is not something to take on trust.
        /// </remarks>
        public bool KnowsBlock(string name) => _db.IsBlock(name);
    }
}
