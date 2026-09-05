using NIFSharp;
using SECmd.Nif;

namespace SECmd.Conversion
{
    /// <summary>
    /// Blend factors, named as OpenGL names them, which is how FBXWrangler writes
    /// them into FBX so a DCC tool can show something meaningful.
    /// </summary>
    public enum GlBlendMode
    {
        One = 0,
        Zero = 1,
        SrcColor = 2,
        OneMinusSrcColor = 3,
        DstColor = 4,
        OneMinusDstColor = 5,
        SrcAlpha = 6,
        OneMinusSrcAlpha = 7,
        DstAlpha = 8,
        OneMinusDstAlpha = 9,
        SrcAlphaSaturate = 10
    }

    /// <summary>Alpha test comparison functions.</summary>
    public enum GlTestMode
    {
        Always = 0,
        Less = 1,
        Equal = 2,
        LEqual = 3,
        Greater = 4,
        NotEqual = 5,
        GEqual = 6,
        Never = 7
    }

    /// <summary>
    /// A decoded <c>NiAlphaProperty</c>.
    /// </summary>
    /// <remarks>
    /// NIF packs all of this into one 16-bit word. FBX has nowhere to put it, so
    /// FBXWrangler spreads it across user-defined properties on the material and
    /// reassembles the word on import. We do the same, using the same property
    /// names, so files stay interchangeable with ck-cmd.
    /// </remarks>
    public sealed class AlphaSettings
    {
        public bool ColorBlendingEnable { get; set; }

        public GlBlendMode SourceBlendMode { get; set; } = GlBlendMode.One;

        public GlBlendMode DestinationBlendMode { get; set; } = GlBlendMode.One;

        public bool AlphaTestEnable { get; set; }

        public GlTestMode AlphaTestMode { get; set; } = GlTestMode.Always;

        /// <summary>Disables triangle sorting.</summary>
        public bool NoSorter { get; set; }

        /// <summary>
        /// Bethesda's bit 14, which nif.xml names <c>Clone Unique</c>.
        /// </summary>
        /// <remarks>
        /// nif.xml: "Bethesda-only. Always true for weapon blood after FO3." Modelling
        /// the word as the six documented fields and dropping the top two bits meant a
        /// rebuilt alpha property lost them, and this one is not rare: it is the single
        /// commonest `Flags` difference left in the sweep, 201 of a 1,500-mesh sample.
        /// </remarks>
        public bool CloneUnique { get; set; }

        /// <summary>
        /// Bethesda's bit 15, which nif.xml names <c>Editor Alpha Threshold</c>.
        /// </summary>
        /// <remarks>
        /// nif.xml: "Bethesda-only. True if the Alpha Threshold is externally
        /// controlled." Lost the same way, on 38 shapes of the same sample.
        /// </remarks>
        public bool EditorAlphaThreshold { get; set; }

        public byte Threshold { get; set; }

        /// <summary>
        /// Decodes the packed flags word.
        /// </summary>
        /// <remarks>
        /// Bit 0 is blending, bits 1-4 the source factor, 5-8 the destination
        /// factor, 9 the alpha test, 10-12 the test function, 13 the sort flag, and
        /// 14 and 15 Bethesda's two of their own.
        /// </remarks>
        public static AlphaSettings FromFlags(ushort flags, byte threshold) => new()
        {
            ColorBlendingEnable = (flags & 0x1) != 0,
            SourceBlendMode = (GlBlendMode)((flags >> 1) & 0xF),
            DestinationBlendMode = (GlBlendMode)((flags >> 5) & 0xF),
            AlphaTestEnable = (flags >> 9 & 0x1) != 0,
            AlphaTestMode = (GlTestMode)((flags >> 10) & 0x7),
            NoSorter = (flags >> 13 & 0x1) != 0,
            CloneUnique = (flags >> 14 & 0x1) != 0,
            EditorAlphaThreshold = (flags >> 15 & 0x1) != 0,
            Threshold = threshold
        };

        /// <summary>Re-packs the flags word.</summary>
        public ushort ToFlags()
        {
            int flags = 0;

            if (ColorBlendingEnable)
                flags |= 0x1;

            flags |= ((int)SourceBlendMode & 0xF) << 1;
            flags |= ((int)DestinationBlendMode & 0xF) << 5;

            if (AlphaTestEnable)
                flags |= 1 << 9;

            flags |= ((int)AlphaTestMode & 0x7) << 10;

            if (NoSorter)
                flags |= 1 << 13;

            if (CloneUnique)
                flags |= 1 << 14;

            if (EditorAlphaThreshold)
                flags |= 1 << 15;

            return (ushort)flags;
        }

