﻿using System;
using System.Linq;
using Krofiler;

namespace Prototype
{
	class MainClass
	{
		public static void Main(string[] args)
		{
			var session = KrofilerSession.CreateFromFile("/Users/davidkarlas/Documents/3SnapshotsWithOpeningAndCLosingProject.mlpd");
			session.NewHeapshot += (s, e) => {
				var hs = e;
				hs.GetShortestPathToRoot(hs.ObjectsInfoMap.Keys.First());
				Console.WriteLine (new string ('=', 30));
				Console.WriteLine ("Hs:" + e.Name);
				foreach (var obj in hs.ObjectsInfoMap.Values) {
					if(obj.ReferencesFrom.Count==0 && !hs.Roots.ContainsKey(obj.ObjAddr)){
						Console.WriteLine($"{obj.ObjAddr} type:{s.GetTypeName(obj.TypeId)}");
					}
				}
				Console.WriteLine (new string ('=', 30));
			};
			session.StartParsing().Wait();
		}
	}
}
