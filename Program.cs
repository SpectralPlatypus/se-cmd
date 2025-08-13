using HKX2;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using SECmd.Core;
using SECmd.Utils;

namespace SECmd
{
    internal class Program
    {
        static Dictionary<string, List<string>> altNameLut = new()
        {
            ["werewolfbeast"] = ["Werewolfbeast", "Werewolf"],
            ["dragonpriest"] = ["Dragonpriest", "Dragon_Priest", "DPriest"],
            ["benthiclurker"] = ["BenthicLurker", "Fishman"],
            ["mudcrab"] = ["Mudcrab", "Mcrab", "Crab"],
            ["hagraven"] = ["Hagraven", "Havgraven"],
            ["sabrecat"] = ["SabreCat", "SCat", "Sabrecast" ], 
        };
        static void Main(string[] args)
        {
            Dictionary<string, string> retargetMap = [];

            if (args.Length < 2)
            {
                Console.WriteLine("Usage: ck-cmdnet retargetcreature <base project> <target project>");
            }
            string inFile = args[0];
            string targetName = args[1];

            if (!File.Exists(inFile))
            {
                Console.WriteLine("Input directory does not exist!");
            }

            string meshDir = inFile.Substring(0,inFile.IndexOf("actors"));

            var srcDir = Path.GetDirectoryName(inFile)!;
            var dstDir = Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, targetName));

            FileInfo animDataFile = new(Path.Combine(meshDir, "animationdatasinglefile.txt"));
            FileInfo animSetDataFile = new(Path.Combine(meshDir, "animationsetdatasinglefile.txt"));
            AnimationCache? animCache = null;
            try
            {
                animCache = new(animDataFile, animSetDataFile);
            }
            catch(Exception ex)
            {
                Console.WriteLine("Anim Cache parsing failed: {0}", ex.ToString());
                return;
            }
            if(animCache == null)
            {
                Console.WriteLine("Anim cache object is null!");
                return;
            }

            var projectRoot = OpenHavokFile(new FileInfo(inFile));
            var projectVariant = GetVariant<hkbProjectData>(projectRoot);
            var stringData = projectVariant?.m_stringData;
            if (stringData?.m_characterFilenames?.Count == 0)
            {
                Console.WriteLine("Havok project file contains no character file, exiting");
                return;
            }
            string srcCharPath = Path.Combine(srcDir, stringData!.m_characterFilenames[0]);
            if (!File.Exists(srcCharPath))
            {
                Console.WriteLine("Character file doesn't exist!");
            }
            Console.WriteLine($"Character file: {stringData!.m_characterFilenames[0]}");
            string srcName = Path.GetFileNameWithoutExtension(srcCharPath);

            Console.WriteLine("Old Char name: {0}", srcName);

            // Use a pre-defined list if possible, creature names can have a lot of variations between hkx and esm
            List<string> oldNameList = [];
            if (!altNameLut.ContainsKey(srcName.ToLower()))
            {
                oldNameList = [srcName.ToLower()];
            }
            else
            {
                oldNameList = altNameLut[srcName.ToLower()];
            }

            var dstCharPath =
                stringData!.m_characterFilenames[0].Replace(srcName, targetName, StringComparison.OrdinalIgnoreCase);

            Warmup.Init();

            Console.WriteLine("Project character: {0}, new name: {1}", stringData!.m_characterFilenames[0], dstCharPath);
            stringData!.m_characterFilenames[0] = dstCharPath;

            // Get root and then obtain named variant
            WriteHavok(projectRoot, new FileInfo(Path.Combine(dstDir.FullName, targetName + "project.hkx")));

            Console.WriteLine("Loading char file {0}", srcCharPath);
            var charRoot = OpenHavokFile(new FileInfo(srcCharPath));
            var charVariant = GetVariant<hkbCharacterData>(charRoot);
            if (charVariant?.m_stringData?.m_name == null)
            {
                Console.WriteLine("Cannot open character file!");
                return;
            }

