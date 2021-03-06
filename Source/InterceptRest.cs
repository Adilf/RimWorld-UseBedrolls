﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Reflection.Emit;
using Verse;
using Verse.AI;
using RimWorld;
using Harmony;

namespace UseBedrolls
{
	[HarmonyPatch(typeof(JobGiver_GetRest), "TryGiveJob")]
	static class InterceptRest
	{
		static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			MethodInfo FindBedrollJobInfo = AccessTools.Method(typeof(InterceptRest), nameof(InterceptRest.FindBedrollJob));

			bool foundNew = false;
			foreach(CodeInstruction i in instructions)
			{
				//ldsfld       class Verse.JobDef RimWorld.JobDefOf::LayDown
				yield return i;
				if (i.opcode == OpCodes.Newobj)
				{
					if (!foundNew) foundNew = true;
					else
					{
						yield return new CodeInstruction(OpCodes.Ldarg_1);
						yield return new CodeInstruction(OpCodes.Call, FindBedrollJobInfo);
					}
				}
			}
		}

		static Job FindBedrollJob(Job fallbackJob, Pawn pawn)
		{	
			if (!pawn.IsColonistPlayerControlled) return fallbackJob;
			Log.Message(pawn + " looking for inventory beds");

			MinifiedThing invBed = (MinifiedThing)FindMinifiedBed(pawn);
			if (invBed == null)	return fallbackJob;
			Log.Message(pawn + " found " + invBed);

			Map map = pawn.Map;
			Building_Bed bed = (Building_Bed)invBed.GetInnerIfMinified();

			Func<IntVec3, Rot4, bool> cellValidatorDir = delegate (IntVec3 c, Rot4 direction)
			{
				if (RegionAndRoomQuery.RoomAtFast(c,map).isPrisonCell != pawn.IsPrisoner)
					return false;

				if (!GenConstruct.CanPlaceBlueprintAt(invBed.GetInnerIfMinified().def, c, direction, map).Accepted)
					return false;

                if (c.IsForbidden(pawn))
                    return false;

                for (CellRect.CellRectIterator iterator = GenAdj.OccupiedRect(c, direction, bed.def.size).GetIterator();
                        !iterator.Done(); iterator.MoveNext())
                {
                    foreach (Thing t in iterator.Current.GetThingList(map))
                        if (!(t is Pawn) && GenConstruct.BlocksConstruction(bed, t))
                            return false;
                    if (!(map.zoneManager.ZoneAt(c) is null))
                        return false;
                }

				return true;
			};

			// North/East would be redundant, except for cells on edge ; oh well, too much code to handle that
			Predicate<IntVec3> cellValidator = c => cellValidatorDir(c, Rot4.South) || cellValidatorDir(c, Rot4.West);
			
			Predicate<IntVec3> goodCellValidator = c =>
				!RegionAndRoomQuery.RoomAt(c, map).PsychologicallyOutdoors && cellValidator(c);

			IntVec3 placePosition = IntVec3.Invalid;
			IntVec3 root = pawn.Position;
			TraverseParms trav = TraverseParms.For(pawn);
			if (!CellFinder.TryFindRandomReachableCellNear(root, map, 4, trav, goodCellValidator, null, out placePosition))
				if (!CellFinder.TryFindRandomReachableCellNear(root, map, 12, trav, goodCellValidator, null, out placePosition))
					if (!CellFinder.TryFindRandomReachableCellNear(root, map, 4, trav, cellValidator, null, out placePosition))
						CellFinder.TryFindRandomReachableCellNear(root, map, 12, trav, cellValidator, null, out placePosition);

			if (placePosition.IsValid)
			{
				Rot4 dir = cellValidatorDir(placePosition, Rot4.South) ? Rot4.South : Rot4.West;
				Blueprint_Install blueprint = GenConstruct.PlaceBlueprintForInstall(invBed, placePosition, map, dir, pawn.Faction);

				Log.Message(pawn + " placing " + blueprint + " at " + placePosition);

				return new Job(JobDefOf.PlaceBedroll, invBed, blueprint)
				{
					haulMode = HaulMode.ToContainer
				};
			}
			Log.Message(pawn + " couldn't find place for " + invBed);

			return fallbackJob;
		}

