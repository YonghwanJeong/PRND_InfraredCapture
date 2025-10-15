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

        //From PLC
        /// <summary>
        /// Index 0: 차량진입, index 1 : 차량출차 완료, Index10 : 수동, Index11 : 자동, Inded12 : 설비알람, Index14 : 시퀀스 초기화, Index15 : 응답완료
        /// </summary>
        public int PLCStatusAddress{ get; set; }
        public int TurnTableAngleAddress { get; set; }
        public int Light1StatusAddress { get; set; }
        public int Light2StatusAddress { get; set; }
        public int Light3StatusAddress { get; set; }
        public int Light4StatusAddress { get; set; }
        public int Robot1StatusAddress { get; set; }
        public int Robot2StatusAddress { get; set; }
        public int Robot3StatusAddress { get; set; }
        public int Robot4StatusAddress { get; set; }


        //ToPLC
        
        public int HeartBeatAddress { get; set; }
        /// <summary>
        /// Index 0: 검사시작, Index1 : 0도 회전요청, Index2 : 45도 회전 요청, Index3 : 200도 회전 요청, index4:180도 회전 요청 Index15 : Heartbeat신호
        /// </summary>
        public int PLCResponseAddress { get; set; }
        public int Module1LightOnAddress{ get; set; }
        public int Module2LightOnAddress { get; set; }
        public int Module3LightOnAddress { get; set; }
        public int Module4LightOnAddress { get; set; }
        public int Robot1MoveAddress { get; set; }
        public int Robot2MoveAddress { get; set; }
        public int Robot3MoveAddress { get; set; }
        public int Robot4MoveAddress { get; set; }
        public int DistanceAlarmAddress { get; set; }



        //Light Curtain
        // ===== 사용자 설정 =====
        public string LightCurtainPortName { get; set; }
        public int LightCurtainBaudRate { get; set; }

        // mm/스텝 (빔 간격 환산값) — 기본 30mm
        //public double LightCurtainUnit { get; set; } = 30.0;

        // 장비 설치 오프셋(mm). (예: 장비가 바닥에서 떠있는 높이 등)
        public int LightCurtainHeightOffset{ get; set; } = 750;


        //InfraredCam
        public List<string> CamPathList { get; set; } = new List<string>();
        public string ImageDataSavePath { get; set; }
        public bool IsUsingLight { get; set; } = true;

        public List<TCPConnectionPoint> LaserConnectionList { get; set; } = new List<TCPConnectionPoint>();


        public List<TCPConnectionPoint> RobotConnectionList { get; set; } = new List<TCPConnectionPoint>();

    }
}
