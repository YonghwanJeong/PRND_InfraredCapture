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
        private int _heartbeatGate = 0; // 0: idle, 1: running
        private bool _IsHeartBeatOn = false;

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


        //내부 사용 변수
        private string _CarNumber = "";
        private int _InfraredFrameCount = 80; 
        private int _CurrentCarHeight = 0;
        int _LastSentPCResponse = 0;
        int _LastSentLaserWarning = 0;
        // 폴링/타임아웃 파라미터
        private TimeSpan _poll = TimeSpan.FromMilliseconds(100);
        private TimeSpan _ackTimeout = TimeSpan.FromSeconds(10);    // 검사 시작 수락 대기
        private TimeSpan _doneTimeout = TimeSpan.FromSeconds(10);   // 로봇 이동 완료 대기 (설비에 맞게 조정)
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
                //LightCurtain Stop
                _LightCurtain.Stop();

                //카메라 Disconnect
                _CamController.DisconnectAll();

                StopAllLaserScan();
                //레이저 거리센서 Disconnect
                for (int i = 0; i < _Lasers.Length; i++)
                {
                    if (!_Lasers[i].IsConnected)
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

                //Robot Disconnect
                _RobotServer.Stop();
                _RobotServer.Dispose();

                IsOnlineMode = false;

                Logger.Instance.Print(Logger.LogLevel.INFO, $"Online 모드 정지", true);
            }
            catch(Exception ex)
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
            await SafeSetDevice(PlcDeviceType.D, SystemParam.HeartBeatAddress, _IsHeartBeatOn? 1:0);
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
            _MainLogicTask = RunningMainLogicTask(carNumber, _MainLogicCts.Token);
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
                    if(_PLCCommandEdgeDetector.IsRisingEdge((BitIndex)PLCStatusCommand.CarEntrySignal)) //차량 진입 신호
                    {
                        Logger.Instance.Print(Logger.LogLevel.INFO, "차량 진입 신호 감지", true);
                        
                        await SetPCResponseBit((int)PCCommand.ResponseOK, true); //응답 완료 신호 on
                        bool checkAck = await WaitForWordBitAsync(PlcDeviceType.D, SystemParam.PLCStatusAddress, (int)PLCStatusCommand.ResponseOK, true, _ackTimeout, _poll, token, "").ConfigureAwait(false);
                        if (!checkAck)
                            Logger.Instance.Print(Logger.LogLevel.INFO, $"PLC 응답 없음", true);
                        else
                            await SetPCResponseBit((int)PCCommand.ResponseOK, false); //응답 완료 신호 off

                        _LightCurtain.Start(SystemParam.LightCurtainHeightOffset);

                    }
                    else if(_PLCCommandEdgeDetector.IsRisingEdge((BitIndex)PLCStatusCommand.SequenceInitialize))
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


        private async Task RunningMainLogicTask(string carNumber,CancellationToken token)
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
                else
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
                        Logger.Instance.Print(Logger.LogLevel.ERROR, $"로봇{i} TCP 세션 없음(타임아웃)", true);
                        return;
                    }
                    robotSessions[i] = session;
                    Logger.Instance.Print(Logger.LogLevel.INFO, $"로봇{i} TCP 세션 연결됨", true);
                }

                //Step1
                var step1RobotMoveTasks = new List<Task>();

                //로봇 1 동작
                step1RobotMoveTasks.Add(ExecuteRobotCaptureSequence1Async(robotSession: robotSessions, currentStep: "Step1", currentIndex: ModuleIndex.Module1,
                                                  robotTeachedPosition: 0, 
                                                  carNumber: carNumber, distanceOffset: 500, 
                                                  camFocus: (float)70, acquisitionAngle: AcquisitionAngle.Angle_0,
                                                  firstReceiveCheckString: "Position1-2",
                                                  secondReceiveCheckingString: "Position1-3", 
                                                  firstInfraredPositionName: "Right2", 
                                                  secondInfraredPositionName: "Right1",
                                                  token: token));
