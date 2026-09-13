using System.CommandLine;

namespace SECmd
{
    internal class Program
    {

        static void Main(string[] args)
        {
            RootCommand root = new("se-cmd utility");
            Commands.RetargetCreature.Register(root);
            Commands.ExportFbx.Register(root);
            Commands.ImportFbx.Register(root);
            Commands.ImportCreature.Register(root);
            Commands.ExportCreature.Register(root);
            Commands.Convert.Register(root);

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
            if (args.Any(a => a.StartsWith('-'))) return args;

            if (args.Any(a => root.Subcommands.Any(c => c.Name == a || c.Aliases.Contains(a))))
                return args;

            return args.Any(Commands.Convert.Handles) ? ["convert", .. args] : args;
        }
    }
}