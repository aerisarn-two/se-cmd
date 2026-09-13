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
        /// here rather than demanded of somebody holding a mouse.
        ///
        /// Only when the first argument is a path that exists and is not a command:
        /// anything else is left exactly as it was typed, so a mistyped verb still
        /// gets the error it deserves rather than being treated as a filename.
        /// </remarks>
        private static string[] Dropped(RootCommand root, string[] args)
        {
            if (args.Length == 0) return args;
            if (args[0].StartsWith('-')) return args;
            if (root.Subcommands.Any(c => c.Name == args[0] || c.Aliases.Contains(args[0]))) return args;
            if (!File.Exists(args[0]) && !Directory.Exists(args[0])) return args;

            return ["convert", .. args];
        }
    }
}