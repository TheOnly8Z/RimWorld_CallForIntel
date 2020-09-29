using HarmonyLib;
using RimWorld;
using RimWorld.QuestGen;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;
using VFE_Settlers.Workers;

namespace CallForIntel
{

    [StaticConstructorOnStartup]
    static class MyPatcher
    {
        static MyPatcher()
        {
            Harmony harmony = new Harmony("rimworld.8z.callforintel");
            harmony.PatchAll();
        }

    }

    // A class that holds an action (function that generates intel) and a relative weight
    // Used to generate a intel for a specfic choice when multiple are possible
    public class WeightedIntel
    {
        public int weight;
        public Action action;

        public WeightedIntel(int weight, Action action)
        {
            this.weight = weight;
            this.action = action;
        }

        public static void Choose(List<WeightedIntel> intel, Faction faction)
        {
            int totalWeight = 0;
            foreach (WeightedIntel wi in intel)
            {
                totalWeight += wi.weight;
            }
            int i = Rand.Range(0, totalWeight);
            foreach(WeightedIntel wi in intel)
            {
                i = i - wi.weight;
                if (i <= 0)
                {
                    wi.action.Invoke();
                    return;
                }
            }

            // If we reach this point, that means we failed to run any intel
            // Best to pop a notification or something
            faction.lastTraderRequestTick = Find.TickManager.TicksGame;
            Find.LetterStack.ReceiveLetter("IntelFailedLabel", "IntelFailedDialouge".Translate(faction.leader).CapitalizeFirst(), LetterDefOf.NeutralEvent);

        }
    }

    [HarmonyPatch(typeof(FactionDialogMaker))]
    [HarmonyPatch("FactionDialogFor")]
    public static class FactionDialogMakerPatch
    {

        static string[] VikingHunts = { "VFEV_FenrirHunt", "VFEV_LothurrHunt", "VFEV_NjorunHunt", "VFEV_OdinHunt", "VFEV_ThrumboHunt" };

        static void GenerateIntelQuest(Faction faction, string name, int goodwill)
        {
            Slate slate = new Slate();
            slate.Set("points", StorytellerUtility.DefaultThreatPointsNow(Find.World));
            slate.Set("asker", faction.leader);
            Quest newQuest = QuestUtility.GenerateQuestAndMakeAvailable(DefDatabase<QuestScriptDef>.GetNamed(name), slate);
            QuestUtility.SendLetterQuestAvailable(newQuest);
            faction.lastTraderRequestTick = Find.TickManager.TicksGame;
            faction.TryAffectGoodwillWith(Faction.OfPlayer, -goodwill, canSendMessage: false, canSendHostilityLetter: true, "GoodwillChangedReason_RequestedIntel".Translate());
        }

        static void GenerateIntelIncident(Faction faction, string name, IncidentParms parms, int goodwill)
        {
            IncidentDef incident = DefDatabase<IncidentDef>.GetNamed(name);
            bool execute = incident.Worker.TryExecute(parms);
            if (execute)
            {
                faction.lastTraderRequestTick = Find.TickManager.TicksGame;
                faction.TryAffectGoodwillWith(Faction.OfPlayer, -goodwill, canSendMessage: false, canSendHostilityLetter: true, "GoodwillChangedReason_RequestedIntel".Translate());
            } else
            {
                // It is possible to fail a incident execution, in which case we do not charge goodwill
                faction.lastTraderRequestTick = Find.TickManager.TicksGame;
                Find.LetterStack.ReceiveLetter("IntelFailedLabel", "IntelFailedDialouge".Translate(faction.leader).CapitalizeFirst(), LetterDefOf.NeutralEvent);
            }

        }

