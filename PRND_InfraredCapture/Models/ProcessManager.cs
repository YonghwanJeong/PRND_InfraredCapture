using CP.OptrisCam;
using CP.OptrisCam.models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using static MCProtocol.Mitsubishi;

namespace PRND_InfraredCapture.Models
{
    public class ProcessManager : IDisposable
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



        private bool _IsDebugMode = false;

        //PLC
        private McProtocolTcp _MCProtocolTCP;
        private readonly SemaphoreSlim _mcProtocolLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _pcRespSem = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _laserWarnSem = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _PLCMonitoringCts;
        private EdgeDetector _PLCCommandEdgeDetector = new EdgeDetector();
        private Task _PLCSignalMonitoringTask;
        private Timer _HeartbeatTimer;
        private bool _IsHeartBeatOn = false;
        private int _heartbeatGate = 0; // 0: idle, 1: running

        private CancellationTokenSource _MainLogicCts;
        private Task _MainLogicTask;

        public Action<bool> OnPLCDisconnected { get; set; }

        //Device
        private CamController _CamController;
        private LightCurtainComm _LightCurtain;
        private LeuzeMdiClient[] _Lasers;
        private RobotServer _RobotServer;

        private bool _IsInspectionRunning = false;


        //MainLogic 이벤트
        public Action<int> OnSendCarHeight { get; set; }

        //Camera
        private readonly SemaphoreSlim _captureGate = new SemaphoreSlim(1, 1);
        private int _InfraredFrameCount = 80;


        //내부 사용 변수
        private string _CarNumber = "";
        private int _CurrentCarHeight = 0;
        int _LastSentPCResponse = 0;
        int _LastSentLaserWarning = 0;
        // 폴링/타임아웃 파라미터
        private TimeSpan _poll = TimeSpan.FromMilliseconds(100);
        private TimeSpan _ackTimeout = TimeSpan.FromSeconds(10);    // 검사 시작 수락 대기
        private TimeSpan _doneTimeout = TimeSpan.FromMinutes(5);   // 로봇 이동 완료 대기 (설비에 맞게 조정)
        private static readonly TimeSpan _TcpTimeout = TimeSpan.FromSeconds(5);
        private const int _TcpMaxRetry = 3;

        private int _RobotFirstMovingDelay = 250; //최초 로봇 이동 딜레이 타임
        private int _CaptureDelay = 1200;       //카메라 취득 신호 전송 후 다음 움직임 딜레이
        private int _AfterMovingDelay = 500;     //로봇 이동 완료 후 잔진동이 없어질때까지 기다리는 딜레이


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

        public async Task StartOnline()
        {
            if (IsOnlineMode)
                return;
            try
            {
                if (!_IsDebugMode)
                {
                    //LightCurtain 연결
                    _LightCurtain = new LightCurtainComm(SystemParam.LightCurtainPortName, SystemParam.LightCurtainBaudRate);

                    //카메라 연결
                    _CamController = new CamController(SystemParam.CamPathList);
                    ConnectCam();

                    //레이저 거리센서 연결
                    _Lasers = new LeuzeMdiClient[SystemParam.LaserConnectionList.Count];
                    for (int i = 0; i < SystemParam.LaserConnectionList.Count; i++)
                    {
                        _Lasers[i] = new LeuzeMdiClient((ModuleIndex)i, SystemParam.LaserConnectionList[i].IPAddress, SystemParam.LaserConnectionList[i].Port);
                        _Lasers[i].FrameReceived += _Laser_FrameReceived;
                        _Lasers[i].WarningStateChanged += _Laser_WarningStateChanged;
                        await _Lasers[i].ConnectAsync();
                        await _Lasers[i].StartMonitoringAsync();
                    }

                    //PLC 연결
                    _MCProtocolTCP = new McProtocolTcp(SystemParam.PLCAddress, SystemParam.PLCPort, McFrame.MC3E);
                    await _MCProtocolTCP.Open();
                    if (!_MCProtocolTCP.Connected)
                    {
                        Logger.Instance.Print(Logger.LogLevel.INFO, "PLC 연결에 실패하였습니다.", true);
                        return;
                    }
                    Logger.Instance.Print(Logger.LogLevel.INFO, "PLC 연결성공.", true);
                    OnPLCDisconnected?.Invoke(true);
                    _PLCMonitoringCts = new CancellationTokenSource();
                    _PLCSignalMonitoringTask = PLCSignalMonitoringAsync(_PLCMonitoringCts.Token);
                    StartHeartbeat();
                }


                //Robot Server 시작 포트는 50000 고정
                _RobotServer = new RobotServer(IPAddress.Any, 50000, SystemParam.RobotConnectionList);
                _ = _RobotServer.StartAsync(); //비동기 시작

                IsOnlineMode = true;
            }
            catch (Exception ex)
            {
                Logger.Instance.Print(Logger.LogLevel.INFO, $"예상치 못한 오류: {ex.Message}", true);

            }

            Logger.Instance.Print(Logger.LogLevel.INFO, $"Online 모드 시작", true);
        }

        private void _Laser_WarningStateChanged(ModuleIndex index, bool arg2)
        {
            //PLC 로직 구성 필요
            _ = SetLaserWarningBit((int)index, arg2);
        }

