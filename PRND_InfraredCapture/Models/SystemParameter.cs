using CP.Common.Models;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PRND_InfraredCapture.Models
{
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
        public double LightCurtainHeightOffset{ get; set; } = 440.0;


        //InfraredCam
        public string Cam1ConfigPath { get; set; }
        public string Cam2ConfigPath { get; set; }
        public string Cam3ConfigPath { get; set; }
        public string Cam4ConfigPath { get; set; }
        public string ImageDataSavePath { get; set; }

    }
}
