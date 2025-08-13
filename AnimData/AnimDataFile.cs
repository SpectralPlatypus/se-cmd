#nullable disable 

using System.Text;

namespace SECmd.AnimData
{
    internal class AnimDataFile : IBlock
    {
        List<string> projects = new(1000);
        public List<string> Projects { get { return projects; } }

        public List<ProjectBlock> ProjectBlocks { get; } = new(1000);

        Dictionary<int, ProjectDataBlock> projectMovtBlocks = [];

        public Dictionary<int, ProjectDataBlock> MovementData {  get { return projectMovtBlocks; } }

        public int AddProject(string name, ProjectBlock project)
        {
            projects.Add(name);
            ProjectBlocks.Add(project);
            return ProjectBlocks.Count - 1;
        }

        public int AddProject(string name, ProjectBlock project, ProjectDataBlock movement)
        {
            projects.Add(name);
            int next = ProjectBlocks.Count;
            ProjectBlocks.Add(project);
            projectMovtBlocks.Add(next, movement);
            return next;
        }

        // Handling line count here so that block classes can be used for individual project files
        public void ReadBlock(TextReader reader)
        {
            int count = int.Parse(reader.ReadLine());
            for (int i = 0; i < count; i++)
            {
                projects.Add(reader.ReadLine());
            }
            for (int i = 0; i < projects.Count; i++)
            {
                StringBuilder blockBuilder = new();
                count = int.Parse(reader.ReadLine());
                using MemoryStream ms = new();
                using (StringWriter writer = new(blockBuilder))
                {
                    for (int j = 0; j < count; ++j)
                    {
                        writer.WriteLine(reader.ReadLine());
                    }
                }
                
                var projectBlock = new ProjectBlock();

                using (StringReader sr = new(blockBuilder.ToString()))
                    projectBlock.ReadBlock(sr);

                ProjectBlocks.Add(projectBlock);
                if(projectBlock.HasAnimationCache)
                {
                    blockBuilder.Clear();
                    count = int.Parse(reader.ReadLine());
                    using (StringWriter writer = new(blockBuilder))
                    {
                        for (int j = 0; j < count; j++)
                        {
                            writer.WriteLine(reader.ReadLine());
                        }
                    }
                    ms.Position = 0;
                    var movementBlock = new ProjectDataBlock();

                    using (StringReader sr = new(blockBuilder.ToString()))
                        movementBlock.ReadBlock(sr);
                    projectMovtBlocks.Add(i, movementBlock);
                }
            }
        }

        public void WriteBlock(TextWriter writer)
        {
            writer.WriteLine(projects.Count);
            foreach (var project in projects)
                writer.WriteLine(project);

            for (int i = 0; i < ProjectBlocks.Count; ++i)
            {
                writer.WriteLine(ProjectBlocks[i].LineCount);
                ProjectBlocks[i].WriteBlock(writer);
                if (ProjectBlocks[i].HasAnimationCache)
                {
                    if (!projectMovtBlocks.ContainsKey(i))
                        throw new IOException("Malformed project block: Missing animation cache");

                    writer.WriteLine(projectMovtBlocks[i].LineCount);
                    projectMovtBlocks[i].WriteBlock(writer);
                }
            }
        }
    }
}