        static void Postfix(Pawn negotiator, Faction faction, ref DiaNode __result)
        {
            TaggedString requestIntelString = "RequestIntel".Translate();

            // Cannot use if pawn has Social disabled
            if (negotiator.skills.GetSkill(SkillDefOf.Social).TotallyDisabled)
            {
                DiaOption failSkill = new DiaOption(requestIntelString);
                failSkill.Disable("WorkTypeDisablesOption".Translate(SkillDefOf.Social.label));
                __result.options.Insert(__result.options.Count - 1, failSkill);
                return;
            }

            // Cannot use if not ally
            if (faction.PlayerRelationKind != FactionRelationKind.Ally)
            {
                DiaOption failAlly = new DiaOption(requestIntelString);
                failAlly.Disable("MustBeAlly".Translate());
                __result.options.Insert(__result.options.Count - 1, failAlly);
                return;
            }

            // Cannot use if too soon (TODO: separate variable)
            int num = faction.lastTraderRequestTick + 240000 - Find.TickManager.TicksGame;
            if (num > 0)
            {
                DiaOption failTime = new DiaOption(requestIntelString);
                failTime.Disable("WaitTime".Translate(num.ToStringTicksToPeriod()));
                __result.options.Insert(__result.options.Count - 1, failTime);
                return;
            }

            bool VFE_Vikings = LoadedModManager.RunningModsListForReading.Any(x => x.PackageId == "oskarpotocki.vfe.vikings");
            bool VFE_Settlers = LoadedModManager.RunningModsListForReading.Any(x => x.PackageId == "oskarpotocki.vanillafactionsexpanded.settlersmodule");
            bool VFE_Medieval = LoadedModManager.RunningModsListForReading.Any(x => x.PackageId == "oskarpotocki.vanillafactionsexpanded.medievalmodule");
            bool GoExplore = LoadedModManager.RunningModsListForReading.Any(x => x.PackageId == "albion.goexplore");
            bool IsVikingClan = VFE_Vikings && (faction.def.defName == "VFE_VikingsClan" || faction.def.defName == "VFE_VikingsSlaver");
            bool IsSettlers = VFE_Settlers && (faction.def.defName == "SettlerCivil" || faction.def.defName == "SettlerRough");

            DiaOption diaOption = new DiaOption(requestIntelString);

            DiaNode nodeSent = new DiaNode("IntelSent".Translate(faction.leader).CapitalizeFirst());
            nodeSent.options.Add(new DiaOption("OK".Translate()){linkLateBind = () => FactionDialogMaker.FactionDialogFor(negotiator, faction)});

            DiaNode nodeChoose = new DiaNode("ChooseIntelKind".Translate(faction.leader).CapitalizeFirst());
            diaOption.link = nodeChoose;

            // Vikings will give you hunt quests
            if (IsVikingClan)
            {
                DiaOption optionHunt = new DiaOption("IntelKindHunt".Translate(10));
                optionHunt.link = nodeSent;
                optionHunt.action = delegate
                {
                    GenerateIntelQuest(faction, VikingHunts[Rand.Range(0, VikingHunts.Length)], 10);
                };
                nodeChoose.options.Add(optionHunt);
            }
            
            // Settlers will give you bounties
            if (IsSettlers) {
                DiaOption optionBounty = new DiaOption("IntelKindBounty".Translate(10));
                optionBounty.link = nodeSent;
                optionBounty.action = delegate
                {
                    GenerateIntelQuest(faction, "Settlers_Wanted", 10);
                };
                nodeChoose.options.Add(optionBounty);
            }

            // Combat quests
            DiaOption optionCombat = new DiaOption("IntelKindCombat".Translate(10));
            optionCombat.link = nodeSent;
            optionCombat.action = delegate
            {
                List<WeightedIntel> list = new List<WeightedIntel>();
                list.Add(new WeightedIntel(100, delegate
                {
                    GenerateIntelQuest(faction, "OpportunitySite_BanditCamp", 10);
                }));

                if (VFE_Settlers)
                {
                    list.Add(new WeightedIntel(50, delegate
                    {
                        IncidentParms parm = new IncidentParms();
                        GenerateIntelIncident(faction, "Settlers_CaravanRaid", parm, 10);
                    }));
                }

                if (GoExplore)
                {
                    list.Add(new WeightedIntel(50, delegate
                    {
                        IncidentParms parm = new IncidentParms();
                        GenerateIntelIncident(faction, "PrisonCampLGE", parm, 10);
                    }));
                }

                WeightedIntel.Choose(list, faction);
            };
            nodeChoose.options.Add(optionCombat);

            DiaOption optionTrade = new DiaOption("IntelKindTrade".Translate(15));
            optionTrade.link = nodeSent;
            optionTrade.action = delegate
            {
                GenerateIntelQuest(faction, "TradeRequest", 15);
            };
            nodeChoose.options.Add(optionTrade);

            DiaOption optionPlace = new DiaOption("IntelKindPlace".Translate(15));
            optionPlace.link = nodeSent;
            optionPlace.action = delegate
            {

                List<WeightedIntel> list = new List<WeightedIntel>();

                list.Add(new WeightedIntel(100, delegate
                {
                    GenerateIntelQuest(faction, "OpportunitySite_ItemStash", 15);
                }));

                if (VFE_Vikings)
                {
                    if (!IsVikingClan)
                    {
                        list.Add(new WeightedIntel(5, delegate
                        {
                            GenerateIntelQuest(faction, "VFEV_OpportunitySite_LegendaryGrave", 15);
                        }));
                    }
                    list.Add(new WeightedIntel(10, delegate
                    {
                        GenerateIntelQuest(faction, "VFEV_OpportunitySite_AncientRuin", 15);
                    }));
                }

                if (VFE_Medieval)
                {
                    list.Add(new WeightedIntel(5, delegate
                    {
                        IncidentParms parm = new IncidentParms();
                        GenerateIntelIncident(faction, "VFEM_Quest_MedievalTournament", parm, 15);
                    }));

                    list.Add(new WeightedIntel(30, delegate
                    {
                        GenerateIntelQuest(faction, "VFEV_OpportunitySite_CastleRuins", 15);
                    }));
                }

                if (GoExplore)
                {
                    list.Add(new WeightedIntel(10, delegate
                    {
                        IncidentParms parm = new IncidentParms();
                        GenerateIntelIncident(faction, "LostCityLGE", parm, 15);
                    }));

                    list.Add(new WeightedIntel(15, delegate
                    {
                        IncidentParms parm = new IncidentParms();
                        GenerateIntelIncident(faction, "AmbrosiaAnimalsLGE", parm, 15);
                    }));

                    list.Add(new WeightedIntel(2, delegate
                    {
                        IncidentParms parm = new IncidentParms();
                        GenerateIntelIncident(faction, "ShipCoreStartupLGE", parm, 15);
                    }));
                }

                WeightedIntel.Choose(list, faction);
            };
            nodeChoose.options.Add(optionPlace);

            DiaOption optionPawn = new DiaOption("IntelKindPawn".Translate(25));
            optionPawn.link = nodeSent;
            optionPawn.action = delegate
            {

                List<WeightedIntel> list = new List<WeightedIntel>();

                list.Add(new WeightedIntel(100, delegate
                {
                    GenerateIntelQuest(faction, "OpportunitySite_DownedRefugee", 25);
                }));

                list.Add(new WeightedIntel(25, delegate
                {
                    GenerateIntelQuest(faction, "ThreatReward_Raid_Joiner", 25);
                }));

                if (VFE_Vikings && !IsVikingClan)
                {
                    list.Add(new WeightedIntel(25, delegate
                    {
                        GenerateIntelQuest(faction, "VFEV_ThreatReward_Raid_SlavesJoiner", 25);
                    }));
                }

                WeightedIntel.Choose(list, faction);
            };
            nodeChoose.options.Add(optionPawn);

            DiaOption optionBack = new DiaOption("GoBack".Translate());
            optionBack.linkLateBind = () => FactionDialogMaker.FactionDialogFor(negotiator, faction);
            nodeChoose.options.Add(optionBack);

            // Insert before the last disconnect option
            __result.options.Insert(__result.options.Count - 1, diaOption);

        }
    }
}