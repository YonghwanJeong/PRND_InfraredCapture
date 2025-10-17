using CommunityToolkit.Mvvm.Input;
using CP.Common;
using CP.OptrisCam;
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


        private string _CarNumber;
        public string CarNumber
        {
            get { return _CarNumber; }
            set { SetProperty(ref _CarNumber, value); }
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

        private bool _IsUsingLight;
        public bool IsUsingLight
        {
            get { return _IsUsingLight; }
            set 
            {
                if (SetProperty(ref _IsUsingLight, value))
                {
                    _ProcessManager.SystemParam.IsUsingLight = value;
                }
            }
        }

        //public ICommand TestCommand { get; set; }
        public ICommand ControlOnOffLineCommand { get; set; }
        public ICommand InspectionStartCommand { get; set; }
        public ICommand InspectionStartCommandType2 { get; set; }

        public ICommand InspectionStopCommand { get; set; }
        public ICommand CaptureImageCommand { get; set; }
        public ICommand GetLaserDistanceCommand { get; set; }
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
            InspectionStartCommand = new RelayCommand(OnInspectionStartCommand);
            InspectionStartCommandType2 = new RelayCommand(OnInspectionStartType2Command);
            InspectionStopCommand = new RelayCommand(OnInspectionStopCommand);
            CaptureImageCommand = new RelayCommand<object>(OnCaptureImageCommand);
            GetLaserDistanceCommand = new RelayCommand<object>(OnGetLaserDistanceCommand);
            PageLoadedCommmand = new RelayCommand(OnPageLoaded);
            LightCurtainStartCommand = new RelayCommand(OnStartLightCurtain);
            LightCurtainStopCommand = new RelayCommand(OnStopLightCurtain);
            LaserStartCommand = new RelayCommand(OnLaserStartCommand);
            LaserStopCommand = new RelayCommand(OnLaserStopCommand);
            ConvertRawToBitmapCommand = new RelayCommand(OnConvertRawToBitmapCommand);


            IsUsingLight = _ProcessManager.SystemParam.IsUsingLight;
            if (_IsFirstLoaded) Initialize();
            ChaningEvent();
        }


        public void ProcessAllSubfolders(string rootFolder, int width, int height)
        {
            // 하위 폴더 탐색
            var subFolders = Directory.GetDirectories(rootFolder);
            if (subFolders.Length == 0)
            {
                Logger.Instance.Print(Logger.LogLevel.WARN, $"하위 폴더가 없습니다: {rootFolder}", true);
                return;
            }

            foreach (var subFolder in subFolders)
            {
                try
                {
                    var filePaths = Directory.GetFiles(subFolder, "*.raw")
                                             .OrderBy(p => Path.GetFileNameWithoutExtension(p))
                                             .ToList();

                    if (filePaths.Count == 0)
                    {
                        Logger.Instance.Print(Logger.LogLevel.WARN, $"RAW 파일 없음: {subFolder}", true);
                        continue;
                    }

                    string folderName = Path.GetFileName(subFolder.TrimEnd(Path.DirectorySeparatorChar));
                    string outputFolder = Path.Combine(rootFolder, $"{folderName}_Image");

                    Directory.CreateDirectory(outputFolder);
                    //Directory.CreateDirectory(Path.Combine(outputFolder, "DiffImage"));
                    //Directory.CreateDirectory(Path.Combine(outputFolder, "Image"));

                    Logger.Instance.Print(Logger.LogLevel.INFO, $"폴더 처리 시작: {folderName}", true);

                    //단순 이미지 변환 (필요시 주석 해제)
                    /*
                    int idx = 0;
                    foreach (var filePath in filePaths)
                    {
                        var data = ThermalImageUtil.BuildGrayscaleBitmapFromFloatRaw(filePath, width, height);
                        string imagepath = Path.Combine(outputFolder, "Image", $"{idx}.png");
                        data.Save(imagepath, ImageFormat.Png);
                        idx++;
                    }
                    */

                    // 차이미지 변환
                    float diffMax = 0f;
                    float maxdiffIndex = 0;
                    for (int i = 0; i < filePaths.Count; i++)
                    {
                        float diff = 0;
                        var data = ThermalImageUtil.BuildDiffGray8FromFloatRaw(filePaths[i], filePaths[0], width, height, 0, 10, ref diff);
                        if (diff > diffMax)
                        {
                            diffMax = diff;
                            maxdiffIndex = i;
                        }
                        string imagepath = Path.Combine(outputFolder, $"{Path.GetFileNameWithoutExtension(filePaths[i])}.png");
                        data.Save(imagepath, ImageFormat.Png);
                    }

                    Logger.Instance.Print(Logger.LogLevel.INFO, $"[{folderName}] Max Diff Index : {maxdiffIndex}, Max Diff Span : {diffMax}", true);
                }
                catch (Exception ex)
                {
                    Logger.Instance.Print(Logger.LogLevel.ERROR, $"폴더 처리 중 오류: {subFolder} → {ex.Message}", true);
                }
            }
        }
        private void ConvertRawToBitmap()
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
            float diffMax = 0f;
            int maxDiffIndex = 0;

            foreach (var filePath in filePaths)
            {

                float diff = 0;
                var data = ThermalImageUtil.BuildDiffGray8FromFloatRaw(filePath, filePaths[0], width, height, 0, 10, ref diff);
                if (diff > diffMax)
                {
                    diffMax = diff;
                    maxDiffIndex = i;
                }
                string imagepath = Path.Combine(ConvertTargetPath, "DiffImage", $"{Path.GetFileNameWithoutExtension(filePath)}.png");
                Directory.CreateDirectory(Path.GetDirectoryName(imagepath));
                data.Save(imagepath, ImageFormat.Png);
                i++;
            }
            Logger.Instance.Print(Logger.LogLevel.INFO, $"Max Diff Inex : {maxDiffIndex}, Max Diff Span : {diffMax}", true);
        }
        private void OnConvertRawToBitmapCommand()
        {
            int width = 382;
            int height = 288;
            ProcessAllSubfolders(RawSourcePath, width, height);
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

        private void OnInspectionStartCommand()
        {
            _ProcessManager.StartInspectionSequence(CarNumber);
        }

        private void OnInspectionStartType2Command()
        {
            _ProcessManager.StartInspectionSequenceType2(CarNumber);
        }

        private void OnInspectionStopCommand()
        {
            _ProcessManager.StopInsepction();
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
            //_ProcessManager.GetDistancebyLaserAsync(CP.OptrisCam.ModuleIndex.Module1);
            //_ProcessManager.GetDistancebyLaserAsync(CP.OptrisCam.ModuleIndex.Module2);
        }

        private void OnLaserStopCommand()
        {
            _ProcessManager.StopAllLaserScan();
        }

        private async void OnCaptureImageCommand(object obj)
        {
            int index = Convert.ToInt32(obj);
            //_ProcessManager.StartCaptureWithoutLightCheck((ModuleIndex)index, (float)70.5,80,CP.OptrisCam.models.AcquisitionAngle.Angle_0);
            //await _ProcessManager.StartCaptureImage((ModuleIndex)index, (float)70.5, 80, CP.OptrisCam.models.AcquisitionAngle.Angle_0, CarNumber,"");
            bool isSuccess = await _ProcessManager.StartCaptureImageWithAsync((ModuleIndex)index, (float)70.5, 80, CP.OptrisCam.models.AcquisitionAngle.Angle_0, CarNumber, "");
            Logger.Instance.Print(Logger.LogLevel.INFO, $"이미지 취득 성공 여부 : {isSuccess}", true);
        }
        private void OnGetLaserDistanceCommand(object obj)
        {
            int index = Convert.ToInt32(obj);
            double distance = _ProcessManager.GetDistancebyLaser((ModuleIndex)index);
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
