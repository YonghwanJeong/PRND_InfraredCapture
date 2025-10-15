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
                _ = _ProcessManager.StartOnline();
                OnOfflineBtnText = "Stop Online";
                Logger.Instance.Print(Logger.LogLevel.INFO, "Change to Online Mode");
            }
            else
            {
                _ = _ProcessManager.StopOnline();
                OnOfflineBtnText = "Start Online";
                Logger.Instance.Print(Logger.LogLevel.INFO, "Change to Offline Mode");
            }

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
