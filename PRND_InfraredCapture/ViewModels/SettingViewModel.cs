using CommunityToolkit.Mvvm.Input;
using CP.Common.Util;
using PRND_InfraredCapture.Bases;
using PRND_InfraredCapture.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace PRND_InfraredCapture.ViewModels
{
    public class SettingViewModel : ViewModelBase
    {
        #region PLC Address Field
        private string _pLCAddress;
        public string PLCAddress
        {
            get => _pLCAddress;
            set => SetProperty(ref _pLCAddress, value);
        }

        private int _pLCPort;
        public int PLCPort
        {
            get => _pLCPort;
            set => SetProperty(ref _pLCPort, value);
        }
        #endregion

        #region LightCurtain
        //Serial Properties
        public ObservableCollection<string> AvailableComPortCollection { get; set; }

        private string _selectedComPort;
        public string SelectedComPort { get => _selectedComPort; set => SetProperty(ref _selectedComPort, value); }

        public ObservableCollection<int> AvailableBaudRateCollection { get; set; }

        private int _selectedBaudRate;
        public int SelectedBaudRate { get => _selectedBaudRate; set => SetProperty(ref _selectedBaudRate, value); }


        private double _LightCurtainUnit;
        public double LightCurtainUnit
        {
            get { return _LightCurtainUnit; }
            set { SetProperty(ref _LightCurtainUnit, value); }
        }


        private int _LightCurtainHeightOffset;
        public int LightCurtainHeightOffset
        {
            get { return _LightCurtainHeightOffset; }
            set { SetProperty(ref _LightCurtainHeightOffset, value); }
        }
        #endregion

        #region InfraredCamera
        public ObservableCollection<string> SupportedICamfileExtensions { get; set; } = new ObservableCollection<string> { "xml"};

        public ObservableCollection<FilePathModel> CamPathList { get; set; } = new ObservableCollection<FilePathModel>();
        public ObservableCollection<TCPConnectionPoint> LaserConnectionList { get; set; } = new ObservableCollection<TCPConnectionPoint>();
        public ObservableCollection<TCPConnectionPoint> RobotConnectionList { get; set; } = new ObservableCollection<TCPConnectionPoint>();

        private string _ImageDataSavePath;
        public string ImageDataSavePath
        {
            get { return _ImageDataSavePath; }
            set { SetProperty(ref _ImageDataSavePath, value); }
        }
        #endregion


        public ICommand AddCamCommand { get; set; }
        public ICommand DelCamCommand { get; set; }
        public ICommand AddLaserCommand { get; set; }
        public ICommand DelLaserCommand { get; set; }
        public ICommand AddRobotCommand { get; set; }
        public ICommand DelRobotCommand { get; set; }
        



        private ProcessManager _ProcessManager = ProcessManager.Instance;
        private static bool _IsFirstLoaded = true;


        public SettingViewModel()
        {
            if(_IsFirstLoaded) Initialize();
            
            InitAvailableSerialPort();
            AddCamCommand = new RelayCommand(OnAddCam);
            DelCamCommand = new RelayCommand(OnDelCam);
            AddLaserCommand = new RelayCommand(OnAddLaser);
            DelLaserCommand = new RelayCommand(OnDelLaser);
            AddRobotCommand = new RelayCommand(OnAddRobot);
            DelRobotCommand = new RelayCommand(OnDelRobot);

            UpdateParam2UI();

        }

        public void Initialize()
        {
            

            _IsFirstLoaded = false;
        }

        public void InitAvailableSerialPort()
        {
            AvailableComPortCollection = new ObservableCollection<string>(SerialComm.GetAvailablePorts());

            AvailableBaudRateCollection = new ObservableCollection<int>{
            1200,   // 저속
            2400,
            4800,
            9600,   // 기본값으로 많이 사용
            14400,
            19200,
            38400,
            57600,
            115200, // 고속 통신
            230400,
            460800,
            921600  // 초고속 통신
            };
        }
        private void OnDelCam()
        {
            CamPathList.RemoveAt(CamPathList.Count - 1);
        }

        private void OnAddCam()
        {
            CamPathList.Add(new FilePathModel { FilePath=""});

        }

        private void OnDelLaser()
        {
            LaserConnectionList.RemoveAt(LaserConnectionList.Count - 1);
        }

        private void OnAddLaser()
        {
            LaserConnectionList.Add(new TCPConnectionPoint { IPAddress = "", Port = 0 });
        }

        private void OnDelRobot()
        {
            RobotConnectionList.RemoveAt(RobotConnectionList.Count - 1);
        }

        private void OnAddRobot()
        {
            RobotConnectionList.Add(new TCPConnectionPoint { IPAddress = "", Port = 0 });
        }


        private void UpdateUI2Param()
        {
            _ProcessManager.SystemParam.PLCAddress = PLCAddress;
            _ProcessManager.SystemParam.PLCPort = PLCPort;
            _ProcessManager.SystemParam.LightCurtainPortName = SelectedComPort;
            _ProcessManager.SystemParam.LightCurtainBaudRate = SelectedBaudRate;
            _ProcessManager.SystemParam.LightCurtainHeightOffset = LightCurtainHeightOffset;
            _ProcessManager.SystemParam.CamPathList = CamPathList.Select(item => item.FilePath).ToList();
            _ProcessManager.SystemParam.ImageDataSavePath = ImageDataSavePath;
            _ProcessManager.SystemParam.LaserConnectionList = LaserConnectionList.ToList();
            _ProcessManager.SystemParam.RobotConnectionList= RobotConnectionList.ToList();

            _ProcessManager.SaveSystemParameter();
        }

        private void UpdateParam2UI()
        {
            PLCAddress = _ProcessManager.SystemParam.PLCAddress;
            PLCPort = _ProcessManager.SystemParam.PLCPort;
            SelectedComPort = _ProcessManager.SystemParam.LightCurtainPortName;
            SelectedBaudRate = _ProcessManager.SystemParam.LightCurtainBaudRate;
            LightCurtainHeightOffset = _ProcessManager.SystemParam.LightCurtainHeightOffset ;
            CamPathList.Clear();
            _ProcessManager.SystemParam.CamPathList.Select(p=> new FilePathModel { FilePath = p }).ToList().ForEach(p => CamPathList.Add(p));
            LaserConnectionList.Clear();
            foreach (var item in _ProcessManager.SystemParam.LaserConnectionList)
                LaserConnectionList.Add(item);
            RobotConnectionList.Clear();
            foreach (var item in _ProcessManager.SystemParam.RobotConnectionList)
                RobotConnectionList.Add(item);
            ImageDataSavePath = _ProcessManager.SystemParam.ImageDataSavePath;
        }


        public override Task OnNavigatedFromAsync()
        {
            UpdateUI2Param();
            Logger.Instance.Print(Logger.LogLevel.INFO, "Escape SeetingView");
            return base.OnNavigatedFromAsync();
        }

        public override Task OnNavigatedToAsync()
        {
            Logger.Instance.Print(Logger.LogLevel.INFO, "Enter SeetingView");
            return base.OnNavigatedToAsync();
        }
        public override void Dispose()
        {
            Logger.Instance.Print(Logger.LogLevel.INFO, "SettingView Disposed");
            base.Dispose();
        }


    }
}