        /// <summary>
        /// The GL-style name for a blend factor, as written into FBX.
        /// </summary>
        public static string NameOf(GlBlendMode mode) => mode switch
        {
            GlBlendMode.One => "ONE",
            GlBlendMode.Zero => "ZERO",
            GlBlendMode.SrcColor => "SRC_COLOR",
            GlBlendMode.OneMinusSrcColor => "ONE_MINUS_SRC_COLOR",
            GlBlendMode.DstColor => "DST_COLOR",
            GlBlendMode.OneMinusDstColor => "ONE_MINUS_DST_COLOR",
            GlBlendMode.SrcAlpha => "SRC_ALPHA",
            GlBlendMode.OneMinusSrcAlpha => "ONE_MINUS_SRC_ALPHA",
            GlBlendMode.DstAlpha => "DST_ALPHA",
            GlBlendMode.OneMinusDstAlpha => "ONE_MINUS_DST_ALPHA",
            GlBlendMode.SrcAlphaSaturate => "SRC_ALPHA_SATURATE",
            _ => "ONE"
        };

        public static string NameOf(GlTestMode mode) => mode switch
        {
            GlTestMode.Always => "ALWAYS",
            GlTestMode.Less => "LESS",
            GlTestMode.Equal => "EQUAL",
            GlTestMode.LEqual => "LEQUAL",
            GlTestMode.Greater => "GREATER",
            GlTestMode.NotEqual => "NOTEQUAL",
            GlTestMode.GEqual => "GEQUAL",
            GlTestMode.Never => "NEVER",
            _ => "ALWAYS"
        };

        /// <summary>
        /// Parses a blend factor name back.
        /// </summary>
        /// <remarks>
        /// FBXWrangler's own parser compares the first entry against "GL_ONE" while
        /// its writer emits "ONE", so that case falls through to the default. The
        /// default is also One, which is why the bug never showed. We accept both
        /// spellings rather than reproduce a mismatch that only ever worked by
        /// accident.
        /// </remarks>
        public static GlBlendMode ParseBlendMode(string name) => name switch
        {
            "ONE" or "GL_ONE" => GlBlendMode.One,
            "ZERO" => GlBlendMode.Zero,
            "SRC_COLOR" => GlBlendMode.SrcColor,
            "ONE_MINUS_SRC_COLOR" => GlBlendMode.OneMinusSrcColor,
            "DST_COLOR" => GlBlendMode.DstColor,
            "ONE_MINUS_DST_COLOR" => GlBlendMode.OneMinusDstColor,
            "SRC_ALPHA" => GlBlendMode.SrcAlpha,
            "ONE_MINUS_SRC_ALPHA" => GlBlendMode.OneMinusSrcAlpha,
            "DST_ALPHA" => GlBlendMode.DstAlpha,
            "ONE_MINUS_DST_ALPHA" => GlBlendMode.OneMinusDstAlpha,
            "SRC_ALPHA_SATURATE" => GlBlendMode.SrcAlphaSaturate,
            _ => GlBlendMode.One
        };

        public static GlTestMode ParseTestMode(string name) => name switch
        {
            "ALWAYS" => GlTestMode.Always,
            "LESS" => GlTestMode.Less,
            "EQUAL" => GlTestMode.Equal,
            "LEQUAL" => GlTestMode.LEqual,
            "GREATER" => GlTestMode.Greater,
            "NOTEQUAL" => GlTestMode.NotEqual,
            "GEQUAL" => GlTestMode.GEqual,
            "NEVER" => GlTestMode.Never,
            _ => GlTestMode.Always
        };
    }

    /// <summary>
    /// A material in the form both sides of the conversion agree on, read from a
    /// <c>BSLightingShaderProperty</c> and its texture set.
    /// </summary>
    public sealed class MaterialData
    {
        /// <summary>Texture slots, indexed as the NIF stores them.</summary>
        public const int DiffuseSlot = 0;

        public const int NormalSlot = 1;

        public string Name { get; set; } = string.Empty;

        /// <summary>The shader path, kept as its enum name so it survives a round trip.</summary>
        public string ShaderType { get; set; } = string.Empty;

        /// <summary>
        /// The shader property block's own name, which is not the material's.
        /// </summary>
        /// <remarks>
        /// A shader property is a `NiObjectNET` and carries a name like anything else
        /// on that level. It is usually empty, and when it is not it says something a
        /// reader relies on: the water shaders are called "water", and a rebuilt one
        /// that is not called anything is a shader nothing can find by name.
        ///
        /// Distinct from <see cref="Name"/>, which is the FBX material's -- built from
        /// the shape it hangs off, since FBX needs every material to have one.
        /// </remarks>
        public string ShaderName { get; set; } = string.Empty;