        public async Task StopOnline()
        {
            if (!IsOnlineMode)
                return;

            try
            {
                if (!_IsDebugMode)
                {
                    //LightCurtain Stop
                    _LightCurtain.Stop();

                    //카메라 Disconnect
                    _CamController.DisconnectAll();

                    StopAllLaserScan();
                    //레이저 거리센서 Disconnect
                    for (int i = 0; i < _Lasers.Length; i++)
                    {
                        if (_Lasers[i] != null || !_Lasers[i].IsConnected)
                            continue;
                        await _Lasers[i].DisconnectAsync();
                        _Lasers[i].FrameReceived -= _Laser_FrameReceived;
                    }

                    //PLC Disconnect
                    StopHeartbeat();
                    if (_PLCSignalMonitoringTask != null)
                        await StopPLCMonitoringTaskAsync();
                    if (_MainLogicTask != null)
                        await StopMainLogicTaskAsync();
                    _MCProtocolTCP.Close();
                    OnPLCDisconnected?.Invoke(false);
                }


                //Robot Disconnect
                _RobotServer.Stop();
                _RobotServer.Dispose();

                IsOnlineMode = false;

                Logger.Instance.Print(Logger.LogLevel.INFO, $"Online 모드 정지", true);
            }
            catch (Exception ex)
            {
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"Online 모드 정지 중 오류 발생: {ex.Message}", true);
            }

        }
        private async Task StopMainLogicTaskAsync()
        {
            _MainLogicCts?.Cancel();
            try
            {
                await _MainLogicTask;
            }
            catch (OperationCanceledException)
            {
                Logger.Instance.Print(Logger.LogLevel.INFO, "메인 로직 정지", true);
            }
            finally
            {
                _MainLogicCts.Dispose();
                _IsInspectionRunning = false;
            }
        }
        private async Task StopPLCMonitoringTaskAsync()
        {
            _PLCMonitoringCts.Cancel();
            try
            {
                await _PLCSignalMonitoringTask;
            }
            catch (OperationCanceledException)
            {
                Logger.Instance.Print(Logger.LogLevel.INFO, "PLC 모니터링 정지", true);
            }
            finally
            {
                _PLCMonitoringCts.Dispose();
            }
        }
        public void StartHeartbeat()
        {
            // 시작은 연결 성공 이후에!
            _HeartbeatTimer = new Timer(async state =>
            {
                if (Interlocked.Exchange(ref _heartbeatGate, 1) == 1)
                    return; // 재진입 방지

                try
                {
                    if (!IsOnlineMode || _MCProtocolTCP == null)
                        return;

                    await OnHeartBeatAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.Instance.Print(Logger.LogLevel.ERROR, "Heartbeat error: " + ex, true);
                }
                finally
                {
                    Volatile.Write(ref _heartbeatGate, 0);
                }
            },
               null,
               1000,   // dueTime
               1000);  // period
        }

        public void StopHeartbeat()
        {
            var t = _HeartbeatTimer;
            _HeartbeatTimer = null;
            if (t != null) t.Dispose();
        }


        private async Task OnHeartBeatAsync()
        {
            // 예: HeartbeatBit만 토글 (다른 비트 덮어쓰지 않도록!)
            _IsHeartBeatOn = !_IsHeartBeatOn;
            await SafeSetDevice(PlcDeviceType.D, SystemParam.HeartBeatAddress, _IsHeartBeatOn ? 1 : 0);
        }


        public void StartInspectionSequence(string carNumber)
        {
            if (!IsOnlineMode)
            {
                Logger.Instance.Print(Logger.LogLevel.INFO, "Online 모드가 아닙니다.", true);
                return;
            }
            if (_IsInspectionRunning)
            {
                Logger.Instance.Print(Logger.LogLevel.INFO, "이미 Sequence가 시작 중입니다.", true);
                return;
            }
            _CarNumber = carNumber;
            _MainLogicCts = new CancellationTokenSource();
            //_MainLogicTask = RunningMainLogicTask(carNumber, _MainLogicCts.Token);
            //_MainLogicTask = CommTestTask(carNumber, _MainLogicCts.Token);
            _MainLogicTask = Step2Robot2TestFunc(_MainLogicCts.Token);
        }

        private async Task PLCSignalMonitoringAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await SafeGetDevice(PlcDeviceType.D, SystemParam.PLCStatusAddress);
                    int value = _MCProtocolTCP.Device;

                    _PLCCommandEdgeDetector.Update(value);
                    if (_PLCCommandEdgeDetector.IsRisingEdge((BitIndex)PLCStatusCommand.CarEntrySignal)) //차량 진입 신호
                    {
                        Logger.Instance.Print(Logger.LogLevel.INFO, "차량 진입 신호 감지", true);

                        await SetPCResponseBit((int)PCCommand.ResponseOK, true); //응답 완료 신호 on
                        bool checkAck = await WaitForWordBitAsync(PlcDeviceType.D, SystemParam.PLCStatusAddress, (int)PLCStatusCommand.ResponseOK, true, _ackTimeout, _poll, token, "").ConfigureAwait(false);
                        if (!checkAck)
                            Logger.Instance.Print(Logger.LogLevel.INFO, $"PLC 응답 없음", true);
                        await SetPCResponseBit((int)PCCommand.ResponseOK, false); //응답 완료 신호 off

                        _LightCurtain.Start(SystemParam.LightCurtainHeightOffset);

                    }
                    else if (_PLCCommandEdgeDetector.IsRisingEdge((BitIndex)PLCStatusCommand.SequenceInitialize))
                    {
                        //**검사 초기화 시퀀스 추가해야 함.
                    }
                    else
                    {
                        await Task.Delay(100, token);
                    }
                }
                catch (TaskCanceledException)
                {
                    Logger.Instance.Print(Logger.LogLevel.INFO, "PLC 모니터링 로직이 종료되었습니다.", true);
                    break;
                }
            }
        }
        private async Task Step1Robot1TestFunc(CancellationToken token)
        {
            ModuleIndex currentIndex = ModuleIndex.Module1;
            int robotIndex = (int)currentIndex;
            int robotTeachedPosition = 0;
            string currentStep = "step1";
            string currentIndexString = Enum.GetName(typeof(ModuleIndex), currentIndex);

            string firstReceiveCheckString = "1,1,1";
            string secondReceiveCheckingString = "1,1,2";
            string homeReceiveCheckingString = "1,1,0";

            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentIndexString} 로봇 이동 시작", true);

            await MoveRobotTeachedPosition(currentIndex, robotTeachedPosition, token).ConfigureAwait(false);

            //거리센서 측정
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString} 거리센서 측정", true);
            double distance = await GetDistanceByLaserAsync(currentIndex, token).ConfigureAwait(false);
            double moveDistance = distance - 620;

            //Robot 전진이동 TCP 신호 전송, 대기
            string robotMessage = $"{moveDistance}";
            var robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, robotMessage, firstReceiveCheckString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);

            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇  전진 이동 응답 없음", true);

            await Task.Delay(_AfterMovingDelay);

            await StartCaptureImage(currentIndex, 70, 80, AcquisitionAngle.Angle_0, $"{currentStep}_{0}");
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString} 열화상 이미지 촬영 시작", true);
            await Task.Delay(_CaptureDelay);

            //로봇에 수평이동 TCP 신호 전송, 대기
            robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, "5001", secondReceiveCheckingString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇 수평 이동 응답 없음", true);

            await Task.Delay(_AfterMovingDelay);

            await StartCaptureImage(currentIndex, 70, 80, AcquisitionAngle.Angle_0, $"{currentStep}_{1}");
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString} 열화상 이미지 촬영 시작", true);
            await Task.Delay(_CaptureDelay);

            //로봇에 원점 이동 명령
            robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, "5000", homeReceiveCheckingString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇 원점 이동 응답 없음", true);

            robotTeachedPosition = 1;
            await MoveRobotTeachedPosition(currentIndex, robotTeachedPosition, token).ConfigureAwait(false);

            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString} 거리센서 측정", true);
            distance = await GetDistanceByLaserAsync(currentIndex, token).ConfigureAwait(false);  //적어두고

            //로봇에 원점 이동 명령
            robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, "5000", homeReceiveCheckingString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇 원점 이동 응답 없음", true);
        }
        private async Task Step1Robot2TestFunc(CancellationToken token)
        {
            ModuleIndex currentIndex = ModuleIndex.Module2;
            int robotIndex = (int)currentIndex;
            int robotTeachedPosition = 0;
            string currentStep = "step1";
            string currentIndexString = Enum.GetName(typeof(ModuleIndex), currentIndex);

            string firstReceiveCheckString = "1,2,1";
            string secondReceiveCheckingString = "1,2,2";
            string homeReceiveCheckingString = "1,2,0";

            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentIndexString} 로봇 이동 시작", true);

            await MoveRobotTeachedPosition(currentIndex, robotTeachedPosition, token).ConfigureAwait(false);

            //거리센서 측정
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString} 거리센서 측정", true);
            double distance = await GetDistanceByLaserAsync(currentIndex, token).ConfigureAwait(false);
            double moveDistance = distance - 620;

            //Robot 전진이동 TCP 신호 전송, 대기
            string robotMessage = $"{moveDistance}";
            var robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, robotMessage, firstReceiveCheckString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);

            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇  전진 이동 응답 없음", true);

            await Task.Delay(_AfterMovingDelay);

            await StartCaptureImage(currentIndex, 70, 80, AcquisitionAngle.Angle_0, $"{currentStep}_{0}");
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString} 열화상 이미지 촬영 시작", true);
            await Task.Delay(_CaptureDelay);

            //로봇에 수평이동 TCP 신호 전송, 대기
            robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, "5001", secondReceiveCheckingString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇 수평 이동 응답 없음", true);

            await Task.Delay(_AfterMovingDelay);

            await StartCaptureImage(currentIndex, 70, 80, AcquisitionAngle.Angle_0, $"{currentStep}_{1}");
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString} 열화상 이미지 촬영 시작", true);
            await Task.Delay(_CaptureDelay);

            //로봇에 원점 이동 명령
            robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, "5000", homeReceiveCheckingString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇 원점 이동 응답 없음", true);
        }
        //필수 체크 항목 : 수평 이동/ 원점이동 명령시 0이 아니라 5000: 원점, 5001~ 순서대로.

        private async Task Step1Robot3TestFunc(CancellationToken token)
        {
            ModuleIndex currentIndex = ModuleIndex.Module3;
            int robotIndex = (int)currentIndex;
            int robotTeachedPosition = 0;
            string currentStep = "step1";
            string currentIndexString = Enum.GetName(typeof(ModuleIndex), currentIndex);

            string firstReceiveCheckString = "1,3,1";
            string secondReceiveCheckingString = "1,3,2";
            string thirdReceiveCheckingString = "1,3,3";
            string fourthReceiveCheckingString = "1,3,4";
            string homeReceiveCheckingString = "1,3,0";

            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentIndexString} 로봇 이동 시작", true);

            await MoveRobotTeachedPosition(currentIndex, robotTeachedPosition, token).ConfigureAwait(false);

            //거리센서 측정
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString} 거리센서 측정", true);
            double distance = await GetDistanceByLaserAsync(currentIndex, token).ConfigureAwait(false);
            if (distance > 740)
                distance = 740;
            double moveDistance = distance - 620;

            //Robot 전진이동 TCP 신호 전송, 대기
            string robotMessage = $"{moveDistance}";
            var robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, robotMessage, firstReceiveCheckString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);

            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇  전진 이동 응답 없음", true);

            await Task.Delay(_AfterMovingDelay);

            await StartCaptureImage(currentIndex, 70, 80, AcquisitionAngle.Angle_0, $"{currentStep}_{0}");
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString} 열화상 이미지 촬영 시작", true);
            await Task.Delay(_CaptureDelay);

            //로봇에 수평이동 TCP 신호 전송, 대기
            robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, "5001", secondReceiveCheckingString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇 수평 이동 응답 없음", true);

            await Task.Delay(_AfterMovingDelay);

            await StartCaptureImage(currentIndex, 70, 80, AcquisitionAngle.Angle_0, $"{currentStep}_{1}");
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString} 열화상 이미지 촬영 시작", true);
            await Task.Delay(_CaptureDelay);

            //로봇에 원점 이동 명령
            robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, "5000", homeReceiveCheckingString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇 원점 이동 응답 없음", true);

            await Task.Delay(1000);
            robotTeachedPosition = 1;
            await MoveRobotTeachedPosition(currentIndex, robotTeachedPosition, token).ConfigureAwait(false);

            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString} 거리센서 측정", true);
            distance = await GetDistanceByLaserAsync(currentIndex, token).ConfigureAwait(false);  //적어두고
            if (distance > 1000)
                distance = 1000;
            moveDistance = distance - 720;
            robotMessage = $"{moveDistance}";
            //로봇에 전진 이동 TCP 신호 전송, 대기
            robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, robotMessage, thirdReceiveCheckingString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇 전진 이동 응답 없음", true);

            await Task.Delay(_AfterMovingDelay);

            await StartCaptureImage(currentIndex, 70, 80, AcquisitionAngle.Angle_0, $"{currentStep}_{2}");
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString} 열화상 이미지 촬영 시작", true);
            await Task.Delay(_CaptureDelay);


            //로봇에 상승 이동 TCP 신호 전송, 대기
            robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, "5002", fourthReceiveCheckingString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇 상승 이동 응답 없음", true);

            //로봇에 원점 이동 명령
            robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, "5000", homeReceiveCheckingString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇 원점 이동 응답 없음", true);
        }
        private async Task Step1Robot4TestFunc(CancellationToken token)
        {
            ModuleIndex currentIndex = ModuleIndex.Module4;
            int robotIndex = (int)currentIndex;
            int robotTeachedPosition = 0;
            string currentStep = "step1";
            string currentIndexString = Enum.GetName(typeof(ModuleIndex), currentIndex);

            string firstReceiveCheckString = "1,4,1";
            string secondReceiveCheckingString = "1,4,2";
            string homeReceiveCheckingString = "1,4,0";

            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentIndexString} 로봇 이동 시작", true);

            await MoveRobotTeachedPosition(currentIndex, robotTeachedPosition, token).ConfigureAwait(false);

            //거리센서 측정
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString} 거리센서 측정", true);
            double distance = await GetDistanceByLaserAsync(currentIndex, token).ConfigureAwait(false);
            double moveDistance = distance - 620;

            //Robot 전진이동 TCP 신호 전송, 대기
            string robotMessage = $"{moveDistance}";
            var robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, robotMessage, firstReceiveCheckString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);

            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇  전진 이동 응답 없음", true);

            await Task.Delay(_AfterMovingDelay);

            await StartCaptureImage(currentIndex, 70, 80, AcquisitionAngle.Angle_0, $"{currentStep}_{0}");
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString} 열화상 이미지 촬영 시작", true);
            await Task.Delay(_CaptureDelay);

            //로봇에 수평이동 TCP 신호 전송, 대기
            robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, "5001", secondReceiveCheckingString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇 수평 이동 응답 없음", true);

            await Task.Delay(_AfterMovingDelay);

            await StartCaptureImage(currentIndex, 70, 80, AcquisitionAngle.Angle_0, $"{currentStep}_{1}");
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString} 열화상 이미지 촬영 시작", true);
            await Task.Delay(_CaptureDelay);

            //로봇에 원점 이동 명령
            robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, "5000", homeReceiveCheckingString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇 원점 이동 응답 없음", true);
        }
        private async Task Step2Robot1TestFunc(CancellationToken token)
        {
            ModuleIndex currentIndex = ModuleIndex.Module1;
            int robotIndex = (int)currentIndex;
            int robotTeachedPosition = 0;
            string currentStep = "step2";
            string currentIndexString = Enum.GetName(typeof(ModuleIndex), currentIndex);

            string firstReceiveCheckString = "2,1,1";
            string secondReceiveCheckingString = "2,1,2";
            string homeReceiveCheckingString = "2,1,0";

            robotTeachedPosition = 2;
            await MoveRobotTeachedPosition(currentIndex, robotTeachedPosition, token).ConfigureAwait(false);

            //거리센서 측정
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString} 거리센서 측정", true);
            double distance = await GetDistanceByLaserAsync(currentIndex, token).ConfigureAwait(false);
            double moveDistance = distance - 620;

            //Robot 전진이동 TCP 신호 전송, 대기
            string robotMessage = $"{moveDistance}";
            var robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, robotMessage, firstReceiveCheckString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);

            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇  전진 이동 응답 없음", true);

            await Task.Delay(_AfterMovingDelay);

            await StartCaptureImage(currentIndex, 70, 80, AcquisitionAngle.Angle_0, $"{currentStep}_{2}");
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString} 열화상 이미지 촬영 시작", true);
            await Task.Delay(_CaptureDelay);

            //로봇에 수평이동 TCP 신호 전송, 대기
            robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, "5002", secondReceiveCheckingString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇 수평 이동 응답 없음", true);

            await Task.Delay(_AfterMovingDelay);

            await StartCaptureImage(currentIndex, 70, 80, AcquisitionAngle.Angle_0, $"{currentStep}_{3}");
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString} 열화상 이미지 촬영 시작", true);
            await Task.Delay(_CaptureDelay);

            //로봇에 원점 이동 명령
            robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, "5000", homeReceiveCheckingString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇 원점 이동 응답 없음", true);
        }
        private async Task Step2Robot2TestFunc(CancellationToken token)
        {
            ModuleIndex currentIndex = ModuleIndex.Module2;
            int robotIndex = (int)currentIndex;
            int robotTeachedPosition = 0;
            string currentStep = "step2";
            string currentIndexString = Enum.GetName(typeof(ModuleIndex), currentIndex);

            string firstReceiveCheckString = "2,2,1";
            string homeReceiveCheckingString = "2,2,0";


            robotTeachedPosition = 1;
            await MoveRobotTeachedPosition(currentIndex, robotTeachedPosition, token).ConfigureAwait(false);



            //거리센서 측정
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString} 거리센서 측정", true);
            double distance = await GetDistanceByLaserAsync(currentIndex, token).ConfigureAwait(false);
            double moveDistance = distance - 720;
            string robotMessage = $"{0}";// SUV 어떻게 할 것인가

            var robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, robotMessage, firstReceiveCheckString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇  하강 이동 응답 없음", true);

            await Task.Delay(_AfterMovingDelay);
            await StartCaptureImage(currentIndex, 70, 80, AcquisitionAngle.Angle_0, $"{currentStep}_{4}");
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString} 열화상 이미지 촬영 시작", true);
            await Task.Delay(_CaptureDelay);

            //로봇에 원점 이동 명령
            robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, "5000", homeReceiveCheckingString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇 원점 이동 응답 없음", true);

        }
        private async Task Step2Robot3TestFunc(CancellationToken token)
        {
            ModuleIndex currentIndex = ModuleIndex.Module3;
            int robotIndex = (int)currentIndex;
            int robotTeachedPosition = 0;
            string currentStep = "step2";
            string currentIndexString = Enum.GetName(typeof(ModuleIndex), currentIndex);

            string firstReceiveCheckString = "2,3,1";
            string secondReceiveCheckingString = "2,3,2";
            string homeReceiveCheckingString = "2,3,0";

            robotTeachedPosition = 2;
            await MoveRobotTeachedPosition(currentIndex, robotTeachedPosition, token).ConfigureAwait(false);

            //거리센서 측정
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString} 거리센서 측정", true);
            double distance = await GetDistanceByLaserAsync(currentIndex, token).ConfigureAwait(false);
            double moveDistance = distance - 620;

            //Robot 전진이동 TCP 신호 전송, 대기
            string robotMessage = $"{moveDistance}";
            var robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, robotMessage, firstReceiveCheckString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);

            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇  전진 이동 응답 없음", true);

            await Task.Delay(_AfterMovingDelay);

            await StartCaptureImage(currentIndex, 70, 80, AcquisitionAngle.Angle_0, $"{currentStep}_{2}");
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString} 열화상 이미지 촬영 시작", true);
            await Task.Delay(_CaptureDelay);

            //로봇에 수평이동 TCP 신호 전송, 대기
            robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, "5003", secondReceiveCheckingString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇 수평 이동 응답 없음", true);

            await Task.Delay(_AfterMovingDelay);

            await StartCaptureImage(currentIndex, 70, 80, AcquisitionAngle.Angle_0, $"{currentStep}_{3}");
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString} 열화상 이미지 촬영 시작", true);
            await Task.Delay(_CaptureDelay);

            //로봇에 원점 이동 명령
            robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, "5000", homeReceiveCheckingString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇 원점 이동 응답 없음", true);
        }
        private async Task Step2Robot4TestFunc(CancellationToken token)
        {
            ModuleIndex currentIndex = ModuleIndex.Module4;
            int robotIndex = (int)currentIndex;
            int robotTeachedPosition = 0;
            string currentStep = "step2";
            string currentIndexString = Enum.GetName(typeof(ModuleIndex), currentIndex);

            string firstReceiveCheckString = "2,4,1";
            string secondReceiveCheckingString = "2,4,2";
            string thirdReceiveCheckingString = "2,4,3";
            string homeReceiveCheckingString = "2,4,0";


            robotTeachedPosition = 1;
            await MoveRobotTeachedPosition(currentIndex, robotTeachedPosition, token).ConfigureAwait(false);

            //Robot 전진이동 TCP 신호 전송, 대기
            double distance = 100;  //입력해야 함.  1150을 빼는걸로. 
            string robotMessage = $"{distance}";
            var robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, robotMessage, firstReceiveCheckString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇  전진 이동 응답 없음", true);

            //로봇에 수평이동 TCP 신호 전송, 대기
            robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, "5002", secondReceiveCheckingString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇 수평 이동 응답 없음", true);

            await Task.Delay(_AfterMovingDelay);


            //거리센서 측정
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString} 거리센서 측정", true);
            distance = await GetDistanceByLaserAsync(currentIndex, token).ConfigureAwait(false);
            double moveDistance = distance - 720;
            robotMessage = $"{moveDistance}";
            robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, robotMessage, thirdReceiveCheckingString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇  하강 이동 응답 없음", true);

            await Task.Delay(_AfterMovingDelay);
            await StartCaptureImage(currentIndex, 70, 80, AcquisitionAngle.Angle_0, $"{currentStep}_{2}");
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString} 열화상 이미지 촬영 시작", true);
            await Task.Delay(_CaptureDelay);

            //로봇에 원점 이동 명령
            robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, "5000", homeReceiveCheckingString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇 원점 이동 응답 없음", true);
        }


        //private async Task RobotSequenceTestFunc(CancellationToken token)
        //{





        //}
        private async Task PLCCommTestAsync(CancellationToken token)
        {
            if (_IsInspectionRunning)
            {
                Logger.Instance.Print(Logger.LogLevel.INFO, "이미 Sequence가 시작 중입니다.", true);
                return;
            }
            _IsInspectionRunning = true;

            await SetPCResponseBit((int)PCCommand.StartInspection, true); //검사 시작 On
                                                                          // 2) PLC -> PC : 특정 워드의 특정 비트가 ON 될 때까지 폴링
            bool ResponseAck = await WaitForWordBitAsync(PlcDeviceType.D, SystemParam.PLCStatusAddress, (int)PLCStatusCommand.ResponseOK, true, _ackTimeout, _poll, token, "START_ACK").ConfigureAwait(false);
            if (!ResponseAck)
                Logger.Instance.Print(Logger.LogLevel.INFO, $"PLC 응답 없음", true);
            await SetPCResponseBit((int)PCCommand.StartInspection, false); //검사 시작 Off


            //차량 높이 정보 업데이트
            _CurrentCarHeight = _LightCurtain.Stop();
            OnSendCarHeight?.Invoke(_CurrentCarHeight);

            //턴테이블 조명 ON 확인
            bool lightOnAck = await WaitForWordBitAsync(PlcDeviceType.D, SystemParam.PLCStatusAddress, (int)PLCStatusCommand.TurnTableLightOn, true, _ackTimeout, _poll, token, "START_ACK").ConfigureAwait(false);
            if (!lightOnAck)
                Logger.Instance.Print(Logger.LogLevel.INFO, $"턴테이블 조명 응답 없음", true);

            //45도 회전 요청
            await SetPCResponseBit((int)PCCommand.TurnAnlge45, true); // 45도 회전 요청
                                                                      // 2) PLC -> PC : 특정 워드의 특정 비트가 ON 될 때까지 폴링
            ResponseAck = await WaitForWordBitAsync(PlcDeviceType.D, SystemParam.PLCStatusAddress, (int)PLCStatusCommand.ResponseOK, true, _ackTimeout, _poll, token, "START_ACK").ConfigureAwait(false);
            if (!ResponseAck)
                Logger.Instance.Print(Logger.LogLevel.INFO, $"PLC 응답 없음", true);
            await SetPCResponseBit((int)PCCommand.TurnAnlge45, false); //45도 회전 완료 off

            bool roatateAck = await WaitForWordBitAsync(PlcDeviceType.D, SystemParam.TurnTableAngleAddress, (int)PCCommand.TurnAnlge45, true, _doneTimeout, _poll, token, "roatateAck").ConfigureAwait(false);
            if (!roatateAck)
                Logger.Instance.Print(Logger.LogLevel.INFO, $"PLC 응답 없음", true);
            await SetPCResponseBit((int)PCCommand.TurnAnlge45, false);


            //200도 회전 요청
            await SetPCResponseBit((int)PCCommand.TurnAnlge200, true); // 45도 회전 요청
                                                                       // 2) PLC -> PC : 특정 워드의 특정 비트가 ON 될 때까지 폴링
            ResponseAck = await WaitForWordBitAsync(PlcDeviceType.D, SystemParam.PLCStatusAddress, (int)PLCStatusCommand.ResponseOK, true, _ackTimeout, _poll, token, "START_ACK").ConfigureAwait(false);
            if (!ResponseAck)
                Logger.Instance.Print(Logger.LogLevel.INFO, $"PLC 응답 없음", true);
            await SetPCResponseBit((int)PCCommand.TurnAnlge200, false); //45도 회전 완료 off

            roatateAck = await WaitForWordBitAsync(PlcDeviceType.D, SystemParam.TurnTableAngleAddress, (int)PCCommand.TurnAnlge200, true, _doneTimeout, _poll, token, "roatateAck").ConfigureAwait(false);
            if (!roatateAck)
                Logger.Instance.Print(Logger.LogLevel.INFO, $"PLC 응답 없음", true);
            await SetPCResponseBit((int)PCCommand.TurnAnlge200, false);


            //45도 회전 요청
            await SetPCResponseBit((int)PCCommand.TurnAnlge180, true); // 45도 회전 요청
                                                                       // 2) PLC -> PC : 특정 워드의 특정 비트가 ON 될 때까지 폴링
            ResponseAck = await WaitForWordBitAsync(PlcDeviceType.D, SystemParam.PLCStatusAddress, (int)PLCStatusCommand.ResponseOK, true, _ackTimeout, _poll, token, "START_ACK").ConfigureAwait(false);
            if (!ResponseAck)
                Logger.Instance.Print(Logger.LogLevel.INFO, $"PLC 응답 없음", true);
            await SetPCResponseBit((int)PCCommand.TurnAnlge180, false); //45도 회전 완료 off

            roatateAck = await WaitForWordBitAsync(PlcDeviceType.D, SystemParam.TurnTableAngleAddress, (int)PCCommand.TurnAnlge0, true, _doneTimeout, _poll, token, "roatateAck").ConfigureAwait(false);
            if (!roatateAck)
                Logger.Instance.Print(Logger.LogLevel.INFO, $"PLC 응답 없음", true);
            await SetPCResponseBit((int)PCCommand.TurnAnlge180, false);

            bool isCarExit = await WaitForWordBitAsync(PlcDeviceType.D, SystemParam.PLCStatusAddress, (int)PLCStatusCommand.CarExitSignal, true, _doneTimeout, _poll, token, "CAREXIT_ACK").ConfigureAwait(false);
            if (!isCarExit)
                Logger.Instance.Print(Logger.LogLevel.INFO, $"차량 출차 응답없음", true);
            await SetPCResponseBit((int)PCCommand.ResponseOK, true);
            isCarExit = await WaitForWordBitAsync(PlcDeviceType.D, SystemParam.PLCStatusAddress, (int)PLCStatusCommand.CarExitSignal, false, _ackTimeout, _poll, token, "CAREXIT_ACK").ConfigureAwait(false);
            if (!isCarExit)
                Logger.Instance.Print(Logger.LogLevel.INFO, $"응답완료 신호 응답없음", true);
            await SetPCResponseBit((int)PCCommand.ResponseOK, false);

            _IsInspectionRunning = false;

        }
        private async Task CommTestTask(string carNumber, CancellationToken token)
        {
            try
            {
                if (_IsInspectionRunning)
                {
                    Logger.Instance.Print(Logger.LogLevel.INFO, "이미 Sequence가 시작 중입니다.", true);
                    return;
                }
                _IsInspectionRunning = true;


                Logger.Instance.Print(Logger.LogLevel.INFO, $"{carNumber} 차량 검사 시퀀스 시작", true);

                //로봇 통신 세션
                var robotSessions = new Dictionary<int, RobotSession>();
                for (int i = 0; i < _RobotServer.RobotCount; i++)
                {
                    var session = await WaitGetSessionAsync(_RobotServer, i, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
                    if (session == null)
                    {
                        Logger.Instance.Print(Logger.LogLevel.ERROR, $"{Enum.GetName(typeof(RobotIndex), i)} TCP 세션 없음(타임아웃)", true);
                        return;
                    }
                    robotSessions[i] = session;
                    Logger.Instance.Print(Logger.LogLevel.INFO, $"{Enum.GetName(typeof(RobotIndex), i)} TCP 세션 연결됨", true);
                }

                //Step1
                var step1RobotMoveTasks = new List<Task>();

                //로봇 3 동작
                step1RobotMoveTasks.Add(ExecuteRobotCaptureSequence1Async(currentStep: "Step1", currentIndex: ModuleIndex.Module3,
                                                  robotTeachedPosition: 0, carNumber:
                                                  carNumber, distanceOffset: 500,
                                                  camFocus: (float)70, acquisitionAngle: AcquisitionAngle.Angle_0,
                                                  firstReceiveCheckString: "1,3,1",
                                                  secondReceiveCheckingString: "1,3,2",
                                                  thirdReceiveCheckingString: "1,3,0",
                                                  firstInfraredPositionName: "Left5",
                                                  secondInfraredPositionName: "Left6",
                                                  token: token));

                await Task.WhenAll(step1RobotMoveTasks).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"CommTestTask 오류: {ex.Message}", true);
                return;
            }
        }

        private async Task RunningMainLogicTask(string carNumber, CancellationToken token)
        {
            try
            {

                if (_IsInspectionRunning)
                {
                    Logger.Instance.Print(Logger.LogLevel.INFO, "이미 Sequence가 시작 중입니다.", true);
                    return;
                }
                _IsInspectionRunning = true;


                Logger.Instance.Print(Logger.LogLevel.INFO, $"{carNumber} 차량 검사 시퀀스 시작", true);


                await SetPCResponseBit((int)PCCommand.StartInspection, true); //검사 시작 On
                // 2) PLC -> PC : 특정 워드의 특정 비트가 ON 될 때까지 폴링
                bool ResponseAck = await WaitForWordBitAsync(PlcDeviceType.D, SystemParam.PLCStatusAddress, (int)PLCStatusCommand.ResponseOK, true, _ackTimeout, _poll, token, "START_ACK").ConfigureAwait(false);
                if (!ResponseAck)
                    Logger.Instance.Print(Logger.LogLevel.INFO, $"PLC 응답 없음", true);
                await SetPCResponseBit((int)PCCommand.StartInspection, false); //검사 시작 Off

                //차량 높이 정보 업데이트
                _CurrentCarHeight = _LightCurtain.Stop();
                OnSendCarHeight?.Invoke(_CurrentCarHeight);

                //턴테이블 조명 ON 확인
                bool lightOnAck = await WaitForWordBitAsync(PlcDeviceType.D, SystemParam.PLCStatusAddress, (int)PLCStatusCommand.TurnTableLightOn, true, _ackTimeout, _poll, token, "START_ACK").ConfigureAwait(false);
                if (!lightOnAck)
                    Logger.Instance.Print(Logger.LogLevel.INFO, $"턴테이블 조명 응답 없음", true);


                //로봇 통신 세션
                var robotSessions = new Dictionary<int, RobotSession>();
                for (int i = 0; i < _RobotServer.RobotCount; i++)
                {
                    var session = await WaitGetSessionAsync(_RobotServer, i, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
                    if (session == null)
                    {
                        Logger.Instance.Print(Logger.LogLevel.ERROR, $"{Enum.GetName(typeof(RobotIndex), i)} TCP 세션 없음(타임아웃)", true);
                        return;
                    }
                    robotSessions[i] = session;
                    Logger.Instance.Print(Logger.LogLevel.INFO, $"{Enum.GetName(typeof(RobotIndex), i)} TCP 세션 연결됨", true);
                }

                //Step1
                var step1RobotMoveTasks = new List<Task>();

                //로봇 1 동작
                step1RobotMoveTasks.Add(ExecuteRobotCaptureSequence1Async(currentStep: "Step1", currentIndex: ModuleIndex.Module1,
                                                  robotTeachedPosition: 0,
                                                  carNumber: carNumber, distanceOffset: 500,
                                                  camFocus: (float)70, acquisitionAngle: AcquisitionAngle.Angle_0,
                                                  firstReceiveCheckString: "1,1,1",
                                                  secondReceiveCheckingString: "1,1,2",
                                                  thirdReceiveCheckingString: "1,1,0",
                                                  firstInfraredPositionName: "Right2",
                                                  secondInfraredPositionName: "Right1",
                                                  token: token));
                ;

                await Task.Delay(_RobotFirstMovingDelay).ConfigureAwait(false);

                //로봇 2 동작
                step1RobotMoveTasks.Add(ExecuteRobotCaptureSequence1Async(currentStep: "Step1", currentIndex: ModuleIndex.Module2,
                                                  robotTeachedPosition: 0, carNumber:
                                                  carNumber, distanceOffset: 500,
                                                  camFocus: (float)70, acquisitionAngle: AcquisitionAngle.Angle_0,
                                                  firstReceiveCheckString: "1,2,1",
                                                  secondReceiveCheckingString: "1,2,2",
                                                  thirdReceiveCheckingString: "1,2,0",
                                                  firstInfraredPositionName: "Right5",
                                                  secondInfraredPositionName: "Right6",
                                                  token: token));

                await Task.Delay(_RobotFirstMovingDelay).ConfigureAwait(false);
                //로봇 3 동작
                step1RobotMoveTasks.Add(ExecuteRobotCaptureSequence1Async(currentStep: "Step1", currentIndex: ModuleIndex.Module3,
                                                  robotTeachedPosition: 0, carNumber:
                                                  carNumber, distanceOffset: 500,
                                                  camFocus: (float)70, acquisitionAngle: AcquisitionAngle.Angle_0,
                                                  firstReceiveCheckString: "1,3,1",
                                                  secondReceiveCheckingString: "1,3,2",
                                                  thirdReceiveCheckingString: "1,3,0",
                                                  firstInfraredPositionName: "Left5",
                                                  secondInfraredPositionName: "Left6",
                                                  token: token));

                await Task.Delay(_RobotFirstMovingDelay).ConfigureAwait(false);
                //로봇 4 동작
                step1RobotMoveTasks.Add(ExecuteRobotCaptureSequence1Async("Step1", currentIndex: ModuleIndex.Module4,
                                                  robotTeachedPosition: 0, carNumber:
                                                  carNumber, distanceOffset: 500,
                                                  camFocus: (float)70, acquisitionAngle: AcquisitionAngle.Angle_0,
                                                  firstReceiveCheckString: "1,4,1",
                                                  secondReceiveCheckingString: "1,4,2",
                                                  thirdReceiveCheckingString: "1,4,0",
                                                  firstInfraredPositionName: "Left2",
                                                  secondInfraredPositionName: "Left1",
                                                  token: token));


                await Task.WhenAll(step1RobotMoveTasks).ConfigureAwait(false);
                Logger.Instance.Print(Logger.LogLevel.INFO, $"Step 1 로봇 측면 촬영 완료", true);


                //차량 거리 측정
                Logger.Instance.Print(Logger.LogLevel.INFO, $"Step1 차량 길이 측정 시작", true);
                await MoveRobotTeachedPosition(ModuleIndex.Module1, 1, token).ConfigureAwait(false);
                await MoveRobotTeachedPosition(ModuleIndex.Module3, 1, token).ConfigureAwait(false);

                var results = await Task.WhenAll(
                                    GetDistanceByLaserAsync(ModuleIndex.Module1, token),
                                    GetDistanceByLaserAsync(ModuleIndex.Module3, token)
                                    ).ConfigureAwait(false);

                double distanceFront = results[0];
                double distanceRear = results[1];


                //거리 측정 알고리즘 추가.
                Logger.Instance.Print(Logger.LogLevel.INFO, $"거리센서 측정 완료. 차량길이 :mm ", true);


                //로봇에 홈위치 명령어 줘야함. "1,3,0" 대기 필요.


                //회전 요청
                await SetPCResponseBit((int)PCCommand.TurnAnlge45, true); // 45도 회전 요청
                // 2) PLC -> PC : 특정 워드의 특정 비트가 ON 될 때까지 폴링
                bool roatateAck = await WaitForWordBitAsync(PlcDeviceType.D, SystemParam.TurnTableAngleAddress, (int)PCCommand.TurnAnlge45, true, _doneTimeout, _poll, token, "roatateAck").ConfigureAwait(false);
                if (!roatateAck)
                    Logger.Instance.Print(Logger.LogLevel.INFO, $"PLC 응답 없음", true);
                await SetPCResponseBit((int)PCCommand.TurnAnlge45, false);


                //Step2
                var step2RobotMoveTasks = new List<Task>();

                //1번 로봇 이동
                step2RobotMoveTasks.Add(ExecuteRobotCaptureSequence1Async(currentStep: "Step2", currentIndex: ModuleIndex.Module1,
                                      robotTeachedPosition: 2,
                                      carNumber: carNumber, distanceOffset: 500,
                                      camFocus: (float)70, acquisitionAngle: AcquisitionAngle.Angle_0,
                                      firstReceiveCheckString: "2,1,1",
                                      secondReceiveCheckingString: "2,1,2",
                                      thirdReceiveCheckingString: "2,1,0",
                                      firstInfraredPositionName: "Right3",
                                      secondInfraredPositionName: "Right4",
                                      token: token));

                //3번 로봇 이동
                step2RobotMoveTasks.Add(ExecuteRobotCaptureSequence1Async(currentStep: "Step2", currentIndex: ModuleIndex.Module3,
                                      robotTeachedPosition: 2,
                                      carNumber: carNumber, distanceOffset: 500,
                                      camFocus: (float)70, acquisitionAngle: AcquisitionAngle.Angle_0,
                                      firstReceiveCheckString: "2,3,1",
                                      secondReceiveCheckingString: "2,3,2",
                                      thirdReceiveCheckingString: "2,3,0",
                                      firstInfraredPositionName: "Right3",
                                      secondInfraredPositionName: "Right4",
                                      token: token));

                //4번 로봇 이동
                step2RobotMoveTasks.Add(ExecuteRobotCaptureSequence2Async(currentStep: "Step2", currentIndex: ModuleIndex.Module4,
                                      robotTeachedPosition: 2,
                                      carNumber: carNumber, distanceOffset: 800, distanceFront: distanceFront,
                                      camFocus: (float)70, acquisitionAngle: AcquisitionAngle.Angle_0,
                                      firstReceiveCheckString: "2,4,1",
                                      secondReceiveCheckingString: "2,4,2",
                                      thirdReceiveCheckingString: "2,4,0",
                                      firstInfraredPositionName: "Front",
                                      token: token));



                await Task.WhenAll(step2RobotMoveTasks).ConfigureAwait(false);
                Logger.Instance.Print(Logger.LogLevel.INFO, "Step 2 로봇 촬영 완료", true);

                Logger.Instance.Print(Logger.LogLevel.INFO, $"{carNumber} 차량 검사 시퀀스 종료", true);
                _IsInspectionRunning = false;

            }
            catch (TaskCanceledException)
            {
                Logger.Instance.Print(Logger.LogLevel.INFO, "MainLoginc 오류로 중단 되었습니다.", true);
            }

        }
        private async Task TCPTestAsync(CancellationToken token)
        {
            var robotTCPReceiveOK = await ResilientSendAndExpectAsync(0, "100", "test", _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"응답 없음", true);
            int a = 0;
            robotTCPReceiveOK = await ResilientSendAndExpectAsync(0, "100", "test", _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"응답 없음", true);
            a = 0;
        }

        //기본 포지션이동 후 수평 or 수직 이동 Sequence
        private async Task ExecuteRobotCaptureSequence1Async(string currentStep, ModuleIndex currentIndex,
                                            int robotTeachedPosition, string carNumber, double distanceOffset, float camFocus, AcquisitionAngle acquisitionAngle,
                                            string firstReceiveCheckString, string secondReceiveCheckingString, string thirdReceiveCheckingString,
                                            string firstInfraredPositionName, string secondInfraredPositionName, CancellationToken token)
        {
            try
            {
                //RobotSession session = robotSession[(int)currentIndex];
                int robotIndex = (int)currentIndex;
                string currentIndexString = Enum.GetName(typeof(ModuleIndex), currentIndex);
                Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentIndexString} 로봇 이동 시작", true);

                await MoveRobotTeachedPosition(currentIndex, robotTeachedPosition, token).ConfigureAwait(false);

                //거리센서 측정
                Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString} 거리센서 측정", true);
                double distance = await GetDistanceByLaserAsync(currentIndex, token).ConfigureAwait(false);
                double moveDistance = distance - distanceOffset;

                //Robot 전진이동 TCP 신호 전송, 대기
                string robotMessage = $"{moveDistance}";
                var robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, robotMessage, firstReceiveCheckString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);

                if (!robotTCPReceiveOK)
                    Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇  전진 이동 응답 없음", true);

                await Task.Delay(_AfterMovingDelay);

                await StartCaptureImage(currentIndex, camFocus, _InfraredFrameCount, acquisitionAngle, $"{carNumber}_{firstInfraredPositionName}");
                Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString}_{carNumber}_{firstInfraredPositionName} 열화상 이미지 촬영 시작", true);
                await Task.Delay(_CaptureDelay);

                //로봇에 수평이동 TCP 신호 전송, 대기
                robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, "", secondReceiveCheckingString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
                if (!robotTCPReceiveOK)
                    Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇 수평 이동 응답 없음", true);

                await Task.Delay(_AfterMovingDelay);

                await StartCaptureImage(currentIndex, 100, _InfraredFrameCount, AcquisitionAngle.Angle_0, $"{carNumber}_{secondInfraredPositionName}");
                Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString}_{carNumber}_{secondInfraredPositionName} 열화상 이미지 촬영 시작", true);
                await Task.Delay(_CaptureDelay);

                //로봇에 원점 이동 명령
                robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, "0", thirdReceiveCheckingString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
                if (!robotTCPReceiveOK)
                    Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇 원점 이동 응답 없음", true);
            }
            catch (Exception ex)
            {
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"ExecuteRobotCaptureSequence1Async 오류: {ex.Message}", true);
                return;
            }

        }
        //Step 2 본넷 부분
        private async Task ExecuteRobotCaptureSequence2Async(string currentStep, ModuleIndex currentIndex,
                                           int robotTeachedPosition, string carNumber, double distanceOffset, float camFocus, double distanceFront,
                                           AcquisitionAngle acquisitionAngle,
                                           string firstReceiveCheckString, string secondReceiveCheckingString, string thirdReceiveCheckingString,
                                           string firstInfraredPositionName, CancellationToken token)
        {
            try
            {
                //4번 로봇 본넷
                //4번 로봇 2번 포지션 이동
                int robotIndex = (int)currentIndex;
                string currentIndexString = Enum.GetName(typeof(ModuleIndex), currentIndex);
                await MoveRobotTeachedPosition(ModuleIndex.Module4, 2, token).ConfigureAwait(false);

                //차량 거리만큼 수평이동
                double distance = distanceFront;
                string robotMessage = $"{distance}";
                var robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, robotMessage, firstReceiveCheckString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
                if (!robotTCPReceiveOK)
                    Logger.Instance.Print(Logger.LogLevel.ERROR, $"4번 로봇 수평 이동 응답 없음", true);

                //거리센서 측정
                Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString} 거리센서 측정", true);
                distance = await GetDistanceByLaserAsync(currentIndex, token).ConfigureAwait(false);
                double moveDistance = distance - distanceOffset;

                //거리센서 측정값 만큼 하강
                robotMessage = $"{moveDistance}";
                robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, robotMessage, secondReceiveCheckingString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
                if (!robotTCPReceiveOK)
                    Logger.Instance.Print(Logger.LogLevel.ERROR, $"4번 로봇 하강 이동 응답 없음", true);

                await Task.Delay(_AfterMovingDelay);

                await StartCaptureImage(currentIndex, camFocus, _InfraredFrameCount, acquisitionAngle, $"{carNumber}_{firstInfraredPositionName}");
                Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString}_{carNumber}_{firstInfraredPositionName} 열화상 이미지 촬영 시작", true);
                await Task.Delay(_CaptureDelay);

                //로봇에 원점 이동 명령
                robotTCPReceiveOK = await ResilientSendAndExpectAsync(robotIndex, "0", thirdReceiveCheckingString, _doneTimeout, TimeSpan.FromSeconds(3), 5, ct: token).ConfigureAwait(false);
                if (!robotTCPReceiveOK)
                    Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇 원점 이동 응답 없음", true);
            }
            catch (Exception ex)
            {
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"ExecuteRobotCaptureSequence2Async 오류: {ex.Message}", true);
                return;
            }
        }
        private async Task<RobotSession> WaitGetSessionAsync(int robotIndex, TimeSpan timeout, TimeSpan pollInterval, CancellationToken ct)
        {
            var start = DateTime.UtcNow;
            while (!ct.IsCancellationRequested)
            {
                var s = _RobotServer.GetSession(robotIndex);
                if (s != null) return s;

                if (DateTime.UtcNow - start >= timeout) return null;

                await Task.Delay(pollInterval, ct).ConfigureAwait(false);
            }
            return null;
        }

        private async Task<bool> ResilientSendAndExpectAsync(int robotIndex, string sendMessage, string expectMessage,
                                                        TimeSpan receiveTimeout,         // 각 시도당 응답 대기
                                                        TimeSpan sessionWaitTimeout,     // 세션 재획득 대기
                                                        int maxReconnectAttempts,        // 세션 재획득/재시도 횟수
                                                        CancellationToken ct)
        {
            // 지수 백오프(최대 1초)
            TimeSpan backoff = TimeSpan.FromMilliseconds(150);

            for (int attempt = 1; attempt <= maxReconnectAttempts; attempt++)
            {
                // 1) 현재 세션 확보(필요하면 기다림)
                var session = await WaitGetSessionAsync(
                    _RobotServer, robotIndex, sessionWaitTimeout, TimeSpan.FromMilliseconds(100), ct
                ).ConfigureAwait(false);

                if (session == null)
                {
                    Logger.Instance.Print(Logger.LogLevel.WARN,
                        string.Format("[R{0}] 세션 없음(시도 {1}/{2})", robotIndex, attempt, maxReconnectAttempts), true);
                }
                else
                {
                    try
                    {
                        // 기존 래퍼 재사용
                        var ok = await SendAndExpectAsync(
                            session, sendMessage, expectMessage, receiveTimeout, ct
                        ).ConfigureAwait(false);

                        if (ok) return true;

                        Logger.Instance.Print(Logger.LogLevel.WARN,
                            string.Format("[R{0}] 응답 타임아웃/불일치(시도 {1}/{2})", robotIndex, attempt, maxReconnectAttempts), true);
                    }
                    catch (TimeoutException)
                    {
                        Logger.Instance.Print(Logger.LogLevel.WARN,
                            string.Format("[R{0}] 응답 타임아웃(시도 {1}/{2})", robotIndex, attempt, maxReconnectAttempts), true);
                    }
                    catch (OperationCanceledException)
                    {
                        // 외부에서 진짜로 취소한 경우에만 중단
                        if (ct.IsCancellationRequested) throw;

                        // 내부 세션(_cts) 취소/끊김으로 인한 취소라면
                        // 세션 재획득하여 재시도
                        Logger.Instance.Print(Logger.LogLevel.WARN, string.Format("[R{0}] 세션 취소 감지(끊김?) → 재연결/재시도", robotIndex), true);
                        // 여기서는 그냥 루프를 계속 타게 놔둡니다.
                    }
                    catch (Exception ex)
                    {
                        // 소켓 끊김, 스트림 오류 등: 다음 루프에서 세션 재획득
                        Logger.Instance.Print(Logger.LogLevel.WARN,
                            string.Format("[R{0}] 전송 중 예외: {1} (시도 {2}/{3})", robotIndex, ex.Message, attempt, maxReconnectAttempts), true);
                    }
                }

                // 2) 약간 쉬었다 재시도
                await Task.Delay(backoff, ct).ConfigureAwait(false);
                var nextMs = (int)Math.Min(backoff.TotalMilliseconds * 1.6, 1000.0);
                backoff = TimeSpan.FromMilliseconds(nextMs);
            }

            return false;
        }
        public async Task<bool> SendAndExpectAsync(
            RobotSession session,
            string sendMessage,                  // 처음/재시도 시 보낼 메시지
            string expectMessage,                // 수신에서 기대하는 문자열
            TimeSpan receiveTimeout,             // 각 시도당 수신 타임아웃
            CancellationToken ct)
        {
            // 1) 최초 전송
            await session.SendLineAsync(sendMessage, ct).ConfigureAwait(false);

            // 2) 수신 대기 + 재시도
            using (var rxTimeoutCts = new CancellationTokenSource(receiveTimeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, rxTimeoutCts.Token))
            {
                try
                {
                    await ExpectWithRetryAsync(
                        session,
                        expectMessage,
                        async () =>
                        {
                            // 재시도 시 동일 메시지 재전송
                            await session.SendLineAsync(sendMessage, ct).ConfigureAwait(false);
                        },
                        linked.Token
                    ).ConfigureAwait(false);

                    return true; // 성공
                }
                catch (TimeoutException)
                {
                    Logger.Instance.Print(Logger.LogLevel.ERROR, "응답 수신 재시도 초과(타임아웃)", true);
                    return false;
                }
                catch (OperationCanceledException)
                {
                    Logger.Instance.Print(Logger.LogLevel.INFO, "사용자/상위 취소로 수신 대기 중단", true);
                    throw; // 상위에서 취소 흐름 유지
                }
            }
        }

        private async Task SetPCResponseBit(int index, bool isOn)
        {
            await _pcRespSem.WaitAsync().ConfigureAwait(false);
            try
            {
                _LastSentPCResponse = SetDeviceBit(_LastSentPCResponse, index, isOn);
                await SafeSetDevice(PlcDeviceType.D, SystemParam.PLCResponseAddress, _LastSentPCResponse).ConfigureAwait(false);
            }
            finally { _pcRespSem.Release(); }
        }

        private async Task SetLaserWarningBit(int index, bool isOn)
        {
            await _laserWarnSem.WaitAsync().ConfigureAwait(false);
            try
            {
                _LastSentLaserWarning = SetDeviceBit(_LastSentLaserWarning, index, isOn);
                await SafeSetDevice(PlcDeviceType.D, SystemParam.DistanceAlarmAddress, _LastSentLaserWarning).ConfigureAwait(false);
            }
            finally { _laserWarnSem.Release(); }
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

        public void StartLightCurtain()
        {
            _LightCurtain.Start(SystemParam.LightCurtainHeightOffset);
        }

        public void StopLightCurtain()
        {
            int maxHeight = _LightCurtain.Stop();
            Logger.Instance.Print(Logger.LogLevel.INFO, $"최대 높이: {maxHeight} mm", true);
        }

        public async Task<double> GetDistanceByLaserAsync(ModuleIndex index, CancellationToken token)
        {
            if (!IsOnlineMode)
                return -1;

            var i = (int)index;
            if (_Lasers == null || i < 0 || i >= _Lasers.Length)
                return -1;

            var laser = _Lasers[i];
            if (laser == null || !laser.IsConnected)
                return -1;

            var res = await laser.CaptureMinAvgAsync(
                  frames: 10,
                  window: 5,
                  roiStart: null,
                  roiEnd: null,
                  stride: 1,
                  ignoreZero: false
            ).ConfigureAwait(false);

            Logger.Instance.Print(Logger.LogLevel.INFO, $"{Enum.GetName(typeof(ModuleIndex), (ModuleIndex)index)} Min Avg Distance: {res.AvgDistanceMm} mm at Angle: {res.AngleDeg}° over {res.FramesConsidered} frames starting from index {res.StartIndex}", true);
            return res.AvgDistanceMm;

        }
        public double GetDistancebyLaser(ModuleIndex index)
        {
            return GetDistanceByLaserAsync(index, CancellationToken.None).GetAwaiter().GetResult();
        }
        public void StopAllLaserScan()
        {
            if (_Lasers == null)
                return;
            for (int i = 0; i < _Lasers.Length; i++)
            {
                if (_Lasers[i] != null && _Lasers[i].IsConnected)
                    _Lasers[i].Stop();
            }

        }

        public void ConnectCam()
        {
            try
            {
                for (int i = 0; i < SystemParam.CamPathList.Count; i++)
                {
                    if (string.IsNullOrEmpty(SystemParam.CamPathList[i]))
                    {
                        Logger.Instance.Print(Logger.LogLevel.ERROR, $"Cam{i + 1} 경로가 설정되지 않았습니다.", true);
                        continue;
                    }
                    _CamController.Connect((ModuleIndex)i);
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"카메라 연결 중 오류 발생: {ex.Message}", true);
                return;
            }
        }

        public void DisconnectCam()
        {
            _CamController.DisconnectAll();

        }

        public async Task MoveRobotTeachedPosition(ModuleIndex moduleIndex, int positionIndex, CancellationToken token)
        {
            var robotStatusMap = new Dictionary<ModuleIndex, int>
            {
                { ModuleIndex.Module1, SystemParam.Robot1StatusAddress },
                { ModuleIndex.Module2, SystemParam.Robot2StatusAddress},
                { ModuleIndex.Module3, SystemParam.Robot3StatusAddress},
                { ModuleIndex.Module4, SystemParam.Robot4StatusAddress}
            };
            var robotMap = new Dictionary<ModuleIndex, int>
            {
                { ModuleIndex.Module1, SystemParam.Robot1MoveAddress },
                { ModuleIndex.Module2, SystemParam.Robot2MoveAddress },
                { ModuleIndex.Module3, SystemParam.Robot3MoveAddress },
                { ModuleIndex.Module4, SystemParam.Robot4MoveAddress }
            };
            int robotStatus = robotStatusMap[moduleIndex];
            int moveAddress = robotMap[moduleIndex];

            try
            {
                if (!IsOnlineMode)
                    return;
                if (!_MCProtocolTCP.Connected)
                    return;


                // 로봇 운전중 아님 확인
                bool robotReadyAck = await WaitForWordBitAsync(PlcDeviceType.D, robotStatus, (int)RobotStatusCommand.RobotMoving, false, _ackTimeout, _poll, token, "robotReadyAck").ConfigureAwait(false);
                if (!robotReadyAck)
                    Logger.Instance.Print(Logger.LogLevel.INFO, $"{Enum.GetName(typeof(ModuleIndex), moduleIndex)} 로봇 상태를 확인하세요", true);


                //로봇 이동 명령
                var position = SetDeviceBit(0, positionIndex, true);
                await SafeSetDevice(PlcDeviceType.D, moveAddress, position);
                // 2) PLC -> PC : 특정 워드의 특정 비트가 ON 될 때까지 폴링
                bool robotStartAck = await WaitForWordBitAsync(PlcDeviceType.D, robotStatus, (int)RobotStatusCommand.RobotMoving, true, _ackTimeout, _poll, token, "robotStartAck").ConfigureAwait(false);
                if (!robotStartAck)
                    Logger.Instance.Print(Logger.LogLevel.WARN, $"{Enum.GetName(typeof(ModuleIndex), moduleIndex)} 로봇 이동 요청에 PLC 응답 없음", true);
                await SafeSetDevice(PlcDeviceType.D, moveAddress, 0);


                bool moveDone = await WaitForWordBitAsync(PlcDeviceType.D, robotStatus, (int)RobotStatusCommand.RobotMoving, false, _doneTimeout, _poll, token, "MOVE_DONE").ConfigureAwait(false); //이동 완료 대기
                if (!moveDone)
                    Logger.Instance.Print(Logger.LogLevel.WARN, $"{Enum.GetName(typeof(ModuleIndex), moduleIndex)} 로봇 이동 응답 없음", true);
                else
                    Logger.Instance.Print(Logger.LogLevel.INFO, $"{Enum.GetName(typeof(ModuleIndex), moduleIndex)} 로봇 이동 완료", true);

            }
            catch (OperationCanceledException)
            {
                // 취소 시 로깅만
                Logger.Instance.Print(Logger.LogLevel.INFO,
                    $"{Enum.GetName(typeof(ModuleIndex), moduleIndex)} 이동 작업 취소", true);
            }
            catch (Exception ex)
            {
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{Enum.GetName(typeof(ModuleIndex), moduleIndex)} 로봇 이동 중 오류 발생: {ex.Message}", true);
            }
        }

        public async Task StartCaptureImage(ModuleIndex index, float focus, int framecnt, AcquisitionAngle angle, string positionName = "")
        {
            await _captureGate.WaitAsync().ConfigureAwait(false);
            try
            {
                int statusAddress = GetLightStatusAddress(index);
                int triggerAddress = GetLightTriggerAddress(index);

                try
                {
                    if (_CamController != null)
                    {
                        await CheckLightStatus(index, statusAddress);
                        _CamController.ReadyCapture(index, focus);
                        await Task.Delay(1000);
                        _CamController.CaptureImage(index, framecnt, SystemParam.ImageDataSavePath, angle, positionName);
                        await TurnOnLight(index, triggerAddress, statusAddress);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Instance.Print(Logger.LogLevel.ERROR, $"{Enum.GetName(typeof(ModuleIndex), index)} 카메라 취득 중 오류 발생: {ex.Message}", true);
                    return;
                }
            }
            finally
            {
                _captureGate.Release();
            }
        }

        public void StartCaptureWithoutLightCheck(ModuleIndex index, float focus, int framecnt, AcquisitionAngle angle, string positionName = "")
        {
            try
            {
                if (_CamController != null)
                {
                    _CamController.ReadyCapture(index, focus);
                    Task.Delay(1000).Wait();
                    _CamController.CaptureImage(index, framecnt, SystemParam.ImageDataSavePath, angle);
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"카메라 취득 중 오류 발생: {ex.Message}", true);
                return;
            }
        }


        private async Task CheckLightStatus(ModuleIndex index, int statusAddress)
        {
            if (!IsOnlineMode)
                return;
            if (!_MCProtocolTCP.Connected)
                return;

            try
            {
                CancellationToken token = new CancellationToken();
                bool lightAck = await WaitForWordBitsAsync(PlcDeviceType.D, statusAddress, new[] { (int)LightSatusCommand.Light1Ready, (int)LightSatusCommand.Light2Ready }, true, _ackTimeout, _poll, token, "lightAck ").ConfigureAwait(false);
                if (!lightAck)
                    Logger.Instance.Print(Logger.LogLevel.INFO, $"{Enum.GetName(typeof(ModuleIndex), index)} 조명 상태를 확인하세요", true);
            }
            catch (Exception ex)
            {
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{Enum.GetName(typeof(ModuleIndex), index)} 조명 On 오류 발생: {ex.Message}", true);
            }

        }
        private async Task TurnOnLight(ModuleIndex index, int triggerAddress, int statusAddrdss)
        {
            if (!IsOnlineMode)
                return;
            if (!_MCProtocolTCP.Connected)
                return;

            try
            {
                CancellationToken token = new CancellationToken();
                await SafeSetDevice(PlcDeviceType.D, triggerAddress, 1);
                // 2) PLC -> PC : 특정 워드의 특정 비트가 ON 될 때까지 폴링
                bool lightON_ACK = await WaitForWordBitAsync(PlcDeviceType.D, statusAddrdss, (int)LightSatusCommand.ResponseOK, true, _ackTimeout, _poll, token, "LightON_ACK").ConfigureAwait(false); //응답 대기
                if (!lightON_ACK)
                    Logger.Instance.Print(Logger.LogLevel.WARN, $"{Enum.GetName(typeof(ModuleIndex), index)} 조명 On 요청세 PLC 응답 없음", true);
                await SafeSetDevice(PlcDeviceType.D, triggerAddress, 0);
            }
            catch (Exception ex)
            {
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{Enum.GetName(typeof(ModuleIndex), index)} 조명 On 오류 발생: {ex.Message}", true);
            }
        }

        public void StartCaptureImageAll()
        {
            try
            {
                for (int i = 0; i < SystemParam.CamPathList.Count; i++)
                    _ = StartCaptureImage((ModuleIndex)i, 100, 240, AcquisitionAngle.Angle_0);
            }
            catch (Exception ex)
            {
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"카메라 취득 중 오류 발생: {ex.Message}", true);
                return;
            }
        }

        public int SetDeviceBit(int currentValue, int bitIndex, bool isOn)
        {
            // 비트 위치 계산: VisionReady → bit 7, Response → bit 3
            //int bitIndex = (int)index;

            if (isOn)
            {
                currentValue |= (1 << bitIndex);         // 해당 비트를 1로 설정
            }
            else
            {
                currentValue &= ~(1 << bitIndex);        // 해당 비트를 0으로 설정
            }

            return currentValue;
        }
        private async Task<int> SafeSetDevice(PlcDeviceType type, int address, int value)
        {
            await _mcProtocolLock.WaitAsync();
            try
            {
                return await _MCProtocolTCP.SetDevice(type, address, value);
            }
            catch (Exception ex)
            {
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"Unexpected Exception in SafeSetDevice: {ex.Message}");
                return -1;
            }
            finally
            {
                _mcProtocolLock.Release();
            }
        }
        private async Task<int> SafeGetDevice(PlcDeviceType type, int address)
        {
            await _mcProtocolLock.WaitAsync();
            try
            {
                return await _MCProtocolTCP.GetDevice(type, address);
            }
            catch (Exception ex)
            {
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"Unexpected Exception in SafeGetDevice: {ex.Message}");
                return -1;
            }
            finally
            {
                _mcProtocolLock.Release();
            }
        }
        private async Task<int> SafeWriteDeviceBlock(PlcDeviceType type, int address, int count, int[] data)
        {
            await _mcProtocolLock.WaitAsync();
            try
            {
                return await _MCProtocolTCP.WriteDeviceBlock(type, address, count, data);
            }
            catch (Exception ex)
            {
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"Unexpected Exception in SafeWriteDeviceBlock: {ex.Message}");
                return -1;
            }
            finally
            {
                _mcProtocolLock.Release();
            }
        }
        private async Task<byte[]> SafeReadDeviceBlock(PlcDeviceType type, int address, int count)
        {
            await _mcProtocolLock.WaitAsync();
            try
            {
                return await _MCProtocolTCP.ReadDeviceBlock(type, address, count);

            }
            finally
            {
                _mcProtocolLock.Release();
            }
        }
        private async Task<bool> WaitForWordBitAsync(PlcDeviceType wordType, int wordAddr, int bitIndex, bool expectOn, TimeSpan timeout, TimeSpan pollInterval, CancellationToken ct, string logTag)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                ct.ThrowIfCancellationRequested();

                await SafeGetDevice(wordType, wordAddr).ConfigureAwait(false);
                int val = _MCProtocolTCP.Device;
                bool on = IsBitOn(val, bitIndex);

                if (on == expectOn)
                    return true;

                await Task.Delay(pollInterval, ct).ConfigureAwait(false);
            }

            Logger.Instance.Print(Logger.LogLevel.ERROR,
                $"{logTag}: D{wordAddr}.{bitIndex} 기대 상태({(expectOn ? "ON" : "OFF")}) 타임아웃",
                true);
            return false;
        }
        public async Task<bool> WaitForWordBitsAsync(PlcDeviceType wordType, int wordAddr, IReadOnlyList<int> bitIndices, bool expectOnAll, TimeSpan timeout, TimeSpan pollInterval, CancellationToken ct, string logTag)
        {
            if (bitIndices == null || bitIndices.Count == 0)
                throw new ArgumentException("bitIndices must have at least one bit.");

            // 선택한 비트들의 마스크
            int mask = 0;
            for (int i = 0; i < bitIndices.Count; i++)
                mask |= (1 << bitIndices[i]);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                ct.ThrowIfCancellationRequested();

                await SafeGetDevice(wordType, wordAddr).ConfigureAwait(false);
                int val = _MCProtocolTCP.Device;

                bool ok = expectOnAll
                    ? ((val & mask) == mask)  // 모든 비트가 ON
                    : ((val & mask) == 0);    // 모든 비트가 OFF

                if (ok) return true;

                await Task.Delay(pollInterval, ct).ConfigureAwait(false);
            }

            Logger.Instance.Print(Logger.LogLevel.ERROR,
                $"{logTag}: D{wordAddr} 비트 {string.Join(",", bitIndices)} 기대 상태({(expectOnAll ? "ALL ON" : "ALL OFF")}) 타임아웃",
                true);
            return false;
        }
        public static async Task<RobotSession> WaitGetSessionAsync(RobotServer server, int id, TimeSpan timeout, TimeSpan poll, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                ct.ThrowIfCancellationRequested();
                var s = server.GetSession(id);
                if (s != null) return s;
                await Task.Delay(poll, ct).ConfigureAwait(false); // ex) 50~100ms
            }
            return null;
        }

        public async Task ExpectWithRetryAsync(
        RobotSession s, string expect, Func<Task> onRetry, CancellationToken ct)
        {
            for (int attempt = 1; attempt <= _TcpMaxRetry; attempt++)
            {
                try
                {
                    string got = await s.WaitForMessageAsync(
                        line => string.Equals(line, expect, StringComparison.OrdinalIgnoreCase),
                        _TcpTimeout, ct).ConfigureAwait(false);

                    Logger.Instance.Print(Logger.LogLevel.INFO, $"[R{s.RobotIndex}] '{expect}' 수신", true);
                    return;
                }
                catch (TimeoutException)
                {
                    if (attempt >= _TcpMaxRetry) break;
                    Logger.Instance.Print(Logger.LogLevel.WARN, $"[R{s.RobotIndex}] '{expect}' 타임아웃, 재시도 {attempt}/{_TcpMaxRetry - 1}", true);
                    if (onRetry != null) await onRetry().ConfigureAwait(false);
                }
            }
            throw new TimeoutException(string.Format("'{0}' 수신 실패 (재시도 {1}회 초과)", expect, _TcpMaxRetry));
        }

        // 워드 안 특정 비트(on/off) 검사
        private static bool IsBitOn(int wordValue, int bitIndex)
        {
            // bitIndex: 0~15 (Q시리즈 D는 1워드=16비트)
            int mask = 1 << bitIndex;
            return (wordValue & mask) != 0;
        }

        private int GetLightStatusAddress(ModuleIndex i)
        {
            switch (i)
            {
                case ModuleIndex.Module1: return SystemParam.Light1StatusAddress;
                case ModuleIndex.Module2: return SystemParam.Light2StatusAddress;
                case ModuleIndex.Module3: return SystemParam.Light3StatusAddress;
                case ModuleIndex.Module4: return SystemParam.Light4StatusAddress;
                default: throw new ArgumentOutOfRangeException(nameof(i));
            }
        }

        private int GetLightTriggerAddress(ModuleIndex i)
        {
            switch (i)
            {
                case ModuleIndex.Module1: return SystemParam.Module1LightOnAddress;
                case ModuleIndex.Module2: return SystemParam.Module2LightOnAddress;
                case ModuleIndex.Module3: return SystemParam.Module3LightOnAddress;
                case ModuleIndex.Module4: return SystemParam.Module4LightOnAddress;
                default: throw new ArgumentOutOfRangeException(nameof(i));
            }
        }

        public void Dispose()
        {
            _ = StopOnline();
        }
    }
}
