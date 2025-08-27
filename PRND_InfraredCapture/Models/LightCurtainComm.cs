using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PRND_InfraredCapture.Models
{
    public class LightCurtainComm
    {
        // ===== 사용자 설정 =====
        private string _PortName { get; set; } 
        private int _BaudRate { get; set; } 
        private Parity _ParityMode { get; set; } = Parity.None;
        private int _DataBits { get; set; } = 8;
        private StopBits StopBitsMode { get; set; } = StopBits.One;

        // mm/스텝 (빔 간격 환산값) — 기본 30mm
        private int _MmPerStep { get; set; } = 30;

        // 장비 설치 오프셋(mm). (예: 장비가 바닥에서 떠있는 높이 등)
        private int _OffsetMm { get; set; } = 440;

        // Half-Duplex 모듈 수동 전환 쓰는 경우만 사용 (대부분은 false로 둠)
        private bool UseRtsReceiveHold { get; set; } = false;
        private bool RtsReceiveLevel { get; set; } = false;

        private SerialPort _port;
        private CancellationTokenSource _cts;
        private Task _rxTask;
        private int _MaxHeight = 0;

        public LightCurtainComm(string portName, int baudRate)
        {
            _PortName = portName;
            _BaudRate = baudRate;
        }

        public void Start(int heightOffset)
        {
            if (_rxTask != null) return;

            _OffsetMm = heightOffset;
            _cts = new CancellationTokenSource();
            _MaxHeight = 0;

            _port = new SerialPort(_PortName, _BaudRate, _ParityMode, _DataBits, StopBitsMode)
            {
                ReadTimeout = 500,
                WriteTimeout = 500,
                Handshake = Handshake.None,
                DtrEnable = false,
                RtsEnable = false
            };

            try
            {
                _port.Open();
                if (UseRtsReceiveHold) _port.RtsEnable = RtsReceiveLevel;
            }
            catch (Exception ex)
            {
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"포트 오픈 실패: {ex.Message}", true);
                _port?.Dispose();
                _port = null;
                return;
            }

            _rxTask = Task.Run(() => ReceiveLoop(_cts.Token), _cts.Token);
        }

        public int Stop()
        {
            try
            {
                _cts?.Cancel();
                _rxTask?.Wait(500);
            }
            catch { /* ignore */ }
            finally
            {
                if (_port != null)
                {
                    try { if (_port.IsOpen) _port.Close(); } catch { }
                    _port.Dispose();
                    _port = null;
                }
                _rxTask = null;
                _cts = null;
                Logger.Instance.Print(Logger.LogLevel.INFO, $"LightCurtain 수신 정지");
            }
            return _MaxHeight;
        }

        private void ReceiveLoop(CancellationToken ct)
        {
            var buffer = new List<byte>(256);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    int available = _port?.BytesToRead ?? 0;
                    if (available > 0)
                    {
                        var temp = new byte[available];
                        int n = _port.Read(temp, 0, temp.Length);
                        if (n > 0) buffer.AddRange(temp);
                    }

                    // 2바이트 단위로만 파싱
                    while (buffer.Count >= 2)
                    {
                        byte lowIdx = buffer[0];
                        byte highIdx = buffer[1];
                        buffer.RemoveRange(0, 2);

                        // 위치(mm) 계산
                        int lowMmRaw = lowIdx * _MmPerStep;
                        int highMmRaw = highIdx * _MmPerStep;

                        int lowMmAdj = lowMmRaw + _OffsetMm;
                        int highMmAdj = highMmRaw + _OffsetMm;

                        // (선택) 두 지점 차이도 보고 싶으면:
                        int spanMm = (highIdx - lowIdx) * _MmPerStep;

                        OnPairDecoded(lowIdx, highIdx, lowMmAdj, highMmAdj, spanMm);
                    }

                    Thread.Sleep(3);
                }
                catch (TimeoutException)
                {
                    // ignore
                }
                catch (Exception ex)
                {
                    Logger.Instance.Print(Logger.LogLevel.ERROR, $"수신 오류 : {ex.Message}");
                    Thread.Sleep(20);
                }
            }
        }

        /// <summary>
        /// 디코딩된 한 쌍(low, high) 이벤트 콜백.
        /// 여기서 UI에 바인딩하거나 로그로 출력하세요.
        /// </summary>
        private void OnPairDecoded(byte lowIdx, byte highIdx, int lowMmAdj, int highMmAdj, int spanMm)
        {
            // 예시: Debug 출력 (필요 시 ObservableCollection 등에 추가)
            string msg =
                $"LOW: idx={lowIdx}, h={lowMmAdj:0.##} mm | " +
                $"HIGH: idx={highIdx}, h={highMmAdj:0.##} mm | " +
                $"SPAN: {spanMm:0.##} mm (MmPerStep={_MmPerStep}, Offset={_OffsetMm})";

            if(_MaxHeight< highMmAdj)
                _MaxHeight = highMmAdj;

            Logger.Instance.Print(Logger.LogLevel.INFO, $"{msg}", true);

        }

    }
}
