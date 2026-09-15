using System.CommandLine;

namespace SECmd
{
    internal class Program
    {

        static void Main(string[] args)
        {
            // Anything this writes says which version wrote it: the NIF header's
            // author field and the FBX's Creator, both of which outlive the run.
            NIFBX.Authoring.Signature = Build.Signature;

            RootCommand root = new($"se-cmd {Build.Version}");
            Commands.RetargetCreature.Register(root);
            Commands.ExportFbx.Register(root);
            Commands.ImportFbx.Register(root);
            Commands.ImportCreature.Register(root);
            Commands.ExportCreature.Register(root);
            Commands.Convert.Register(root);
            Commands.FindNpc.Register(root);
            Commands.ExportNpc.Register(root);
            Commands.ImportNpc.Register(root);

            root.Parse(Dropped(root, args)).Invoke();
        }

        /// <summary>
        /// Files dropped on the executable, read as a request to convert them.
        /// </summary>
        /// <remarks>
        /// Dropping files on a program hands it their paths and nothing else, with
        /// no verb to say what to do with them. The only sensible reading is
        /// "convert these", which is what `convert` is for, so the verb is supplied
        /// here rather than demanded of somebody holding a mouse. Typing a filename
        /// on its own means the same thing and gets the same treatment.
        ///
        /// The test is whether any argument is a folder or a file of a kind this
        /// converts -- any, not the first, because a selection dragged from a file
        /// manager arrives in whatever order it was made in, and a readme picked up
        /// with the meshes should not decide what the whole drop means.
        ///
        /// A command named anywhere in the arguments settles it the other way, and
        /// so does an argument that starts with a dash: someone typing a real
        /// command with a path in it is not dropping anything, and a mistyped
        /// command still gets the error it deserves rather than being read as a
        /// filename.
        /// </remarks>
        private static string[] Dropped(RootCommand root, string[] args)
        {
            if (args.Length == 0) return args;

            // A command first means somebody typed a command, whatever follows it.
            if (root.Subcommands.Any(c => c.Name == args[0] || c.Aliases.Contains(args[0])))
                return args;

            // A path first means a drop, and the options after it are convert's own:
            // `se-cmd creature/folder -o somewhere` is a reasonable thing to type and
            // was refused while any dash anywhere vetoed the whole reading.
            if (Commands.Convert.Handles(args[0]))
                return ["convert", .. args];

            // Otherwise a dash settles it: a mistyped command should get the error it
            // deserves rather than be read as a filename.
            if (args[0].StartsWith('-')) return args;

            // And last, the selection-order case: a drop from a file manager arrives
            // in whatever order it was made in, so a readme picked up with the meshes
            // must not decide what the whole drop means.
            return args.Any(Commands.Convert.Handles) ? ["convert", .. args] : args;
        }
    }
}