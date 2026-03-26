using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI.Ingame;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using VRage;
using VRage.Collections;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Game.ObjectBuilders.Definitions;
using VRageMath;

namespace IngameScript
{
    public partial class Program : MyGridProgram
    {
        // START UESRSPACE
        const string OUTPUT_LCD_KEYWORD = "[MWI-Ore]";
        // END USERSPACE

        private Queue<double> runtimeHistory = new Queue<double>();
        private double runtimeSum = 0;
        private const double UpdatesPerSecond = 0.6;   // Update10  = 6/s,  Update100 = 0.6/s
        private const int SampleWindowSecs = 100;
        private const int MaxSamples = (int)(UpdatesPerSecond * SampleWindowSecs);

        Dictionary<string, MyFixedPoint> prevOre             = new Dictionary<string, MyFixedPoint>();
        Dictionary<string, MyFixedPoint> prevIngots          = new Dictionary<string, MyFixedPoint>();
        Dictionary<string, MyFixedPoint> totalOreConsumed    = new Dictionary<string, MyFixedPoint>();
        Dictionary<string, MyFixedPoint> totalIngotsProduced = new Dictionary<string, MyFixedPoint>();

        readonly MyIni _ini = new MyIni();

        List<IMyTextPanel> namedLCDs = new List<IMyTextPanel>();
        private IMyTextPanel lcd_output;

        Dictionary<string, MyFixedPoint> prevQueueAmounts = new Dictionary<string, MyFixedPoint>();

        public Program()
        {
            Runtime.UpdateFrequency = UpdateFrequency.Update100;
            LoadData();

            GridTerminalSystem
            .GetBlocksOfType<IMyTextPanel>(
                namedLCDs, 
                lcd => lcd.CustomName.Contains(OUTPUT_LCD_KEYWORD)
                    && lcd.CubeGrid == Me.CubeGrid
            );

            lcd_output = namedLCDs.FirstOrDefault();

            if (lcd_output != null)
            {
                lcd_output.ContentType = VRage.Game.GUI.TextPanel.ContentType.TEXT_AND_IMAGE;
                lcd_output.FontSize = 1.0f;
                lcd_output.Alignment = VRage.Game.GUI.TextPanel.TextAlignment.CENTER;
            }
            
        }

        public void Save()
        {
            SaveData();
        }

        void SaveData()
        {
            _ini.Clear();

            foreach (var kv in totalOreConsumed)
                _ini.Set("TotalOreConsumed", kv.Key, kv.Value.ToString());

            foreach (var kv in totalIngotsProduced)
                _ini.Set("TotalIngotsProduced", kv.Key, kv.Value.ToString());

            Storage = _ini.ToString();
        }

        void LoadData()
        {
            if (string.IsNullOrEmpty(Storage)) return;

            MyIniParseResult result;
            if (!_ini.TryParse(Storage, out result))
            {
                Echo($"Failed to parse Storage: {result}");
                return;
            }

            // Load total ore consumed
            List<MyIniKey> keys = new List<MyIniKey>();
            _ini.GetKeys("TotalOreConsumed", keys);
            foreach (var key in keys)
                totalOreConsumed[key.Name] = MyFixedPoint.DeserializeString(_ini.Get(key).ToString());

            // Load total ingots produced
            keys.Clear();
            _ini.GetKeys("TotalIngotsProduced", keys);
            foreach (var key in keys)
                totalIngotsProduced[key.Name] = MyFixedPoint.DeserializeString(_ini.Get(key).ToString());

            Echo("Data loaded from storage.");
        }


