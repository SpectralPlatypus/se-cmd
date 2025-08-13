#nullable disable 

namespace SECmd.AnimData
{
    internal class ProjectAttackListBlock : IBlock
    {
        internal List<string> ProjectFiles { get; private set; } = [];
        internal List<ProjectAttackBlock> ProjectAttackBlocks { get; private set; } = [];

        public void ReadBlock(TextReader reader)
        {
            ProjectFiles.Clear();
            ProjectAttackBlocks.Clear();

            int count = int.Parse(reader.ReadLine());
            for (int i = 0; i < count; i++) 
                ProjectFiles.Add(reader.ReadLine());

            for (int i = 0; i < ProjectFiles.Count; i++)
            {
                ProjectAttackBlock pb = new();
                pb.ReadBlock(reader);
                ProjectAttackBlocks.Add(pb);
            }
        }

        public void WriteBlock(TextWriter writer)
        {
            writer.WriteLine(ProjectFiles.Count);
            foreach(var pf in ProjectFiles) 
                writer.WriteLine(pf);

            foreach (var pb in ProjectAttackBlocks)
                pb.WriteBlock(writer);
        }
    }
}
