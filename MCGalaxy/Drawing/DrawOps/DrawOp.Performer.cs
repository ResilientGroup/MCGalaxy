﻿/*
    Copyright 2015 MCGalaxy
        
    Dual-licensed under the Educational Community License, Version 2.0 and
    the GNU General Public License, Version 3 (the "Licenses"); you may
    not use this file except in compliance with the Licenses. You may
    obtain a copy of the Licenses at
    
    http://www.opensource.org/licenses/ecl2.php
    http://www.gnu.org/licenses/gpl-3.0.html
    
    Unless required by applicable law or agreed to in writing,
    software distributed under the Licenses are distributed on an "AS IS"
    BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express
    or implied. See the Licenses for the specific language governing
    permissions and limitations under the Licenses.
 */
using System;
using System.Collections.Generic;
using MCGalaxy.BlockPhysics;
using MCGalaxy.Commands;
using MCGalaxy.Drawing.Brushes;
using MCGalaxy.Drawing.Ops;
using MCGalaxy.Undo;

namespace MCGalaxy.Drawing {
    internal struct PendingDrawOp {
        public DrawOp Op;
        public Brush Brush;
        public Vec3S32[] Marks;
        public long Affected;
        public Level Level;
    }
}

namespace MCGalaxy.Drawing.Ops {
    
    public abstract partial class DrawOp {
        
        public static bool DoDrawOp(DrawOp op, Brush brush, Player p,
                                    ushort x1, ushort y1, ushort z1, ushort x2, ushort y2, ushort z2) {
            Vec3S32[] marks = new [] { new Vec3S32(x1, y1, z1), new Vec3S32(x2, y2, z2) };
            return DoDrawOp(op, brush, p, marks);
        }
        
        public static bool DoDrawOp(DrawOp op, Brush brush, Player p,
                                    Vec3S32[] marks, bool checkLimit = true) {
            op.SetMarks(marks);
            op.Level = p == null ? null : p.level;
            if (op.Level != null && !op.Level.DrawingAllowed) {
                Player.Message(p, "Drawing commands are turned off on this map.");
                return false;
            }
            if (op.Level != null && !op.Level.BuildAccess.CheckDetailed(p))
                return false;
            
            long affected = op.BlocksAffected(op.Level, marks);
            if (p != null && op.AffectedByTransform)
                p.Transform.GetBlocksAffected(ref affected);
            
            if (checkLimit && !op.CanDraw(marks, p, affected))
                return false;
            if (brush != null && affected != -1) {
                const string format = "{0}({1}): affecting up to {2} blocks";
                Player.Message(p, format, op.Name, brush.Name, affected);
            } else if (affected != -1) {
                const string format = "{0}: affecting up to {1} blocks";
                Player.Message(p, format, op.Name, affected);
            }
            
            AppendDrawOp(p, op, brush, marks, affected);
            return true;
        }
        
        static void AppendDrawOp(Player p, DrawOp op, Brush brush, Vec3S32[] marks, long affected) {
            if (p == null) {
                BufferedBlockSender buffer = new BufferedBlockSender(op.Level);
                op.Perform(marks, p, op.Level, brush,
                           b => ConsoleOutputBlock(b, op.Level, buffer));
                buffer.Send(true);
                return;
            }
            
            PendingDrawOp item = new PendingDrawOp();
            item.Op = op;
            item.Brush = brush;
            item.Marks = marks;
            item.Affected = affected;
            item.Level = op.Level;
            
            lock (p.pendingDrawOpsLock) {
                p.PendingDrawOps.Add(item);
                // Another thread is already processing draw ops.
                if (p.PendingDrawOps.Count > 1) return;
            }
            ProcessDrawOps(p);
        }
        
        static void ConsoleOutputBlock(DrawOpBlock b, Level lvl, BufferedBlockSender buffer) {
            int index = lvl.PosToInt(b.X, b.Y, b.Z);
            if (!lvl.DoPhysicsBlockchange(index, b.Block, false,
                                          default(PhysicsArgs), b.ExtBlock)) return;
            buffer.Add(index, b.Block, b.ExtBlock);
        }
        
