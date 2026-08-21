using HKX2;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Archives.DI;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Cache.Internals.Implementations;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using SECmd.AnimData;
using SECmd.Utils;
using System.CommandLine;

namespace SECmd.Commands
{
    internal static class RetargetCreature
    {
        static readonly Dictionary<string, List<string>> altNameLut = new()
        {
            ["werewolfbeast"] = ["Werewolfbeast", "Werewolf"],
            ["dragonpriest"] = ["Dragonpriest", "Dragon_Priest", "DPriest"],
            ["benthiclurker"] = ["BenthicLurker", "Fishman"],
            ["mudcrab"] = ["Mudcrab", "Mcrab", "Crab", "Mcarbt"],
            ["hagraven"] = ["Hagraven", "Havgraven"],
            ["sabrecat"] = ["SabreCat", "SCat", "Sabrecast"],
            ["dog"] = ["Dog", "Canine"],
        };

        public static void Register(RootCommand root)
        {
            Option<string> raceOption = new("--input", "-i") { Description = "Source race editor ID to be retargeted", Required = true };
            Option<string> targetOption = new("--target", "-t") { Description = "Name of the target creature", Required = true };
            Option<DirectoryInfo> directoryOption = new("--output", "-o") { Description = "Output directory", DefaultValueFactory = parseResult => new(Environment.CurrentDirectory) };
            Option<DirectoryInfo> skyrimDataOption = new("--sdata", "-s")
            {
                Description = "Location of Skyrim SE data folder",
                DefaultValueFactory = parseResult => new(GameEnvironment.Typical.Skyrim(SkyrimRelease.SkyrimSE).DataFolderPath.Path)
            };
            Option<bool> outputXmlOption = new("--xml", "-x") { Description = "Output Havok files in XML along with HKX", DefaultValueFactory = ParseResult => false };

            Command retargetCommand = new("retarget", "Retarget creature project, form and assets")
            {
                raceOption,
                targetOption,
                directoryOption,
                skyrimDataOption,
                outputXmlOption
            };

            retargetCommand.SetAction(parseResults =>
            Execute(
                parseResults.GetValue(raceOption)!,
                parseResults.GetValue(targetOption)!,
                parseResults.GetValue(directoryOption)!,
                parseResults.GetValue(skyrimDataOption)!,
                parseResults.GetValue(outputXmlOption)!
                ));

            root.Subcommands.Add(retargetCommand);
        }

