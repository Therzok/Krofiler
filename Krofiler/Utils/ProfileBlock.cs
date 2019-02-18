using System;
using System.Diagnostics;

namespace Krofiler.Utils
{
	public class ProfileBlock : IDisposable
	{
		readonly Stopwatch sw = new Stopwatch();
		readonly string header;

		public ProfileBlock(string header)
		{
			this.header = header;

			Console.WriteLine("[START]: {0}", header);
		}

		public void Dispose()
		{
			Console.WriteLine("[END]: {0}ms {1}", sw.ElapsedMilliseconds.ToString(), header);
		}
	}
}