        static void ProcessDrawOps(Player p) {
            while (true) {
                PendingDrawOp item;
                lock (p.pendingDrawOpsLock) {
                    if (p.PendingDrawOps.Count == 0) return;
                    item = p.PendingDrawOps[0];
                    p.PendingDrawOps.RemoveAt(0);
                    
                    // Flush any remaining draw ops if the player has left the server.
                    // (so as to not keep alive references)
                    if (p.disconnected) {
                        p.PendingDrawOps.Clear();
                        return;
                    }
                }
                
                UndoDrawOpEntry entry = new UndoDrawOpEntry();
                entry.DrawOpName = item.Op.Name;
                entry.LevelName = item.Level.name;
                entry.Start = DateTime.UtcNow;
                // Use same time method as DoBlockchange writing to undo buffer
                int timeDelta = (int)DateTime.UtcNow.Subtract(Server.StartTime).TotalSeconds;
                entry.Start = Server.StartTime.AddTicks(timeDelta * TimeSpan.TicksPerSecond);
                
                if (item.Brush != null) item.Brush.Configure(item.Op, p);
                DoDrawOp(item, p);
                timeDelta = (int)DateTime.UtcNow.Subtract(Server.StartTime).TotalSeconds;
                entry.End = Server.StartTime.AddTicks(timeDelta * TimeSpan.TicksPerSecond);
                
                p.DrawOps.Add(entry);
                if (p.DrawOps.Count > 200)
                    p.DrawOps.RemoveFirst();
                if (item.Affected > Server.DrawReloadLimit)
                    DoReload(p, item.Level);
            }
        }
        
        static void DoDrawOp(PendingDrawOp item, Player p) {
            Level lvl = item.Level;
            Action<DrawOpBlock, Player, Level> output = null;
            
            if (item.Affected > Server.DrawReloadLimit) {
                output = SetTileOutput;
            } else if (item.Level.bufferblocks) {
                output = BufferedOutput;
            } else {
                output = SlowOutput;
            }
            
            if (item.Op.AffectedByTransform) {
                p.Transform.Perform(item.Marks, p, lvl, item.Op, item.Brush,
                                    b => output(b, p, lvl));
            } else {
                item.Op.Perform(item.Marks, p, lvl, item.Brush,
                                b => output(b, p, lvl));
            }
        }
        
        static void SetTileOutput(DrawOpBlock b, Player p, Level lvl) {
            if (b.Block == Block.Zero) return;
            byte old = lvl.GetTile(b.X, b.Y, b.Z);
            if (old == Block.Zero) return;
            
            bool same = old == b.Block;
            if (same && b.Block == Block.custom_block)
                same = lvl.GetExtTile(b.X, b.Y, b.Z) == b.ExtBlock;
            if (same || !lvl.CheckAffectPermissions(p, b.X, b.Y, b.Z, old, b.Block, b.ExtBlock))
                return;
            
            lvl.SetTile(b.X, b.Y, b.Z, b.Block, p, b.ExtBlock);
            p.IncrementBlockStats(b.Block, true);
        }
        
        static void BufferedOutput(DrawOpBlock b, Player p, Level lvl) {
            if (b.Block == Block.Zero) return;
            if (!lvl.DoBlockchange(p, b.X, b.Y, b.Z, b.Block, b.ExtBlock, true)) return;
            
            int index = lvl.PosToInt(b.X, b.Y, b.Z);
            lvl.AddToBlockDB(p, index, b.Block, b.ExtBlock, b.Block == 0);
            BlockQueue.Addblock(p, index, b.Block, b.ExtBlock);
        }
        
        static void SlowOutput(DrawOpBlock b, Player p, Level lvl) {
            if (b.Block == Block.Zero) return;
            if (!lvl.DoBlockchange(p, b.X, b.Y, b.Z, b.Block, b.ExtBlock, true)) return;
            
            int index = lvl.PosToInt(b.X, b.Y, b.Z);
            lvl.AddToBlockDB(p, index, b.Block, b.ExtBlock, b.Block == 0);
            Player.GlobalBlockchange(lvl, b.X, b.Y, b.Z, b.Block, b.ExtBlock);
        }
        
        static void DoReload(Player p, Level lvl) {
            Player[] players = PlayerInfo.Online.Items;
            foreach (Player pl in players) {
                if (pl.level.name.CaselessEq(lvl.name))
                    LevelActions.ReloadMap(p, pl, true);
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }
}