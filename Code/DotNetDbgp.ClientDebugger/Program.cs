using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DotNetDbgp.ClientDebugger {
	public class Program {
		static void Main(String[] args) {
			//Console.WriteLine("Arguments:");
			//for(var i = 0; i < args.Length; i++) {
			//	Console.WriteLine(String.Format("{0}: [{1}]", i, args[i]));
			//}

			try {
				int port = 9000;
				int? pid = null;

				if (args.Length > 0) {
					int argPid;
					if (int.TryParse(args[0], out argPid)) {
						pid = argPid;
					} else {
						pid = FindPidForAppPool(args[0]);
					}
					if (args.Length > 1) {
						port = int.Parse(args[1]);
					}
				}

				if (pid == null) {
					pid = FindPidForAppPool("DefaultAppPool");
				}
				if (pid == null) {
					throw new Exception("Unknown pid");
				}

				Console.WriteLine("PID: "+System.Diagnostics.Process.GetCurrentProcess().Id);

				new Client(pid.Value, port).Start();
			} catch (Exception e) {
				Console.WriteLine(e.ToString());
				Console.WriteLine("Press any key to continue...");
				Console.ReadKey();
			}
		}

		private static int? FindPidForAppPool(String appPoolName) {
			var appPools = new Microsoft.Web.Administration.ServerManager().ApplicationPools;

			Console.WriteLine("AppPools:");
			foreach(var appPoolI in appPools) {
				Console.WriteLine(appPoolI.Name);
			}

			var appPool = appPools.SingleOrDefault(i => i.Name == appPoolName);
			if (appPool == null) { return null; }

			var worker = appPool.WorkerProcesses.FirstOrDefault();
			if (worker == null) { return null; }

			return worker.ProcessId;
		}
	}
}
