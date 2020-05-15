﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Numerics;
using System.Reflection;
using System.IO;
using System.Drawing;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NC_Reactor_Planner
{
    /// <summary>
    /// This struct holds a slightly more efficient representation of the reactor.
    /// I'm saving all cells as "Type":[List of coordinates] entries instead of as individual blocks.
    /// </summary>
    public struct CompressedSaveFile
    {
        public Version SaveVersion;
        public List<Dictionary<string, List<Vector3>>> CompressedReactor;
        public List<Tuple<Vector3, string, bool>> FuelCells;
        public Vector3 InteriorDimensions;

        public CompressedSaveFile(Version sv, List<Dictionary<string, List<Vector3>>> cr, List<Tuple<Vector3, string, bool>> fc, Vector3 id)
        {
            SaveVersion = sv;
            CompressedReactor = cr;
            FuelCells = fc;
            InteriorDimensions = id;
        }
    }

    /// <summary>
    /// This struct holds reactor stat values for saving into the json \ passing around.
    /// </summary>
    public struct ReactorStats
    {
        public double TotalOutput;
        public double RawOutput;
        public double TotalHeatPerTick;
        public double TotalCoolingPerTick;
        public double NetHeatPerTick;
        public double OverallEfficiency;
        public double OverallHeatMultiplier;
        public int FunctionalBlocks;
        public int TotalInteriorBlocks;
        public double SparsityPenaltyMultiplier;

        public ReactorStats(double totalOutput, double rawOutput, double totalHeatPerTick, double totalCoolingPerTick, double netHeatPerTick, double overallEfficiency, double overallHeatMultiplier, int functionalBlocks, int totalInteriorBlocks, double sparsityPenaltyMultiplier)
        {
            TotalOutput = totalOutput;
            RawOutput = rawOutput;
            TotalHeatPerTick = totalHeatPerTick;
            TotalCoolingPerTick = totalCoolingPerTick;
            NetHeatPerTick = netHeatPerTick;
            OverallEfficiency = overallEfficiency;
            OverallHeatMultiplier = overallHeatMultiplier;
            FunctionalBlocks = functionalBlocks;
            TotalInteriorBlocks = totalInteriorBlocks;
            SparsityPenaltyMultiplier = sparsityPenaltyMultiplier;
        }
    }

    /// <summary>
    /// Ugly main class in need of refactoring!
    /// Holds the reactor structure, handles everything to do with simulating, modifying,
    /// calculating stats, save\loading, etc.
    /// </summary>
    public static class Reactor
    {
        public static readonly PlannerUI UI;

        private static Block[,,] blocks;
        public static List<Cluster> clusters;
        public static List<ConductorGroup> conductorGroups;
        public static List<ReactorGridLayer> layers;
        public static Vector3 interiorDims;
        public static readonly Version saveVersion;
        
        public static Dictionary<string, List<HeatSink>> heatSinks;
        public static List<FuelCell> fuelCells;
        public static Dictionary<string, List<Moderator>> moderators;
        public static List<Conductor> conductors;
        public static Dictionary<string, List<Reflector>> reflectors;
        public static List<Irradiator> irradiators;
        public static int totalCasings;
        public static int totalInteriorBlocks;

        public static readonly List<Vector3> sixAdjOffsets = new List<Vector3> { new Vector3(-1, 0, 0), new Vector3(1, 0, 0), new Vector3(0, -1, 0), new Vector3(0, 1, 0), new Vector3(0, 0, -1), new Vector3(0, 0, 1) };// x+-1, y+-1, z+-1

        public static double totalCoolingPerTick = 0;
        public static Dictionary<string, double> totalCoolingPerType;
        public static double totalHeatPerTick = 0;
        public static CoolantRecipeValues coolantRecipe;
        public static string coolantRecipeName;
        public static double totalOutputPerTick = 0;
        
        public static double heatMultiplier = 0;
        public static double efficiency = 0;
        public static int functionalBlocks = 0;
        public static double sparsityPenalty = 0;

        static Reactor()
        {
            saveVersion = Assembly.GetEntryAssembly().GetName().Version;
            UI = new PlannerUI();
            UI.Controls.Add(Palette.PaletteControl);
            Palette.PaletteControl.Parent = UI;
            Palette.PaletteControl.Location = UI.PalettePanelLocation;
        }

        public static void InitializeReactor(int interiorX, int interiorY, int interiorZ)
        {
            interiorDims = new Vector3(interiorX, interiorY, interiorZ);
            blocks = CreateBlockArray(interiorX, interiorY, interiorZ);
            ConstructLayers();

        }

        public static void InitializeReactor(Vector3 interiorDims)
        {
            InitializeReactor((int)interiorDims.X, (int)interiorDims.Y, (int)interiorDims.Z);
        }

        private static Block[,,] CreateBlockArray(int interiorX, int interiorY, int interiorZ)
        {
            Block[,,] newBlocks = new Block[interiorX + 2, interiorY + 2, interiorZ + 2];
            for (int x = 0; x < interiorX + 2; x++)
                for (int y = 0; y < interiorY + 2; y++)
                    for (int z = 0; z < interiorZ + 2; z++)
                        newBlocks[x, y, z] = new Block("Air", BlockTypes.Air, Palette.Textures["Air"], new Vector3(x, y, z));

            for (int y = 1; y < interiorY + 1; y++)
                for (int z = 1; z < interiorZ + 1; z++)
                {
                    newBlocks[0, y, z] = new Casing("Casing", null, new Vector3(0, y, z));
                    newBlocks[interiorX + 1, y, z] = new Casing("Casing", null, new Vector3(interiorX + 1, y, z));
                }
            for (int x = 1; x < interiorX + 1; x++)
                for (int z = 1; z < interiorZ + 1; z++)
                {
                    newBlocks[x, 0, z] = new Casing("Casing", null, new Vector3(x, 0, z));
                    newBlocks[x, interiorY + 1, z] = new Casing("Casing", null, new Vector3(x, interiorY + 1, z));
                }
            for (int y = 1; y < interiorY + 1; y++)
                for (int x = 1; x < interiorX + 1; x++)
                {
                    newBlocks[x, y, interiorZ + 1] = new Casing("Casing", null, new Vector3(x, y, interiorZ + 1));
                    newBlocks[x, y, 0] = new Casing("Casing", null, new Vector3(x, y, 0));
                }

            return newBlocks;
        }

        public static void ConstructLayers()
        {
            DisposeClearLayers();
            layers = new List<ReactorGridLayer>();

            for (int y = 1; y <= interiorDims.Y; y++)
            {
                layers.Add(new ReactorGridLayer(y));
            }
        }

        private static void DisposeClearLayers()
        {
            if (layers != null)
            {
                foreach (ReactorGridLayer layer in layers)
                    layer.Dispose();
                layers.Clear();
            }
        }

        public static void Redraw()
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine("Redrawring reactor");
#endif
            foreach (ReactorGridLayer layer in layers)
            {
                layer.Refresh();
            }
        }

        public static void Update()
        {
            RegenerateTypedLists();
            clusters = new List<Cluster>();
            
            foreach (KeyValuePair<string, List<HeatSink>> heatSinks in heatSinks)
                foreach (HeatSink heatSink in heatSinks.Value)
                    heatSink.RevertToSetup();

            foreach (Conductor conductor in conductors)
                conductor.RevertToSetup();

            foreach (Irradiator irradiator in irradiators)
                irradiator.RevertToSetup();

            foreach (FuelCell fuelCell in fuelCells)
            {
                fuelCell.RevertToSetup();
                if (fuelCell.Primed && !fuelCell.CanBePrimed())
                    fuelCell.Primed = false;
            }

            foreach (var reflectorType in reflectors)
                foreach (Reflector reflector in reflectorType.Value)
                    reflector.RevertToSetup();

            FormConductorGroups();

            RunFuelCellActivation();
            foreach (FuelCell fuelCell in fuelCells)
                fuelCell.FilterAdjacentStuff();

            foreach (var reflectorType in reflectors)
                foreach (Reflector reflector in reflectorType.Value)
                    reflector.UpdateStats();

            foreach (KeyValuePair<string, List<Moderator>> moderators in moderators)
                foreach (Moderator moderator in moderators.Value)
                    moderator.RevertToSetup();

            UpdateModerators();

            OrderedUpdateHeatSinks();

            FormClusters();
            foreach (Cluster cluster in clusters)
                cluster.UpdateStats();

            UpdateStats();
        }

        private static void RegenerateTypedLists()
        {
            heatSinks = new Dictionary<string, List<HeatSink>>();
            fuelCells = new List<FuelCell>();
            moderators = new Dictionary<string, List<Moderator>>
            {
                { "Graphite", new List<Moderator>() },
                { "Beryllium", new List<Moderator>() },
                { "HeavyWater", new List<Moderator>() }
            };
            conductors = new List<Conductor>();
            reflectors = new Dictionary<string, List<Reflector>>();
            irradiators = new List<Irradiator>();

            functionalBlocks = 0;

            foreach (Block block in blocks)
            {
                if (block is HeatSink heatSink)
                {
                    if (heatSinks.ContainsKey(heatSink.DisplayName))
                        heatSinks[heatSink.DisplayName].Add(heatSink);
                    else
                        heatSinks.Add(heatSink.DisplayName, new List<HeatSink> { heatSink });
                    ++functionalBlocks;
                }
                else if (block is FuelCell fuelCell)
                {
                    fuelCells.Add(fuelCell);
                    ++functionalBlocks;
                }
                else if (block is Moderator moderator)
                {
                    if (moderators.ContainsKey(moderator.DisplayName))
                        moderators[moderator.DisplayName].Add(moderator);
                    else
                        moderators.Add(moderator.DisplayName, new List<Moderator> { moderator });
                    ++functionalBlocks;
                }
                else if (block is Conductor conductor)
                    conductors.Add(conductor);
                else if (block is Reflector reflector)
                {
                    if (reflectors.ContainsKey(reflector.ReflectorType))
                        reflectors[reflector.ReflectorType].Add(reflector);
                    else
                        reflectors.Add(reflector.ReflectorType, new List<Reflector> { reflector });
                    ++functionalBlocks;
                }
                else if (block is Irradiator irradiator)
                {
                    irradiators.Add(irradiator);
                    ++functionalBlocks;
                }
            }
        }

        private static void RunFuelCellActivation()
        {
            List<FuelCell> visited = new List<FuelCell>();
            List<FuelCell> activeFuelCells = fuelCells.FindAll(delegate (FuelCell fc) { return fc.Primed; });
            foreach (FuelCell activeFuelCell in activeFuelCells)
            {
                List<FuelCell> queue = new List<FuelCell>{activeFuelCell};
                FuelCell fuelCell;
                while (queue.Count > 0)
                {
                    fuelCell = queue.First();
                    queue.Remove(fuelCell);
                    visited.Add(fuelCell);
                    foreach (FuelCell fc in fuelCell.FindModeratorsThenAdjacentCells())
                    {
                        if (!visited.Contains(fc))
                            queue.Add(fc);
                    }
                }
            }
        }

        private static void UpdateModerators()
        {
            foreach (KeyValuePair<string, List<Moderator>> moderators in moderators)
                foreach (Moderator moderator in moderators.Value)
                    moderator.UpdateStats();
        }

        private static void UpdateIrradiators()
        {
            foreach (var irradiator in irradiators)
            {
                irradiator.UpdateStats();
            }
        }

        private static void FormClusters()
        {
            bool FormCluster(Block root, int id)
            {
                if (root.Cluster != -1)
                    return false;

                List<Block> queue = new List<Block>{root};
                clusters.Add(new Cluster(id));

                while (queue.Count > 0)
                {
                    root = queue.First();
                    if (!root.Valid || root.Cluster != -1 || (root.BlockType == BlockTypes.Moderator) || (root.BlockType == BlockTypes.Air) || (root.BlockType == BlockTypes.Reflector))
                    {
                        queue.Remove(root);
                        continue;
                    }

                    if(!clusters[id].Blocks.Contains(root))
                        clusters[id].AddBlock(root);
                    root.SetCluster(id);

                    foreach(Vector3 offset in sixAdjOffsets)
                    {
                        Vector3 pos = root.Position + offset;
                        Block neighbour = BlockAt(pos);
                        if(neighbour is Conductor conductor)
                        {
                                clusters[id].HasPathToCasing |= conductorGroups[conductor.GroupID].HasPathToCasing;
                        }
                        else if (neighbour.BlockType == BlockTypes.Casing)
                        {
                                clusters[id].HasPathToCasing = true;
                        }
                        else if((neighbour.BlockType != BlockTypes.Moderator) & (neighbour.BlockType != BlockTypes.Air)& (neighbour.BlockType != BlockTypes.Reflector))
                        {
                            if(neighbour.Cluster == -1)
                                queue.Add(neighbour);
                        }
                    }
                    queue.Remove(root);
                }
                return true;
            }
            
            int clusterID = 0;
            foreach (FuelCell fuelCell in fuelCells.FindAll(fc => fc.Valid))
            {
                if (FormCluster(fuelCell, clusterID))
                    clusterID++;
            }

            foreach (Irradiator irradiator in irradiators)
            {
                if (FormCluster(irradiator, clusterID))
                    clusterID++;
            }
        }

        public static void FormConductorGroups()
        {
            bool FormConductorGroup(Conductor root, int id)
            {
                if (root.GroupID != -1)
                    return false;
                List<Conductor> queue = new List<Conductor>{root};
                conductorGroups.Add(new ConductorGroup(id));
                while (queue.Count > 0)
                {
                    root = queue.First();
                    root.GroupID = id;
                    conductorGroups[id].conductors.Add(root);
                    foreach (Vector3 offset in sixAdjOffsets)
                    {
                        if (BlockAt(root.Position + offset) is Conductor conductor)
                        {
                            if (conductor.GroupID == -1)
                            {
                                conductor.GroupID = id;
                                queue.Add(conductor);
                            }
                        }
                        else if(BlockAt(root.Position + offset) is Casing casing)
                        {
                            root.HasPathToCasing = true;
                            conductorGroups[id].HasPathToCasing = true;
                        }
                    }
                    queue.Remove(root);
                }
                return true;
            }

            conductorGroups = new List<ConductorGroup>();
            int groupId = 0;
            foreach(Conductor conductor in conductors)
            {
                if (FormConductorGroup(conductor, groupId))
                {
                    if (conductorGroups[groupId].HasPathToCasing)
                        foreach (Conductor c in conductorGroups[groupId].conductors)
                            c.HasPathToCasing = true;
                    groupId++;
                }
            }
        }

        private static void OrderedUpdateHeatSinks()
        {
            foreach (string type in Palette.UpdateOrder)
            {
                if (heatSinks.ContainsKey(type))
                    foreach (HeatSink heatSink in heatSinks[type])
                        heatSink.UpdateStats();
            }
        }

        private static void UpdateStats()
        {
            totalCoolingPerTick = 0;
            totalCoolingPerType = new Dictionary<string, double>();
            totalHeatPerTick = 0;
            totalOutputPerTick = 0;

            totalCasings = 0;
            totalCasings += (int)(2 * interiorDims.X * interiorDims.Z);
            totalCasings += (int)(2 * interiorDims.X * interiorDims.Y);
            totalCasings += (int)(2 * interiorDims.Z * interiorDims.Y);

            totalInteriorBlocks = (int)(interiorDims.X * interiorDims.Y * interiorDims.Z);

            int activeFuelCells = 0;
            double sumEfficiency = 0;
            efficiency = 0;
            double sumHeatMultiplier = 0;
            heatMultiplier = 0;

            foreach (Cluster cluster in clusters)
            {
                if (!cluster.Valid)
                    continue;

                totalOutputPerTick += cluster.TotalOutput;
                totalCoolingPerTick += cluster.TotalCoolingPerTick;
                totalHeatPerTick += cluster.TotalHeatPerTick;
                sumEfficiency += cluster.Efficiency * cluster.ActiveFuelCells.Count;
            }

            foreach (FuelCell fuelCell in fuelCells)
            {
                if (!fuelCell.Active || !clusters[fuelCell.Cluster].Valid)
                    continue;
                activeFuelCells++;
                sumHeatMultiplier += fuelCell.HeatMultiplier;
            }

            heatMultiplier = (activeFuelCells > 0) ? (sumHeatMultiplier / activeFuelCells) : 0;

            double density = (double)functionalBlocks / (double)totalInteriorBlocks;
            double spt = Configuration.Fission.SparsityPenaltyThreshold;
            double mspm = Configuration.Fission.MaxSparsityPenaltyMultiplier;
            if (density >= spt)
            {
                sparsityPenalty = 1;
            }
            else
            {
                sparsityPenalty = ((1 - mspm) * Math.Sin(density * Math.PI / (2 * spt))) + mspm;
            }
            efficiency = (activeFuelCells > 0) ? (sumEfficiency * sparsityPenalty / activeFuelCells) : 0;
            
            totalOutputPerTick *= sparsityPenalty;
        }

        public static string GetStatString(bool includeClusterInfo = true)
        {
            StringBuilder stats = new StringBuilder();
            stats.AppendLine("Overall reactor stats:");
            stats.AppendLine(string.Format("Total output: {0} mb/t of {1}", (int)(totalOutputPerTick * coolantRecipe.OutToInRatio / coolantRecipe.HeatCapacity), coolantRecipe.OutputName));
            stats.AppendLine(string.Format("Total Heat: {0} HU/t", totalHeatPerTick));
            stats.AppendLine(string.Format("Total Cooling: {0} HU/t", totalCoolingPerTick));
            stats.AppendLine(string.Format("Net Heat: {0} HU/t", totalHeatPerTick - totalCoolingPerTick));
            stats.AppendLine(string.Format("Overall Efficiency: {0} %", (int)(efficiency * 100)));
            stats.AppendLine(string.Format("Overall Heat Multiplier: {0} %", (int)(heatMultiplier * 100)));
            stats.AppendLine(string.Format("Functional \\ total blocks: {0} \\ {1}", functionalBlocks, totalInteriorBlocks));
            stats.AppendLine(string.Format("Sparsity penalty multiplier: {0}", (sparsityPenalty == 0) ? "1" : Math.Round(sparsityPenalty, 4).ToString()));
            stats.AppendLine();

            if(includeClusterInfo)
                foreach (Cluster cluster in clusters)
                {
                    stats.Append(cluster.GetStatString());
                }
            return stats.ToString();
        }

        public static ReactorStats GetOverallStats()
        {
            return new ReactorStats(
                totalOutputPerTick * coolantRecipe.OutToInRatio / coolantRecipe.HeatCapacity,
                totalOutputPerTick,
                totalHeatPerTick,
                totalCoolingPerTick,
                totalHeatPerTick - totalCoolingPerTick,
                efficiency,
                heatMultiplier,
                functionalBlocks,
                totalInteriorBlocks,
                sparsityPenalty
                );
        }

        public static void Save(FileInfo saveFile)
        {
            using (TextWriter tw = File.CreateText(saveFile.FullName))
            {
                JsonSerializer jss = new JsonSerializer
                {
                    NullValueHandling = NullValueHandling.Ignore,
                    Formatting = Formatting.Indented,
                    TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Full
                };
                //CompressedSaveFile csf = new CompressedSaveFile(saveVersion, CompressReactor(out List<Tuple<Vector3, string, bool>> fuelCells), fuelCells, interiorDims);
                jss.Serialize(tw, ComposeSaveData());
            }
        }

        public static string SaveToString()
        {
            using (StringWriter sw = new StringWriter())
            {
                JsonSerializer jss = new JsonSerializer
                {
                    NullValueHandling = NullValueHandling.Ignore,
                    Formatting = Formatting.Indented,
                    TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Full
                };
                //CompressedSaveFile csf = new CompressedSaveFile(saveVersion, CompressReactor(out List<Tuple<Vector3, string, bool>> fuelCells), fuelCells, interiorDims);
                jss.Serialize(sw, ComposeSaveData());
                return sw.ToString();
            }
        }

        private static SaveData ComposeSaveData()
        {
            Dictionary<string, List<Vector3>> saveHeatSinks = new Dictionary<string, List<Vector3>>();
            Dictionary<string, List<Vector3>> saveModerators = new Dictionary<string, List<Vector3>>();
            List<Vector3> saveConductors = new List<Vector3>();
            Dictionary<string, List<Vector3>> saveReflectors = new Dictionary<string, List<Vector3>>();
            Dictionary<string, List<Vector3>> saveFuelCells = new Dictionary<string, List<Vector3>>();
            Dictionary<string, List<Vector3>> saveIrradiators = new Dictionary<string, List<Vector3>>();

            foreach (KeyValuePair<string, List<HeatSink>> kvp in heatSinks)
            {
                if (!saveHeatSinks.ContainsKey(kvp.Key))
                    saveHeatSinks.Add(kvp.Key, new List<Vector3>());
                foreach (HeatSink hs in kvp.Value)
                    saveHeatSinks[kvp.Key].Add(hs.Position);
            }

            foreach (KeyValuePair<string, List<Moderator>> kvp in moderators)
            {
                if (!saveModerators.ContainsKey(kvp.Key))
                    saveModerators.Add(kvp.Key, new List<Vector3>());
                foreach (Moderator md in kvp.Value)
                    saveModerators[kvp.Key].Add(md.Position);
            }

            foreach (Conductor cd in conductors)
                saveConductors.Add(cd.Position);

            foreach (var reflectorType in reflectors)
            {
                if (!saveReflectors.ContainsKey(reflectorType.Key))
                    saveReflectors.Add(reflectorType.Key, new List<Vector3>());
                foreach (Reflector reflector in reflectorType.Value)
                    saveReflectors[reflector.ReflectorType].Add(reflector.Position);
            }

            foreach (FuelCell fc in fuelCells)
            {
                if (!saveFuelCells.ContainsKey(fc.ToSaveString()))
                    saveFuelCells.Add(fc.ToSaveString(), new List<Vector3>());
                saveFuelCells[fc.ToSaveString()].Add(fc.Position);
            }

            foreach (Irradiator irradiator in irradiators)
            {
                string iv = (new IrradiatorValues(irradiator.HeatPerFlux, irradiator.EfficiencyMultiplier)).ToString();
                if (!saveIrradiators.ContainsKey(iv))
                    saveIrradiators.Add(iv, new List<Vector3> { irradiator.Position });
                else
                    saveIrradiators[iv].Add(irradiator.Position);
            }

            Dictionary<string, object> data = new Dictionary<string, object>();
            data.Add("HeatSinks", saveHeatSinks);
            data.Add("Moderators", saveModerators);
            data.Add("Conductors", saveConductors);
            data.Add("Reflectors", saveReflectors);
            data.Add("FuelCells", saveFuelCells);
            data.Add("Irradiators", saveIrradiators);
            data.Add("InteriorDimensions", interiorDims);
            data.Add("CoolantRecipeName", coolantRecipeName);
            data.Add("OverallStats", GetOverallStats());
            return new SaveData(saveVersion, data);
        }

        public static ValidationResult Load(String jsonString, bool fromFile, string saveFileName = null)
        {
            Dictionary<string, List<Vector3>> Point3DDictToVector3(JToken dictionary)
            {
                Dictionary<string, List<Vector3>> result = new Dictionary<string, List<Vector3>>();
                if (dictionary == null)
                    return result;

                foreach (var child in dictionary.Children())
                {
                    var children = child.Children().First().Children().ToList();
                    string name = child.ToObject<JProperty>().Name;
                    result.Add(name, new List<Vector3>());
                    foreach (var coord in children)
                    {
                        string[] posV = coord.ToObject<string>().Split(',');
                        result[name].Add(new Vector3(Convert.ToInt32(posV[0]), Convert.ToInt32(posV[1]), Convert.ToInt32(posV[2])));
                    }
                }
                return result;
            }

            List<Vector3> Point3DListToVector3(JToken list)
            {
                List<Vector3> result = new List<Vector3>();
                if (list == null)
                    return result;

                string name = list.Path;
                foreach (var coord in list.Children())
                {
                    string[] posV = coord.ToObject<string>().Split(',');
                    result.Add(new Vector3(Convert.ToInt32(posV[0]), Convert.ToInt32(posV[1]), Convert.ToInt32(posV[2])));
                }
                return result;
            }

            SaveData save;
            JsonSerializer js = new JsonSerializer();
            JObject saveJSONObject = JObject.Parse(jsonString);
            Version v = saveJSONObject["SaveVersion"].ToObject<Version>();
            Dictionary<string, object> data = new Dictionary<string, object>();
            Dictionary<string, List<Vector3>> heatSinks = new Dictionary<string, List<Vector3>>();
            Dictionary<string, List<Vector3>> moderators = new Dictionary<string, List<Vector3>>();
            Dictionary<string, List<Vector3>> fuelCells = new Dictionary<string, List<Vector3>>();

            if (v.Major != 2)
            {
                System.Windows.Forms.MessageBox.Show("Only overhaul saves can be loaded!");
                return new ValidationResult(false, "Incorrect savefile version");
            }

            if(v <= new Version(2,0,31))
            {
                JToken dict = saveJSONObject["HeatSinks"];
                heatSinks = Point3DDictToVector3(dict);
                dict = saveJSONObject["Moderators"];
                moderators = Point3DDictToVector3(dict);
                dict = saveJSONObject["FuelCells"];
                fuelCells = Point3DDictToVector3(dict);
                List<Vector3> conductors = new List<Vector3>();
                JToken list = saveJSONObject["Conductors"];
                conductors = Point3DListToVector3(list);
                list = saveJSONObject["Reflectors"];
                Dictionary<string, List<Vector3>> reflectors = new Dictionary<string, List<Vector3>>();
                reflectors.Add(Configuration.Reflectors.First().Key, Point3DListToVector3(list));
                string[] interiorDims = saveJSONObject["InteriorDimensions"].ToObject<string>().Split(',');
                Vector3 inDimsVector = new Vector3(Convert.ToInt32(interiorDims[0]), Convert.ToInt32(interiorDims[1]), Convert.ToInt32(interiorDims[2]));

                data.Add("HeatSinks", heatSinks);
                data.Add("Moderators", moderators);
                data.Add("Conductors", conductors);
                data.Add("Reflectors", reflectors);
                data.Add("FuelCells", fuelCells);
                data.Add("InteriorDimensions", inDimsVector);
                save = new SaveData(v, data);
            }
            else if(v < new Version(2,0,38))
            {
                heatSinks = saveJSONObject["HeatSinks"]?.ToObject<Dictionary<string, List<Vector3>>>();
                moderators = saveJSONObject["Moderators"]?.ToObject<Dictionary<string, List<Vector3>>>();
                fuelCells = saveJSONObject["FuelCells"]?.ToObject<Dictionary<string, List<Vector3>>>();
                List<Vector3> conductors = saveJSONObject["Conductors"]?.ToObject<List<Vector3>>();
                Dictionary<string, List<Vector3>> reflectors = saveJSONObject["Reflectors"].ToObject<Dictionary<string, List<Vector3>>>();
                Vector3 inDimsVector = saveJSONObject["InteriorDimensions"].ToObject<Vector3>();
                string coolantRecipe = saveJSONObject["CoolantRecipeName"]?.ToObject<string>();

                data.Add("HeatSinks", heatSinks);
                data.Add("Moderators", moderators);
                data.Add("Conductors", conductors);
                data.Add("Reflectors", reflectors);
                data.Add("FuelCells", fuelCells);
                data.Add("InteriorDimensions", inDimsVector);
                data.Add("CoolantRecipeName", coolantRecipe);
                save = new SaveData(v, data);
            }
            else
            {
                heatSinks = saveJSONObject["Data"]["HeatSinks"]?.ToObject<Dictionary<string, List<Vector3>>>();
                moderators = saveJSONObject["Data"]["Moderators"]?.ToObject<Dictionary<string, List<Vector3>>>();
                fuelCells = saveJSONObject["Data"]["FuelCells"]?.ToObject<Dictionary<string, List<Vector3>>>();
                List<Vector3> conductors = saveJSONObject["Data"]["Conductors"]?.ToObject<List<Vector3>>();
                Dictionary<string, List<Vector3>> reflectors = saveJSONObject["Data"]["Reflectors"].ToObject<Dictionary<string, List<Vector3>>>();
                Dictionary<string, List<Vector3>> irradiators = saveJSONObject["Data"]["Irradiators"]?.ToObject<Dictionary<string, List<Vector3>>>();
                Vector3 inDimsVector = saveJSONObject["Data"]["InteriorDimensions"].ToObject<Vector3>();
                string coolantRecipe = saveJSONObject["Data"]["CoolantRecipeName"]?.ToObject<string>();

                data.Add("HeatSinks", heatSinks);
                data.Add("Moderators", moderators);
                data.Add("Conductors", conductors);
                data.Add("Reflectors", reflectors);
                data.Add("FuelCells", fuelCells);
                data.Add("Irradiators", irradiators);
                data.Add("InteriorDimensions", inDimsVector);
                data.Add("CoolantRecipeName", coolantRecipe);
                save = new SaveData(v, data);
            }

            ValidationResult vr = save.PerformValidation();
            if (vr.Successful)
            {
                if (fromFile)
                    UI.LoadedSaveFile = new FileInfo(saveFileName);
                else
                    UI.LoadedSaveFile = null;
                LoadFromSaveData(save);
            }
            return vr;
        }

        private static void LoadFromSaveData(SaveData save)
        {
            InitializeReactor((Vector3)save.Data["InteriorDimensions"]);

            object savedValue;
            save.Data.TryGetValue("HeatSinks", out savedValue);
            Dictionary<string, List<Vector3>> saveHeatSinks = savedValue as Dictionary<string, List<Vector3>> ?? new Dictionary<string, List<Vector3>>();
            foreach (KeyValuePair<string, List<Vector3>> kvp in saveHeatSinks)
            {
                if (!Palette.BlockPalette.ContainsKey(kvp.Key))
                    continue;
                foreach (Vector3 pos in kvp.Value)
                    SetBlock(Palette.BlockPalette[kvp.Key].Copy(pos), pos);
            }

            save.Data.TryGetValue("Moderators", out savedValue);
            Dictionary<string, List<Vector3>> saveModerators = savedValue as Dictionary<string, List<Vector3>> ?? new Dictionary<string, List<Vector3>>();
            foreach (KeyValuePair<string, List<Vector3>> kvp in saveModerators)
            {
                if (!Palette.BlockPalette.ContainsKey(kvp.Key))
                    continue;
                foreach (Vector3 pos in kvp.Value)
                    SetBlock(Palette.BlockPalette[kvp.Key].Copy(pos), pos);
            }

            save.Data.TryGetValue("FuelCells", out savedValue);
            Dictionary<string, List<Vector3>> saveFuelCells = savedValue as Dictionary<string, List<Vector3>> ?? new Dictionary<string, List<Vector3>>();
            foreach (KeyValuePair<string, List<Vector3>> kvp in saveFuelCells)
            {
                FuelCell restoredFuelCell;
                List<string> props = kvp.Key.Split(';').ToList();
                if(props.Count != 3)
                {
                    throw new ArgumentException("Tried to load an unexpected FuelCell: " + kvp.Key);
                }
                if(!Palette.FuelPalette.ContainsKey(props[0]))
                {
                    System.Windows.Forms.MessageBox.Show("There is no " + props[0] + "-fuel in the current configuration" +
                            ". This reactor was probably created with a different planner configuration! Resetting the FuelCell!");
                    props[0] = Palette.FuelPalette.First().Key;
                }
                foreach (Vector3 pos in kvp.Value)
                {
                    restoredFuelCell = new FuelCell("FuelCell", Palette.Textures["FuelCell"], pos, Palette.FuelPalette[props[0]], Convert.ToBoolean(props[1]), props[2]);
                    if (restoredFuelCell.Primed && !Configuration.NeutronSources.ContainsKey(restoredFuelCell.NeutronSource))
                    {
                        System.Windows.Forms.MessageBox.Show("There is no " + restoredFuelCell.NeutronSource + " neutron source in the current configuration " +
                            "for FuelCell at " + restoredFuelCell.Position +
                            ". This reactor was probably created with a different planner configuration! Resetting the FuelCell!");
                        restoredFuelCell.UnPrime();
                    }
                    SetBlock(restoredFuelCell, pos);
                }
            }

            save.Data.TryGetValue("Conductors", out savedValue);
            List<Vector3> saveConductors = savedValue as List<Vector3> ?? new List<Vector3>();
            foreach (Vector3 pos in saveConductors)
                SetBlock(new Conductor("Conductor", Palette.Textures["Conductor"], pos), pos);

            save.Data.TryGetValue("Reflectors", out savedValue);
            Dictionary<string, List<Vector3>> saveReflectors = savedValue as Dictionary<string, List<Vector3>> ?? new Dictionary<string, List<Vector3>>();
            foreach (var reflectorType in saveReflectors)
            {
                string textureName = reflectorType.Key.Replace('-', '_');
                if (!Palette.Textures.ContainsKey(textureName))
                    textureName = "NoTexture";
                foreach (Vector3 pos in reflectorType.Value)
                    SetBlock(new Reflector(reflectorType.Key, reflectorType.Key, Palette.Textures[textureName], pos), pos);
            }

            save.Data.TryGetValue("Irradiators", out savedValue);
            Dictionary<string, List<Vector3>> saveIrradiators = savedValue as Dictionary<string, List<Vector3>> ?? new Dictionary<string, List<Vector3>>();
            foreach (var irradiator in saveIrradiators)
                foreach (Vector3 pos in irradiator.Value)
                    SetBlock(new Irradiator("Irradiator", Palette.Textures["Irradiator"], pos, JsonConvert.DeserializeObject<IrradiatorValues>(irradiator.Key)), pos);

            save.Data.TryGetValue("CoolantRecipeName", out savedValue);
            coolantRecipeName = savedValue as string;
        }

        public static void SaveLayerAsImage(int layer, string fileName)
        {
            Bitmap layerImage = layers[layer - 1].DrawToImage();
            using (FileStream fs = File.OpenWrite(fileName))
                layerImage.Save(fs, System.Drawing.Imaging.ImageFormat.Png);
            layerImage.Dispose();
        }

        public static void SaveReactorAsImage(string fileName, int statStringLines,bool includeClusterInfo = false, int fontSize = 24)
        {
            int layersPerRow = (int)Math.Ceiling(Math.Sqrt(interiorDims.Y));
            int rows = (int)Math.Ceiling((interiorDims.Y / layersPerRow));
            int bs = Reactor.UI.BlockSize;

            Point StatsRectSize = new Point(28 * fontSize, (statStringLines + 4) * (fontSize + 2));

            Bitmap reactorImage = new Bitmap(Math.Max(StatsRectSize.X, (int)(layersPerRow * interiorDims.X * bs + (layersPerRow - 1) * bs)),
                                             StatsRectSize.Y + bs + (int)(rows * interiorDims.Z * bs + (rows - 1) * bs));
            using (Graphics gr = Graphics.FromImage(reactorImage))
            {
                gr.Clear(Color.LightGray);

                foreach (ReactorGridLayer layer in layers)
                {
                    Bitmap layerImage = layer.DrawToImage();
                    int y = layer.Y - 1;
                    gr.DrawImage(layerImage,
                                    new Rectangle((int)((y % layersPerRow) * interiorDims.X * bs + (y % layersPerRow) * bs),
                                                StatsRectSize.Y + bs + (int)((y / layersPerRow) * interiorDims.Z * bs + (y / layersPerRow) * bs),
                                                (int)(interiorDims.X * bs), (int)(interiorDims.Z * bs)),
                                    new Rectangle(0, 0, layerImage.Size.Width, layerImage.Size.Height),
                                    GraphicsUnit.Pixel);
                    layerImage.Dispose();
                }

                string report = string.Format("Planner version: {0}\r\n", Updater.ShortVersionString(saveVersion));
                report += string.Format("Recipe: 1 mb of {0} to {2} mb of {1}\r\n", coolantRecipe.InputName, coolantRecipe.OutputName, coolantRecipe.OutToInRatio);
                gr.DrawString(report + GetStatString(includeClusterInfo), new Font(FontFamily.GenericSansSerif, fontSize, GraphicsUnit.Pixel), Brushes.Black, 0, 0);
            }
            using (FileStream fs = File.OpenWrite(fileName))
            {
                reactorImage.Save(fs, System.Drawing.Imaging.ImageFormat.Png);
            }
            reactorImage.Dispose();
        }

        public static Block BlockAt(Vector3 position)
        {
            return blocks[(int)position.X, (int)position.Y, (int)position.Z];
        }

        public static void SetBlock(Block block, Vector3 position)
        {
            blocks[(int)position.X, (int)position.Y, (int)position.Z] = block;
        }

        public static void ClearLayer(ReactorGridLayer layer)
        {
            for (int x = 0; x < interiorDims.X; x++)
                for (int z = 0; z < interiorDims.Z; z++)
                    SetBlock(new Block("Air", BlockTypes.Air, Palette.Textures["Air"], new Vector3(x + 1, layer.Y, z + 1)), new Vector3(x + 1, layer.Y, z + 1));
            Update();
            Redraw();
        }

        public static void CopyLayer(ReactorGridLayer layer)
        {
            PlannerUI.LayerBuffer = new Block[layer.X, layer.Z];
            for (int x = 0; x < layer.X; x++)
                for (int z = 0; z < layer.Z; z++)
                {
                    PlannerUI.LayerBuffer[x, z] = blocks[x + 1, layer.Y, z + 1];
                }
        }

        public static void PasteLayer(ReactorGridLayer layer)
        {
            if (PlannerUI.LayerBuffer == null)
                return;
            if (PlannerUI.LayerBuffer.Length != layer.X * layer.Z)
            {
                System.Windows.Forms.MessageBox.Show("Buffered layer size doesn't match the layout!");
                return;
            }

            for (int x = 0; x < layer.X; x++)
                for (int z = 0; z < layer.Z; z++)
                {
                    Vector3 position = new Vector3(x + 1, layer.Y, z + 1);
                    SetBlock(PlannerUI.LayerBuffer[x, z].Copy(position), position);
                }
            Update();
            Redraw();
        }

        public static void DeleteLayer(int y)
        {
            if (y == 0 | y == interiorDims.Y + 1)
                throw new ArgumentException("Tried to delete a casing layer!");

            Block[,,] newReactor = new Block[(int)interiorDims.X + 2, (int)interiorDims.Y + 1, (int)interiorDims.Z + 2];
            for (int layer = 0; layer < y; layer++)
            {
                for (int x = 0; x < interiorDims.X+2; x++)
                {
                    for (int z = 0; z < interiorDims.Z+2; z++)
                    {
                        newReactor[x, layer, z] = blocks[x, layer, z].Copy(new Vector3(x, layer, z));
                    }
                }
            }
            for (int layer = y + 1; layer <= interiorDims.Y+1; layer++)
            {
                for (int x = 0; x < interiorDims.X + 2; x++)
                {
                    for (int z = 0; z < interiorDims.Z + 2; z++)
                    {
                        newReactor[x, layer-1, z] = blocks[x, layer, z].Copy(new Vector3(x, layer-1, z));
                    }
                }
            }

            blocks = newReactor;
            interiorDims = new Vector3(interiorDims.X, interiorDims.Y - 1, interiorDims.Z);
        }

        public static void InsertLayer(int y)
        {
            Block[,,] newReactor = new Block[(int)interiorDims.X + 2, (int)interiorDims.Y + 3, (int)interiorDims.Z + 2];
            for (int layer = 0; layer < y; layer++)
            {
                for (int x = 0; x < interiorDims.X + 2; x++)
                {
                    for (int z = 0; z < interiorDims.Z + 2; z++)
                    {
                        newReactor[x, layer, z] = blocks[x, layer, z].Copy(new Vector3(x, layer, z));
                    }
                }
            }

            for (int x = 0; x < interiorDims.X + 2; x++)
            {
                for (int z = 0; z < interiorDims.Z + 2; z++)
                {
                    if(((x == 0 | x == interiorDims.X + 1)&(z > 0 & z < interiorDims.Z + 1)) || ((z == 0 | z == interiorDims.Z + 1) & (x > 0 & x < interiorDims.X + 1)))
                        newReactor[x, y, z] = new Casing("Casing", null, new Vector3(x, y, z));
                    else
                        newReactor[x, y, z] = new Block("Air", BlockTypes.Air, Palette.Textures["Air"], new Vector3(x, y, z));
                }
            }

            for (int layer = y + 1; layer < interiorDims.Y + 3; layer++)
            {
                for (int x = 0; x < interiorDims.X + 2; x++)
                {
                    for (int z = 0; z < interiorDims.Z + 2; z++)
                    {
                        newReactor[x, layer, z] = blocks[x, layer-1, z].Copy(new Vector3(x, layer, z));
                    }
                }
            }

            blocks = newReactor;
            interiorDims = new Vector3(interiorDims.X, interiorDims.Y + 1, interiorDims.Z);
        }

        public static void ModifySize(int interiorX, int interiorY, int interiorZ, Point copyCorner, Point pasteCorner)
        {
            Block[,,] newBlocks = CreateBlockArray(interiorX, interiorY, interiorZ);
            int copyX;
            int copyZ;
            for (int y = 1; y <= interiorY; y++)
            {
                copyX = copyCorner.X;
                for (int x = pasteCorner.X; x <= interiorX & copyX <= interiorX & copyX <= interiorDims.X; x++, copyX++)
                {
                    copyZ = copyCorner.Y;
                    for (int z = pasteCorner.Y; z <= interiorZ & copyZ <= interiorZ & copyZ <= interiorDims.Z; z++, copyZ++)
                    {
                        newBlocks[x, y, z] = blocks[copyX, y, copyZ].Copy(new Vector3(x,y,z));
                    }
                }
            }
            blocks = newBlocks;
            interiorDims = new Vector3(interiorX, interiorY, interiorZ);
        }
    }
}