            charVariant.m_stringData.m_name = targetName;
            string srcBehaviorName = charVariant.m_stringData.m_behaviorFilename;
            var dstBehaviorPath = srcBehaviorName.Replace(srcName, targetName, StringComparison.OrdinalIgnoreCase);
            retargetMap[srcBehaviorName] = dstBehaviorPath;

            FileInfo dstBehavior = new FileInfo(Path.Combine(dstDir.FullName, dstBehaviorPath));

            charVariant.m_stringData.m_behaviorFilename = dstBehaviorPath;

            string srcRigPath = Path.Combine(srcDir, charVariant.m_stringData.m_rigName);
            var rigRoot = OpenHavokFile(new FileInfo(srcRigPath));
            var rigVariant = GetVariant<hkaAnimationContainer>(rigRoot);
            if (rigRoot == null)
            {
                Console.WriteLine("Cannot open character file!");
                return;
            }

            WriteHavok(rigRoot, new FileInfo(Path.Combine(dstDir.FullName, charVariant.m_stringData.m_rigName)));

            /* Animation */
            Dictionary<string,string> retargetSOUN = new();
            for (int i = 0; i < charVariant.m_stringData.m_animationNames.Count; i++)
            {
                string name = charVariant.m_stringData.m_animationNames[i];
                string newName = name;
                foreach (string oldName in oldNameList)
                {
                    newName = newName.Replace(oldName, targetName);
                }
                if(newName != name)
                {
                    Console.WriteLine("Will substitute {0} references with {1}", name, newName);
                    string srcAnimCrc = HKCrc.Compute(Path.GetFileNameWithoutExtension(name));
                    string dstAnimCrc = HKCrc.Compute(Path.GetFileNameWithoutExtension(newName));
                    
                    retargetMap[name] = newName;
                    retargetMap[srcAnimCrc] = dstAnimCrc;

                    charVariant.m_stringData.m_animationNames[i] = newName;
                }
       
                var animFile = new FileInfo(Path.Combine(srcDir, name));
                if (!animFile.Exists)
                {
                    Console.WriteLine("Anim file doesn't exist: {0}", animFile.Name);
                    continue;
                }
                Console.WriteLine("Found animation {0}", animFile);
                var animVariants = OpenHavokFile(animFile);
                if (animVariants == null)
                {
                    Console.WriteLine("Failed to retrieve anim data for {0}", animFile);
                    continue;
                }

                foreach (var variant in animVariants.m_namedVariants)
                {
                    if (variant?.m_variant is hkaAnimationContainer animContainer)
                    {
                        foreach (var anim in animContainer.m_animations)
                            foreach (var annotation in anim.m_annotationTracks)
                            {
                                for (int j = 0; j < annotation.m_annotations.Count; j++)
                                {
                                    var text = annotation.m_annotations[j].m_text;
                                    string dstAnnotation = text;
                                    foreach (string oldName in oldNameList)
                                    {
                                        dstAnnotation = dstAnnotation.Replace(oldName, targetName);
                                    }
                                    annotation.m_annotations[j].m_text = dstAnnotation;

                                    if (text.StartsWith("SoundPlay."))
                                    {
                                        retargetSOUN.TryAdd(text["SoundPlay.".Length..], dstAnnotation["SoundPlay.".Length..]);
                                        retargetMap[text] = dstAnnotation;
                                    }
                                }
                            }
                    }
                }

                var animOutPath = Path.Combine(dstDir.FullName, newName);
                WriteHavok(animVariants, new FileInfo(animOutPath));

                var srcCrcPath = Path.GetRelativePath(meshDir, animFile.FullName).ToLower();
                srcCrcPath = Path.GetDirectoryName(srcCrcPath);
                if(srcCrcPath == null)
                {
                    Console.WriteLine("Animation location is invalid, make sure to run against assets extracted from BSA");
                    return;
                }
                string srcCrc = HKCrc.Compute(srcCrcPath);
                if(!retargetMap.ContainsKey(srcCrc))
                {
                    var dstCrcPath = srcCrcPath;
                    foreach (var oldName in oldNameList)
                        dstCrcPath = dstCrcPath.Replace(oldName, targetName, StringComparison.OrdinalIgnoreCase).ToLower();
                    var dstCrc = HKCrc.Compute(Path.GetDirectoryName(dstCrcPath)!);
                    retargetMap[srcCrc] = dstCrc;
                }
            }/* END Animation */

