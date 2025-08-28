using System.CommandLine;

namespace SECmd
{
    internal class Program
    {

        static void Main(string[] args)
        {
            RootCommand root = new("se-cmd utility");
            Commands.RetargetCreature.Register(root);
            Commands.ExportNif.Register(root);

            root.Parse(args).Invoke();
        }
    }
}