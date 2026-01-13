using SECmd.AnimData;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SECmd.AnimData
{
    internal class AnimationCache
    {
        #region Internal Structures
        enum EventType
        {
            Idle,
            Attack
        }
        struct EventInfo(EventType eventType, bool mirrored, List<HandVariableData.Data> handData)
        {
            public EventType Type = eventType;
            public bool Mirrored = mirrored;
            public List<HandVariableData.Data>? HandData = handData;
        }
        #endregion

        static string animationDataFolder = "animationdata";
        public static readonly string AnimationDataMergedFile = "animationdatasinglefile.txt";
        static string animationSetDataFolder = "animationsetdata";
        public static readonly string AnimationSetMergedDataFile = "animationsetdatasinglefile.txt";

        AnimDataFile animationData = new();
        AnimSetDataFile animationSetData = new();

        List<CacheEntry> miscEntries = [];
        List<CreatureCacheEntry> creatureEntries = [];

        Dictionary<string, CacheEntry> projectIndices = [];
        Dictionary<(string, string), ClipMovementData> movementMap = [];
        Dictionary<(string, string), List<EventInfo>> eventMap = [];

        public AnimationCache(string meshDir) :
            this(new(Path.Combine(meshDir, AnimationDataMergedFile)), new(Path.Combine(meshDir, AnimationSetMergedDataFile)))
        {
        }

        public AnimationCache(FileInfo animDataFile, FileInfo animSetDataFile)
        {
            if (!animDataFile.Exists)
                throw new FileNotFoundException("Animation data file does not exist!");
            if (!animSetDataFile.Exists)
                throw new FileNotFoundException("Animation set data file does not exist!");

           Build(animDataFile, animSetDataFile);
        }

        public CreatureCacheEntry? CloneCreature(string srcProject, string dstProject)
        {
            CacheEntry? src;
            if(!projectIndices.TryGetValue(srcProject.ToLower(), out src))
            {
                return null;
            }
            if(src == null || src is not CreatureCacheEntry)
            { 
                return null; 
            }
            var creature = (CreatureCacheEntry)src;
            ProjectBlock block = (ProjectBlock)creature.Block.Clone();
            ProjectDataBlock movements = (ProjectDataBlock)creature.Movement!.Clone();
            ProjectAttackListBlock sets = (ProjectAttackListBlock)creature.AttackList.Clone();

            int index = animationData.AddProject(dstProject + ".txt", block, movements);
            int creatureIdx = animationSetData.AddProjectAttackBlock($"{dstProject}Data\\{dstProject}.txt", sets);

            creatureEntries.Add(new(dstProject, 
                animationData.ProjectBlocks[index], 
                animationData.MovementData[index], 
                animationSetData.ProjectAttacks[creatureIdx]));

            RebuildIndex();
            return (CreatureCacheEntry)projectIndices[dstProject.ToLower()];
        }

        public void SaveCreature(string projectName, CacheEntry cacheEntry, FileInfo animDataFile, FileInfo animSetDataFile, bool saveMergedSets = true)
        {
            // Write Data block for this project
            string outputDir = animDataFile.Directory?.FullName ?? Environment.CurrentDirectory;
            var animDataDir = Directory.CreateDirectory(Path.Combine(outputDir, animationDataFolder));
            using (StreamWriter streamWriter = new(Path.Combine(animDataDir.FullName, projectName + ".txt")))
                cacheEntry.Block.WriteBlock(streamWriter);

            if (cacheEntry.HasAnimationCache() && cacheEntry.Movement != null)
            {
                var dir = Directory.CreateDirectory(Path.Combine(outputDir, animationDataFolder, "boundanims"));
                using StreamWriter streamWriter = new(Path.Combine(dir.FullName, $"anims_{projectName}.txt"));
                cacheEntry.Movement.WriteBlock(streamWriter);
            }

            if (cacheEntry is CreatureCacheEntry crEntry)
            {
                var setDataDir = Directory.CreateDirectory(Path.Combine(outputDir, animationSetDataFolder, projectName + "data"));
                using (StreamWriter dirWriter = new(Path.Combine(outputDir, animationSetDataFolder, "dirlist.txt")))
                {
                    foreach (var p in animationSetData.Projects)
                    {
                        dirWriter.WriteLine(p);
                    }
                }
                var setProjects = crEntry.AttackList.ProjectFiles;
                var data = crEntry.AttackList.ProjectAttackBlocks;
                for(int i = 0; i < setProjects.Count; i++)
                {
                    using StreamWriter sw = new(Path.Combine(setDataDir.FullName, setProjects[i]));
                    data[i].WriteBlock(sw);
                }

                string masterFile = Path.Combine(setDataDir.FullName, projectName + ".txt").ToLower();
                using (StreamWriter sw = new(masterFile))
                {
                    foreach (var entry in setProjects)
                        sw.WriteLine(entry);
                }
            }

            if(saveMergedSets)
            {
                Save(animDataFile, animSetDataFile);
            }
        }

        void Save(FileInfo animDataFile, FileInfo animSetDataFile)
        {
            using (StreamWriter sw = new(animDataFile.FullName))
                animationData.WriteBlock(sw);
            using (StreamWriter sw = new(animSetDataFile.FullName))
                animationSetData.WriteBlock(sw);
        }

        void Build(FileInfo animDataFile, FileInfo animSetDataFile)
        {
            using var animDataReader = new StreamReader(animDataFile.FullName);
            animationData.ReadBlock(animDataReader);

            using var animSetDataReader = new StreamReader(animSetDataFile.FullName);
            animationSetData.ReadBlock(animSetDataReader);

            int index = 0;
            foreach(var project in animationData.Projects)
            {
                string projectName = Path.GetFileNameWithoutExtension(project);
                string entryName = Path.Combine(projectName + "Data", projectName + ".txt");
                
                if(animationSetData.TryGetProjectAttackBlock(entryName,out ProjectAttackListBlock block))
                {
                    creatureEntries.Add(new(projectName,
                        animationData.ProjectBlocks[index],
                        animationData.MovementData[index],
                        block));
                }
                else
                {
                    ProjectDataBlock? movementData = animationData.MovementData.GetValueOrDefault(index);
                    miscEntries.Add(new(entryName, 
                        animationData.ProjectBlocks[index],
                        movementData));
                }
                index++;
            }

            RebuildIndex();
        }

        void RebuildIndex()
        {
            foreach(var entry in creatureEntries)
            {
                projectIndices[entry.Name.ToLower()] =  entry;
            }
            foreach(var entry in miscEntries)
            {
                projectIndices[entry.Name.ToLower()] = entry;
            }

            movementMap.Clear();
            foreach(var creature in creatureEntries)
            {
                string projectName = creature.Name.ToLower();
                var movtData = creature.Movement?.GetMovementData();
                // Linking clips to root motion data
                if (movtData != null)
                {
                    foreach (var clip in creature.Block.Clips)
                    {
                        if (clip.CacheIndex < movtData.Count)
                        {
                            var output = movtData.Find((data) => data.CacheIndex == clip.CacheIndex);
                            if (output != null)
                            {
                                movementMap[(projectName, clip.Name)] = output;
                            }
                        }
                    }
                }
                foreach(var attackBlock in creature.AttackList.ProjectAttackBlocks)
                {
                    if(!attackBlock.HasHandVariableData())
                    {
                        foreach(var idleEvent in attackBlock.SwapEvents) 
                        {
                            AddToEventMap((projectName, idleEvent), new());
                        }
                    }

                    foreach(var attackData in attackBlock.ClipAttack.AttackData)
                    {
                        AddToEventMap((projectName, attackData.EventName),
                            new(EventType.Attack,
                            attackData.IsMirrored(),
                            attackBlock.HandVariableData.Variables));
                    }
                }
            }
        }

        void AddToEventMap((string, string) key, EventInfo value)
        {
            if(eventMap.ContainsKey(key))
            {
                eventMap[key].Add(value);
            }
            else
            {
                eventMap[key] = [value];
            }
        }
    }
}