            //Write Character file
            WriteHavok(charRoot, new FileInfo(Path.Combine(dstDir.FullName, dstCharPath)));

            // Locate and retarget all behavior files
            string srcBehaviorMain = Path.Combine(srcDir, srcBehaviorName);
            var srcBehaviorDir = Directory.GetParent(srcBehaviorMain);
            if (srcBehaviorDir == null) {
                Console.WriteLine("Behavior folder doesn't exist!");
                return;
            }

            Dictionary<string, string> retargetMOVT = [];
            Dictionary<string, string> retargetRFCT = [];
            // Iterate over behaviors
            foreach(FileInfo bFile in srcBehaviorDir.GetFiles("*.hkx"))
            {
                // Used later for handling hacky behavior. The proper way of handling this would be to evaluate nested conditions 
                bool mainFile = srcBehaviorMain.Contains(bFile.Name, StringComparison.OrdinalIgnoreCase);

                Dictionary<uint, IHavokObject> parsedObj;
                var bRoot = OpenHavokFile(bFile, out parsedObj);
                if (bRoot == null) continue;

                var bGraphVariant = GetVariant<hkbBehaviorGraph>(bRoot);
                if (bGraphVariant?.m_data?.m_stringData == null) continue;


                Console.WriteLine($"Graph: {bGraphVariant.m_name}");
                Console.WriteLine("Retargeting Events:");
                for(int i = 0; i < bGraphVariant.m_data.m_stringData.m_eventNames.Count; ++i)
                {
                    string eventName = bGraphVariant.m_data.m_stringData.m_eventNames[i];
                    string dstEventName = eventName;
                    if (FindNames(eventName, oldNameList, StringComparison.OrdinalIgnoreCase))
                    {
                        dstEventName = ReplaceNames(eventName, oldNameList, targetName);
                    }
                    else if(mainFile && eventName.StartsWith("SoundPlay.") || eventName.StartsWith("Func."))
                    {
                        // We still have to assign a unique name to this event to be able to export a new FORM
                        // Update; 
                        dstEventName = eventName.Insert(eventName.IndexOf('.')+1, targetName);
                    }

                    if(dstEventName != eventName)
                    {
                        Console.WriteLine("\tWill substitute {0} references with {1}", eventName, dstEventName);
                        if (eventName.StartsWith("SoundPlay."))
                        {
                            retargetSOUN.TryAdd(eventName["SoundPlay.".Length..], dstEventName["SoundPlay.".Length..]);
                        }
                        else if (eventName.StartsWith("Func."))
                        {
                            retargetRFCT.TryAdd(eventName["Func.".Length..], dstEventName["Func.".Length..]);
                        }

                        bGraphVariant.m_data.m_stringData.m_eventNames[i] = dstEventName;
                        retargetMap[eventName] = dstEventName;
                    }
                }

                Console.WriteLine("Retargeting Variables:");
                for(int i = 0; i < bGraphVariant.m_data.m_stringData.m_variableNames.Count; ++i)
                {
                    string varName = bGraphVariant.m_data.m_stringData.m_variableNames[i];
                    string dstVarName = varName;
                    if (FindNames(varName, oldNameList))
                    {
                        dstVarName = ReplaceNames(varName, oldNameList, targetName);
                    }
                    else if(mainFile && varName.StartsWith("iState_"))
                    {
                        dstVarName = varName.Insert(varName.IndexOf('_') + 1, targetName);
                    }

                    if(dstVarName != varName)
                    {
                        Console.WriteLine("\tWill substitute {0} references with {1}", varName, dstVarName);
                        if(varName.StartsWith("iState_"))
                        {
                            retargetMOVT.TryAdd(varName["iState_".Length..], dstVarName["iState_".Length..]);
                        }

                        bGraphVariant.m_data.m_stringData.m_variableNames[i] = dstVarName;
                        retargetMap[varName] = dstVarName;
                    }
                }

                Console.WriteLine("Retargeting objects:");
                foreach(var obj in parsedObj)
                {
                    switch (obj.Value)
                    {
                        case hkbClipGenerator clipGen:
                            if (retargetMap.TryGetValue(clipGen.m_animationName, out string? dstClip))
                            {
                                Console.WriteLine($"\tRetargeting clip {clipGen.m_animationName} to {dstClip}");
                                clipGen.m_animationName = dstClip;
                            }
                            break;
                        case BSSynchronizedClipGenerator bsSyncGen:
                            string clipName = ReplaceNames(bsSyncGen.m_name,oldNameList,targetName);
                            Console.WriteLine($"\tSubstituting clip {bsSyncGen.m_name} with {clipName}");
                            bsSyncGen.m_name = clipName;
                            break;
                        case hkbExpressionDataArray expData:
                            for(int i = 0; i < expData.m_expressionsData.Count; ++i)
                            {
                                var data = expData.m_expressionsData[i].m_expression;
                                if(FindNames(data, oldNameList))
                                {
                                    var dstData = ReplaceNames(data, oldNameList, targetName);
                                    Console.WriteLine($"\tSubstituting expression {data} with {dstData}");
                                    expData.m_expressionsData[i].m_expression = dstData;
                                }
                            }
                            break;
                    }
                }
                string outputName = Path.Combine(bFile.Directory!.Name, ReplaceNames(bFile.Name,oldNameList,targetName));
                if(retargetMap.TryGetValue(outputName, out string? value))
                {
                    outputName = value;
                }
                string bOutputPath = Path.Combine(dstDir.FullName, outputName);
                WriteHavok(bRoot, new(bOutputPath));
            }

