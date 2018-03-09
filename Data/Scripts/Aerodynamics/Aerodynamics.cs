using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using VRage;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

// This mod can also be disabled by other mods if the actions are overlapping and you want people to still be able to use the wings as props.
// ----
// A quick way to disable it is by calling this after a second after the session is not null:
//
// MyAPIGateway.Utilities.SendModMessage(473571246, MyTuple.Create(false, "YourModName"));
//
// -----
// Alternatively you can register the same message ID to get a getter and a setter which you can call whenever you want.
//
// private const long AERODYNAMICS_WORKSHOP_ID = 473571246;
// private Func<bool> aerodynamicsGetter = null;
// private Action<bool, string> aerodynamicsSetter = null;
//
// // in your init method
//     MyAPIGateway.Utilities.RegisterMessageHandler(AERODYNAMICS_WORKSHOP_ID, AerodynamicsMethods);
//
// // in UnloadData()
//     MyAPIGateway.Utilities.UnregisterMessageHandler(AERODYNAMICS_WORKSHOP_ID, AerodynamicsMethods);
//
// void AerodynamicsMethods(object obj)
// {
//     if(obj is MyTuple<Func<bool>, Action<bool, string>>)
//     {
//         var methods = (MyTuple<Func<bool>, Action<bool, string>>)obj;
//         aerodynamicsGetter = methods.Item1;
//         aerodynamicsSetter = methods.Item2;
//
//         // avoiding a collection changed exception by calling the unregister after it's done iterating handlers.
//         MyAPIGateway.Utilities.InvokeOnGameThread(() => MyAPIGateway.Utilities.UnregisterMessageHandler(AERODYNAMICS_WORKSHOP_ID, AerodynamicsMethods));
//     }
// }
//
// // anywhere you want to toggle, example
// if(aerodynamicsGetter != null)
// {
//     var enabled = aerodynamicsGetter.Invoke();
//     aerodynamicsSetter.Invoke(!enabled, "MyMod");
// }

