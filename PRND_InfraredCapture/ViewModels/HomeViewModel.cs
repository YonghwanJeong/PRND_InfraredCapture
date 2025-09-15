using CommunityToolkit.Mvvm.Input;
using CP.Common;
using CP.OptrisCam.models;
using PRND_InfraredCapture.Bases;
using PRND_InfraredCapture.Models;
using System;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace PRND_InfraredCapture.ViewModels
{
    public class HomeViewModel : ViewModelBase
    {

        public ObservableCollection<string> ProgramLogs { get; set; } = Logger.Instance.ProgramLogs;

        private BitmapSource _TestImage;
        public BitmapSource TestImage
        {
            get { return _TestImage; }
            set { SetProperty(ref _TestImage, value); }
        }


        private string _OnOfflineBtnText;
        public string OnOfflineBtnText
        {
            get { return _OnOfflineBtnText; }
            set { SetProperty(ref _OnOfflineBtnText, value); }
        }

        public ICommand InspectionStartCommand { get; set; }
        public ICommand ControlOnOffLineCommand { get; set; }
        public ICommand CaptureImageCommand { get; set; }
        public ICommand PageLoadedCommmand { get; set; }
        public ICommand LightCurtainStartCommand { get; set; }
        public ICommand LightCurtainStopCommand { get; set; }
        public ICommand LaserStartCommand { get; set; }
        public ICommand LaserStopCommand { get; set; }


        private static bool _IsFirstLoaded = true;

        private DateTime _lastUpdateTime = DateTime.UtcNow;
        private int tickCount = 0;
        private ProcessManager _ProcessManager = ProcessManager.Instance;


        public HomeViewModel()
        {
            Title = "Home";
            OnOfflineBtnText = "Start Online";
            PageLoadedCommmand = new RelayCommand(OnPageLoaded);
            
            ControlOnOffLineCommand = new RelayCommand(OnControlOnOfflineCommand);
            InspectionStartCommand = new RelayCommand(OnInspectionStartCommand);
            
            if (_IsFirstLoaded) Initialize();
            ChaningEvent();

            //TestFunc();

        }
        static void FireAndForget(Task t)
        {
            t.ContinueWith(tt =>
            {
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"BG task faulted: {tt.Exception}", true);
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
        private async void TestFunc()
        {
            using (var opCts = new CancellationTokenSource())
            {
                var token = opCts.Token;

                var robotServer = new RobotServer(IPAddress.Any, 50000, _ProcessManager.SystemParam.RobotConnectionList);
                var serverTask = robotServer.StartAsync();

                var session1 = await WaitGetSessionAsync(robotServer, 1,
                    TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);

                if (session1 == null)
                {
                    Logger.Instance.Print(Logger.LogLevel.ERROR, "로봇1 TCP 세션 없음(타임아웃)", true);
                    return;
                }

                var session2 = await WaitGetSessionAsync(robotServer, 2,
                  TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);

                if (session2 == null)
                {
                    Logger.Instance.Print(Logger.LogLevel.ERROR, "로봇1 TCP 세션 없음(타임아웃)", true);
                    return;
                }



                var bgTask = Task.Run(async () =>
                {
                    await session1.SendLineAsync("poseMsg", token).ConfigureAwait(false);

                    using (var rxTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, rxTimeoutCts.Token))
                    {
                        
                        try
                        {
                            await _ProcessManager.ExpectWithRetryAsync(session1, "test", async () =>
                            {
                                await session1.SendLineAsync("poseMsg", token).ConfigureAwait(false);
                            }, linked.Token).ConfigureAwait(false);
                        }
                        catch (TimeoutException)
                        {
                            Logger.Instance.Print(Logger.LogLevel.ERROR, "응답 수신 재시도 초과(타임아웃)", true);
                            return;
                        }
                        catch (OperationCanceledException)
                        {
                            Logger.Instance.Print(Logger.LogLevel.INFO, "사용자/상위 취소로 수신 대기 중단", true);
                            return;
                        }
                    }
                });

                FireAndForget(bgTask);
                await Task.Run(async () =>
                {
                    await session2.SendLineAsync("poseMsg", token).ConfigureAwait(false);

                    using (var rxTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, rxTimeoutCts.Token))
                    {
                        try
                        {
                            await _ProcessManager.ExpectWithRetryAsync(session2, "test", async () =>
                            {
                                await session2.SendLineAsync("poseMsg", token).ConfigureAwait(false);
                            }, linked.Token).ConfigureAwait(false);
                        }
                        catch (TimeoutException)
                        {
                            Logger.Instance.Print(Logger.LogLevel.ERROR, "응답 수신 재시도 초과(타임아웃)", true);
                            return;
                        }
                        catch (OperationCanceledException)
                        {
                            Logger.Instance.Print(Logger.LogLevel.INFO, "사용자/상위 취소로 수신 대기 중단", true);
                            return;
                        }
                    }
                });



                int a = 0;
            }
        }

        public static async Task<RobotSession> WaitGetSessionAsync(
    RobotServer server, int id, TimeSpan timeout, TimeSpan poll, CancellationToken ct)
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


        private void OnInspectionStartCommand()
        {
            _ProcessManager.StartInspectionSequence("Test");
        }

        public void ChaningEvent()
        {
            Logger.Instance.OnLogSavedAction += OnLogSaved;
            _ProcessManager.OnCamLogsaved += OnLogSaved;
            _ProcessManager.OnPLCDisconnected += OnPLCDisconnected;
        }

        private void OnPLCDisconnected(bool isPLCConnectec)
        {
            if(!isPLCConnectec)
            {
                Application.Current?.Dispatcher.BeginInvoke(new Action(delegate ()
                {
                    ProgramLogs.Add("PLC Disconnected");
                }));
            }
            else
            {
                Application.Current?.Dispatcher.BeginInvoke(new Action(delegate ()
                {
                    ProgramLogs.Add("PLC Connected");
                }));
            }
        }

        private void OnLogSaved(string obj)
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(delegate ()
            {
                ProgramLogs.Add(obj);
            }));
        }

        public void DeChaningEvent()
        {
            Logger.Instance.OnLogSavedAction -= OnLogSaved;
            _ProcessManager.OnCamLogsaved -= OnLogSaved;
        }
        private void OnControlOnOfflineCommand()
        {
            if (!_ProcessManager.IsOnlineMode)
            {
                _ProcessManager.StartOnline();
                OnOfflineBtnText = "Stop Online";
                Logger.Instance.Print(Logger.LogLevel.INFO, "Change to Online Mode");
            }
            else
            {
                _ProcessManager.StopOnline();
                OnOfflineBtnText = "Start Online";
                Logger.Instance.Print(Logger.LogLevel.INFO, "Change to Offline Mode");
            }

        }
        private void OnStartLightCurtain()
        {
            _ProcessManager.StartLightCurtain();
        }

        private void OnStopLightCurtain()
        {
            _ProcessManager.StopLightCurtain();
        }

        private void OnLaserStartCommand()
        {
            _ProcessManager.GetDistancebyLaser(CP.OptrisCam.ModuleIndex.Module1);
            _ProcessManager.GetDistancebyLaser(CP.OptrisCam.ModuleIndex.Module2);
        }

        private void OnLaserStopCommand()
        {
            _ProcessManager.StopAllLaserScan();
        }

        private void OnCaptureImageCommand()
        {
            _ProcessManager.StartCaptureImageAll();
        }

        private void OnUpdateGrabCount(int obj)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                ProgramLogs.Add($"초당 GrabCount : {obj}");
            }));
        }

        private void OnImageReceived(Bitmap bitmap)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (bitmap != null)
                    TestImage = BitmapController.BitmapToBitmapSource(bitmap);
            }));
        }

        /// <summary>
        /// 한번만 실행되야 하는 로직 추가
        /// </summary>
        public void Initialize()
        {


            _IsFirstLoaded = false;
        }

        private void OnPageLoaded()
        {
            TestImage = BitmapController.LoadBitmapImage(@"C:\Users\jijon\Work\2. Development\PRND_InfraredCapture\PRND_InfraredCapture\Resources\CustomAir_Cylinder.bmp");
        }


        public override Task OnNavigatedFromAsync()
        {
            Logger.Instance.Print(Logger.LogLevel.INFO, "Escape HomeView");
            DeChaningEvent();
            return base.OnNavigatedFromAsync();
        }

        public override Task OnNavigatedToAsync()
        {
            Logger.Instance.Print(Logger.LogLevel.INFO, "Enter HomeView");
            return base.OnNavigatedToAsync();
        }

        public override void Dispose()
        {
            Logger.Instance.Print(Logger.LogLevel.INFO, "HomeView Disposed");
            base.Dispose();
        }
    }
}