        public static void Execute(string raceEdid, string targetName, DirectoryInfo outputDir, DirectoryInfo skyrimDataDir, bool outputXml)
        {
            Dictionary<string, string> retargetMap = [];

            //using var env = GameEnvironment.Typical.Skyrim(SkyrimRelease.SkyrimSE);
            using var env = GameEnvironment.Typical.Builder<ISkyrimMod, ISkyrimModGetter>(GameRelease.SkyrimSE)
                .WithTargetDataFolder(skyrimDataDir)
                .Build();
            // Get all master
            var loadOrder = env.LoadOrder.ListedOrder.Where(x => x.Mod != null && x.Mod.IsMaster);
            var linkCache = loadOrder.ToImmutableLinkCache();
            var archive = Archive.CreateReader(GameRelease.SkyrimSE, env.DataFolderPath.GetFile("Skyrim - Animations.bsa"));
            var outputMod = new SkyrimMod(ModKey.FromName(targetName, ModType.Plugin), SkyrimRelease.SkyrimSE);

            if (!linkCache.TryResolve<IRaceGetter>(raceEdid, out var raceRecord))
            {
                Console.WriteLine($"Failed to find the requested RACE form: {raceEdid}");
                return;
            }

            if (raceRecord.BehaviorGraph?.Male == null)
            {
                Console.WriteLine("RACE Record is missing male behavior graph");
                return;
            }

            string bhkProjPath = raceRecord.BehaviorGraph.Male.File.DataRelativePath.Path;
            FileInfo inputFile = new(env.DataFolderPath.GetFile(bhkProjPath));

            if (!inputFile.Exists)
            {
                Console.WriteLine("Input directory does not exist!");
                return;
            }

            string meshDir = Path.Combine(env.DataFolderPath, "meshes");
            var srcDir = inputFile.Directory?.FullName;
            if (srcDir == null)
            {
                Console.WriteLine("Source behavior parent directory cannot be found??");
                return;
            }

            var dstDir = Directory.CreateDirectory(Path.Combine(outputDir.FullName, targetName));

            AnimationCache animCache;
            try
            {
                animCache = new(meshDir);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Anim Cache parsing failed: {0}", ex.ToString());
                return;
            }

            var projectRoot = OpenHavokFile(inputFile);
            if (projectRoot == null)
            {
                Console.WriteLine($"Failed to open file: {inputFile.Name}");
                return;
            }

            var projectVariant = GetVariant<hkbProjectData>(projectRoot);
            var stringData = projectVariant?.m_stringData;
            if (stringData is null || stringData.m_characterFilenames?.Count == 0)
            {
                Console.WriteLine("Havok project doesn't contain characters, exiting");
                return;
            }

            string srcCharPath = Path.Combine(srcDir, stringData.m_characterFilenames![0]);

            if (!File.Exists(srcCharPath))
            {
                Console.WriteLine("Character file doesn't exist!");
                return;
            }
            Console.WriteLine($"Character file: {stringData.m_characterFilenames[0]}");
            string srcName = Path.GetFileNameWithoutExtension(srcCharPath);
            {
                int trailIdx = srcName.IndexOf("character", StringComparison.OrdinalIgnoreCase);
                if (trailIdx > 0)
                {
                    srcName = srcName[0..trailIdx];
                }
            }

            Console.WriteLine($"Source Character name: {srcName}");

            // Use a pre-defined list if possible, creature names can have a lot of variations between hkx and esm
            List<string> srcNameList = altNameLut.GetValueOrDefault(srcName.ToLower(), [srcName.ToLower()]);

            var dstCharPath =
                stringData!.m_characterFilenames[0].Replace(srcName, targetName, StringComparison.OrdinalIgnoreCase);

            Warmup.Init();

            Console.WriteLine($"Project character: {stringData.m_characterFilenames[0]}, new name: {dstCharPath}");
            stringData!.m_characterFilenames[0] = dstCharPath;

            // Write project file
            WriteHavok(projectRoot, new FileInfo(Path.Combine(dstDir.FullName, targetName + "Project.hkx")), outputXml);

            Console.WriteLine($"Loading char file {srcCharPath}");

            //using Stream charStream = GetStream(env.DataFolderPath, srcCharPath, archive);
            var charRoot = OpenHavokFile(new FileInfo(srcCharPath));
            if (charRoot is null)
            {
                Console.WriteLine($"Failed to open character file: {Path.GetFileName(srcCharPath)}");
                return;
            }
            var charVariant = GetVariant<hkbCharacterData>(charRoot);
            if (charVariant?.m_stringData?.m_name == null)
            {
                Console.WriteLine("Failed to retrieve character name!");
                return;
            }

            charVariant.m_stringData.m_name = targetName;
            string srcBehaviorName = charVariant.m_stringData.m_behaviorFilename;
            var dstBehaviorPath = srcBehaviorName.Replace(srcName, targetName, StringComparison.OrdinalIgnoreCase);
            retargetMap[srcBehaviorName] = dstBehaviorPath;

            FileInfo dstBehavior = new(Path.Combine(dstDir.FullName, dstBehaviorPath));

            charVariant.m_stringData.m_behaviorFilename = dstBehaviorPath;

            string srcRigPath = Path.Combine(srcDir, charVariant.m_stringData.m_rigName);
            var rigRoot = OpenHavokFile(new FileInfo(srcRigPath));

            if (rigRoot == null)
            {
                Console.WriteLine($"Failed to open skeleton file: {Path.GetFileName(srcRigPath)}");
                return;
            }

            // We could just copy the file over but validating its contents could be useful
            var rigVariant = GetVariant<hkaAnimationContainer>(rigRoot);
            WriteHavok(rigRoot, new FileInfo(Path.Combine(dstDir.FullName, charVariant.m_stringData.m_rigName)), outputXml);

            /* Animation */
            Dictionary<string, string> retargetSOUN = [];
            for (int i = 0; i < charVariant.m_stringData.m_animationNames.Count; i++)
            {
                string name = charVariant.m_stringData.m_animationNames[i];
                string newName = name;
                foreach (string oldName in srcNameList)
                {
                    newName = newName.Replace(oldName, targetName);
                }
                if (newName != name)
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
                                    foreach (string oldName in srcNameList)
                                    {
                                        dstAnnotation = dstAnnotation.Replace(oldName, targetName);
                                    }
                                    annotation.m_annotations[j].m_text = dstAnnotation;

                                    if (text.StartsWith("SoundPlay.") || text.StartsWith("SoundStop.") || text.StartsWith("SoundRelease."))
                                    {
                                        retargetSOUN.TryAdd(text.Split('.')[1], dstAnnotation.Split('.')[1]);
                                        retargetMap[text] = dstAnnotation;
                                    }
                                }
                            }
                    }
                }

                var animOutPath = Path.Combine(dstDir.FullName, newName);
                WriteHavok(animVariants, new FileInfo(animOutPath), outputXml);

                var srcCrcPath = Path.GetRelativePath(meshDir, animFile.FullName).ToLower();
                srcCrcPath = Path.GetDirectoryName(srcCrcPath);
                if (srcCrcPath == null)
                {
                    Console.WriteLine("Animation location is invalid, make sure to run against assets extracted from BSA");
                    return;
                }
                string srcCrc = HKCrc.Compute(srcCrcPath);
                if (!retargetMap.ContainsKey(srcCrc))
                {
                    var dstCrcPath = srcCrcPath;
                    foreach (var oldName in srcNameList)
                        dstCrcPath = dstCrcPath.Replace(oldName, targetName, StringComparison.OrdinalIgnoreCase).ToLower();
                    var dstCrc = HKCrc.Compute(Path.GetDirectoryName(dstCrcPath)!);
                    retargetMap[srcCrc] = dstCrc;
                }
            }/* END Animation */

            //Write Character file
            WriteHavok(charRoot, new FileInfo(Path.Combine(dstDir.FullName, dstCharPath)), outputXml);

            // Locate and retarget all behavior files
            Queue<string> behaviorQueue = new();
            behaviorQueue.Enqueue(srcBehaviorName);
            string srcBehaviorMain = Path.Combine(srcDir, srcBehaviorName);
            var srcBehaviorDir = Directory.GetParent(srcBehaviorMain);
            if (srcBehaviorDir == null)
            {
                Console.WriteLine("Behavior folder doesn't exist!");
                return;
            }

            Dictionary<string, string> retargetMOVT = [];
            Dictionary<string, string> retargetRFCT = [];
            while (behaviorQueue.TryDequeue(out string? file))
            {
                if(file is null)
                 continue;

                FileInfo bFile = new(Path.Combine(srcDir, file));

                // Used later for handling hacky behavior. The proper way of handling this would be to evaluate nested conditions 
                bool mainFile = srcBehaviorMain.Contains(bFile.Name, StringComparison.OrdinalIgnoreCase);

                var bRoot = OpenHavokFile(bFile, out Dictionary<uint, IHavokObject> parsedObj);
                if (bRoot == null) continue;

                var bGraphVariant = GetVariant<hkbBehaviorGraph>(bRoot);
                if (bGraphVariant?.m_data?.m_stringData == null) continue;
                Console.WriteLine($"=== Graph: {bGraphVariant.m_name} ===");

                foreach (var obj in parsedObj)
                {
                    if (obj.Value is hkbBehaviorReferenceGenerator behaviorRef)
                    {
                        Console.WriteLine($"Discovered a referenced behavior: {behaviorRef.m_behaviorName}");
                        behaviorQueue.Enqueue(behaviorRef.m_behaviorName);
                    }
                }

                Console.WriteLine("Retargeting Events:");
                for (int i = 0; i < bGraphVariant.m_data.m_stringData.m_eventNames.Count; ++i)
                {
                    string eventName = bGraphVariant.m_data.m_stringData.m_eventNames[i];
                    string dstEventName = eventName;
                    if (FindNames(eventName, srcNameList, StringComparison.OrdinalIgnoreCase))
                    {
                        dstEventName = ReplaceNames(eventName, srcNameList, targetName);
                    }
                    else if (mainFile && (eventName.StartsWith("SoundPlay.") || eventName.StartsWith("SoundStop.") ||
                        eventName.StartsWith("SoundRelease.") || eventName.StartsWith("Func.")))
                    {
                        // We still have to assign a unique name to this event to be able to export a new FORM
                        // Update; 
                        dstEventName = eventName.Insert(eventName.IndexOf('.') + 1, targetName);
                    }

                    if (dstEventName != eventName)
                    {
                        Console.WriteLine("\tWill substitute {0} references with {1}", eventName, dstEventName);
                        if (eventName.StartsWith("SoundPlay.") || eventName.StartsWith("SoundStop.") ||
                        eventName.StartsWith("SoundRelease."))
                        {
                            retargetSOUN.TryAdd(eventName.Split('.')[1], dstEventName.Split('.')[1]);
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
                for (int i = 0; i < bGraphVariant.m_data.m_stringData.m_variableNames.Count; ++i)
                {
                    string varName = bGraphVariant.m_data.m_stringData.m_variableNames[i];
                    string dstVarName = varName;
                    if (FindNames(varName, srcNameList))
                    {
                        dstVarName = ReplaceNames(varName, srcNameList, targetName);
                    }
                    else if (mainFile && varName.StartsWith("iState_"))
                    {
                        dstVarName = varName.Insert(varName.IndexOf('_') + 1, targetName);
                    }

                    if (dstVarName != varName)
                    {
                        Console.WriteLine($"\tWill substitute {varName} references with {dstVarName}");
                        if (varName.StartsWith("iState_"))
                        {
                            retargetMOVT.TryAdd(varName["iState_".Length..], dstVarName["iState_".Length..]);
                        }

                        bGraphVariant.m_data.m_stringData.m_variableNames[i] = dstVarName;
                        retargetMap[varName] = dstVarName;
                    }
                }

                Console.WriteLine("Retargeting objects:");
                foreach (var obj in parsedObj)
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
                            string clipName = ReplaceNames(bsSyncGen.m_name, srcNameList, targetName);
                            Console.WriteLine($"\tSubstituting clip {bsSyncGen.m_name} with {clipName}");
                            bsSyncGen.m_name = clipName;
                            break;
                        case hkbExpressionDataArray expData:
                            for (int i = 0; i < expData.m_expressionsData.Count; ++i)
                            {
                                var data = expData.m_expressionsData[i].m_expression;
                                if (FindNames(data, srcNameList))
                                {
                                    var dstData = ReplaceNames(data, srcNameList, targetName);
                                    Console.WriteLine($"\tSubstituting expression {data} with {dstData}");
                                    expData.m_expressionsData[i].m_expression = dstData;
                                }
                            }
                            break;
                    }
                }
                string outputName = Path.Combine(bFile.Directory!.Name, ReplaceNames(bFile.Name, srcNameList, targetName));
                if (retargetMap.TryGetValue(outputName, out string? value))
                {
                    outputName = value;
                }
                string bhkOutputPath = Path.Combine(dstDir.FullName, outputName);
                WriteHavok(bRoot, new(bhkOutputPath), outputXml);
            }

            // HANDLE ANIM CACHE HERE
            string srcHavokCacheName = Path.GetFileNameWithoutExtension(inputFile.Name);
            var dstCache = animCache.CloneCreature(srcHavokCacheName, targetName.ToLower() + "project");
            if (dstCache == null)
            {
                Console.WriteLine("Failed to build cache for target creature!");
                return;
            }

            // This is easier than manually updating each field
            using (StringWriter sw = new())
            {
                dstCache.Block.WriteBlock(sw);
                string data = sw.ToString();
                data = ReplaceNames(data, srcNameList, targetName);

                using StringReader sr = new(data);
                dstCache.Block.ReadBlock(sr);
            }
            using (StringWriter sw = new())
            {
                dstCache.AttackList.WriteBlock(sw);
                string data = sw.ToString();
                data = ReplaceNames(data, srcNameList, targetName);
                foreach (var pair in retargetMap)
                {
                    data = data.Replace(pair.Key, pair.Value);
                }

                using StringReader sr = new(data);
                dstCache.AttackList.ReadBlock(sr);
            }

            // Save creature in anim cache
            try
            {
                animCache.SaveCreature(targetName + "project", dstCache,
                    new(Path.Combine(outputDir.FullName, AnimationCache.AnimationDataMergedFile)),
                    new(Path.Combine(outputDir.FullName, AnimationCache.AnimationSetMergedDataFile)));
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to save target creature: {0}", ex.ToString());
                return;
            }

            foreach (var srcEdId in retargetSOUN)
            {
                SoundDescriptor? targetForm = null;
                ISoundDescriptorGetter? sndr = null;
                if (linkCache.TryResolve<ISoundDescriptorGetter>(srcEdId.Key, out sndr))
                {
                    targetForm = outputMod.SoundDescriptors.AddNew();
                    targetForm.DeepCopyIn(sndr);
                }
                else if (linkCache.TryResolve<ISoundMarkerGetter>(srcEdId.Key, out var soun))
                {
                    if (!soun.SoundDescriptor.IsNull && linkCache.TryResolve<ISoundDescriptorGetter>(soun.SoundDescriptor, out sndr))
                    {
                        targetForm = outputMod.SoundDescriptors.AddNew();
                        targetForm.DeepCopyIn(sndr);
                    }
                }
                //Failed to retrieve form
                if (targetForm == null) continue;
                targetForm.EditorID = CreateFormId(srcEdId.Key, srcNameList, targetName);
                Console.WriteLine($"Retargeted SNDR: {sndr!.EditorID} -> {targetForm.EditorID}");
            }

            foreach (var movtForm in loadOrder.MovementType().WinningOverrides())
            {
                if (movtForm?.EditorID is null || movtForm.Name is null) continue;
                if (retargetMOVT.TryGetValue(movtForm.Name, out var movtId))
                {
                    var targetForm = outputMod.MovementTypes.AddNew();
                    targetForm.DeepCopyIn(movtForm);
                    targetForm.EditorID = CreateFormId(movtForm.EditorID, srcNameList, targetName);
                    targetForm.Name = movtId;
                    Console.WriteLine($"Retargeted MOVT: {movtForm.EditorID ?? "N/A"} -> {targetForm.EditorID}");
                }
            }

            foreach (var srcEdId in retargetRFCT)
            {
                if (linkCache.TryResolve<IVisualEffectGetter>(srcEdId.Key, out var rfct))
                {
                    var targetForm = outputMod.VisualEffects.AddNew();
                    targetForm.DeepCopyIn(rfct);
                    targetForm.EditorID = CreateFormId(srcEdId.Key, srcNameList, targetName);
                    Console.WriteLine($"Retargeted RFCT: {rfct.EditorID ?? "N/A"} -> {targetForm.EditorID}");
                }
            }

            Dictionary<FormKey, FormKey> exportedIdles = [];
            foreach (var idle in loadOrder.IdleAnimation().WinningOverrides())
            {
                HashSet<FormKey> idleForms = [];
                bool validChain = CrawlIdleForms(idle, linkCache, idleForms, srcNameList);

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
                            dstIdle.EditorID = CreateFormId(srcIdle.EditorID, srcNameList, targetName);
                        if (srcIdle.Filename != null)
                            dstIdle.Filename = new(ReplaceNames(srcIdle.Filename.GivenPath, srcNameList, targetName));

                        exportedIdles[srcIdle.FormKey] = dstIdle.FormKey;
                        Console.WriteLine($"Retargeted IDLE: {srcIdle.EditorID ?? "N/A"} -> {dstIdle.EditorID ?? "N/A"}");
                    }
                }
            }
            // Fix ANAM links
            outputMod.RemapLinks(exportedIdles);

            // Finally, export RACE record etc
            CopyRaceForm(raceRecord, linkCache, outputMod, srcNameList, targetName);

            outputMod.WriteToBinary($"{Path.Combine(outputDir.FullName, targetName)}.esp");
        }

        private static Stream GetStream(string baseDir, string fileRelativePath, IArchiveReader archive)
        {
            var fileAbsPath = Path.Combine(baseDir, fileRelativePath);
            if (File.Exists(fileAbsPath))
                return new FileInfo(fileAbsPath).OpenRead();

            var fileDir = Path.GetDirectoryName(fileRelativePath);
            if (fileDir != null && archive.TryGetFolder(fileDir, out var folder))
            {
                foreach (var file in folder.Files.Where(x => x.Path.Equals(fileRelativePath, StringComparison.OrdinalIgnoreCase)))
                {
                    return new MemoryStream(file.GetBytes());
                }
            }

            throw new FileNotFoundException("Cannot located requested file: " + fileRelativePath);
        }

        static void CopyRaceForm(IRaceGetter raceForm, ImmutableLoadOrderLinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, SkyrimMod mod, List<string> patterns, string target)
        {
            Dictionary<FormKey, FormKey> formMapping = [];

            var dstRace = CopyForm<Race>(raceForm, mod, patterns, target, formMapping);

            if (raceForm.BehaviorGraph?.Male != null)
            {
                dstRace.BehaviorGraph.Male!.File = ReplaceNames(raceForm.BehaviorGraph.Male.File, patterns, target);
            }
            if (raceForm.SkeletalModel?.Male != null)
            {
                dstRace.SkeletalModel!.Male!.File = ReplaceNames(raceForm.SkeletalModel.Male.File, patterns, target);
            }
            mod.Races.Add(dstRace);
            Console.WriteLine($"Retargeted RACE: {raceForm.EditorID} -> {dstRace.EditorID}");

            if (!raceForm.Skin.TryResolve(linkCache, out var srcArmo) || !FindNames(srcArmo.EditorID, patterns))
                return;

            var dstArmo = CopyForm<Armor>(srcArmo, mod, patterns, target, formMapping);
            mod.Armors.Add(dstArmo);
            Console.WriteLine($"Retargeted ARMO: {srcArmo.EditorID} -> {dstArmo.EditorID}");

            foreach (var armaLink in srcArmo.Armature)
            {
                if (!armaLink.TryResolve(linkCache, out var arma) || !FindNames(arma.EditorID, patterns))
                    continue;

                if (!arma.Race.TryResolve(linkCache, out var armaRace) || armaRace.FormKey != raceForm.FormKey)
                    continue;

                // ARMA
                var dstArma = CopyForm<ArmorAddon>(arma, mod, patterns, target, formMapping);
                dstArma.AdditionalRaces.Clear();
                mod.ArmorAddons.Add(dstArma);
                Console.WriteLine($"Retargeted ARMA: {arma.EditorID} -> {dstArma.EditorID}");

                if (!arma.FootstepSound.TryResolve(linkCache, out var fsts) || !FindNames(fsts.EditorID, patterns))
                    continue;

                // FSTS
                var dstFsts = CopyForm<FootstepSet>(fsts, mod, patterns, target, formMapping);
                mod.FootstepSets.Add(dstFsts);
                Console.WriteLine($"Retargeted FSTS: {fsts.EditorID} -> {dstFsts.EditorID}");

                var stepLists = new[]{fsts.WalkForwardFootsteps,
                                fsts.WalkForwardAlternateFootsteps, fsts.WalkForwardAlternateFootsteps2,
                                fsts.RunForwardFootsteps, fsts.RunForwardAlternateFootsteps };

                foreach (var fstp in stepLists.SelectMany(x => x).Distinct().ToArray())
                {
                    if (!fstp.TryResolve(linkCache, out var fstpRec) || !FindNames(fstpRec.EditorID, patterns))
                        continue;

                    // FSTP
                    var dstFstp = CopyForm<Footstep>(fstpRec, mod, patterns, target, formMapping);
                    dstFstp.Tag = ReplaceNames(fstpRec.Tag, patterns, target);
                    mod.Footsteps.Add(dstFstp);
                    Console.WriteLine($"Retargeted FSTP: {fstpRec.EditorID} -> {dstFstp.EditorID}");

                    if (!fstpRec.ImpactDataSet.TryResolve(linkCache, out var ipds) || !FindNames(ipds.EditorID, patterns))
                        continue;

                    // IPDS
                    var dstIpds = CopyForm<ImpactDataSet>(ipds, mod, patterns, target, formMapping);
                    mod.ImpactDataSets.Add(dstIpds);
                    Console.WriteLine($"Retargeted IPDS: {ipds.EditorID} -> {dstIpds.EditorID}");
                    // IPCT
                    foreach (var ipct in dstIpds.Impacts.Select(x => x.Impact).Distinct().ToArray())
                    {
                        if (!ipct.TryResolve(linkCache, out var ipctRec) || !FindNames(ipctRec.EditorID, patterns))
                            continue;

                        var dstIpct = CopyForm<Impact>(ipctRec, mod, patterns, target, formMapping);
                        mod.Impacts.Add(dstIpct);
                        Console.WriteLine($"Retargeted IPCT: {ipctRec.EditorID} -> {dstIpct.EditorID}");

                        foreach (var sound in new[] { ipctRec.Sound1, ipctRec.Sound2 })
                        {
                            if (!sound.TryResolve(linkCache, out var soundRec) || !FindNames(soundRec.EditorID, patterns))
                                continue;

                            if (soundRec is ISoundMarkerGetter soun)
                            {
                                var dstSoun = CopyForm<SoundMarker>(soun, mod, patterns, target, formMapping);
                                mod.SoundMarkers.Add(dstSoun);
                                Console.WriteLine($"Retargeted SOUN: {soun.EditorID} -> {dstSoun.EditorID}");
                            }
                            else if (soundRec is ISoundDescriptorGetter sndr)
                            {
                                var dstSndr = CopyForm<SoundDescriptor>(sndr, mod, patterns, target, formMapping);
                                mod.SoundDescriptors.Add(dstSndr);
                                Console.WriteLine($"Retargeted SNDR: {sndr.EditorID} -> {dstSndr.EditorID}");
                            }
                        }
                    }
                }
            }

            mod.RemapLinks(formMapping);
        }

        static U CopyForm<U>(ISkyrimMajorRecordGetter formGetter, SkyrimMod mod, List<string> patterns, string target, Dictionary<FormKey, FormKey> mapper)
            where U : SkyrimMajorRecord
        {
            SkyrimMajorRecord dstForm = formGetter.Duplicate(mod.GetNextFormKey());
            if (!formGetter.EditorID.IsNullOrEmpty())
                dstForm.EditorID = CreateFormId(formGetter.EditorID, patterns, target);

            if (dstForm is INamed named && named.Name != null)
            {
                named.Name = ReplaceNames(named.Name, patterns, target);
            }

            mapper.Add(formGetter.FormKey, dstForm.FormKey);

            return (U)dstForm;
        }

        // TODO: Recursion isn't necessary, also check for behavior name only at top level node?
        static bool CrawlIdleForms(IIdleAnimationGetter parent, ILinkCache cache, HashSet<FormKey> forms, List<string> patterns, bool validChain = false)
        {
            // We have to get the filename here to make sure 
            if (!validChain &&
                (parent.Filename != null && FindNames(Path.GetFileName(parent.Filename), patterns)))
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
                srcEdId = replacement + srcEdId;
            }

            return srcEdId;
        }

        static bool FindNames(string? text, IList<string> patterns, StringComparison stringComparison = StringComparison.OrdinalIgnoreCase)
        {
            if (text is null) return false;

            foreach (string p in patterns)
            {
                if (text.Contains(p, stringComparison)) return true;
            }

            return false;
        }

        static string ReplaceNames(string text, IList<string> patterns, string replacement, StringComparison stringComparison = StringComparison.OrdinalIgnoreCase)
        {
            var orig = text;
            foreach (string p in patterns)
            {
                text = text.Replace(p, replacement, stringComparison);
            }

            return text;
        }

        static T? GetVariant<T>(hkRootLevelContainer? root) where T : class?, IHavokObject
        {
            _ = root ?? throw new ArgumentNullException(nameof(root));
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
            return OpenHavokFile(rs);
        }
        
        static hkRootLevelContainer? OpenHavokFile(Stream inputStream)
        {
            var br = new BinaryReaderEx(inputStream);
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
        {
            var ps = new PackFileSerializer();
            if (outputPath.Extension != "hkx")
            {
                outputPath = new FileInfo(Path.ChangeExtension(outputPath.FullName, "hkx"));
            }
            Directory.CreateDirectory(outputPath.DirectoryName!);

            using FileStream ws = File.Create(outputPath.FullName);
            var bw = new BinaryWriterEx(ws);
            ps.Serialize(root, bw, HKXHeader.SkyrimSE());

            if (xml)
            {
                var xs = new XmlSerializer();
                outputPath = new FileInfo(Path.ChangeExtension(outputPath.FullName, "xml"));
                xs.Serialize(root, HKXHeader.SkyrimSE(), outputPath.OpenWrite());
            }

        }
    }
}