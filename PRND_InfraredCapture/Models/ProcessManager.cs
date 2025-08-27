using System;
using CP.OptrisCam;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static MCProtocol.Mitsubishi;
using CP.OptrisCam.models;
using System.Windows;

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
        public bool IsOnlineMode { get; set; }
        public readonly string SYSTEM_PARAM_PATH = Path.Combine(Environment.CurrentDirectory, "SystemParam.insp");
        public SystemParameter SystemParam { get; set; } = new SystemParameter();

        public void SaveSystemParameter() => SystemParam.Save(SYSTEM_PARAM_PATH);
        public void LoadSystemParameter() => SystemParam.Load(SYSTEM_PARAM_PATH);

        public Action<string> OnCamLogsaved { get; set; }



        private McProtocolTcp _MCProtocolTCP;
        private readonly SemaphoreSlim _mcProtocolLock = new SemaphoreSlim(1, 1);

        private CancellationTokenSource _cts;
        private Task _PLCSignalMonitoringTask;

        private CamController _CamController;
        private LightCurtainComm _LightCurtain;



        public ProcessManager()
        {
            if (File.Exists(SYSTEM_PARAM_PATH) == false)
                SaveSystemParameter();
            else
                LoadSystemParameter();

            CamLogger.Instance.OnLogSavedAction += OnLogSaveAction;

        }

        private void OnLogSaveAction(string obj)
        {
            OnCamLogsaved?.Invoke(obj);
        }

        public void StartOnline()
        {
            if (IsOnlineMode)
                return;
            
            _CamController = new CamController(SystemParam.CamPathList);
            _LightCurtain = new LightCurtainComm(SystemParam.LightCurtainPortName, SystemParam.LightCurtainBaudRate);

            IsOnlineMode = true;

            ConnectCam();

            Logger.Instance.Print(Logger.LogLevel.INFO, $"Online 모드 시작", true);
        }
        public void StopOnline()
        {
            if (!IsOnlineMode)
                return;
            
            _CamController.DisconnectAll();

            IsOnlineMode = false;
            Logger.Instance.Print(Logger.LogLevel.INFO, $"Online 모드 정지", true);
        }
        public void StartLightCurtain()
        {
            _LightCurtain.Start(SystemParam.LightCurtainHeightOffset);
        }

        public void StopLightCurtain()
        {
            int maxHeight =_LightCurtain.Stop();
            Logger.Instance.Print(Logger.LogLevel.INFO, $"최대 높이: {maxHeight} mm", true);
        }

        public void ConnectCam()
        {
            if (!IsOnlineMode)
                return;
            try
            {
                for (int i = 0; i < SystemParam.CamPathList.Count; i++)
                {
                    if (string.IsNullOrEmpty(SystemParam.CamPathList[i]))
                        Logger.Instance.Print(Logger.LogLevel.ERROR, $"Cam{i + 1} 경로가 설정되지 않았습니다.", true);

                    _CamController.Connect((CamIndex)i);

                    
                }
            }
            catch(Exception ex)
            {
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"카메라 연결 중 오류 발생: {ex.Message}", true);
                return;
            }
        }

        public void DisconnectCam()
        {
            _CamController.DisconnectAll();

        }
        public void StartCaptureImage()
        {
            try
            {
                for (int i = 0; i < SystemParam.CamPathList.Count; i++)
                    _CamController?.CaptureImage((CamIndex)i, 80, SystemParam.ImageDataSavePath);
            }
            catch (Exception ex)
            {
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"카메라 취득 중 오류 발생: {ex.Message}", true);
                return;
            }
        }
    }
}
