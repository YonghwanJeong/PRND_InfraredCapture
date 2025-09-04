using CommunityToolkit.Mvvm.Input;
using CP.Common;
using PRND_InfraredCapture.Bases;
using PRND_InfraredCapture.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace PRND_InfraredCapture.ViewModels
{
    public class TestViewModel : ViewModelBase
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


        private string _RawSourcePath;
        public string RawSourcePath
        {
            get { return _RawSourcePath; }
            set { SetProperty(ref _RawSourcePath, value); }
        }


        private string _ConvertTargetPath;
        public string ConvertTargetPath
        {
            get { return _ConvertTargetPath; }
            set { SetProperty(ref _ConvertTargetPath, value); }
        }

        //public ICommand TestCommand { get; set; }
        public ICommand ControlOnOffLineCommand { get; set; }
        public ICommand CaptureImageCommand { get; set; }
        public ICommand PageLoadedCommmand { get; set; }
        public ICommand LightCurtainStartCommand { get; set; }
        public ICommand LightCurtainStopCommand { get; set; }
        public ICommand LaserStartCommand { get; set; }
        public ICommand LaserStopCommand { get; set; }
        public ICommand ConvertRawToBitmapCommand { get; set; }


        private static bool _IsFirstLoaded = true;

        private DateTime _lastUpdateTime = DateTime.UtcNow;
        private int tickCount = 0;
        private ProcessManager _ProcessManager = ProcessManager.Instance;

        public TestViewModel()
        {
            Title = "Home";
            OnOfflineBtnText = "Start Online";
            ControlOnOffLineCommand = new RelayCommand(OnControlOnOfflineCommand);
            CaptureImageCommand = new RelayCommand(OnCaptureImageCommand);
            PageLoadedCommmand = new RelayCommand(OnPageLoaded);
            LightCurtainStartCommand = new RelayCommand(OnStartLightCurtain);
            LightCurtainStopCommand = new RelayCommand(OnStopLightCurtain);
            LaserStartCommand = new RelayCommand(OnLaserStartCommand);
            LaserStopCommand = new RelayCommand(OnLaserStopCommand);
            ConvertRawToBitmapCommand = new RelayCommand(OnConvertRawToBitmapCommand);
            if (_IsFirstLoaded) Initialize();
            ChaningEvent();
        }

        private void OnConvertRawToBitmapCommand()
        {

            var filePaths = Directory.GetFiles(RawSourcePath, "*.raw")
                             .OrderBy(p => Path.GetFileNameWithoutExtension(p))
                             .ToList();

            int width = 382;
            int height = 288;
            //int width = 288;
            //int height = 384;
            int i = 0;
            //단순 이미지 반환
            //foreach (var filePath in filePaths)
            //{
            //    var data = ThermalImageUtil.BuildGrayscaleBitmapFromFloatRaw(filePath,width,height);
            //    string imagepath = Path.Combine(ConvertTargetPath, "Image", $"{i.ToString()}.png");
            //    Directory.CreateDirectory(Path.GetDirectoryName(imagepath));
            //    data.Save(imagepath,ImageFormat.Png);
            //    i++;
            //}

            //차이미지 반환
            foreach (var filePath in filePaths)
            {
                var data = ThermalImageUtil.BuildDiffGray8FromFloatRaw(filePath, filePaths[0], width, height,0,5);
                string imagepath = Path.Combine(ConvertTargetPath, "DiffImage", $"{i.ToString()}.png");
                Directory.CreateDirectory(Path.GetDirectoryName(imagepath));
                data.Save(imagepath, ImageFormat.Png);
                i++;
            }
        }

        public void ChaningEvent()
        {
            Logger.Instance.OnLogSavedAction += OnLogSaved;
            _ProcessManager.OnCamLogsaved += OnLogSaved;
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