		public static Thing FindMinifiedBed(Pawn pawn)
		{
			//inventory bed
			if (InventoryBed(pawn) is Thing invBed)
				return invBed;

			//minified bed laying around
			if (GroundMinifedBed(pawn) is Thing groundBed)
				return groundBed;

			//bed on another pawn? last chance.
			return SharedInventoryBed(pawn);
		}

		public static Thing InventoryBed(Pawn pawn)
		{
			return pawn.inventory.innerContainer.FirstOrDefault(tmini => tmini.GetInnerIfMinified() is Building_Bed b && b.def.building.bed_humanlike);
		}

		public static Thing GroundMinifedBed(Pawn sleepy_pawn)
		{
			Predicate<Thing> validator = delegate (Thing t)
			{
				return t.GetInnerIfMinified() is Building_Bed b && b.def.building.bed_humanlike && !t.IsForbidden(sleepy_pawn) && sleepy_pawn.CanReserveAndReach(t, PathEndMode.ClosestTouch, Danger.None);
			};
			List<Thing> groundBeds = sleepy_pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.MinifiedThing).FindAll(t => validator(t));
			if (groundBeds.NullOrEmpty())
				return null;
			return groundBeds.MinBy(t => DistanceTo(t, sleepy_pawn));
		}

		public static Thing SharedInventoryBed(Pawn pawn)
		{
			Thing spareBed = null;
			Pawn pawnWithSpareBed = PawnWithSpareBed(pawn);
			if ((pawnWithSpareBed != null))
			{
				spareBed = InventoryBed(pawnWithSpareBed);
				//dropping here is fine since this isn't a commanded job, shouldn't get multiple calls to TryGiveJob
				pawnWithSpareBed.inventory.innerContainer.TryDrop(spareBed, ThingPlaceMode.Near, out spareBed);
				Log.Message(pawnWithSpareBed + " dropped bed at " + spareBed.Position);
			}
			return spareBed;
		}
		public static Pawn PawnWithSpareBed(Pawn sleepyPawn)
		{
			TraverseParms traverseParams = TraverseParms.For(sleepyPawn, Danger.Deadly, TraverseMode.ByPawn, false);
			Predicate<Pawn> surplusFinder = delegate (Pawn p) {
				int count = p.CountBeds();
				Log.Message(p + " has " + count + " beds");
				if (count > 1 || (count > 0 && SingleInvBedIsSpare(p, sleepyPawn)))
				{
					Log.Message(p + " has can spare some");
					if (sleepyPawn.Map.reachability.CanReach(sleepyPawn.Position, p, PathEndMode.ClosestTouch, traverseParams))
					{
						Log.Message(sleepyPawn + " can reach " + p);
						return true;
					}
				}
				return false;
			};
			List<Pawn> surplusPawns = sleepyPawn.Map.mapPawns.SpawnedPawnsInFaction(sleepyPawn.Faction).FindAll(surplusFinder);
			if (surplusPawns.NullOrEmpty())
				return null;

			Log.Message("surplusPawns are " + surplusPawns.ToStringSafeEnumerable());
			Pawn generousPawn = surplusPawns.MinBy(p => DistanceTo(p,sleepyPawn));
			Log.Message("generousPawn is " + generousPawn);
			return generousPawn;
		}

		public static bool SingleInvBedIsSpare(Pawn p, Pawn sleepyPawn)
		{
			return p.RaceProps.Animal || p.ownership.OwnedBed != null || LovePartnerRelationUtility.LovePartnerRelationExists(sleepyPawn, p);
		}

		public static int DistanceTo(Thing t1, Thing t2)
		{
			return (t1.Position - t2.Position).LengthManhattan;
		}
	}
}
