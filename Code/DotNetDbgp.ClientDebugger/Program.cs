using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DotNetDbgp.ClientDebugger {
	public class Program {
		static void Main(String[] args) {
            int? pid = null;
            if (args.Length > 0) {
                int argPid;
                if (int.TryParse(args[0], out argPid)) {
                    pid = argPid;
                } else {
                    pid = FindPidForAppPool(args[0]);
                }
            }
            if (pid == null) {
                pid = FindPidForAppPool("ASP.NET v4.0");
            }
            if (pid == null) {
                throw new Exception("Unknown pid");
            }
			new Client(pid.Value).Start();
		}

        private static int? FindPidForAppPool(String appPoolName) {
            var appPools = new Microsoft.Web.Administration.ServerManager().ApplicationPools;

            var appPool = appPools.SingleOrDefault(i => i.Name == appPoolName);
            if (appPool == null) { return null; }

            var worker = appPool.WorkerProcesses.FirstOrDefault();
            if (worker == null) { return null; }

            return worker.ProcessId;
        }
	}
}
