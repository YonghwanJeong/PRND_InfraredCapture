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


        private double _LightCurtainHeightOffset;
        public double LightCurtainHeightOffset
        {
            get { return _LightCurtainHeightOffset; }
            set { SetProperty(ref _LightCurtainHeightOffset, value); }
        }
        #endregion

        #region InfraredCamera
        public ObservableCollection<string> SupportedICamfileExtensions { get; set; } = new ObservableCollection<string> { "xml"};


        private string _Cam1ConfigPath;
        public string Cam1ConfigPath
        {
            get { return _Cam1ConfigPath; }
            set { SetProperty(ref _Cam1ConfigPath, value); }
        }


        private string _Cam2ConfigPath;
        public string Cam2ConfigPath
        {
            get { return _Cam2ConfigPath; }
            set { SetProperty(ref _Cam2ConfigPath, value); }
        }


        private string _Cam3ConfigPath;
        public string Cam3ConfigPath
        {
            get { return _Cam3ConfigPath; }
            set { SetProperty(ref _Cam3ConfigPath, value); }
        }


        private string _Cam4ConfigPath;
        public string Cam4ConfigPath
        {
            get { return _Cam4ConfigPath; }
            set { SetProperty(ref _Cam4ConfigPath, value); }
        }



        private string _ImageDataSavePath;
        public string ImageDataSavePath
        {
            get { return _ImageDataSavePath; }
            set { SetProperty(ref _ImageDataSavePath, value); }
        }
        #endregion



        private ProcessManager _ProcessManager = ProcessManager.Instance;
        private static bool _IsFirstLoaded = true;


        public SettingViewModel()
        {
            if(_IsFirstLoaded) Initialize();
            
            InitAvailableSerialPort();
            
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

        private void UpdateUI2Param()
        {
            _ProcessManager.SystemParam.PLCAddress = PLCAddress;
            _ProcessManager.SystemParam.PLCPort = PLCPort;
            _ProcessManager.SystemParam.LightCurtainPortName = SelectedComPort;
            _ProcessManager.SystemParam.LightCurtainBaudRate = SelectedBaudRate;
            _ProcessManager.SystemParam.LightCurtainHeightOffset = LightCurtainHeightOffset;
            _ProcessManager.SystemParam.Cam1ConfigPath = Cam1ConfigPath;
            _ProcessManager.SystemParam.Cam2ConfigPath = Cam2ConfigPath;
            _ProcessManager.SystemParam.Cam3ConfigPath = Cam3ConfigPath;
            _ProcessManager.SystemParam.Cam4ConfigPath = Cam4ConfigPath;
            _ProcessManager.SystemParam.ImageDataSavePath = ImageDataSavePath;
        }

        private void UpdateParam2UI()
        {
            PLCAddress = _ProcessManager.SystemParam.PLCAddress;
            PLCPort = _ProcessManager.SystemParam.PLCPort;
            SelectedComPort = _ProcessManager.SystemParam.LightCurtainPortName;
            SelectedBaudRate = _ProcessManager.SystemParam.LightCurtainBaudRate;
            LightCurtainHeightOffset = _ProcessManager.SystemParam.LightCurtainHeightOffset ;
            Cam1ConfigPath = _ProcessManager.SystemParam.Cam1ConfigPath;
            Cam2ConfigPath = _ProcessManager.SystemParam.Cam2ConfigPath;
            Cam3ConfigPath = _ProcessManager.SystemParam.Cam3ConfigPath;
            Cam4ConfigPath = _ProcessManager.SystemParam.Cam4ConfigPath;
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
