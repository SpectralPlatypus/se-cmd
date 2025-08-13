#nullable disable
namespace SECmd.AnimData
{
    internal class AnimSetDataFile : IBlock
    {
        internal List<string> Projects { get; set; } = [];
        internal List<ProjectAttackListBlock> ProjectAttacks { get; private set; } = [];


        public int GetProjectAttackBlockIndex(string projectName)
        {
            for (int i = 0; i < Projects.Count; i++)
            {
                if (Projects[i].Equals(projectName, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        public bool TryGetProjectAttackBlock(string projectName, out  ProjectAttackListBlock block)
        {
            for (int i = 0; i < Projects.Count; i++)
            {
                if (Projects[i].Equals(projectName, StringComparison.OrdinalIgnoreCase))
                {
                    block = ProjectAttacks[i];
                    return true;
                }
            }
            block = null;
            return false;
        }
        public void ReadBlock(TextReader reader)
        {
            int count = int.Parse(reader.ReadLine());
            for (int i = 0; i < count; i++)
            {
                Projects.Add(reader.ReadLine());
            }
            while(reader.Peek() >= 0)
            {
                ProjectAttackListBlock pa = new();
                pa.ReadBlock(reader);
                ProjectAttacks.Add(pa);
            }
        }

        public void WriteBlock(TextWriter writer)
        {
            writer.WriteLine(Projects.Count);
            foreach (string project in Projects)
                writer.WriteLine(project);

            foreach(var pa in  ProjectAttacks)
                pa.WriteBlock(writer);
        }

        public int AddProjectAttackBlock(string name, ProjectAttackListBlock attackBlock)
        {
            Projects.Add(name);
            ProjectAttacks.Add(attackBlock);
            return ProjectAttacks.Count - 1;
        }
    }
}
