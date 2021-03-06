﻿using System.Reflection;
using Verse;
using UnityEngine;
using Harmony;

namespace UseBedrolls
{
	public class Mod : Verse.Mod
	{
		public Mod(ModContentPack content) : base(content)
		{
			// initialize settings
			// GetSettings<Settings>();
#if DEBUG
			HarmonyInstance.DEBUG = true;
#endif
			HarmonyInstance harmony = HarmonyInstance.Create("Uuugggg.rimworld.UseBedrolls.main");
			harmony.PatchAll(Assembly.GetExecutingAssembly());
		}

		// public override void DoSettingsWindowContents(Rect inRect)
		// {
		//	 base.DoSettingsWindowContents(inRect);
		//	 GetSettings<Settings>().DoWindowContents(inRect);
		// }

		// public override string SettingsCategory()
		// {
	  // return "TD.UseBedrolls".Translate();
		// }
	}
}