            // HANDLE ANIM CACHE HERE
            string srcHavokCacheName = Path.GetFileNameWithoutExtension(inFile);
            var dstCache = animCache.CloneCreature(srcHavokCacheName, targetName.ToLower() + "project");
            if(dstCache == null)
            {
                Console.WriteLine("Failed to build cache for target creature!");
                return;
            }

            // This is easier than manually updating each field
            using (StringWriter sw = new())
            {
                dstCache.Block.WriteBlock(sw);
                string data = sw.ToString();
                data = ReplaceNames(data, oldNameList, targetName);

                using StringReader sr = new(data);
                dstCache.Block.ReadBlock(sr);
            }
            using (StringWriter sw = new())
            {
                dstCache.AttackList.WriteBlock(sw);
                string data = sw.ToString();
                data = ReplaceNames(data, oldNameList, targetName);
                foreach (var pair in retargetMap)
                {
                    data = data.Replace(pair.Key, pair.Value);
                }

                using StringReader sr = new(data);
                dstCache.AttackList.ReadBlock(sr);
            }

            // Save creature -- I'm tired boss
            try
            {
                animCache.SaveCreature(targetName + "project", dstCache, new("animationdatasinglefile.txt"), new("animationsetdatasinglefile.txt"));
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to save target creature: {0}", ex.ToString());
                return;
            }

            // Now... ESM time
            using var env = GameEnvironment.Typical.Skyrim(SkyrimRelease.SkyrimSE);
            // Get all master
            var loadOrder = env.LoadOrder.PriorityOrder.Where(x => x.Mod != null && x.Mod.IsMaster);
            var linkCache = loadOrder.ToImmutableLinkCache();
            var outputMod = new SkyrimMod(ModKey.FromNameAndExtension(Path.GetFileName(targetName+".esp")), SkyrimRelease.SkyrimSE);