        public NifColor3 EmissiveColor { get; set; }

        public float EmissiveMultiple { get; set; } = 1f;

        public NifColor3 SpecularColor { get; set; } = new(1f, 1f, 1f);

        /// <summary>
        /// The colour multiplied into the diffuse texture, when the shader has one.
        /// </summary>
        /// <remarks>
        /// A lighting shader has none — its diffuse comes wholly from the texture, so
        /// this stays white and FBX gets white. An effect shader does: its base colour
        /// is what tints the effect, and leaving it out is the difference between a
        /// blue waterfall and a grey one.
        /// </remarks>
        public NifColor3 DiffuseColor { get; set; } = new(1f, 1f, 1f);

        /// <summary>NIF stores this over 0..999; FBX wants 0..1.</summary>
        public float SpecularStrength { get; set; }

        public float Glossiness { get; set; }

        public float Alpha { get; set; } = 1f;

        public float EnvironmentMapScale { get; set; }

        /// <summary>
        /// The shader fields nif.xml gates on the shader type, read off the block.
        /// </summary>
        /// <remarks>
        /// A `BSLightingShaderProperty` holds a different set of extra fields for each
        /// shading path, and nif.xml spells every one of them out with a
        /// `Shader Type == n` condition. They travel as one table rather than as a
        /// named property apiece, because there is nothing to say about any of them
        /// individually: each is a number, or two, or three, belonging to a path this
        /// converter otherwise leaves alone.
        ///
        /// Enumerated from the block rather than listed here. Listed, the list was
        /// wrong: it named eight fields and nif.xml has twelve, so `Skin Tint Color`,
        /// `Sparkle Parameters` and both eye reflection centres went on being lost
        /// after the other eight were fixed. A schema this port already reads is a
        /// better authority than a line of code repeating part of it, and it stays
        /// right when nif.xml gains a thirteenth.
        ///
        /// `Environment Map Scale` is skipped: it has a carrier of its own, and two
        /// carriers writing one field is how one of them loses.
        /// </remarks>
        public static IEnumerable<NifItem> ShaderTypeFieldsOf(NifItem shader)
        {
            foreach (NifItem child in shader.Children)
            {
                if (child.Name != EnvironmentMapScaleField
                    && child.Def.Cond.Contains(ShaderTypeCondition, StringComparison.Ordinal))
                {
                    yield return child;
                }
            }
        }

        /// <summary>The condition nif.xml gates a shading path's fields on.</summary>
        private const string ShaderTypeCondition = "Shader Type";

        /// <summary>The one such field with a carrier of its own.</summary>
        private const string EnvironmentMapScaleField = "Environment Map Scale";

        /// <summary>The FBX property one of those rides under.</summary>
        public static string ShaderTypeFieldProperty(string field) =>
            "nif_shader_" + field.Replace(' ', '_').ToLowerInvariant();

