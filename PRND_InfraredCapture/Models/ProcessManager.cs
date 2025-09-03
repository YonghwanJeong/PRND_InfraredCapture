using CP.OptrisCam;
using CP.OptrisCam.models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
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
        private LeuzeMdiClient[] _Lasers;



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

        public async void StartOnline()
        {
            if (IsOnlineMode)
                return;
            
            _CamController = new CamController(SystemParam.CamPathList);
            _LightCurtain = new LightCurtainComm(SystemParam.LightCurtainPortName, SystemParam.LightCurtainBaudRate);
            
            _Lasers = new LeuzeMdiClient[SystemParam.LaserConnectionList.Count];
            
            for (int i = 0; i < SystemParam.LaserConnectionList.Count; i++)
            {
                _Lasers[i] = new LeuzeMdiClient((ModuleIndex)i, SystemParam.LaserConnectionList[i].IPAddress, SystemParam.LaserConnectionList[i].Port);
                _Lasers[i].FrameReceived += _Laser_FrameReceived;
                await _Lasers[i].ConnectAsync();
                await _Lasers[i].StartMonitoringAsync();
            }
            IsOnlineMode = true;

            ConnectCam();

            Logger.Instance.Print(Logger.LogLevel.INFO, $"Online 모드 시작", true);
        }

        private void _Laser_FrameReceived(ModuleIndex index, Header hdr, ushort[] dist, ushort[] inten)
        {
            double a0 = hdr.FirstAngle_mdeg / 1000.0;
            double da = hdr.DeltaAngle_mdeg / 1000.0;

            //Console.Write($"{Enum.GetName(typeof(ModuleIndex), index)}] #{hdr.PacketNo} {hdr.SubNo}/{hdr.TotalNo}] f={hdr.ScanFreqHz}Hz spots={hdr.ScanSpots} ");
            //int show = Math.Min(5, dist.Length);

            //for (int i = 0; i < show; i++)
            //{
            //    double ang = a0 + i * da;
            //    if (inten.Length == dist.Length)
            //        Console.Write($"({ang:F1}°, {dist[i]}mm, I={inten[i]}) ");
            //    else
            //        Console.Write($"({ang:F1}°, {dist[i]}mm) ");
            //}
            //Console.WriteLine();
        }

        public async void StopOnline()
        {
            if (!IsOnlineMode)
                return;
            
            _CamController.DisconnectAll();
            for (int i = 0; i < _Lasers.Length; i++)
            {
                if (!_Lasers[i].IsConnected)
                    return;
                await _Lasers[i].DisconnectAsync();
                _Lasers[i].FrameReceived -= _Laser_FrameReceived;
            }

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

        public void GetDistancebyLaser(ModuleIndex index)
        {
            if (!IsOnlineMode)
                return;

            // 원하는 시점에 캡처
            var res = _Lasers[(int)index].CaptureMinAvgAsync(frames: 10, window: 5,
                                                roiStart: null, roiEnd: null, stride: 1,
                                                ignoreZero: false)
                            .GetAwaiter().GetResult();

            MessageBox.Show($"Min Avg Distance: {res.AvgDistanceMm} mm at Angle: {res.AngleDeg}° over {res.FramesConsidered} frames starting from index {res.StartIndex}");

        }
        public void StopAllLaserScan()
        {
            for (int i = 0; i < _Lasers.Length; i++)
                _Lasers[i].Stop();
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

                    _CamController.Connect((ModuleIndex)i);

                    
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
                    _CamController?.CaptureImage((ModuleIndex)i, 240, SystemParam.ImageDataSavePath);
            }
            catch (Exception ex)
            {
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"카메라 취득 중 오류 발생: {ex.Message}", true);
                return;
            }
        }
    }
}
