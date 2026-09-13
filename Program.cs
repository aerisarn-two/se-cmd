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

            root.Parse(args).Invoke();
        }
    }
}