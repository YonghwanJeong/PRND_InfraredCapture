using CommunityToolkit.Mvvm.ComponentModel;
using CP.Common.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PRND_InfraredCapture.Models
{
    public class FilePathModel : ObservableObject
    {
        private string _filePath;
        public string FilePath
        {
            get => _filePath;
            set => SetProperty(ref _filePath, value);
        }
        public string FileName => Path.GetFileNameWithoutExtension(FilePath);
    }

    public class TCPConnectionPoint : ObservableObject
    {
        private string _ipAddress;
        public string IPAddress
        {
            get => _ipAddress;
            set => SetProperty(ref _ipAddress, value);
        }
        private int _port;
        public int Port
        {
            get => _port;
            set => SetProperty(ref _port, value);
        }
    }
    public class SystemParameter : Parameter
    {
        //PLC
        public string PLCAddress { get; set; }
        public int PLCPort { get; set; }


        //Light Curtain
        // ===== 사용자 설정 =====
        public string LightCurtainPortName { get; set; }
        public int LightCurtainBaudRate { get; set; }

        // mm/스텝 (빔 간격 환산값) — 기본 30mm
        //public double LightCurtainUnit { get; set; } = 30.0;

        // 장비 설치 오프셋(mm). (예: 장비가 바닥에서 떠있는 높이 등)
        public int LightCurtainHeightOffset{ get; set; } = 440;


        //InfraredCam
        public List<string> CamPathList { get; set; } = new List<string>();
        public string ImageDataSavePath { get; set; }

        public List<TCPConnectionPoint> LaserConnectionList { get; set; } = new List<TCPConnectionPoint>();


        public List<TCPConnectionPoint> RobotConnectionList { get; set; } = new List<TCPConnectionPoint>();

    }
}