            foreach (var srcEdId in retargetSOUN)
            {
                ISoundDescriptorGetter? sndr = null;
                SoundDescriptor? targetForm = null;
                if(linkCache.TryResolve<ISoundDescriptorGetter>(srcEdId.Key, out sndr))
                {
                    targetForm = outputMod.SoundDescriptors.AddNew();
                    targetForm.DeepCopyIn(sndr!);
                }
                else if(linkCache.TryResolve<ISoundMarkerGetter>(srcEdId.Key, out var soun))
                {
                    if(!soun.SoundDescriptor.IsNull && linkCache.TryResolve(soun.SoundDescriptor, out sndr))
                    {
                        targetForm = outputMod.SoundDescriptors.AddNew();
                        targetForm.DeepCopyIn(sndr!);
                    }
                }
                //Failed to retrieve form
                if (targetForm == null) continue;
                targetForm.EditorID = CreateFormId(srcEdId.Key, oldNameList, targetName);
                Console.WriteLine("Retargeted SNDR: {0} -> {1}", sndr!.EditorID!, targetForm.EditorID);
            }

            foreach (var movtForm in loadOrder.MovementType().WinningOverrides())
            {
                if(movtForm?.EditorID == null) continue;
                if(retargetMOVT.TryGetValue(movtForm.Name, out var movtId))
                {
                    var targetForm = outputMod.MovementTypes.AddNew();
                    targetForm.DeepCopyIn(movtForm);
                    targetForm.EditorID = CreateFormId(movtForm.EditorID, oldNameList, targetName);
                    targetForm.Name = movtId;
                    Console.WriteLine("Retargeted MOVT: {0} -> {1}", movtForm.EditorID!, targetForm.EditorID);
                }
            }

            foreach (var srcEdId in retargetRFCT)
            {
                if (linkCache.TryResolve<IVisualEffectGetter>(srcEdId.Key, out var rfct))
                {
                    var targetForm = outputMod.VisualEffects.AddNew();
                    targetForm.DeepCopyIn(rfct);
                    targetForm.EditorID = CreateFormId(srcEdId.Key, oldNameList, targetName);
                    Console.WriteLine("Retargeted RFCT: {0} -> {1}", rfct.EditorID!, targetForm.EditorID);
                }
            }

            Dictionary<FormKey,FormKey> exportedIdles = [];
            foreach (var idle in loadOrder.IdleAnimation().WinningOverrides())
            {
                HashSet<FormKey> idleForms = [];
                bool validChain = CrawlIdleForms(idle, linkCache, idleForms, oldNameList);

                // If we have a valid link, start writing and linking IDLE forms
                if (!validChain) continue;
                foreach (var formKey in idleForms)
                {
                    var srcIdle = linkCache.Resolve<IIdleAnimationGetter>(formKey);
                    if (!exportedIdles.ContainsKey(srcIdle.FormKey))
                    {
                        var dstIdle = outputMod.IdleAnimations.AddNew();
                        dstIdle.DeepCopyIn(srcIdle);
                        if (srcIdle.EditorID != null)
                            dstIdle.EditorID = CreateFormId(srcIdle.EditorID, oldNameList, targetName);
                        if(srcIdle.Filename != null)
                            dstIdle.Filename = new(ReplaceNames(srcIdle.Filename.GivenPath, oldNameList, targetName));

                        exportedIdles[srcIdle.FormKey] = dstIdle.FormKey;
                        Console.WriteLine("Retargeted IDLE: {0} -> {1}", srcIdle.EditorID ?? "N/A", dstIdle.EditorID ?? "N/A");
                    }
                }
            }
            // Second loop, fix ANAM links
            foreach(var idle in outputMod.IdleAnimations.Records)
            {
                for(int i = 0; i<idle.RelatedIdles.Count; i++)
                {
                    if(linkCache.TryResolve(idle.RelatedIdles[i], out var relIdle) && relIdle.Type == typeof(IIdleAnimation))
                    {
                        idle.RelatedIdles[i] = outputMod.IdleAnimations.RecordCache[exportedIdles[relIdle.FormKey]].ToLink();
                    }
                }
            }

