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
        public ObservableCollection<string> ProgramLogs { get; set; } = Logger.Instance.ProgramLogs;

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

        private int _plcStatusAddress;
        public int PLCStatusAddress
        {
            get => _plcStatusAddress;
            set => SetProperty(ref _plcStatusAddress, value);
        }

        private int _turnTableAngleAddress;
        public int TurnTableAngleAddress
        {
            get => _turnTableAngleAddress;
            set => SetProperty(ref _turnTableAngleAddress, value);
        }

        private int _light1StatusAddress;
        public int Light1StatusAddress
        {
            get => _light1StatusAddress;
            set => SetProperty(ref _light1StatusAddress, value);
        }

        private int _light2StatusAddress;
        public int Light2StatusAddress
        {
            get => _light2StatusAddress;
            set => SetProperty(ref _light2StatusAddress, value);
        }

        private int _light3StatusAddress;
        public int Light3StatusAddress
        {
            get => _light3StatusAddress;
            set => SetProperty(ref _light3StatusAddress, value);
        }

        private int _light4StatusAddress;
        public int Light4StatusAddress
        {
            get => _light4StatusAddress;
            set => SetProperty(ref _light4StatusAddress, value);
        }

        private int _robot1StatusAddress;
        public int Robot1StatusAddress
        {
            get => _robot1StatusAddress;
            set => SetProperty(ref _robot1StatusAddress, value);
        }

        private int _robot2StatusAddress;
        public int Robot2StatusAddress
        {
            get => _robot2StatusAddress;
            set => SetProperty(ref _robot2StatusAddress, value);
        }

        private int _robot3StatusAddress;
        public int Robot3StatusAddress
        {
            get => _robot3StatusAddress;
            set => SetProperty(ref _robot3StatusAddress, value);
        }

        private int _robot4StatusAddress;
        public int Robot4StatusAddress
        {
            get => _robot4StatusAddress;
            set => SetProperty(ref _robot4StatusAddress, value);
        }


        private int _HeartBeatAddress;
        public int HeartBeatAddress
        {
            get { return _HeartBeatAddress; }
            set { SetProperty(ref _HeartBeatAddress, value); }
        }
        private int _plcResponseAddress;
        public int PLCResponseAddress
        {
            get => _plcResponseAddress;
            set => SetProperty(ref _plcResponseAddress, value);
        }

        private int _module1LightOnAddress;
        public int Module1LightOnAddress
        {
            get => _module1LightOnAddress;
            set => SetProperty(ref _module1LightOnAddress, value);
        }

        private int _module2LightOnAddress;
        public int Module2LightOnAddress
        {
            get => _module2LightOnAddress;
            set => SetProperty(ref _module2LightOnAddress, value);
        }

        private int _module3LightOnAddress;
        public int Module3LightOnAddress
        {
            get => _module3LightOnAddress;
            set => SetProperty(ref _module3LightOnAddress, value);
        }

        private int _module4LightOnAddress;
        public int Module4LightOnAddress
        {
            get => _module4LightOnAddress;
            set => SetProperty(ref _module4LightOnAddress, value);
        }

        private int _robot1MoveAddress;
        public int Robot1MoveAddress
        {
            get => _robot1MoveAddress;
            set => SetProperty(ref _robot1MoveAddress, value);
        }

        private int _robot2MoveAddress;
        public int Robot2MoveAddress
        {
            get => _robot2MoveAddress;
            set => SetProperty(ref _robot2MoveAddress, value);
        }

        private int _robot3MoveAddress;
        public int Robot3MoveAddress
        {
            get => _robot3MoveAddress;
            set => SetProperty(ref _robot3MoveAddress, value);
        }

        private int _robot4MoveAddress;
        public int Robot4MoveAddress
        {
            get => _robot4MoveAddress;
            set => SetProperty(ref _robot4MoveAddress, value);
        }

        private int _distanceAlarmAddress;
        public int DistanceAlarmAddress
        {
            get => _distanceAlarmAddress;
            set => SetProperty(ref _distanceAlarmAddress, value);
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
            ChaningEvent();
            UpdateParam2UI();

        }

        public void Initialize()
        {
            

            _IsFirstLoaded = false;
        }

        public void ChaningEvent()
        {
            Logger.Instance.OnLogSavedAction += OnLogSaved;
            _ProcessManager.OnCamLogsaved += OnLogSaved;
        }

        public void DeChaningEvent()
        {
            Logger.Instance.OnLogSavedAction -= OnLogSaved;
            _ProcessManager.OnCamLogsaved -= OnLogSaved;
        }
        private void OnLogSaved(string obj)
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(delegate ()
            {
                ProgramLogs.Add(obj);
            }));
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
            _ProcessManager.SystemParam.PLCStatusAddress = PLCStatusAddress;
            _ProcessManager.SystemParam.TurnTableAngleAddress = TurnTableAngleAddress;
            _ProcessManager.SystemParam.Light1StatusAddress = Light1StatusAddress;
            _ProcessManager.SystemParam.Light2StatusAddress = Light2StatusAddress;
            _ProcessManager.SystemParam.Light3StatusAddress = Light3StatusAddress;
            _ProcessManager.SystemParam.Light4StatusAddress = Light4StatusAddress;
            _ProcessManager.SystemParam.Robot1StatusAddress = Robot1StatusAddress;
            _ProcessManager.SystemParam.Robot2StatusAddress = Robot2StatusAddress;
            _ProcessManager.SystemParam.Robot3StatusAddress = Robot3StatusAddress;
            _ProcessManager.SystemParam.Robot4StatusAddress = Robot4StatusAddress;

            _ProcessManager.SystemParam.HeartBeatAddress = HeartBeatAddress;
            _ProcessManager.SystemParam.PLCResponseAddress = PLCResponseAddress;
            _ProcessManager.SystemParam.Module1LightOnAddress = Module1LightOnAddress;
            _ProcessManager.SystemParam.Module2LightOnAddress = Module2LightOnAddress;
            _ProcessManager.SystemParam.Module3LightOnAddress = Module3LightOnAddress;
            _ProcessManager.SystemParam.Module4LightOnAddress = Module4LightOnAddress;
            _ProcessManager.SystemParam.Robot1MoveAddress = Robot1MoveAddress;
            _ProcessManager.SystemParam.Robot2MoveAddress = Robot2MoveAddress;
            _ProcessManager.SystemParam.Robot3MoveAddress = Robot3MoveAddress;
            _ProcessManager.SystemParam.Robot4MoveAddress = Robot4MoveAddress;
            _ProcessManager.SystemParam.DistanceAlarmAddress = DistanceAlarmAddress;

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
            PLCStatusAddress = _ProcessManager.SystemParam.PLCStatusAddress;
            TurnTableAngleAddress = _ProcessManager.SystemParam.TurnTableAngleAddress;
            Light1StatusAddress = _ProcessManager.SystemParam.Light1StatusAddress;
            Light2StatusAddress = _ProcessManager.SystemParam.Light2StatusAddress;
            Light3StatusAddress = _ProcessManager.SystemParam.Light3StatusAddress;
            Light4StatusAddress = _ProcessManager.SystemParam.Light4StatusAddress;
            Robot1StatusAddress = _ProcessManager.SystemParam.Robot1StatusAddress;
            Robot2StatusAddress = _ProcessManager.SystemParam.Robot2StatusAddress;
            Robot3StatusAddress = _ProcessManager.SystemParam.Robot3StatusAddress;
            Robot4StatusAddress = _ProcessManager.SystemParam.Robot4StatusAddress;

            HeartBeatAddress = _ProcessManager.SystemParam.HeartBeatAddress;
            PLCResponseAddress = _ProcessManager.SystemParam.PLCResponseAddress;
            Module1LightOnAddress = _ProcessManager.SystemParam.Module1LightOnAddress;
            Module2LightOnAddress = _ProcessManager.SystemParam.Module2LightOnAddress;
            Module3LightOnAddress = _ProcessManager.SystemParam.Module3LightOnAddress;
            Module4LightOnAddress = _ProcessManager.SystemParam.Module4LightOnAddress;
            Robot1MoveAddress = _ProcessManager.SystemParam.Robot1MoveAddress;
            Robot2MoveAddress = _ProcessManager.SystemParam.Robot2MoveAddress;
            Robot3MoveAddress = _ProcessManager.SystemParam.Robot3MoveAddress;
            Robot4MoveAddress = _ProcessManager.SystemParam.Robot4MoveAddress;
            DistanceAlarmAddress = _ProcessManager.SystemParam.DistanceAlarmAddress;

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
            DeChaningEvent();
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
