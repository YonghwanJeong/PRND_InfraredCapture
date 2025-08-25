using CommunityToolkit.Mvvm.Input;
using CP.Common;
using OptrisCam;
using PRND_InfraredCapture.Bases;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

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


        private int _CurrentXoffset;
        public int CurrentXoffset
        {
            get { return _CurrentXoffset; }
            set { SetProperty(ref _CurrentXoffset, value); }
        }


        private int _CurrentYoffset;
        public int CurrentYoffset
        {
            get { return _CurrentYoffset; }
            set { SetProperty(ref _CurrentYoffset, value); }
        }


        private double _CurrentScale;
        public double CurrentScale
        {
            get { return _CurrentScale; }
            set { SetProperty(ref _CurrentScale, value); }
        }


        //public ICommand TestCommand { get; set; }
        public ICommand ConnectCommnad { get; set; }
        public ICommand CaptureImageCommand { get; set; }
        public ICommand DisConnectCommand { get; set; }
        public ICommand PageLoadedCommmand { get; set; }

        
        private static bool _IsFirstLoaded = true;

        private DateTime _lastUpdateTime = DateTime.UtcNow;
        private int tickCount = 0;
        private CamController _Controller = new CamController();


        public HomeViewModel()
        {
            Title = "Home";
            ConnectCommnad = new RelayCommand(OnConnectCommand);
            CaptureImageCommand = new RelayCommand(OnCaptureImageCommand);
            PageLoadedCommmand = new RelayCommand(OnPageLoaded);
            DisConnectCommand = new RelayCommand(OnDisconnectCommand);
            if (_IsFirstLoaded) Initialize();


            _Controller = new CamController();
            _Controller.OnReceiveImageAction += OnImageReceived;
            _Controller.OnUpdateGrabCount += OnUpdateGrabCount;

        }

        private void OnDisconnectCommand()
        {
            _Controller.Disconnect();
        }

        private void OnCaptureImageCommand()
        {
            _Controller.CaptureImage(80, @"C:\TestFile\250825");
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

        private void OnConnectCommand()
        {
            _Controller.Connect();
            //_Controller.StartImageLoop();
        }

        public override Task OnNavigatedFromAsync()
        {
            Logger.Instance.Print(Logger.LogLevel.INFO, "Escape HomeView");
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
