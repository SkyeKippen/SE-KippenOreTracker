using ParallelTasks;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.Weapons.Guns;
using Sandbox.Game.WorldEnvironment;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI.Ingame;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net.Mime;
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
using ContentType = VRage.Game.GUI.TextPanel.ContentType;

namespace IngameScript
{
    public partial class Program : MyGridProgram
    {
        string version = "v0.2.2";
        string LCD_KEYWORD = "[KOT]";

        float refinery_speed = 1.30f;
        float refinery_game_speed = 1.0f;
        float refineryYield = 2.0f; // 200% - Full Yield
        int filter_amount = 1;
        int filter_sec = 60;
        
        int refinery_count;
        
        class LcdPanel
        {
            public IMyTerminalBlock Block;
            public IMyTextSurface Surface;
            public RectangleF Viewport;
            public MyIni Ini = new MyIni();
            public float FontSize = 0.8f;
        }
        List<LcdPanel> lcdPanels = new List<LcdPanel>();

        List<IMyCargoContainer> cargos = new List<IMyCargoContainer>();
        List<IMyRefinery> refineries = new List<IMyRefinery>();
        
        List<CachedOre> totalOres = new List<CachedOre>();
        
        float totalTime = 0;
        string formatted_totalTime;

        static readonly Dictionary<string, float> OreProcessTimes = new Dictionary<string, float>
        {
            {"Scrap", 0.04f},
            {"Stone", 10.00f / 1000f},
            {"Iron", 0.05f},
            {"Nickel", 0.66f},
            {"Cobalt", 3.00f},
            {"Magnesium", 0.50f},
            {"Silicon", 0.60f},
            {"Silver", 1.00f},
            {"Gold", 0.40f},
            {"Platinum", 3.00f},
            {"Uranium", 4.00f},
            {"Ice", 0.00f},
        };
        
        private static readonly Dictionary<string, float> OreYields = new Dictionary<string, float>
        {
            { "Iron", 0.7f },
            { "Nickel", 0.4f },
            { "Cobalt", 0.3f },
            { "Magnesium", 0.007f },
            { "Silicon", 0.7f },
            { "Silver", 0.1f },
            { "Gold", 0.01f },
            { "Platinum", 0.005f },
            { "Uranium", 0.01f },
            { "Stone", 0.014f }
        };

        class CachedOre
        {
            public string subtypeId;
            public float amount;
            public float time_sec;
            public float refinedAmount;
        }
        
        IMyTextSurface drawingSurface;
        RectangleF viewport;
        
        public Program()
        {
            Runtime.UpdateFrequency = UpdateFrequency.Update100;
            
            OreYields["Iron"] *= refineryYield;
            OreYields["Nickel"] *= refineryYield;
            OreYields["Cobalt"] *= refineryYield;
            OreYields["Silicon"] *= refineryYield;
            OreYields["Magnesium"] *= refineryYield;
            OreYields["Silver"] *= refineryYield;
            OreYields["Gold"] *= refineryYield;
            OreYields["Uranium"] *= refineryYield;
            OreYields["Platinum"] *= refineryYield;

            // Get all LCDs & set them up
            DiscoverLcds();
            if (lcdPanels.Count == 0)
                throw new Exception("No blocks with " + LCD_KEYWORD + " found!");

            var lcdBlocks = new List<IMyTerminalBlock>();
            GridTerminalSystem.GetBlocksOfType(lcdBlocks, b => b != Me && b.CustomName.Contains(LCD_KEYWORD));
            var surfaceProvider = lcdBlocks.Count > 0 ? lcdBlocks[0] as IMyTextSurfaceProvider : null;

            if (surfaceProvider != null && surfaceProvider.SurfaceCount > 0)
            {
                drawingSurface = surfaceProvider.GetSurface(0);
            }
            else
                throw new Exception("Specified block does not have LCDs!");

            viewport = new RectangleF(
                (drawingSurface.TextureSize - drawingSurface.SurfaceSize) / 2f,
                drawingSurface.SurfaceSize
            );

            GridTerminalSystem.GetBlocksOfType(cargos);
            GridTerminalSystem.GetBlocksOfType(refineries);
            
            refinery_count = refineries.Count;

        }

        public void Save()
        {
            
        }

