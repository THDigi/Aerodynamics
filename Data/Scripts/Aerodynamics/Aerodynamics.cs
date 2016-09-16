using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

using Digi.Utils;

namespace Digi.Aerodynamics
{
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class Aerodynamics : MySessionComponentBase
    {
        public override void LoadData()
        {
            Log.SetUp("Aerodynamics", 473571246);
        }

        private bool init = false;

        private int skip = 0;
        private const int SKIP_TICKS = 60 * 3;

        public static readonly List<MyPlanet> planets = new List<MyPlanet>();
        private static readonly HashSet<IMyEntity> ents = new HashSet<IMyEntity>(); // this is always empty

        public void Init()
        {
            init = true;
            Log.Init();
        }

        protected override void UnloadData()
        {
            planets.Clear();
            ents.Clear();
            Log.Close();
        }

        public override void UpdateAfterSimulation()
        {
            if(!init)
            {
                if(MyAPIGateway.Session == null)
                    return;

                Init();
            }

            if(++skip >= SKIP_TICKS)
            {
                skip = 0;
                planets.Clear();
                MyAPIGateway.Entities.GetEntities(ents, delegate (IMyEntity e)
                                                  {
                                                      if(e is MyPlanet)
                                                          planets.Add(e as MyPlanet);

                                                      return false; // no reason to add to the hashset
                                                  });
            }
        }
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_TerminalBlock), "WingAngled1", "WingAngled2", "WingStreight")]
    public class Wing : MyGameLogicComponent
    {
        public static readonly HashSet<string> WingIds = new HashSet<string>()
        {
            "WingAngled1",
            "WingAngled2",
            "WingStreight"
        };

        private bool active = false;

        private int slowUpdate = 0;
        private float atmosphere = 0;
        private int atmospheres = 0;

        private Vector3? debugPrevColor = null;

        private const int SKIP_TICKS = 6;

        public const float MIN_ATMOSPHERE = 0.4f;
        public const float MAX_ATMOSPHERE = 0.7f;

        public static readonly Vector3 DEBUG_COLOR_ACTIVE = new Vector3(120f / 360f, 1, 1);
        public static readonly Vector3 DEBUG_COLOR_INACTIVE = new Vector3(0, 1, 1);

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            Entity.NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            try
            {
                var block = Entity as IMyTerminalBlock;

                if(block == null)
                {
                    Log.Error("Wing script couldn't execute because wing is not a terminal block!");
                    return;
                }

                Entity.NeedsUpdate |= MyEntityUpdateEnum.EACH_FRAME;
                active = true;

                block.SetValueBool("ShowInTerminal", false);
                block.SetValueBool("ShowInToolbarConfig", false);
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        public override void UpdateAfterSimulation()
        {
            try
            {
                if(!active)
                    return;

                var block = Entity as IMyTerminalBlock;

                if(block.MarkedForClose || block.Closed || !block.IsWorking)
                    return;

                var grid = block.CubeGrid as IMyCubeGrid;

                if(grid.Physics == null || grid.Physics.IsStatic)
                    return;

                var gridCenter = grid.Physics.CenterOfMassWorld;

                if(++slowUpdate % SKIP_TICKS == 0)
                {
                    slowUpdate = 0;
                    atmosphere = 0;
                    atmospheres = 0;

                    foreach(var planet in Aerodynamics.planets)
                    {
                        if(planet.Closed || planet.MarkedForClose)
                            continue;

                        if(planet.HasAtmosphere && Vector3D.DistanceSquared(gridCenter, planet.WorldMatrix.Translation) < (planet.AtmosphereRadius * planet.AtmosphereRadius))
                        {
                            atmosphere += planet.GetAirDensity(gridCenter);
                            atmospheres++;
                        }
                    }

                    if(atmospheres > 0)
                    {
                        atmosphere /= atmospheres;
                        atmosphere = MathHelper.Clamp((atmosphere - MIN_ATMOSPHERE) / (MAX_ATMOSPHERE - MIN_ATMOSPHERE), 0f, 1f);
                    }
                }

                bool debug = MyAPIGateway.Session.CreativeMode && block.GetValueBool("ShowInToolbarConfig");
                bool debugText = true;
                IMySlimBlock slim = null;

                if(debug)
                {
                    slim = grid.GetCubeBlock(block.Position);

                    if(MyAPIGateway.Session.ControlledObject != null && MyAPIGateway.Session.ControlledObject.Entity != null)
                    {
                        var controlled = MyAPIGateway.Session.ControlledObject.Entity;
                        debugText = (controlled.EntityId == grid.EntityId || Vector3D.DistanceSquared(block.GetPosition(), controlled.GetPosition()) <= (30 * 30));
                    }

                    if(!debugPrevColor.HasValue)
                    {
                        debugPrevColor = slim.GetColorMask();
                    }
                }
                else if(debugPrevColor.HasValue)
                {
                    grid.ColorBlocks(block.Position, block.Position, debugPrevColor.Value);
                    debugPrevColor = null;
                }

                if(atmospheres == 0 || atmosphere <= float.Epsilon)
                {
                    if(debug)
                    {
                        if(debugText)
                            MyAPIGateway.Utilities.ShowNotification(block.CustomName + ": not in atmosphere", 16, MyFontEnum.Red);

                        if(Vector3.DistanceSquared(slim.GetColorMask(), DEBUG_COLOR_INACTIVE) > float.Epsilon)
                            grid.ColorBlocks(block.Position, block.Position, DEBUG_COLOR_INACTIVE);
                    }

                    return;
                }

                var vel = grid.Physics.GetVelocityAtPoint(block.WorldMatrix.Translation);
                double speedSq = MathHelper.Clamp(vel.LengthSquared() * 2, 0, 10000);

                if(speedSq >= 50)
                {
                    Vector3D fw = block.WorldMatrix.Left;
                    double forceMul = 0.75;

                    switch(block.BlockDefinition.SubtypeId)
                    {
                        case "WingAngled1":
                            forceMul = 1.0;
                            fw = Vector3D.Normalize(block.WorldMatrix.Left + block.WorldMatrix.Forward * 0.15);
                            break;
                        case "WingAngled2":
                            forceMul = 1.25;
                            fw = Vector3D.Normalize(block.WorldMatrix.Left + block.WorldMatrix.Forward * 0.35);
                            break;
                    }

                    double speedDir = fw.Dot(vel);

                    if(speedDir > 0)
                    {
                        var upDir = block.WorldMatrix.Up;
                        double force = upDir.Dot(vel) * forceMul;

                        grid.Physics.AddForce(MyPhysicsForceType.APPLY_WORLD_FORCE, -upDir * force * speedSq * atmosphere, gridCenter, null);

                        if(debug)
                        {
                            var totalForce = (-upDir * force * speedSq * atmosphere).Length();

                            if(debugText)
                                MyAPIGateway.Utilities.ShowNotification(block.CustomName + ": forceMul=" + Math.Round(forceMul, 2) + "; atmosphere=" + Math.Round(atmosphere * 100, 0) + "%; totalforce=" + Math.Round(totalForce / 1000, 2) + " MN", 16, MyFontEnum.Green);

                            if(Vector3.DistanceSquared(slim.GetColorMask(), DEBUG_COLOR_ACTIVE) > float.Epsilon)
                                grid.ColorBlocks(block.Position, block.Position, DEBUG_COLOR_ACTIVE);
                        }
                    }
                    else if(debug)
                    {
                        if(debugText)
                            MyAPIGateway.Utilities.ShowNotification(block.CustomName + ": wrong direction", 16, MyFontEnum.Red);

                        if(Vector3.DistanceSquared(slim.GetColorMask(), DEBUG_COLOR_INACTIVE) > float.Epsilon)
                            grid.ColorBlocks(block.Position, block.Position, DEBUG_COLOR_INACTIVE);
                    }
                }
                else if(debug)
                {
                    if(debugText)
                        MyAPIGateway.Utilities.ShowNotification(block.CustomName + ": not enough speed", 16, MyFontEnum.Red);

                    if(Vector3.DistanceSquared(slim.GetColorMask(), DEBUG_COLOR_INACTIVE) > float.Epsilon)
                        grid.ColorBlocks(block.Position, block.Position, DEBUG_COLOR_INACTIVE);
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        public override MyObjectBuilder_EntityBase GetObjectBuilder(bool copy = false)
        {
            return Entity.GetObjectBuilder(copy);
        }
    }
}