namespace Digi.Aerodynamics
{
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class Aerodynamics : MySessionComponentBase
    {
        public override void LoadData()
        {
            instance = this;
            Log.SetUp("Aerodynamics", WORKSHOP_ID);
        }

        public static Aerodynamics instance = null;

        private bool init = false;
        private short planetRefreshTick = 0;
        private byte delaySendMethods = 60;

        public bool enabled = true;
        public string disabledBy = null;
        public readonly List<MyPlanet> planets = new List<MyPlanet>();

        private const long WORKSHOP_ID = 473571246;
        private const short PLANET_REFRESH_TICKS = 60 * 5; // refreshing planet cache time
        public const float MIN_ATMOSPHERE = 0.4f;
        public const float MAX_ATMOSPHERE = 0.7f;
        public readonly Vector3 DEBUG_COLOR_ACTIVE = new Vector3(120f / 360f, 1, 1);
        public readonly Vector3 DEBUG_COLOR_INACTIVE = new Vector3(0, 1, 1);

        public void Init()
        {
            init = true;
            Log.Init();

            // API enable toggle message handler
            MyAPIGateway.Utilities.RegisterMessageHandler(WORKSHOP_ID, ModMessageHandler);

            // find planets ASAP
            MyAPIGateway.Entities.GetEntities(null, IterateEntity);
        }

        protected override void UnloadData()
        {
            instance = null;

            try
            {
                if(init)
                {
                    init = false;
                    MyAPIGateway.Utilities.UnregisterMessageHandler(WORKSHOP_ID, ModMessageHandler);
                    planets.Clear();
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }

            Log.Close();
        }

        public override void UpdateAfterSimulation()
        {
            try
            {
                if(!init)
                {
                    if(MyAPIGateway.Session == null)
                        return;

                    Init();
                }

                if(++planetRefreshTick >= PLANET_REFRESH_TICKS)
                {
                    planetRefreshTick = 0;
                    planets.Clear();
                    MyAPIGateway.Entities.GetEntities(null, IterateEntity);
                }

                // send the API methods after a second
                if(delaySendMethods > 0 && --delaySendMethods == 0)
                {
                    MyAPIGateway.Utilities.SendModMessage(WORKSHOP_ID, MyTuple.Create<Func<bool>, Action<bool, string>>(ModEnabledGetter, ModEnabledSetter));
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        private bool IterateEntity(IMyEntity e)
        {
            var p = e as MyPlanet;

            if(p != null && p.HasAtmosphere)
                planets.Add(p);

            return false; // never return true or you'll get a NRE because of the null list given to the GetEntities() method.
        }

        private void ModMessageHandler(object obj)
        {
            try
            {
                if(obj is MyTuple<bool, string>)
                {
                    var data = (MyTuple<bool, string>)obj;
                    enabled = data.Item1;

                    if(enabled)
                    {
                        Log.Info("Wing logic re-enabled by mod \"" + data.Item2 + "\".");
                        disabledBy = null;
                    }
                    else
                    {
                        Log.Info("Wing logic turned off by mod \"" + data.Item2 + "\".");
                        disabledBy = data.Item2;
                    }
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        private bool ModEnabledGetter()
        {
            return enabled;
        }

        private void ModEnabledSetter(bool set, string name)
        {
            try
            {
                enabled = set;

                if(enabled)
                {
                    Log.Info("Wing logic re-enabled by mod \"" + name ?? "(unknown mod)" + "\".");
                    disabledBy = null;
                }
                else
                {
                    Log.Info("Wing logic turned off by mod \"" + name ?? "(unknown mod)" + "\".");
                    disabledBy = name;
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_TerminalBlock), false, "WingAngled1", "WingAngled2", "WingStreight")]
    public class Wing : MyGameLogicComponent
    {
        private float atmosphere = 0;
        private int atmospheres = 0;
        private Vector3? debugPrevColor = null;

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            NeedsUpdate = MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            try
            {
                var block = (IMyTerminalBlock)Entity;

                if(block.CubeGrid.Physics == null)
                {
                    NeedsUpdate = MyEntityUpdateEnum.NONE;
                }
                else
                {
                    NeedsUpdate = MyEntityUpdateEnum.EACH_FRAME | MyEntityUpdateEnum.EACH_10TH_FRAME | MyEntityUpdateEnum.EACH_100TH_FRAME;

                    block.ShowInTerminal = false;
                    block.ShowInToolbarConfig = false;
                    block.AppendingCustomInfo += CustomInfo;
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        public override void Close()
        {
            var block = (IMyTerminalBlock)Entity;
            block.AppendingCustomInfo -= CustomInfo;
        }

        public override void UpdateAfterSimulation()
        {
            try
            {
                if(!Aerodynamics.instance.enabled)
                    return;

                var block = (IMyTerminalBlock)Entity;
                var grid = (MyCubeGrid)block.CubeGrid;

                if(grid.Physics == null || grid.Physics.IsStatic || block.MarkedForClose || block.Closed || !block.IsWorking)
                    return;

                var gridCenter = grid.Physics.CenterOfMassWorld;

                bool debug = MyAPIGateway.Session.CreativeMode && block.ShowInToolbarConfig;
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
                    grid.ChangeColor(grid.GetCubeBlock(slim.Position), debugPrevColor.Value);
                    debugPrevColor = null;
                }

                if(atmospheres == 0 || atmosphere <= float.Epsilon)
                {
                    if(debug)
                    {
                        if(debugText)
                            MyAPIGateway.Utilities.ShowNotification(block.CustomName + ": not in atmosphere", 16, MyFontEnum.Red);

                        if(Vector3.DistanceSquared(slim.GetColorMask(), Aerodynamics.instance.DEBUG_COLOR_INACTIVE) > float.Epsilon)
                            grid.ChangeColor(grid.GetCubeBlock(slim.Position), Aerodynamics.instance.DEBUG_COLOR_INACTIVE);
                    }

                    return;
                }

                var blockMatrix = block.WorldMatrix;
                var vel = grid.Physics.GetVelocityAtPoint(blockMatrix.Translation);
                double speedSq = MathHelper.Clamp(vel.LengthSquared() * 2, 0, 10000);

                //if(debug)
                //{
                //    MyTransparentGeometry.AddLineBillboard(MyStringId.GetOrCompute("Square"), (Color.YellowGreen * 0.5f), blockMatrix.Translation, Vector3D.Normalize(grid.Physics.LinearVelocity), 12, 0.1f);
                //}

                if(speedSq >= 50)
                {
                    Vector3D fw = blockMatrix.Left;
                    double forceMul = 0.75;

                    switch(block.BlockDefinition.SubtypeId)
                    {
                        case "WingAngled1":
                            forceMul = 1.0;
                            fw = Vector3D.Normalize(blockMatrix.Left + blockMatrix.Forward * 0.15);
                            break;
                        case "WingAngled2":
                            forceMul = 1.25;
                            fw = Vector3D.Normalize(blockMatrix.Left + blockMatrix.Forward * 0.35);
                            break;
                    }

                    double speedDir = fw.Dot(vel);

                    if(debug)
                    {
                        MyTransparentGeometry.AddLineBillboard(MyStringId.GetOrCompute("Square"), ((speedDir > 0 ? Color.Blue : Color.Red) * 0.5f), blockMatrix.Translation, Vector3D.Normalize(vel), 10, 0.05f);
                    }

                    if(speedDir > 0)
                    {
                        var upDir = blockMatrix.Up;
                        var forceVector = -upDir * upDir.Dot(vel) * forceMul * speedSq * atmosphere;

                        grid.Physics.AddForce(MyPhysicsForceType.APPLY_WORLD_FORCE, forceVector, gridCenter, null);

                        if(debug)
                        {
                            var totalForce = forceVector.Normalize();

                            var height = (float)totalForce / 100000f;
                            var pos = blockMatrix.Translation + forceVector * height;
                            MyTransparentGeometry.AddBillboardOriented(MyStringId.GetOrCompute("Square"), Color.Green * 0.5f, pos, blockMatrix.Forward, forceVector, 1.25f, height);

                            if(debugText)
                                MyAPIGateway.Utilities.ShowNotification(block.CustomName + ": forceMul=" + Math.Round(forceMul, 2) + "; atmosphere=" + Math.Round(atmosphere * 100, 0) + "%; totalforce=" + Math.Round(totalForce / 1000, 2) + " MN", 16, MyFontEnum.Green);

                            if(Vector3.DistanceSquared(slim.GetColorMask(), Aerodynamics.instance.DEBUG_COLOR_ACTIVE) > float.Epsilon)
                                grid.ChangeColor(grid.GetCubeBlock(slim.Position), Aerodynamics.instance.DEBUG_COLOR_ACTIVE);
                        }
                    }
                    else if(debug)
                    {
                        if(debugText)
                            MyAPIGateway.Utilities.ShowNotification(block.CustomName + ": wrong direction", 16, MyFontEnum.Red);

                        if(Vector3.DistanceSquared(slim.GetColorMask(), Aerodynamics.instance.DEBUG_COLOR_INACTIVE) > float.Epsilon)
                            grid.ChangeColor(grid.GetCubeBlock(slim.Position), Aerodynamics.instance.DEBUG_COLOR_INACTIVE);
                    }
                }
                else if(debug)
                {
                    if(debugText)
                        MyAPIGateway.Utilities.ShowNotification(block.CustomName + ": not enough speed", 16, MyFontEnum.Red);

                    if(Vector3.DistanceSquared(slim.GetColorMask(), Aerodynamics.instance.DEBUG_COLOR_INACTIVE) > float.Epsilon)
                        grid.ChangeColor(grid.GetCubeBlock(slim.Position), Aerodynamics.instance.DEBUG_COLOR_INACTIVE);
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        public override void UpdateBeforeSimulation10()
        {
            try
            {
                if(!Aerodynamics.instance.enabled)
                    return;

                var block = (IMyTerminalBlock)Entity;
                var grid = (MyCubeGrid)block.CubeGrid;

                if(grid.Physics == null || grid.Physics.IsStatic || block.MarkedForClose || block.Closed || !block.IsWorking)
                    return;

                var gridCenter = grid.Physics.CenterOfMassWorld;
                var planets = Aerodynamics.instance.planets;

                atmosphere = 0;
                atmospheres = 0;

                for(int i = planets.Count - 1; i >= 0; --i)
                {
                    var planet = planets[i];

                    if(planet.Closed || planet.MarkedForClose)
                    {
                        planets.RemoveAt(i);
                        continue;
                    }

                    if(Vector3D.DistanceSquared(gridCenter, planet.WorldMatrix.Translation) < (planet.AtmosphereRadius * planet.AtmosphereRadius))
                    {
                        atmosphere += planet.GetAirDensity(gridCenter);
                        atmospheres++;
                    }
                }

                if(atmospheres > 0)
                {
                    atmosphere /= atmospheres;
                    atmosphere = MathHelper.Clamp((atmosphere - Aerodynamics.MIN_ATMOSPHERE) / (Aerodynamics.MAX_ATMOSPHERE - Aerodynamics.MIN_ATMOSPHERE), 0f, 1f);
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        public override void UpdateBeforeSimulation100()
        {
            try
            {
                var block = (IMyTerminalBlock)Entity;
                var on = Aerodynamics.instance.enabled;

                block.RefreshCustomInfo();

                if(on)
                    NeedsUpdate |= MyEntityUpdateEnum.EACH_FRAME | MyEntityUpdateEnum.EACH_10TH_FRAME;
                else
                    NeedsUpdate &= ~(MyEntityUpdateEnum.EACH_FRAME | MyEntityUpdateEnum.EACH_10TH_FRAME);
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        private void CustomInfo(IMyTerminalBlock block, StringBuilder info)
        {
            try
            {
                info.Append('\n');

                if(Aerodynamics.instance.enabled)
                    info.Append("Wings are enabled and working.");
                else
                    info.Append("Wings disabled by another mod: ").Append(Aerodynamics.instance.disabledBy ?? "(unknown mod)");
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
    }
}