            outputMod.WriteToBinary($".\\{targetName}.esp");
        }

        static Dictionary<FormKey, FormKey> exportedIdles = [];
        static bool CrawlIdleForms(IIdleAnimationGetter parent, ILinkCache cache, HashSet<FormKey> forms, List<string> patterns, bool validChain = false)
        {
            if (!validChain &&
                (parent.Filename != null && FindNames(parent.Filename, patterns)))
                //(parent.EditorID != null && FindNames(parent.EditorID, patterns)))
            {
                validChain = true; // copy the rest of this chain
            }

            forms.Add(parent.FormKey);
            // Only look up parent IDLE
            if (cache.TryResolve(parent.RelatedIdles[0], out var relIdle) && relIdle.Type == typeof(IIdleAnimation) && relIdle != parent)
            {
                validChain |= CrawlIdleForms((IIdleAnimationGetter)relIdle, cache, forms, patterns, validChain);
            }

            return validChain;
        }
        

        static string CreateFormId(string srcEdId, IList<string> patterns, string replacement)
        {
            if (FindNames(srcEdId, patterns))
            {
                srcEdId = ReplaceNames(srcEdId, patterns, replacement);
            }
            else
            {
                srcEdId  = replacement + srcEdId;
            }

            return srcEdId;
        }

        static bool FindNames(string text, IList<string> patterns, StringComparison stringComparison = StringComparison.OrdinalIgnoreCase)
        {
            foreach(string p in patterns)
            {
                if (text.Contains(p, stringComparison)) return true;
            }

            return false;
        }

        static string ReplaceNames(string text, IList<string> patterns, string replacement, StringComparison stringComparison = StringComparison.OrdinalIgnoreCase)
        {
            foreach (string p in patterns)
            {
                text = text.Replace(p, replacement, stringComparison);
            }

            return text;
        }

        static T? GetVariant<T>(hkRootLevelContainer? root) where T : class?, IHavokObject
        {
            _ = root ?? throw new ArgumentNullException("Empty root container");
            if (root.m_namedVariants.Count == 0)
            {
                throw new IOException("Root does not contain any variants");
            }

            var namedVariants = root.m_namedVariants;
            for (int i = 0; i < namedVariants.Count; ++i)
            {
                if (namedVariants[i]?.m_variant is T)
                    return namedVariants[i]?.m_variant as T;
            }

            return null;
        }

        static hkRootLevelContainer? OpenHavokFile(FileInfo inputFile)
        {
            using FileStream rs = inputFile.OpenRead();

            var br = new BinaryReaderEx(rs);
            var des = new PackFileDeserializer();
            var root = (hkRootLevelContainer)des.Deserialize(br);

            return root;
        }
        static hkRootLevelContainer? OpenHavokFile(FileInfo inputFile, out Dictionary<uint, IHavokObject> parsedObjects)
        {
            using FileStream rs = inputFile.OpenRead();

            var br = new BinaryReaderEx(rs);
            var des = new PackFileDeserializer();
            var root = (hkRootLevelContainer)des.Deserialize(br, out parsedObjects);
            return root;
        }

        static void WriteHavok(IHavokObject root, FileInfo outputPath, bool xml = false)
        {   if (xml)
            {
                var xs = new XmlSerializer();
                if (outputPath.Extension != "xml")
                {
                    outputPath = new FileInfo(Path.ChangeExtension(outputPath.FullName, "xml"));
                }
                Directory.CreateDirectory(outputPath.DirectoryName!);
                xs.Serialize(root, HKXHeader.SkyrimSE(), outputPath.OpenWrite());
            }
            else
            {
                var xs = new PackFileSerializer();
                if (outputPath.Extension != "hkx")
                {
                    outputPath = new FileInfo(Path.ChangeExtension(outputPath.FullName, "hkx"));
                }
                Directory.CreateDirectory(outputPath.DirectoryName!);
                using FileStream ws = File.Create(outputPath.FullName);
                var bw = new BinaryWriterEx(ws);
                xs.Serialize(root, bw, HKXHeader.SkyrimSE());
            }
        }
    }
}