;
                
                await Task.Delay(_RobotFirstMovingDelay).ConfigureAwait(false);

                //로봇 2 동작
                step1RobotMoveTasks.Add(ExecuteRobotCaptureSequence1Async(robotSession: robotSessions, currentStep: "Step1", currentIndex: ModuleIndex.Module2,
                                                  robotTeachedPosition: 0, carNumber: 
                                                  carNumber, distanceOffset: 500,
                                                  camFocus: (float)70, acquisitionAngle: AcquisitionAngle.Angle_0,
                                                  firstReceiveCheckString: "Position2-2",
                                                  secondReceiveCheckingString: "Position2-3", 
                                                  firstInfraredPositionName: "Right5", 
                                                  secondInfraredPositionName: "Right6",
                                                  token: token));
                
                await Task.Delay(_RobotFirstMovingDelay).ConfigureAwait(false);
                //로봇 3 동작
                step1RobotMoveTasks.Add(ExecuteRobotCaptureSequence1Async(robotSession: robotSessions, currentStep: "Step1", currentIndex: ModuleIndex.Module3,
                                                  robotTeachedPosition: 0, carNumber: 
                                                  carNumber, distanceOffset: 500,
                                                  camFocus: (float)70, acquisitionAngle: AcquisitionAngle.Angle_0,
                                                  firstReceiveCheckString: "Position3-2",
                                                  secondReceiveCheckingString: "Position3-3",
                                                  firstInfraredPositionName: "Left5",
                                                  secondInfraredPositionName: "Left6",
                                                  token: token));

                await Task.Delay(_RobotFirstMovingDelay).ConfigureAwait(false);
                //로봇 4 동작
                step1RobotMoveTasks.Add(ExecuteRobotCaptureSequence1Async(robotSession: robotSessions, currentStep: "Step1", currentIndex: ModuleIndex.Module4,
                                                  robotTeachedPosition: 0, carNumber: 
                                                  carNumber, distanceOffset: 500,
                                                  camFocus: (float)70, acquisitionAngle: AcquisitionAngle.Angle_0,
                                                  firstReceiveCheckString: "Position4-2",
                                                  secondReceiveCheckingString: "Position4-3",
                                                  firstInfraredPositionName: "Left2",
                                                  secondInfraredPositionName: "Left1",
                                                  token: token));


                await Task.WhenAll(step1RobotMoveTasks).ConfigureAwait(false);
                Logger.Instance.Print(Logger.LogLevel.INFO, $"Step 1 로봇 측면 촬영 완료", true);


                //차량 거리 측정
                Logger.Instance.Print(Logger.LogLevel.INFO, $"Step1 차량 길이 측정 시작", true);
                await MoveRobot(ModuleIndex.Module1, 1, token).ConfigureAwait(false);
                await MoveRobot(ModuleIndex.Module3, 1, token).ConfigureAwait(false);

                var results = await Task.WhenAll(
                                    GetDistanceByLaserAsync(ModuleIndex.Module1, token),
                                    GetDistanceByLaserAsync(ModuleIndex.Module3, token)
                                    ).ConfigureAwait(false);

                double distanceFront = results[0];
                double distanceRear = results[1];

                
                //거리 측정 알고리즘 추가.
                Logger.Instance.Print(Logger.LogLevel.INFO, $"거리센서 측정 완료. 차량길이 :mm ", true);


                //회전 요청
                await SetPCResponseBit((int)PCCommand.TurnAnlge45, true); // 45도 회전 요청
                // 2) PLC -> PC : 특정 워드의 특정 비트가 ON 될 때까지 폴링
                bool roatateAck = await WaitForWordBitAsync(PlcDeviceType.D, SystemParam.TurnTableAngleAddress, 2, true, _doneTimeout, _poll, token, "roatateAck").ConfigureAwait(false);
                if (!roatateAck)
                    Logger.Instance.Print(Logger.LogLevel.INFO, $"PLC 응답 없음", true);
                else
                    await SetPCResponseBit((int)PCCommand.TurnAnlge45, false);


                //Step2
                var step2RobotMoveTasks = new List<Task>();
                
                //1번 로봇 이동
                step2RobotMoveTasks.Add(ExecuteRobotCaptureSequence1Async(robotSession: robotSessions, currentStep: "Step2", currentIndex: ModuleIndex.Module1,
                                      robotTeachedPosition: 2,
                                      carNumber: carNumber, distanceOffset: 500,
                                      camFocus: (float)70, acquisitionAngle: AcquisitionAngle.Angle_0,
                                      firstReceiveCheckString: "Position1-2",
                                      secondReceiveCheckingString: "Position1-3",
                                      firstInfraredPositionName: "Right3",
                                      secondInfraredPositionName: "Right4",
                                      token: token));

                //3번 로봇 이동
                step2RobotMoveTasks.Add(ExecuteRobotCaptureSequence1Async(robotSession: robotSessions, currentStep: "Step2", currentIndex: ModuleIndex.Module3,
                                      robotTeachedPosition: 2,
                                      carNumber: carNumber, distanceOffset: 500,
                                      camFocus: (float)70, acquisitionAngle: AcquisitionAngle.Angle_0,
                                      firstReceiveCheckString: "Position3-2",
                                      secondReceiveCheckingString: "Position3-3",
                                      firstInfraredPositionName: "Right3",
                                      secondInfraredPositionName: "Right4",
                                      token: token));


                //4번 로봇 본넷
                //로봇 엔지니어와 협의 후 시퀀스

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

        //기본 포지션이동 후 수평 or 수직 이동 Sequence
        private async Task ExecuteRobotCaptureSequence1Async(Dictionary<int, RobotSession> robotSession, string currentStep, ModuleIndex currentIndex,
                                            int robotTeachedPosition, string carNumber,  double distanceOffset, float camFocus, AcquisitionAngle acquisitionAngle, 
                                            string firstReceiveCheckString, string secondReceiveCheckingString, 
                                            string firstInfraredPositionName, string secondInfraredPositionName, CancellationToken token)
        {
            RobotSession session = robotSession[(int)currentIndex];
            string currentIndexString = Enum.GetName(typeof(ModuleIndex), currentIndex);
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentIndexString} 로봇 이동 시작", true);
            
            await MoveRobot(currentIndex, robotTeachedPosition, token).ConfigureAwait(false);

            //거리센서 측정
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString} 거리센서 측정", true);
            double distance = await GetDistanceByLaserAsync(currentIndex,token).ConfigureAwait(false);
            double moveDistance = distance - distanceOffset;

            //Robot 전진이동 TCP 신호 전송, 대기
            string robotMessage = $"move,{moveDistance}";
            var robotTCPReceiveOK = await SendAndExpectAsync(session, robotMessage, firstReceiveCheckString, _doneTimeout, ct: token).ConfigureAwait(false);

            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇  전진 이동 응답 없음", true);

            await Task.Delay(_AfterMovingDelay);

            await StartCaptureImage(currentIndex, camFocus, _InfraredFrameCount, acquisitionAngle, $"{carNumber}_{firstInfraredPositionName}");
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString}_{carNumber}_{firstInfraredPositionName} 열화상 이미지 촬영 시작", true);
            await Task.Delay(_CaptureDelay);

            //로봇에 수평이동 TCP 신호 전송, 대기
            robotTCPReceiveOK = await SendAndExpectAsync(session, "ok", secondReceiveCheckingString, _doneTimeout, ct: token).ConfigureAwait(false);
            if (!robotTCPReceiveOK)
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{currentIndexString} 로봇 수평 이동 응답 없음", true);

            await Task.Delay(_AfterMovingDelay);

            await StartCaptureImage(currentIndex, 100, _InfraredFrameCount, AcquisitionAngle.Angle_0, $"{carNumber}_{secondInfraredPositionName}");
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{currentStep} {currentIndexString}_{carNumber}_{secondInfraredPositionName} 열화상 이미지 촬영 시작", true);
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
            int maxHeight =_LightCurtain.Stop();
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
            for (int i = 0; i < _Lasers.Length; i++)
                _Lasers[i].Stop();
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

        public async Task MoveRobot(ModuleIndex moduleIndex, int positionIndex, CancellationToken token)
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
                else
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

        public async Task StartCaptureImage(ModuleIndex index, float focus, int framecnt, AcquisitionAngle angle, string positionName="")
        {

            var statusMap = new Dictionary<ModuleIndex, int>
            {
                { ModuleIndex.Module1, SystemParam.Light1StatusAddress },
                { ModuleIndex.Module2, SystemParam.Light2StatusAddress },
                { ModuleIndex.Module3, SystemParam.Light3StatusAddress },
                { ModuleIndex.Module4, SystemParam.Light4StatusAddress }
            };

            var triggerMap = new Dictionary<ModuleIndex, int>
            {
                { ModuleIndex.Module1, SystemParam.Module1LightOnAddress },
                { ModuleIndex.Module2, SystemParam.Module2LightOnAddress },
                { ModuleIndex.Module3, SystemParam.Module3LightOnAddress },
                { ModuleIndex.Module4, SystemParam.Module4LightOnAddress }
            };

            int statusAddress = statusMap[index];
            int triggerAddress = triggerMap[index];

            try
            {
                if (_CamController != null)
                {
                    await CheckLightStatus(index, statusAddress);
                    _CamController.ReadyCapture(index, focus);
                    await Task.Delay(1000);
                    _CamController.CaptureImage(index, framecnt, SystemParam.ImageDataSavePath, angle);
                    await TurnOnLight(index, triggerAddress);
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"카메라 취득 중 오류 발생: {ex.Message}", true);
                return;
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
                bool lightAck = await WaitForWordBitsAsync(PlcDeviceType.D, statusAddress, new[] { 0, 1 }, true, _ackTimeout, _poll, token, "lightAck ").ConfigureAwait(false);
                if (!lightAck)
                    Logger.Instance.Print(Logger.LogLevel.INFO, $"{Enum.GetName(typeof(ModuleIndex), index)} 조명 상태를 확인하세요", true);
            }
            catch (Exception ex)
            {
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{Enum.GetName(typeof(ModuleIndex), index)} 조명 On 오류 발생: {ex.Message}", true);
            }

        }
        private async Task TurnOnLight(ModuleIndex index, int triggerAddress)
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
                bool startAck = await WaitForWordBitAsync(PlcDeviceType.D, SystemParam.PLCStatusAddress, (int)PLCStatusCommand.ResponseOK, true, _ackTimeout, _poll, token, "START_ACK").ConfigureAwait(false); //응답 대기
                if (!startAck)
                    Logger.Instance.Print(Logger.LogLevel.WARN, $"{Enum.GetName(typeof(ModuleIndex), index)} 조명 On 요청세 PLC 응답 없음", true);
                else
                    await SafeSetDevice(PlcDeviceType.D, triggerAddress, 0);
            }
            catch(Exception ex)
            {
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{Enum.GetName(typeof(ModuleIndex),index)} 조명 On 오류 발생: {ex.Message}", true);
            }
        }

        public void StartCaptureImageAll()
        {
            try
            {
                for (int i = 0; i < SystemParam.CamPathList.Count; i++)
                    _ =StartCaptureImage((ModuleIndex)i, 100, 240, AcquisitionAngle.Angle_0);
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
    }
}