        public void Main(string argument)
        {
            // RUNTIME DEBUG
            

            double runtime = Runtime.LastRunTimeMs;

            runtimeHistory.Enqueue(runtime);
            runtimeSum += runtime;

            if (runtimeHistory.Count > MaxSamples)
            {
                runtimeSum -= runtimeHistory.Dequeue();
            }

            double averageRuntime = runtimeHistory.Count > 0 
                ? runtimeSum / runtimeHistory.Count 
                : 0;

            double msPerSecond   = averageRuntime * UpdatesPerSecond;
            double percentOfCpu  = msPerSecond / 16.667 * 100;

            string debug = "DEBUG:\n";
            debug += 
                $"Script Runtime: {runtime:N3} ms\n" +
                $"{runtime / 16.667:F4}% of game loop\n" +
                $"Runtime Average last {SampleWindowSecs}s:\n" +
                $"{averageRuntime:N2} ms\n"+
                $"CPU load/sec: {percentOfCpu:F4}%\n";

            Echo(debug);
            // END DEBUG

            string output = "MWI Ore Tracker\n\n";

            if (argument == "reset")
            {
                totalOreConsumed.Clear();
                totalIngotsProduced.Clear();
                Storage = "";
                Echo("Totals reset.");
                return;
            }

            List<IMyRefinery> refineries = new List<IMyRefinery>();
            GridTerminalSystem.GetBlocksOfType(refineries, r => r.IsSameConstructAs(Me));

            if (refineries.Count == 0) { Echo("ERROR: No refineries found."); return; }
            Echo($"Tracking {refineries.Count} refiner{(refineries.Count == 1 ? "y" : "ies")}");

            Echo("Reading inventories...");
            Dictionary<string, MyFixedPoint> currentOre    = new Dictionary<string, MyFixedPoint>();
            Dictionary<string, MyFixedPoint> currentIngots = new Dictionary<string, MyFixedPoint>();

            foreach (var refinery in refineries)
            {
                Echo($"  Reading {refinery.CustomName}...");
                AggregateInventory(refinery.GetInventory(0), currentOre);
                Echo($"  Ore done");
                AggregateInventory(refinery.GetInventory(1), currentIngots);
                Echo($"  Ingots done");
            }

            Echo("Reading queues...");
            Dictionary<string, MyFixedPoint> currentQueue = GetQueuedOreAmounts(refineries);
            Echo("Queues done");

            Echo($"prevOre count: {prevOre.Count}");

            if (prevOre.Count > 0)
            {
                Echo("=== Ore Consumed This Tick ===");
                foreach (var ore in prevOre)
                {
                    MyFixedPoint current = currentOre.ContainsKey(ore.Key) ? currentOre[ore.Key] : 0;
                    MyFixedPoint rawDelta = ore.Value - current; // Total ore that disappeared

                    if (rawDelta <= 0) continue; // Ore increased or stayed same, nothing to analyze

                    // How much did the QUEUE shrink for this ore type?
                    MyFixedPoint prevQ    = prevQueueAmounts.ContainsKey(ore.Key) ? prevQueueAmounts[ore.Key] : 0;
                    MyFixedPoint currentQ = currentQueue.ContainsKey(ore.Key) ? currentQueue[ore.Key] : 0;
                    MyFixedPoint queueDelta = prevQ - currentQ;

                    bool oreWasEmptied = !currentOre.ContainsKey(ore.Key) || currentOre[ore.Key] == 0;
                        bool queueWasCleared = prevQ > 0 && currentQ == 0;

                        MyFixedPoint refined;
                        if (oreWasEmptied && queueWasCleared)
                        {
                            // Queue cleared because ore was pulled out — nothing was refined
                            refined = 0;
                        }
                        else
                        {
                            refined = MyFixedPoint.Min(queueDelta > 0 ? queueDelta : 0, rawDelta);
                        }

                    // Removed = whatever disappeared that wasn't accounted for by refining
                    MyFixedPoint removed = rawDelta - refined;

                    if (refined > 0)
                    {
                        Echo($"  {ore.Key} refined:  -{(double)refined:N0}");

                        if (!totalOreConsumed.ContainsKey(ore.Key))
                            totalOreConsumed[ore.Key] = 0;
                        totalOreConsumed[ore.Key] += refined;
                    }

                    if (removed > 0)
                    {
                        Echo($"  {ore.Key} removed:  -{(double)removed:N0}");
                        // You could track totalOreRemoved here too if you want
                    }
                }

                // Ingot delta logic unchanged — ingots only appear via refining
                Echo("=== Ingots Produced This Tick ===");
                foreach (var ingot in currentIngots)
                {
                    MyFixedPoint prev  = prevIngots.ContainsKey(ingot.Key) ? prevIngots[ingot.Key] : 0;
                    MyFixedPoint delta = ingot.Value - prev;

                    if (delta > 0)
                    {
                        Echo($"  {ingot.Key}: +{(double)delta:N0}");

                        if (!totalIngotsProduced.ContainsKey(ingot.Key))
                            totalIngotsProduced[ingot.Key] = 0;
                        totalIngotsProduced[ingot.Key] += delta;
                    }
                }  
            }

            Echo("=== Total Ore Consumed ===");
            output += $"--- Total Ore Consumed ---\n";
            foreach (var ore in totalOreConsumed)
            {
                Echo($"  {ore.Key}: {(double)ore.Value:N0}");
                output += $"{ore.Key}: {(double)ore.Value:N0}\n";
            }

            Echo("=== Total Ingots Produced ===");
            output += $"\n--- Total Ingots Produced ---\n";
            foreach (var ingot in totalIngotsProduced)
            {
                Echo($"  {ingot.Key}: {(double)ingot.Value:N0}");
                output += $"{ingot.Key}: {(double)ingot.Value:N0}\n";
            }

            Echo("Saving snapshots...");
            prevOre          = currentOre;
            prevIngots       = currentIngots;
            prevQueueAmounts = currentQueue;
            Echo("Done.");

            // LCD Time
            if (lcd_output != null)
            {
                lcd_output.WriteText(output);
            }
            
        }

        Dictionary<string, MyFixedPoint> GetInventoryAmounts(IMyInventory inventory)
        {
            var result = new Dictionary<string, MyFixedPoint>();
            var items  = new List<MyInventoryItem>();
            inventory.GetItems(items);

            foreach (var item in items)
            {
                string name = item.Type.SubtypeId;
                if (result.ContainsKey(name))
                    result[name] += item.Amount;
                else
                    result[name] = item.Amount;
            }

            return result;
        }

        void AggregateInventory(IMyInventory inventory, Dictionary<string, MyFixedPoint> target)
        {
            var items = new List<MyInventoryItem>();
            inventory.GetItems(items);

            foreach (var item in items)
            {
                string name = item.Type.SubtypeId;
                if (target.ContainsKey(name))
                    target[name] += item.Amount;
                else
                    target[name] = item.Amount;
            }
        }

        Dictionary<string, MyFixedPoint> GetQueuedOreAmounts(List<IMyRefinery> refineries)
        {
            var result = new Dictionary<string, MyFixedPoint>();

            foreach (var refinery in refineries)
            {
                var queue = new List<MyProductionItem>();
                refinery.GetQueue(queue);

                foreach (var item in queue)
                {
                    // Blueprint subtypes look like "IronOreToIngot" — strip the suffix
                    string name = item.BlueprintId.SubtypeName.Replace("OreToIngot", "");

                    // "StoneOreToIngot_" edge case for stone
                    name = name.TrimEnd('_');

                    if (result.ContainsKey(name))
                        result[name] += item.Amount;
                    else
                        result[name] = item.Amount;
                }
            }

            return result;
        }
    }
}
