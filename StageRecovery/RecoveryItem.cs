﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace StageRecovery
{
    public class RecoveryItem
    {
        public Vessel vessel;
        public bool recovered
        {
            get
            {
                if (burnedUp) return false;
                if (Settings.instance.FlatRateModel)
                    return Vt < Settings.instance.CutoffVelocity;
                else
                    return Vt < Settings.instance.HighCut;
            }
        }
        public bool burnedUp, poweredRecovery;
        public string StageName, ParachuteModule;
        public float Vt = 0f;
        public List<string> ScienceExperiments = new List<string>();
        public float ScienceRecovered = 0;
        public List<string> KerbalsOnboard = new List<string>();
        public Dictionary<string, int> PartsRecovered = new Dictionary<string, int>();
        public Dictionary<string, float> Costs = new Dictionary<string, float>();
        public float FundsOriginal = 0, FundsReturned = 0, DryReturns = 0, FuelReturns = 0;
        public float KSCDistance = 0;
        public float RecoveryPercent = 0, DistancePercent = 0, SpeedPercent = 0;
        public string ReasonForFailure { get { if (recovered) return "SUCCESS"; if (burnedUp) return "BURNUP"; return "SPEED"; } }
        public Dictionary<string, float> fuelUsed = new Dictionary<string, float>();

        //Creates a new RecoveryItem and calculates everything corresponding to it.
        public RecoveryItem(Vessel stage)
        {
            vessel = stage;
            //Pack all the parts. I got this from MCE and everything works so I haven't tried removing it.
            if (!vessel.packed)
                foreach (Part p in vessel.Parts)
                    p.Pack();
            //Get the name
            StageName = vessel.vesselName;
            //Determine what the terminal velocity should be
            Vt = DetermineTerminalVelocity();
            //Try to perform a powered landing
            float vt_old = Vt;
            if (Vt > (Settings.instance.FlatRateModel ? Settings.instance.CutoffVelocity : Settings.instance.LowCut) && Settings.instance.PoweredRecovery)
                Vt = TryPoweredRecovery();
            poweredRecovery = (Vt < vt_old);
            //Determine if the stage should be burned up
            burnedUp = DetermineIfBurnedUp();
            //Set the Recovery Percentages
            SetRecoveryPercentages();
            //Set the parts, costs, and refunds
            SetPartsAndFunds();
            //Recover Science if we're allowed
            if (recovered && Settings.instance.RecoverScience)
                ScienceRecovered = RecoverScience();
            //Recover Kerbals if we're allowed
            //if (recovered && Settings.instance.RecoverKerbals)
            KerbalsOnboard = RecoverKerbals();
        }

        public static double GetParachuteDragFromPart(AvailablePart parachute)
        {
           /* foreach (AvailablePart.ModuleInfo mi in parachute.moduleInfos)
            {
                if (mi.info.Contains("Fully-Deployed Drag"))
                {
                    string[] split = mi.info.Split(new Char[] { ':', '\n' });
                    for (int i = 0; i < split.Length; i++)
                    {
                        if (split[i].Contains("Fully-Deployed Drag"))
                        {
                            float drag = 500;
                            if (!float.TryParse(split[i + 1], out drag))
                            {
                                string[] split2 = split[i + 1].Split('>');
                                if (!float.TryParse(split2[1], out drag))
                                {
                                    Debug.Log("[SR] Failure trying to read parachute data. Assuming 500 drag.");
                                    drag = 500;
                                }
                            }
                            return drag;
                        }
                    }
                }
            }*/
            double area = 0;
            if (parachute.partPrefab.Modules.Contains("ModuleParachute"))
            {
                area = ((ModuleParachute)parachute.partPrefab.Modules["ModuleParachute"]).areaDeployed;
            }
            return area;
        }

        //This function/method/thing calculates the terminal velocity of the Stage
        private float DetermineTerminalVelocity()
        {
            float v = 0;
            float totalMass = 0;
           // float dragCoeff = 0;
            float RCParameter = 0;
            double totalParachuteArea = 0;
            bool realChuteInUse = false;
            try
            {
                foreach (ProtoPartSnapshot p in vessel.protoVessel.protoPartSnapshots)
                {
                    //Make a list of all the Module Names for easy checking later. This can be avoided, but is convenient.
                    List<string> ModuleNames = new List<string>();
                    foreach (ProtoPartModuleSnapshot ppms in p.modules)
                    {
                        ModuleNames.Add(ppms.moduleName);
                    }
                    //Add the part mass to the total.
                    totalMass += p.mass;
                    //Add resource masses
                    totalMass += GetResourceMass(p.resources);
                    //Assume the part isn't a parachute until proven a parachute
                   // bool isParachute = false;
                    //For instance, by having the ModuleParachute module
                    if (!realChuteInUse && ModuleNames.Contains("ModuleParachute"))
                    {
                        //Find the ModuleParachute (find it in the module list by checking for a module with the name ModuleParachute)
                        ProtoPartModuleSnapshot ppms = p.modules.First(mod => mod.moduleName == "ModuleParachute");
                        if (ppms.moduleRef != null)
                        {
                            ModuleParachute mp = (ModuleParachute)ppms.moduleRef;
                            mp.Load(ppms.moduleValues);
                            totalParachuteArea += mp.areaDeployed;
                        }
                        else
                        {
                            totalParachuteArea += GetParachuteDragFromPart(p.partInfo);
                        }
                        //Add the part mass times the fully deployed drag (typically 500) to the dragCoeff variable (you'll see why later)
                       // dragCoeff += p.mass * drag;
                        //This is most definitely a parachute part
                     //   isParachute = true;
                    }
                    //If the part has the RealChuteModule, we have to do some tricks to access it
                    if (ModuleNames.Contains("RealChuteModule"))
                    {
                        //First off, get the PPMS since we'll need that
                        ProtoPartModuleSnapshot realChute = p.modules.First(mod => mod.moduleName == "RealChuteModule");
                        //Assuming that's not somehow null, then we continue
                        if ((object)realChute != null) //Some of this was adopted from DebRefund, as Vendan's method of handling multiple parachutes is better than what I had.
                        {
                            //This is where the Reflection starts. We need to access the material library that RealChute has, so we first grab it's Type
                            Type matLibraryType = AssemblyLoader.loadedAssemblies
                                .SelectMany(a => a.assembly.GetExportedTypes())
                                .SingleOrDefault(t => t.FullName == "RealChute.Libraries.MaterialsLibrary");

                            //We make a list of ConfigNodes containing the parachutes (usually 1, but now there can be any number of them)
                            //We get that from the PPMS 
                            ConfigNode[] parachutes = realChute.moduleValues.GetNodes("PARACHUTE");
                            //We then act on each individual parachute in the module
                            foreach (ConfigNode chute in parachutes)
                            {
                                //First off, the diameter of the parachute. From that we can (later) determine the Vt, assuming a circular chute
                                float diameter = float.Parse(chute.GetValue("deployedDiameter"));
                                //The name of the material the chute is made of. We need this to get the actual material object and then the drag coefficient
                                string mat = chute.GetValue("material");
                                //This grabs the method that RealChute uses to get the material. We will invoke that with the name of the material from before.
                                System.Reflection.MethodInfo matMethod = matLibraryType.GetMethod("GetMaterial", new Type[] { mat.GetType() });
                                //In order to invoke the method, we need to grab the active instance of the material library
                                object MatLibraryInstance = matLibraryType.GetProperty("instance").GetValue(null, null);
                                //With the library instance we can invoke the GetMaterial method (passing the name of the material as a parameter) to receive an object that is the material
                                object materialObject = matMethod.Invoke(MatLibraryInstance, new object[] { mat });
                                //With that material object we can extract the dragCoefficient using the helper function above.
                                float dragC = (float)StageRecovery.GetMemberInfoValue(materialObject.GetType().GetMember("dragCoefficient")[0], materialObject);
                                //Now we calculate the RCParameter. Simple addition of this doesn't result in perfect results for Vt with parachutes with different diameter or drag coefficients
                                //But it works perfectly for mutiple identical parachutes (the normal case)
                                RCParameter += dragC * (float)Math.Pow(diameter, 2);

                            }
                            //This is a parachute also
                           // isParachute = true;
                            //It's existence means that RealChute is installed and in use on the craft (you could have it installed and use stock chutes, so we only check if it's on the craft)
                            realChuteInUse = true;
                        }
                    }
                    //If the part isn't a parachute (no ModuleParachute or RealChuteModule)
                   // if (!isParachute)
                   // {
                        //If the part reference isn't null, find the maximum drag parameter. Multiply that by the mass (KSP has stupid aerodynamics)
                     /*   if (p.partRef != null)
                            dragCoeff += p.mass * p.partRef.maximum_drag;
                        //Otherwise we assume it's a 0.2 drag. We could probably determine the exact value from the config node
                        else
                            dragCoeff += p.mass * 0.2f;*/
                        //totalParachuteArea += 0.01;
                   // }
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[SR] Error occured while trying to determine terminal velocity.");
                Debug.LogException(e);
            }
            if (totalParachuteArea != 0)
            {
                if (!realChuteInUse)
                {
                    //This all follows from the formulas on the KSP wiki under the atmosphere page. http://wiki.kerbalspaceprogram.com/wiki/Atmosphere
                    //Divide the current value of the dragCoeff by the total mass. Now we have the actual drag coefficient for the vessel
                    //  dragCoeff = dragCoeff / (totalMass);
                    //Calculate Vt by what the wiki says
                    //v = (float)(Math.Sqrt((250 * 6.674E-11 * 5.2915793E22) / (3.6E11 * 1.22309485 * dragCoeff)));

                    v = (float)(63 * Math.Pow(totalMass / totalParachuteArea, 0.4));
                }
                //Otherwise we're using RealChutes and we have a bit different of a calculation
                else
                {
                    //This is according to the formulas used by Stupid_Chris in the Real Chute drag calculator program included with Real Chute. Source: https://github.com/StupidChris/RealChute/blob/master/Drag%20Calculator/RealChute%20drag%20calculator/RCDragCalc.cs
                    v = (float)Math.Sqrt((8000 * totalMass * 9.8) / (1.223 * Math.PI * RCParameter));
                }
            }
            else
            {
                v = 200.0f;
            }
            ParachuteModule = realChuteInUse ? "RealChute" : "Stock";
            Debug.Log("[SR] Vt: " + v);
            return v;
        }

        //This method will calculate the total mass of the provided resources, typically those in a part.
        private float GetResourceMass(List<ProtoPartResourceSnapshot> resources)
        {
            double mass = 0;
            //Loop through the available resources
            foreach (ProtoPartResourceSnapshot resource in resources)
            {
                //Get the ConfigNode which contains the resource information (amount, name, etc)
                ConfigNode RCN = resource.resourceValues;
                //Extract the amount information
                double amount = double.Parse(RCN.GetValue("amount"));
                //Using the name of the resource, find it in the PartResourceLibrary
                PartResourceDefinition RD = PartResourceLibrary.Instance.GetDefinition(resource.resourceName);
                //The mass of that resource is the amount times the density
                mass += amount * RD.density;
            }
            //Return the total mass
            return (float)mass;
        }

        private float TryPoweredRecovery()
        {
            Debug.Log("[SR] Trying powered recovery");
            //ISP references: http://forum.kerbalspaceprogram.com/threads/34315-How-Do-I-calculate-Delta-V-on-more-than-one-engine
            //Thanks to Malkuth, of Mission Controller Extended, for the base of this code.
            float finalVelocity = Vt;
            float totalMass = 0;
            //We keep the active engines and enginesFX for later use
            List<ModuleEngines> engines = new List<ModuleEngines>();
            List<ModuleEnginesFX> enginesFX = new List<ModuleEnginesFX>();
            //netISP is the average ISP for the whole set of active engines
            double netISP = 0;
            //Likewise, this is the total thrust of all the engines
            double totalThrust = 0;
            //we keep track of the total resources and their masses
            Dictionary<string, double> resources = new Dictionary<string, double>();
            Dictionary<string, double> rMasses = new Dictionary<string, double>();
            //The stage must be controlled to be landed this way
            bool stageControllable = vessel.protoVessel.wasControllable;
            try
            {
                if (stageControllable && Settings.instance.UseUpgrades)
                {
                    stageControllable = vessel.GetVesselCrew().Find(c => c.experienceTrait.Title == "Pilot") != null;
                    if (stageControllable)
                        Debug.Log("[SR] Found a kerbal pilot!");
                    else
                    {
                        Debug.Log("[SR] No kerbal pilot found, searching for a probe core...");
                        stageControllable = vessel.protoVessel.protoPartSnapshots.Find(p => p.modules.Find(m => m.moduleName == "ModuleSAS") != null) != null;
                        if (stageControllable)
                            Debug.Log("[SR] Found an SAS compatible probe core!");
                        else
                            Debug.Log("[SR] No probe core with SAS found.");
                    }

                }
                else if (!stageControllable)
                {
                    Debug.Log("[SR] Stage not controlled. Can't perform powered recovery.");
                    return finalVelocity;
                }

                //Loop over all the parts to check for control, engines, and fuel
                foreach (ProtoPartSnapshot p in vessel.protoVessel.protoPartSnapshots)
                {
                  /*  //Search through the Modules on the part for one called ModuleCommand and check if the crew count in the part is greater than or equal to the minimum required for control
                    if (!stageControllable && p.modules.Find(module => (module.moduleName == "ModuleCommand" && ((ModuleCommand)module.moduleRef).minimumCrew <= p.protoModuleCrew.Count)) != null)
                    {
                        //Congrats, the stage is controlled! We can stop looking now.
                        stageControllable = true;
                    }*/
                    //Add the mass of the parts and their resources to the total vessel mass
                    totalMass += p.mass;
                    totalMass += GetResourceMass(p.resources);
                    //Search through the modules for engines
                    foreach (ProtoPartModuleSnapshot ppms in p.modules)
                    {
                        //If we find a standard engine, add it to the list if it's enabled and doesn't use solid fuel (no SRBs here, mister!)
                        if (ppms.moduleName == "ModuleEngines")
                        {
                            ModuleEngines engine = (ModuleEngines)ppms.moduleRef;
                            engine.Load(ppms.moduleValues);
                            if (engine.isEnabled && engine.propellants.Find(prop => prop.name.ToLower().Contains("solidfuel")) == null)//Don't use SRBs
                            {
                                engines.Add(engine);
                            }
                        }
                        //Same with the newer fancy engines (like the one added in 0.23.5)
                        if (ppms.moduleName == "ModuleEnginesFX")
                        {
                            ModuleEnginesFX engine = (ModuleEnginesFX)ppms.moduleRef;
                            engine.Load(ppms.moduleValues);
                            if (engine.isEnabled && engine.propellants.Find(prop => prop.name.ToLower().Contains("solidfuel")) == null)
                            {
                                enginesFX.Add(engine);
                            }
                        }
                    }
                    //Loop through the resources, tracking the number and mass
                    foreach (ProtoPartResourceSnapshot rsc in p.resources)
                    {
                        double amt = double.Parse(rsc.resourceValues.GetValue("amount"));
                        //Debug.Log("[SR] Adding " + amt + " of " + rsc.resourceName + ". density: " + rsc.resourceRef.info.density);
                        if (!resources.ContainsKey(rsc.resourceName))
                        {
                            resources.Add(rsc.resourceName, amt);
                            rMasses.Add(rsc.resourceName, amt * PartResourceLibrary.Instance.GetDefinition(rsc.resourceName).density);
                        }
                        else
                        {
                            resources[rsc.resourceName] += amt;
                            rMasses[rsc.resourceName] += (amt * PartResourceLibrary.Instance.GetDefinition(rsc.resourceName).density);
                        }
                    }

                }
            }
            catch (Exception e) //If the engine moduleRef is null, this will be fired. But I NEED it to exist to do anything practical.
            {
                Debug.LogError("[SR] Error occurred while attempting powered recovery.");
                Debug.LogException(e);
            }
            //Holder for the propellants the engines need and the ratio
            Dictionary<string, float> propsUsed = new Dictionary<string, float>();
            //So, I'm not positive jets really need to be done differently. Though they could go further than normal rockets because of gliding.
            if (stageControllable && (engines.Count > 0 || enginesFX.Count > 0)) //If the stage is controlled and there are engines, we continue.
            {
                //Debug.Log("[SR] Controlled and has engines");
                //Engine landing
                //Determine the total thrust and begin calculating the net ISP
                foreach (ModuleEngines e in engines)
                {
                    totalThrust += e.maxThrust;
                    netISP += (e.maxThrust / e.atmosphereCurve.Evaluate(1));
                }
                foreach (ModuleEnginesFX e in enginesFX)
                {
                    totalThrust += e.maxThrust;
                    netISP += (e.maxThrust / e.atmosphereCurve.Evaluate(1));
                }
                if (totalThrust < (totalMass * 9.81) * Settings.instance.MinTWR) //Need greater than 1 TWR to land. Planes would be different, but we ignore them. This isn't quite true with parachutes, btw.
                    return finalVelocity;
                //Now we determine the netISP by taking the total thrust and dividing by the stuff we calculated earlier.
                netISP = totalThrust / netISP; 
               
                //We need to find out what propellants we need, so we check the first engine or engineFX
                if (engines.Count > 0)
                {
                    foreach (Propellant prop in engines[0].propellants)
                    {
                        //We don't care about air, electricity, or coolant as it's assumed those are infinite.
                        if (resources.ContainsKey(prop.name) && !(prop.name.ToLower().Contains("air") || prop.name.ToLower().Contains("electric") || prop.name.ToLower().Contains("coolant")))
                        {
                            if (!propsUsed.ContainsKey(prop.name))
                                propsUsed.Add(prop.name, prop.ratio);
                        }
                    }
                }
                else if (enginesFX.Count > 0)
                {
                    foreach (Propellant prop in enginesFX[0].propellants)
                    {
                        //We don't care about air, electricity, or coolant as it's assumed those are infinite.
                        if (resources.ContainsKey(prop.name) && !(prop.name.ToLower().Contains("air") || prop.name.ToLower().Contains("electric") || prop.name.ToLower().Contains("coolant")))
                        {
                            if (!propsUsed.ContainsKey(prop.name))
                                propsUsed.Add(prop.name, prop.ratio);
                        }
                    }
                }
                //Determine the cutoff velocity that we're aiming for. This is dependent on the recovery model used (flat rate vs variable rate)
                float cutoff = Settings.instance.FlatRateModel ? Settings.instance.CutoffVelocity : Settings.instance.LowCut;

                double finalMassRequired = totalMass * Math.Exp(-(1.5 * (finalVelocity-cutoff+2)) / (9.81 * netISP));
                double massRequired = totalMass - finalMassRequired;

                //If the engine doesn't need fuel (ie, electric engines from firespitter) then we just say you land
                if (propsUsed.Count == 0)
                    finalVelocity = cutoff-2;
                //Otherwise we need to use fuel
                else
                {
                    //Setup a dictionary with the fuelName and amount required
                    Dictionary<string, float> propAmounts = new Dictionary<string, float>();
                    //We determine something called the DnRnSum, which is the sum of all the densities times the ratio
                    float DnRnSum = 0;
                    foreach (KeyValuePair<string, float> entry in propsUsed)
                    {
                        DnRnSum += entry.Value * PartResourceLibrary.Instance.GetDefinition(entry.Key).density;
                    }
                    //Then we determine the amount of each fuel type required (to expell the correct mass) using the DnRnSum and the ratio
                    foreach (KeyValuePair<string, float> entry in propsUsed)
                    {
                        float amt = (float)massRequired * entry.Value / DnRnSum;
                        propAmounts.Add(entry.Key, amt);
                    }

                    //Assume we have enough fuel until we check
                    bool enoughFuel = true;
                    float limiter = 0;
                    string limitingFuelType = "";
                    //Check if we have enough fuel and determine which fuel is the limiter if we don't (multiply density times amount missing)
                    foreach (KeyValuePair<string, float> entry in propAmounts)
                    {
                        float density = PartResourceLibrary.Instance.GetDefinition(entry.Key).density;
                        if (!resources.ContainsKey(entry.Key) || (entry.Value > resources[entry.Key] &&
                            (entry.Value - resources[entry.Key]) * density > limiter))
                        {
                            enoughFuel = false;
                            limitingFuelType = entry.Key;
                            if (resources.ContainsKey(entry.Key))
                                limiter = (float)(entry.Value - resources[entry.Key]) * density;
                            else
                                limiter = (entry.Value) * density;
                        }
                    }

                    //If we don't have enough fuel, we determine how much we CAN use so that maybe we'll land slow enough for a partial refund
                    if (!enoughFuel)
                    {
                        float limiterAmount = (float)resources[limitingFuelType];
                        float ratio1 = propsUsed[limitingFuelType];
                        foreach (KeyValuePair<string, float> entry in new Dictionary<string, float>(propAmounts))
                        {
                            propAmounts[entry.Key] = (limiterAmount / ratio1) * propsUsed[entry.Key];
                        }
                    }

                    //Set the fuel amounts used for display later
                    fuelUsed = new Dictionary<string, float>(propAmounts);

                    //Delta-V is all about mass differences, so we need to know exactly how much we used
                    float massRemoved = 0;
                    //Loop over the parts and the resources contained, removing what we need
                    foreach (ProtoPartSnapshot p in vessel.protoVessel.protoPartSnapshots)
                        foreach (ProtoPartResourceSnapshot r in p.resources)
                            if (propsUsed.ContainsKey(r.resourceName))
                            {
                                float density = PartResourceLibrary.Instance.GetDefinition(r.resourceName).density;
                                float amountInPart = float.Parse(r.resourceValues.GetValue("amount"));
                                //If there's more in the part then what we need, reduce what's in the part and set the amount we need to 0
                                if (amountInPart > propAmounts[r.resourceName])
                                {
                                    massRemoved += propAmounts[r.resourceName] * density;
                                    amountInPart -= propAmounts[r.resourceName];
                                    propAmounts[r.resourceName] = 0;
                                }
                                //If there's less in the part than what we need, drain the part and lower the amount we need by that much
                                else
                                {
                                    massRemoved += amountInPart * density;
                                    propAmounts[r.resourceName] -= amountInPart;
                                    amountInPart = 0;
                                }
                                //Set the new fuel values in the part (the ONLY time we modify the recovered stage)
                                r.resourceValues.SetValue("amount", amountInPart.ToString());
                                if (r.resourceRef != null)
                                    r.resourceRef.amount = amountInPart;
                            }
                    //Calculate the total delta-v expended.
                    double totaldV = netISP * 9.81 * Math.Log(totalMass / (totalMass - massRemoved));
                    //Divide that by 2.5 and subtract it from the velocity after parachutes.
                    finalVelocity -= (float)(totaldV / 2.5);
                }
            }
            //Hopefully we removed enough fuel to land!
            Debug.Log("[SR] Final Vt: " + finalVelocity);
            return finalVelocity;
        }


        //This determines whether the Stage is destroyed by reentry heating (through a non-scientific method)
        //Note: Does not always return the same value because of the Random. Check if burnedUp is true instead!
        private bool DetermineIfBurnedUp()
        {
            //Check to see if Deadly Reentry is installed (check the loaded assemblies for DeadlyReentry.ReentryPhysics (namespace.class))
          /*  bool DeadlyReentryInstalled = AssemblyLoader.loadedAssemblies
                    .Select(a => a.assembly.GetExportedTypes())
                    .SelectMany(t => t)
                    .FirstOrDefault(t => t.FullName == "DeadlyReentry.ReentryPhysics") != null;*/

            //For 1.0, check if the heating percent is > 0 (later we'll want to scale with that value)
            bool DeadlyReentryInstalled = HighLogic.CurrentGame.Parameters.Difficulty.ReentryHeatScale > 0;

            //Holder for the chance of burning up in atmosphere (through my non-scientific calculations)
            float burnChance = 0f;
            //If DR is installed, the DRMaxVelocity setting is above 0, and the surface speed is above the DRMaxV setting then we calculate the burnChance
            if (DeadlyReentryInstalled && Settings.instance.DeadlyReentryMaxVelocity > 0 && vessel.srfSpeed > Settings.instance.DeadlyReentryMaxVelocity)
            {
                //the burnChance is 2% per 1% that the surface speed is above the DRMaxV
                burnChance = (float)(2 * ((vessel.srfSpeed / Settings.instance.DeadlyReentryMaxVelocity) - 1));
                //Log a message alerting us to the speed and the burnChance
                Debug.Log("[SR] DR velocity exceeded (" + vessel.srfSpeed + "/" + Settings.instance.DeadlyReentryMaxVelocity + ") Chance of burning up: " + burnChance);
            }

            if (burnChance == 0) return false;

            //Holders for the total amount of ablative shielding available, and the maximum total
            float totalHeatShield = 0f, maxHeatShield = 0f;
            foreach (ProtoPartSnapshot p in vessel.protoVessel.protoPartSnapshots)
            {
                if (p.modules.Find(mod => mod.moduleName == "ModuleAblator") != null)
                {
                    //Grab the heat shield module
                    ProtoPartModuleSnapshot heatShield = p.modules.First(mod => mod.moduleName == "ModuleAblator");
                    //Determine what type of shielding is in use
                   /* String ablativeType = heatShield.moduleValues.GetValue("ablative");
                    //Hopefully it's AblativeShielding, because that's what we want
                    if (ablativeType == "AblativeShielding")
                    {
                        //Determine the amount of shielding remaining
                        float shieldRemaining = float.Parse(p.resources.Find(r => r.resourceName == ablativeType).resourceValues.GetValue("amount"));
                        //And the maximum amount of shielding
                        float maxShield = float.Parse(p.resources.Find(r => r.resourceName == ablativeType).resourceValues.GetValue("maxAmount"));
                        //Add those to the totals for the craft
                        totalHeatShield += shieldRemaining;
                        maxHeatShield += maxShield;
                    }
                    else //Non-ablative shielding. Add a semi-random amount of shielding.
                    {
                        //We add 400 to each. This is so there's still a chance of failure
                        totalHeatShield += 400;
                        maxHeatShield += 400;
                    }*/

                    //For stock 1.0
                    //Determine the amount of shielding remaining
                    float shieldRemaining = float.Parse(p.resources.Find(r => r.resourceName == "Ablator").resourceValues.GetValue("amount"));
                    //And the maximum amount of shielding
                    float maxShield = float.Parse(p.resources.Find(r => r.resourceName == "Ablator").resourceValues.GetValue("maxAmount"));
                    //Add those to the totals for the craft
                    totalHeatShield += shieldRemaining;
                    maxHeatShield += maxShield;

                }
            }
            //Assume we're not going to burn up until proven that we will
            bool burnIt = false;
            //Well, we can't burn up unless the chance of doing so is greater than 0
            if (burnChance > 0)
            {
                //If there's heatshields on the vessel then reduce the chance by the current total/the max. Aka, up to 100%
                if (maxHeatShield > 0)
                    burnChance -= (totalHeatShield / maxHeatShield);
                //Pick a random number between 0 and 1
                System.Random rand = new System.Random();
                double choice = rand.NextDouble();
                //If that's less than or equal to the chance of burning, then we burn (25% chance = 0.25, random must be below 0.25)
                burnIt = (choice <= burnChance);
                //Once again, more log messages to help with debugging of people's issues
                Debug.Log("[SR] Burn chance: " + burnChance + " rand: " + choice + " burning? " + burnIt);
            }
            return burnIt;
        }

        //This calculates and sets the three recovery percentages (Recovery, Distance, and Speed Percents) along with the distance from KSC
        private void SetRecoveryPercentages()
        {
            //If we're using the Flat Rate model then we need to check for control
            if (Settings.instance.FlatRateModel)
            {
                //Assume uncontrolled until proven controlled
                bool stageControllable = vessel.protoVessel.wasControllable;
                //Cycle through all of the parts on the ship (well, ProtoPartSnaphsots)
                /*foreach (ProtoPartSnapshot pps in vessel.protoVessel.protoPartSnapshots)
                {
                    //Search through the Modules on the part for one called ModuleCommand and check if the crew count in the part is greater than or equal to the minimum required for control
                    if (pps.modules.Find(module => (module.moduleName == "ModuleCommand" && ((ModuleCommand)module.moduleRef).minimumCrew <= pps.protoModuleCrew.Count)) != null)
                    {
                        //Congrats, the stage is controlled! We can stop looking now.
                        stageControllable = true;
                        break;
                    }
                }*/
                //This is a fun trick for one-liners. The SpeedPercent is equal to 1 if stageControllable==true or the RecoveryModifier saved in the settings if that's false.
                SpeedPercent = stageControllable ? 1.0f : Settings.instance.RecoveryModifier;
                //If the speed is too high then we set the recovery due to speed to 0
                SpeedPercent = Vt < Settings.instance.CutoffVelocity ? SpeedPercent : 0;
            }
            //If we're not using Flat Rate (thus using Variable Rate) then we have to do a bit more work to get the SpeedPercent
            else
                SpeedPercent = GetVariableRecoveryValue(Vt);

            //Calculate the distance from KSC in meters
            KSCDistance = (float)SpaceCenter.Instance.GreatCircleDistance(SpaceCenter.Instance.cb.GetRelSurfaceNVector(vessel.protoVessel.latitude, vessel.protoVessel.longitude));
            //Calculate the max distance from KSC (half way around a circle the size of Kerbin)
            double maxDist = SpaceCenter.Instance.cb.Radius * Math.PI;

            int TSUpgrades = StageRecovery.BuildingUpgradeLevel(SpaceCenterFacility.TrackingStation);
            if (TSUpgrades == 0)
                maxDist *= (0.5);
            else if (TSUpgrades == 1)
                maxDist *= (0.75);

            //Get the reduction in returns due to distance (0.98 at KSC, .1 at maxDist)
            if (Settings.instance.DistanceOverride < 0)
                DistancePercent = Mathf.Lerp(0.98f, 0.1f, (float)(KSCDistance / maxDist));
            else
                DistancePercent = Settings.instance.DistanceOverride;
            //Combine the modifier from the velocity and the modifier from distance together
            RecoveryPercent = SpeedPercent * DistancePercent;
        }

        //This populates the dictionary of Recovered Parts and the dictionary of Costs, along with total funds returns (original, modified, fuel, and dry)
        private void SetPartsAndFunds()
        {
            foreach (ProtoPartSnapshot pps in vessel.protoVessel.protoPartSnapshots)
            {
                //Holders for the "out" below
                float dryCost, fuelCost;
                //Stock function for taking a ProtoPartSnapshot and the corresponding AvailablePart (aka, partInfo) and determining the value 
                //of the fuel contained and base part. Whole thing returns the combined total, but we'll do that manually
                ShipConstruction.GetPartCosts(pps, pps.partInfo, out dryCost, out fuelCost);
                //Set the dryCost to 0 if it's less than 0 (also could be done with dryCost = Math.Max(0, dryCost);)
                dryCost = dryCost < 0 ? 0 : dryCost;
                //Same for the fuelCost
                fuelCost = fuelCost < 0 ? 0 : fuelCost;

                //The unmodified returns are just the costs for the part added to the others
                FundsOriginal += dryCost + fuelCost;

                //Now we add the parts to the Dictionaries for display later
                //If the part title (the nice common name, like "Command Pod Mk1" as opposed to the name which is "mk1pod") isn't in the dictionary, add a new element
                if (!PartsRecovered.ContainsKey(pps.partInfo.title))
                {
                    //Add the title and qty=1 to the PartsRecovered
                    PartsRecovered.Add(pps.partInfo.title, 1);
                    //And the title and modified dryCost to the Costs
                    Costs.Add(pps.partInfo.title, dryCost);
                }
                else
                {
                    //If it is in the dictionary already, just increment the qty. We already know the cost.
                    ++PartsRecovered[pps.partInfo.title];
                }

                //Multiply by the RecoveryPercent
                dryCost *= RecoveryPercent;
                fuelCost *= RecoveryPercent;

                //The FundsReturned is the sum of the current FundsReturned plus the part cost and fuel cost
                FundsReturned += dryCost + fuelCost;
                DryReturns += dryCost;
                FuelReturns += fuelCost;

            }
            //Add refunds for the stage
            if (FundsReturned > 0 && recovered)
                StageRecovery.AddFunds(FundsReturned);
        }

        //This method performs Science recovery and populates the ScienceExperiments list
        private float RecoverScience()
        {
            //We'll return the total at the end
            float totalScience = 0;
            //Go through the parts
            foreach (ProtoPartSnapshot p in vessel.protoVessel.protoPartSnapshots)
            {
                //Go through the modules on each part
                foreach (ProtoPartModuleSnapshot pm in p.modules)
                {
                    ConfigNode node = pm.moduleValues;
                    //Find the ones with the name "ScienceData
                    if (node.HasNode("ScienceData"))
                    {
                        //And loop through them
                        foreach (ConfigNode subjectNode in node.GetNodes("ScienceData"))
                        {
                            //Get the ScienceSubject from the subjectID
                            ScienceSubject subject = ResearchAndDevelopment.GetSubjectByID(subjectNode.GetValue("subjectID"));
                            //Get the amount of data saved
                            float amt = float.Parse(subjectNode.GetValue("data"));
                            String title = subject.title;
                            //And submit that data with the subjectID to the R&D center, getting the amount earned back
                            float science = ResearchAndDevelopment.Instance.SubmitScienceData(amt, subject, 1f);
                            //Add the amount earned to the total earned
                            totalScience += science;
                            //For display we'll keep the title, amt, and science earned in one string
                            //ie: 5 Data from Crew Report at LaunchPad: 8 Science
							string display = "<#6DCFF6>©" + amt + "</> Data from " + title + ": <#6DCFF6>" + science + "</> science";
                            ScienceExperiments.Add(display);
                        }
                    }
                }
            }
            //Return the total
            return totalScience;
        }

        //This recovers Kerbals on the Stage, returning the list of their names
        private List<String> RecoverKerbals()
        { 
            //Currently causing Kerbals to lose exp!
            List<String> kerbals = new List<string>();
            //If there's no crew, why look?
            if (vessel.protoVessel.GetVesselCrew().Count > 0)
            {
                //Recover the kerbals and get their names
                foreach (ProtoCrewMember pcm in vessel.protoVessel.GetVesselCrew())
                {
                    //Yeah, that's all it takes to recover a kerbal. Set them to Available from Assigned
                    if (recovered && Settings.instance.RecoverKerbals)
                    {
                        pcm.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                        //pcm.experienceTrait.Config.
                        FlightLog.Entry deathEntry = pcm.careerLog.Entries.Find(e => e.type == "Die");
                        if (deathEntry != null)
                        {
                            Debug.Log("[SR] Recovered kerbal registered as dead. Attempting to repair.");
                            int flightNum = deathEntry.flight;
                            pcm.careerLog.Entries.Remove(deathEntry);
                            FlightLog.Entry landing = new FlightLog.Entry(flightNum, FlightLog.EntryType.Land, "Kerbin");
                            FlightLog.Entry recovery = new FlightLog.Entry(flightNum, FlightLog.EntryType.Recover);
                            pcm.careerLog.AddEntry(landing);
                            pcm.careerLog.AddEntry(recovery);
                        }
                    }
                    kerbals.Add(pcm.name);
                }
            }
            return kerbals;
        }



        //Fires the correct API event
        public void FireEvent()
        {
            //Create an array with the Percent returned due to Speed (aka, damage), the Funds Returned, and the Science Recovered
            float[] infoArray = new float[] { SpeedPercent, FundsReturned, ScienceRecovered };
            //Fire the RecoverySuccessEvent if recovered or the RecoveryFailureEvent if destroyed
            if (recovered)
                APIManager.instance.RecoverySuccessEvent.Fire(vessel, infoArray, ReasonForFailure);
            else
                APIManager.instance.RecoveryFailureEvent.Fire(vessel, infoArray, ReasonForFailure);
        }

        //Adds the Stage to the appropriate List (Recovered vs Destroyed)
        public void AddToList()
        {
            if (recovered)
                Settings.instance.RecoveredStages.Add(this);
            else
                Settings.instance.DestroyedStages.Add(this);
        }

        //Removes the Stage from the corresponding List
        public void RemoveFromList()
        {
            if (recovered)
                Settings.instance.RecoveredStages.Remove(this);
            else
                Settings.instance.DestroyedStages.Remove(this);
        }

        //This posts either a success or failure message to the Stock Message system
        public void PostStockMessage()
        {
            StringBuilder msg = new StringBuilder();
            if (recovered && Settings.instance.ShowSuccessMessages)
            {
                //Start adding some in-game display messages about the return
				msg.AppendLine("<#8BED8B>Stage '" + StageName + "' recovered " + Math.Round(KSCDistance / 1000, 2) + " km from KSC</>");

                for (int i = 0; i < PartsRecovered.Count; i++)
                {
					msg.AppendLine(PartsRecovered.Values.ElementAt(i) + " x " + PartsRecovered.Keys.ElementAt(i) + ": <#B4D455>£" + (PartsRecovered.Values.ElementAt(i) * Costs.Values.ElementAt(i) * RecoveryPercent) +"</>");
                }
				msg.AppendLine("\n");
                //List the percent returned and break it down into distance and speed percentages
				msg.AppendLine("Recovery percentage: <#8BED8B>" + Math.Round(100 * RecoveryPercent, 1) + "%</>");
				msg.AppendLine("<#8BED8B>" + Math.Round(100 * DistancePercent, 1) + "%</> distance");
				msg.AppendLine("<#8BED8B>" + Math.Round(100 * SpeedPercent, 1) + "%</> speed");
				msg.AppendLine("");
                //List the total refunds for parts, fuel, and the combined total
				msg.AppendLine("Total refunded for parts: <#B4D455>£" + DryReturns + "</>");
				msg.AppendLine("Total refunded for fuel: <#B4D455>£" + FuelReturns + "</>");
				msg.AppendLine("Total refunds: <#B4D455>£" + FundsReturned + "</>");

                if (KerbalsOnboard.Count > 0)
                {
                    msg.AppendLine("\nKerbals recovered:");
                    foreach (string kerbal in KerbalsOnboard)
                        msg.AppendLine("<#E0D503>" + kerbal +"</>");
                }
                if (ScienceExperiments.Count > 0)
                {
                    msg.AppendLine("\nScience recovered: "+ScienceRecovered);
                    foreach (string science in ScienceExperiments)
						msg.AppendLine(science);
                }

                //By this point all the real work is done. Now we just display a bit of information
                msg.AppendLine("\nAdditional Information:");
                //Display which module was used for recovery
                    msg.AppendLine(ParachuteModule + " Module used.");
                //Display the terminal velocity (Vt) and what is needed to have any recovery
                if (Settings.instance.FlatRateModel)
					msg.AppendLine("Terminal velocity of <#8BED8B>" + Math.Round(Vt, 2) + "</> (less than " + Settings.instance.CutoffVelocity + " needed)");
                else
					msg.AppendLine("Terminal velocity of <#8BED8B>" + Math.Round(Vt, 2) + "</> (less than " + Settings.instance.HighCut + " needed)");

                //Setup and then post the message
                MessageSystem.Message m = new MessageSystem.Message("Stage Recovered", msg.ToString(), MessageSystemButton.MessageButtonColor.BLUE, MessageSystemButton.ButtonIcons.MESSAGE);
                MessageSystem.Instance.AddMessage(m);
            }
            else if (!recovered && Settings.instance.ShowFailureMessages)
            {
                msg.AppendLine("<#FF9900>Stage '" + StageName + "' destroyed " + Math.Round(KSCDistance / 1000, 2) + " km from KSC</>");
                msg.AppendLine("Stage contains these parts:");
                for (int i = 0; i < PartsRecovered.Count; i++)
                {
                    msg.AppendLine(PartsRecovered.Values.ElementAt(i) + " x " + PartsRecovered.Keys.ElementAt(i));
                }
                //If we're career mode (MONEY!) then we also let you know the (why do I say 'we'? It's only me working on this) total cost of the parts
                if (HighLogic.CurrentGame.Mode == Game.Modes.CAREER)
                {
                    float totalCost = 0;
                    //Cycle through all the parts
                    foreach (ProtoPartSnapshot pps in vessel.protoVessel.protoPartSnapshots)
                    {
                        float dry, wet;
                        //Add the max of 0 or the part cost (in case they're negative, looking at you MKS and TweakScale!)
                        totalCost += Math.Max(ShipConstruction.GetPartCosts(pps, pps.partInfo, out dry, out wet), 0);
                    }
                    //Alert the user to what the total value was (without modifiers)
                    msg.AppendLine("It was valued at <#FF9900>" + totalCost + "</> Funds."); //ED0B0B
                }

                //By this point all the real work is done. Now we just display a bit of information
                msg.AppendLine("\nAdditional Information:");
                //Display which module was used for recovery
                msg.AppendLine(ParachuteModule + " Module used.");
                //Display the terminal velocity (Vt) and what is needed to have any recovery
                msg.AppendLine("Terminal velocity of <#FF9900>" + Math.Round(Vt, 2) + "</> (less than " + (Settings.instance.FlatRateModel ? Settings.instance.CutoffVelocity : Settings.instance.HighCut) + " needed)");
                
                //If it failed because of burning up (can be in addition to speed) then we'll let you know
                if (burnedUp)
                    msg.AppendLine("The stage burned up in the atmosphere! It was travelling at " + vessel.srfSpeed + " m/s.");

                //Now we actually create and post the message
                MessageSystem.Message m = new MessageSystem.Message("Stage Destroyed", msg.ToString(), MessageSystemButton.MessageButtonColor.RED, MessageSystemButton.ButtonIcons.MESSAGE);
                MessageSystem.Instance.AddMessage(m);
            }
        }

        //When using the variable recovery rate we determine the rate from a negative curvature quadratic with y=100 at velocity=lowCut and y=0 at vel=highCut.
        //No other zeroes are in that range. Check this github issue for an example and some more details: https://github.com/magico13/StageRecovery/issues/1
        public static float GetVariableRecoveryValue(float v)
        {
            //We're following ax^2+bx+c=recovery
            //We know that -b/2a=LowCut since that's the only location where the derivative of the quadratic is 0 (the max)
            //Starting conditions: x=lowCut y=100, x=highCut y=0. Combined with the above info, we can calculate everything
            float x0 = Settings.instance.LowCut;
            float x1 = Settings.instance.HighCut;
            //If we're below the low cut, then return 1 (100%)
            if (v < x0) return 1;
            //If we're above the high cut, return 0
            if (v > x1) return 0;
            //Well, we're inbetween. Calculate the 'a' parameter.
            float a = (float)(-100 / (Math.Pow(x1, 2) - 2 * x0 * x1 + Math.Pow(x0, 2)));
            //From 'a' we can calculate 'b'. 
            float b = -2 * a * x0;
            //And then 'c'
            float c = (float)(a * Math.Pow(x0, 2) + 100);
            //The return value is now a simple matter. The function is setup for percentages but we want to return a float between 0 and 1, so divide by 100
            float ret = (float)(a * Math.Pow(v, 2) + b * v + c)/100f;
            return ret;
        }
    }
}
