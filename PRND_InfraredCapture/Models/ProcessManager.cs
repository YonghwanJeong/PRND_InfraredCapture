using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static MCProtocol.Mitsubishi;

namespace PRND_InfraredCapture.Models
{
    public class ProcessManager
    {
        #region Instance 선언
        private static ProcessManager _Instance;
        public static ProcessManager Instance
        {
            get
            {
                lock (LockObject)
                {
                    if (_Instance == null)
                        _Instance = new ProcessManager();
                    return _Instance;
                }
            }
        }
        private static readonly object LockObject = new object();
        #endregion

        public readonly string SYSTEM_PARAM_PATH = Path.Combine(Environment.CurrentDirectory, "SystemParam.insp");
        public SystemParameter SystemParam { get; set; } = new SystemParameter();


        private McProtocolTcp _MCProtocolTCP;
        private readonly SemaphoreSlim _mcProtocolLock = new SemaphoreSlim(1, 1);

        private CancellationTokenSource _cts;
        private Task _PLCSignalMonitoringTask;

        public void SaveSystemParameter() => SystemParam.Save(SYSTEM_PARAM_PATH);
        public void LoadSystemParameter() => SystemParam.Load(SYSTEM_PARAM_PATH);

        public ProcessManager()
        {
            if (File.Exists(SYSTEM_PARAM_PATH) == false)
                SaveSystemParameter();
            else
                LoadSystemParameter();
        }

        public void StartOnline()
        {

        }
        public void StopOnline()
        {

        }

    }
}