        /// <summary>What the source held in them, by field name, as text.</summary>
        public Dictionary<string, string> ShaderTypeValues { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// The rim and soft-lighting strengths, and how strongly refraction bends.
        /// </summary>
        /// <remarks>
        /// `BSLightingShaderProperty` only, and the defaults are nif.xml's rather than
        /// zero — a shader that never carried them should not be given a zero, which for
        /// `Lighting Effect 2` in particular is a visible change.
        ///
        /// These were not modelled at all, so every lighting shader came back with the
        /// defaults whatever the file said: a rim power of 10 became 0.3.
        /// </remarks>
        public float LightingEffect1 { get; set; } = 0.3f;

        /// <inheritdoc cref="LightingEffect1"/>
        public float LightingEffect2 { get; set; } = 2f;

        /// <inheritdoc cref="LightingEffect1"/>
        public float RefractionStrength { get; set; }

        /// <summary>
        /// The two shader flag words, carried whole.
        /// </summary>
        /// <remarks>
        /// Nothing derived these and nothing wrote them, so every rebuilt shader took
        /// nif.xml's defaults. They are not defaults worth taking: across 20,576 shader
        /// properties in a quarter of Skyrim's meshes, `Shader Flags 1` holds **225**
        /// distinct values and `Shader Flags 2` **111**, and the commonest covers 33%
        /// and 43.7% respectively.
        ///
        /// Two bits are about the mesh rather than about how it is lit, and are forced
        /// on import rather than trusted: `Skinned` (flags 1, bit 1) and `Vertex_Colors`
        /// (flags 2, bit 5). Forced *on* only. Vanilla never leaves either clear when
        /// the content is there -- of 20,576 shapes, not one skinned shape lacks the
        /// skinned bit -- but two do carry it with no skin at all, and clearing it would
        /// edit a file this is meant to reproduce.
        /// </remarks>
        public uint? ShaderFlags1 { get; set; }

        /// <inheritdoc cref="ShaderFlags1"/>
        public uint? ShaderFlags2 { get; set; }

        public NifVector2 UvOffset { get; set; } = new(0f, 0f);

        public NifVector2 UvScale { get; set; } = new(1f, 1f);

        /// <summary>How texture coordinates behave outside 0..1, as NIF's enum value.</summary>
        public uint TextureClampMode { get; set; }

        /// <summary>The texture set, by slot. Empty entries mean an unused slot.</summary>
        public List<string> Textures { get; } = [];

        /// <summary>
        /// Which blocks in the source file this material's parts came from.
        /// </summary>
        /// <remarks>
        /// Sharing is data, not a coincidence of equality. Bethesda's files point
        /// several shapes at one texture set or one alpha property, and also carry
        /// identical blocks side by side where the exporter happened to make two — so
        /// rebuilding by content merges blocks that were meant to be separate, and
        /// rebuilding one per shape splits blocks that were meant to be one.
        ///
        /// Carrying the source index settles it: same index, same block. The numbers
        /// mean nothing outside the file they came from, which is the only place they
        /// are read.
        /// </remarks>
        public int TextureSetId { get; set; } = -1;

        /// <inheritdoc cref="TextureSetId"/>
        public int AlphaId { get; set; } = -1;

        /// <summary>Alpha settings, when the shape carried a <c>NiAlphaProperty</c>.</summary>
        public AlphaSettings? AlphaProperty { get; set; }

        public string TextureAt(int slot) =>
            slot >= 0 && slot < Textures.Count ? Textures[slot] : string.Empty;

        /// <summary>
        /// Rewrites a texture path the way NIF stores them: relative to the game's
        /// data folder, backslash separated, always .dds.
        /// </summary>
        /// <remarks>
        /// A path coming back from a DCC tool is usually absolute, so it is cut down
        /// to start at the "textures" (or "cube") segment. Anything that has neither
        /// is left alone rather than mangled.
        /// </remarks>
        public static string NormalizeTexturePath(string path)
        {
            if (path.Length == 0)
                return path;

            string normalized = path.Replace('/', '\\');

            // A relative path is left exactly as it is. The rewriting below exists to
            // turn a DCC's absolute path into one the game can resolve, and it is not
            // lossless: it drops a leading `data\`, lowercases an extension Bethesda
            // spells `.DDS` as often as `.dds`, and gives an extension to an entry that
            // has none. Vanilla carries all three -- `data\Textures\...`,
            // `BeardShort05.DDS`, `dlc01\build\pc\data\textures\...` from a shipped
            // build tree, and a bare `NOR` -- across 313 texture entries in a
            // 1,200-mesh sample. They resolve as they are, and none of them is ours to
            // tidy.
            //
            // A relative path keeps its shape, then: only its extension is corrected,
            // and only when it names something the game cannot read. `.DDS` is already
            // a DDS and stays as spelled; an entry with no extension at all -- vanilla
            // ships a bare `NOR` -- is not a filename to complete.
            if (!IsRooted(normalized))
                return ForceDdsExtension(normalized);

            int at = normalized.IndexOf("textures", StringComparison.OrdinalIgnoreCase);

            if (at < 0)
                at = normalized.IndexOf("cube", StringComparison.OrdinalIgnoreCase);

            if (at > 0)
                normalized = normalized[at..];

            int dot = normalized.LastIndexOf('.');

            if (dot > normalized.LastIndexOf('\\'))
                normalized = normalized[..dot];

            return normalized + ".dds";
        }

        /// <summary>
        /// A path's extension corrected to <c>.dds</c>, when it has one that is not.
        /// </summary>
        private static string ForceDdsExtension(string path)
        {
            int dot = path.LastIndexOf('.');

            if (dot <= path.LastIndexOf('\\'))
                return path;

            return path.AsSpan(dot).Equals(".dds", StringComparison.OrdinalIgnoreCase)
                ? path
                : string.Concat(path.AsSpan(0, dot), ".dds");
        }

        /// <summary>
        /// Whether a path names a place on a disc rather than one inside the game.
        /// </summary>
        /// <remarks>
        /// Spelled out rather than left to <c>Path.IsPathRooted</c>, which is asked
        /// about a Windows path on whatever host this runs on: a backslash is not a
        /// separator to it on Linux, so `\\server\share\x.dds` reads as relative.
        /// </remarks>
        private static bool IsRooted(string path) =>
            path.StartsWith('\\')
            || path.StartsWith('/')
            || (path.Length > 1 && path[1] == ':');
    }
}