        public void Main(string argument, UpdateType updateSource)
        {
            totalOres.Clear();

            totalTime = 0;

            foreach (IMyCargoContainer cargo in cargos)
            {
                var inventory = cargo.GetInventory(0);

                var inventoryItems = new List<MyInventoryItem>();

                inventory.GetItems(inventoryItems);

                foreach (MyInventoryItem item in inventoryItems)
                {
                    if (item.Type.TypeId.EndsWith("Ore") && item.Amount > filter_amount)
                    {
                        float time = OreProcessTimes[item.Type.SubtypeId];
                        float oreTime = (time / (refinery_speed * refinery_count * refinery_game_speed)) * (int)item.Amount;

                        if (oreTime < filter_sec)
                        {
                            continue;
                        }

                        CachedOre existing = totalOres.Find(ore => ore.subtypeId == item.Type.SubtypeId);

                        if (existing != null)
                        {
                            existing.amount += (float)item.Amount;
                            existing.time_sec += oreTime;
                            existing.refinedAmount += (float)item.Amount * OreYields[item.Type.SubtypeId];
                        }
                        else
                        {
                            totalOres.Add(new CachedOre
                            {
                                subtypeId = item.Type.SubtypeId, 
                                amount = (float)item.Amount, 
                                time_sec = oreTime,
                                refinedAmount = (float)item.Amount * OreYields[item.Type.SubtypeId]
                            });
                        }

                    }
                }
            }

            foreach (IMyRefinery refinery in refineries)
            {
                var ref_inventory = refinery.GetInventory(0);

                var ref_inventoryItems = new List<MyInventoryItem>();

                ref_inventory.GetItems(ref_inventoryItems);

                foreach (MyInventoryItem item in ref_inventoryItems)
                {

                    float time = OreProcessTimes[item.Type.SubtypeId];
                    float oreTime = (time / (refinery_speed * refinery_count * refinery_game_speed)) * (int)item.Amount;

                    if (oreTime < filter_sec)
                    {
                        continue;
                    }

                    CachedOre existing = totalOres.Find(ore => ore.subtypeId == item.Type.SubtypeId);

                    if (existing != null)
                    {
                        existing.amount += (float)item.Amount;
                        existing.time_sec += oreTime;
                        existing.refinedAmount += (float)item.Amount * OreYields[item.Type.SubtypeId];
                    }
                    else
                    {
                        totalOres.Add(new CachedOre
                        {
                            subtypeId = item.Type.SubtypeId, 
                            amount = (float)item.Amount, 
                            time_sec = oreTime,
                            refinedAmount = (float)item.Amount * OreYields[item.Type.SubtypeId]
                        });                    
                    }
                }
            }

            foreach (CachedOre oreItem in totalOres)
            {
                totalTime += oreItem.time_sec;
            }

            TimeSpan tT = TimeSpan.FromSeconds(totalTime);
            formatted_totalTime = $"{tT.Days}d {tT.Hours}hr {tT.Minutes}min {tT.Seconds}sec";

            Echo($"TOTAL TIME: {formatted_totalTime} \n");
            
            // Sort by time
            totalOres = totalOres.OrderByDescending(o => o.time_sec).ToList();

            foreach (CachedOre oreItem in totalOres)
            {
                TimeSpan t = TimeSpan.FromSeconds(oreItem.time_sec);
                string formatted = $"{t.Days}d {t.Hours}hr {t.Minutes}min {t.Seconds}sec";

                Echo($"{oreItem.subtypeId}: {oreItem.amount:N0}");
                Echo($"Time: {formatted} \n");
            }
            
            var frame = drawingSurface.DrawFrame();
            DrawSprites(ref frame);
            frame.Dispose();

        }

        public void PrepareTextSurfaceForSprites(IMyTextSurface textSurface)
        {
            textSurface.ScriptBackgroundColor = new Color(0, 0, 0, 255);
            textSurface.ContentType = ContentType.SCRIPT;
            textSurface.Script = "";
        }

        void DiscoverLcds()
        {
            lcdPanels.Clear();
            var lcdBlocks = new List<IMyTerminalBlock>();
            GridTerminalSystem.GetBlocksOfType(lcdBlocks, b => b != Me && b.CustomName.Contains(LCD_KEYWORD));
            foreach (var block in lcdBlocks)
            {
                var provider = block as IMyTextSurfaceProvider;
                if (provider == null || provider.SurfaceCount == 0) continue;

                var surface = provider.GetSurface(0);
                PrepareTextSurfaceForSprites(surface);

                lcdPanels.Add(new LcdPanel
                {
                    Block = block,
                    Surface = surface,
                    Viewport = new RectangleF(
                        (surface.TextureSize - surface.SurfaceSize) / 2f,
                        surface.SurfaceSize)
                });
            }
        }

        public void DrawSprites(ref MySpriteDrawFrame frame)
        {
            var position = new Vector2(245, 0) + viewport.Position;
            float scale = 1.0f;
            var increment = new Vector2(0, 26) * scale;
            
            frame.Add(new MySprite()
            {
                Type = SpriteType.TEXT,
                Data = $"Kippen Ore Tracker {version}",
                Position = position,
                RotationOrScale = scale,
                Color = Color.White,
                Alignment = TextAlignment.CENTER,
                FontId = "White"
            });
            position += increment * 2;
            
            frame.Add(new MySprite()
            {
                Type = SpriteType.TEXT,
                Data = $"Total Processing Time:\n{formatted_totalTime}",
                Position = position,
                RotationOrScale = scale,
                Color = Color.Magenta,
                Alignment = TextAlignment.CENTER,
                FontId = "White"
            });
            position += increment * 3;

            foreach (CachedOre oreItem in totalOres)
            {
                TimeSpan t = TimeSpan.FromSeconds(oreItem.time_sec);
                string formatted = $"{t.Days}d {t.Hours}hr {t.Minutes}min {t.Seconds}sec";
                float percentOfTotal = oreItem.time_sec / totalTime * 100;
                
                frame.Add(new MySprite()
                {
                    Type = SpriteType.TEXT,
                    Data = $"{oreItem.subtypeId}: {oreItem.amount:N0} ({oreItem.refinedAmount:N0} Ingots)\n{formatted} - {percentOfTotal:N1}%",
                    Position = position,
                    RotationOrScale = scale,
                    Color = Color.Teal,
                    Alignment = TextAlignment.CENTER,
                    FontId = "White"
                });
                
                position += increment * 3;
            }
        }

    }
}
