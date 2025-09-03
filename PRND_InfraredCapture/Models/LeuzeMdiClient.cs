using CP.OptrisCam;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PRND_InfraredCapture.Models
{
    // ======== Parser (Distance만) ========
    public class Header
    {
        public byte PacketType;      // 0=Distance only, 1=Distance+Intensity
        public ushort PacketSize;
        public ushort PacketNo;
        public byte TotalNo;
        public byte SubNo;
        public ushort ScanFreqHz;
        public ushort ScanSpots;
        public int FirstAngle_mdeg;
        public int DeltaAngle_mdeg;
        public ushort Timestamp_ms;
    }
    public struct MinAvgResult
    {
        public int FramesConsidered;     // 실제 집계한 프레임 수
        public int StartIndex;           // 최소 평균이 나온 5점 윈도우 시작 spot index
        public double AvgDistanceMm;     // 최소 평균 거리(mm)
        public double AngleDeg;          // 해당 윈도우 중앙 각도(도)
        public Header HeaderAtMin;       // 그 순간의 헤더
    }


    public class LeuzeMdiClient : IDisposable
    {
        // ===== Public props =====
        public bool IsRunning => _receiveTask != null && !_receiveTask.IsCompleted;

        // ===== Public =====
        public event Action<ModuleIndex, Header, ushort[], ushort[]> FrameReceived; // (Header, Dist[], Intensity[])
        public bool IsConnected => _client != null && _client.Connected;

        public LeuzeMdiClient(ModuleIndex index, string host, int port, TimeSpan? reconnectBackoff = null)
        {
            _ModuleIndex = index;
            _host = host;
            _port = port;
        }

        /// <summary>
        /// 수신/파싱 모니터링 시작(백그라운드). 필요시 sendStartCommand=true로 명령도 같이 보냄.
        /// </summary>
        public async Task StartMonitoringAsync(bool sendStartCommand = true)
        {
            if (!IsConnected) return;
            if (IsRunning) return;

            if (sendStartCommand) await SendStartCommandAsync().ConfigureAwait(false);

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            // .NET Fx 4.7은 ReadAsync 취소가 안 되므로 취소 시 소켓을 닫아 깨운다
            token.Register(() => { try { _client?.Close(); } catch { } });

            _receiveTask = Task.Run(() => ReceiveLoopAsync(token));
        }
        /// <summary>모니터링만 중지(소켓은 유지). 필요하면 이후 StartMonitoringAsync로 재개가능.</summary>
        public void Stop()
        {
            if (!IsRunning) return;
            try { _cts?.Cancel(); } catch { }
        }

        /// <summary>모니터링 중지 후 소켓 연결 종료.</summary>
        public async Task DisconnectAsync()
        {
            Stop();
            await WaitUntilStoppedAsync().ConfigureAwait(false);
            DisposeStreamsOnly();
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{Enum.GetName(typeof(ModuleIndex), _ModuleIndex)} Disconnected", true);
        }

        /// <summary>모니터링 루프가 끝날 때까지 대기(선택)</summary>
        public Task WaitUntilStoppedAsync()
        {
            return _receiveTask ?? Task.CompletedTask;
        }

        /// <summary>
        /// '지금부터' frames 프레임을 모아, 인접 window점 평균 Distance가 가장 짧을 때의
        /// 평균거리/각도를 계산한다. ROI와 stride로 연산량을 줄일 수 있다.
        /// (사전조건: Connect + StartMonitoringAsync로 프레임이 들어오고 있어야 함)
        /// </summary>
        public Task<MinAvgResult> CaptureMinAvgAsync(
            int frames = 10,      // 모을 프레임 수
            int window = 5,       // 인접 점수(평균 구간)
            int? roiStart = null, // 관심 구간 시작 인덱스 (null=0)
            int? roiEnd = null,   // 관심 구간 끝 인덱스(미포함, null=spots)
            int stride = 1,       // 스캔 간격(>=1). 2~4로 올리면 연산량 ↓
            bool ignoreZero = true, // 0 mm는 무효로 간주(아주 큰 값으로 취급)
            CancellationToken ct = default)
        {
            // 중복 실행 방지
            if (System.Threading.Interlocked.CompareExchange(ref _captureActive, 1, 0) != 0)
                throw new InvalidOperationException("Capture is already running.");

            var tcs = new TaskCompletionSource<MinAvgResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            int targetFrames = Math.Max(1, frames);
            int w = Math.Max(1, window);
            int st = Math.Max(1, stride);

            int bestSum = int.MaxValue;
            int bestStartIdx = -1;
            Header bestHdr = null;
            int framesSeen = 0;

            // 취소 처리: 구독 해제 & 플래그 리셋
            if (ct.CanBeCanceled)
            {
                ct.Register(() =>
                {
                    FrameReceived -= handler;
                    System.Threading.Interlocked.Exchange(ref _captureActive, 0);
                    tcs.TrySetCanceled();
                });
            }

            // 이벤트 핸들러(로컬 함수)
            void handler(ModuleIndex index, Header hdr, ushort[] dist, ushort[] inten)
            {
                try
                {
                    if (dist == null || dist.Length < w)
                    {
                        if (++framesSeen >= targetFrames) finish();
                        return;
                    }

                    int spots = dist.Length;
                    int start = Math.Max(0, roiStart ?? 0);
                    int endEx = Math.Min(spots, roiEnd ?? spots);
                    if (endEx - start < w)
                    {
                        if (++framesSeen >= targetFrames) finish();
                        return;
                    }

                    // 값 변환(0을 무효로 크게 취급)
                    Func<int, int> D = i =>
                    {
                        int d = dist[i];
                        if (ignoreZero && d == 0) return 1_000_000;
                        return d;
                    };

                    // 첫 윈도우
                    int localBestSum = int.MaxValue;
                    int localBestIdx = start;

                    for (int i = start; i + w <= endEx; i += st)
                    {
                        int sum = 0;
                        for (int k = 0; k < w; k++) sum += D(i + k);
                        if (sum < localBestSum)
                        {
                            localBestSum = sum;
                            localBestIdx = i;
                        }
                    }

                    if (localBestSum < bestSum)
                    {
                        bestSum = localBestSum;
                        bestStartIdx = localBestIdx;
                        bestHdr = hdr;
                    }

                    if (++framesSeen >= targetFrames) finish();
                }
                catch
                {
                    // 예외 시 안전 해제
                    FrameReceived -= handler;
                    System.Threading.Interlocked.Exchange(ref _captureActive, 0);
                    throw;
                }
            }

            void finish()
            {
                FrameReceived -= handler;
                System.Threading.Interlocked.Exchange(ref _captureActive, 0);

                var res = new MinAvgResult
                {
                    FramesConsidered = framesSeen,
                    StartIndex = bestStartIdx,
                    AvgDistanceMm = (bestSum == int.MaxValue) ? double.NaN : bestSum / (double)w,
                    AngleDeg = (bestStartIdx < 0)
                        ? double.NaN
                        : (bestHdr.FirstAngle_mdeg +
                            (bestStartIdx + (w / 2.0)) * bestHdr.DeltaAngle_mdeg) / 1000.0,
                    HeaderAtMin = bestHdr
                };
                tcs.TrySetResult(res);
            }

            // 구독 시작
            FrameReceived += handler;

            return tcs.Task;
        }


        public void Dispose() => DisposeStreamsOnly();


        // ===== Internals =====
        private readonly string _host;
        private readonly int _port;
        private TcpClient _client;
        private NetworkStream _stream;

        private CancellationTokenSource _cts;
        private Task _receiveTask;

        private static readonly byte[] SYNC = { 0x4C, 0x45, 0x55, 0x5A }; // 'L','E','U','Z'
        private const int HEADER_SIZE = 31;
        private const int CRC_SIZE = 2;
        private const int MIN_PACKET = HEADER_SIZE + CRC_SIZE; // 33
        private const int MAX_PACKET = 1433;

        // STX + "cWN SendMDI" + ETX
        private static readonly byte[] CMD_SEND_MDI = new byte[]
        { 0x02, 0x63, 0x57, 0x4E, 0x20, 0x53, 0x65, 0x6E, 0x64, 0x4D, 0x44, 0x49, 0x03 };

        private int _captureActive;
        private ModuleIndex _ModuleIndex;


        #region 레이저 프로파일 비상 정지 로직 관련 
        // ===== 경고 판정 파라미터 =====
        const int PIX_WINDOW = 10;      // 인접 픽셀 평균 창
        const int FRAME_WINDOW = 10;      // 누적 프레임 수
        const double DIST_THRESH = 450.0;   // 경고 임계(mm)
        const int REQUIRED_COUNT = 7;       // 최근 10프레임 중 N프레임 이상이 임계 미만이면 경고

        // (선택) 해제 히스테리시스
        const double CLEAR_THRESH = 500.0;  // 해제 임계(mm)
        const int CLEAR_FRAMES = 3;      // 해제 연속 프레임 수

        // ===== 상태 버퍼 =====
        double[] _frameMinBuf = new double[FRAME_WINDOW];
        int _frameMinPos = 0;
        int _frameMinCount = 0;
        bool _warnActive = false;
        int _clearStreak = 0;

        // dist에서 인접 window 평균들의 최소값(=프레임 최소 평균)을 O(N)으로 계산
        private static bool TryMinMovingAvg(ushort[] dist, out double minAvg, int window = PIX_WINDOW, bool ignoreZero = true)
        {
            minAvg = double.NaN;
            if (dist == null || dist.Length < window) return false;

            Func<int, int> D = i => (ignoreZero && dist[i] == 0) ? 1_000_000 : dist[i];

            int sum = 0;
            for (int i = 0; i < window; i++) sum += D(i);
            int best = sum;

            for (int i = 1; i + window <= dist.Length; i++)
            {
                sum += D(i + window - 1) - D(i - 1);
                if (sum < best) best = sum;
            }

            minAvg = best / (double)window;
            return true;
        }
        #endregion

        private void DisposeStreamsOnly()
        {
            try { _stream?.Dispose(); } catch { }
            try { _client?.Close(); } catch { }
            _stream = null;
            _client = null;
            _receiveTask = null;
            _cts = null;
        }

        /// <summary>소켓 연결만 수행 (필요 시 여러 번 호출 가능)</summary>
        public async Task ConnectAsync()
        {
            if (IsConnected) return;
            try
            {
                DisposeStreamsOnly();
                _client = new TcpClient { NoDelay = true };
                await _client.ConnectAsync(_host, _port).ConfigureAwait(false);
                _stream = _client.GetStream();
                Logger.Instance.Print(Logger.LogLevel.INFO, $"{Enum.GetName(typeof(ModuleIndex), _ModuleIndex)} Connected", true);
            }
            catch(Exception ex)
            {
                Logger.Instance.Print(Logger.LogLevel.ERROR, $"{Enum.GetName(typeof(ModuleIndex), _ModuleIndex)} Connect fail: {ex.Message}", true);
                DisposeStreamsOnly();
            }
        }

        private Task SendStartCommandAsync()
        {
            if (_stream == null) throw new InvalidOperationException("Not connected.");
            Logger.Instance.Print(Logger.LogLevel.INFO, $"{Enum.GetName(typeof(ModuleIndex), _ModuleIndex)} SendMDI command.", true);
            // .NET Fx 4.7에는 CancellationToken 버전 오버로드가 없으므로 기본 오버로드 사용
            return _stream.WriteAsync(CMD_SEND_MDI, 0, CMD_SEND_MDI.Length);
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buf = new byte[8192];
            using (var stash = new MemoryStream(64 * 1024))
            {
                while (!ct.IsCancellationRequested && IsConnected)
                {
                    try
                    {
                        int n = await _stream.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
                        if (n <= 0) throw new IOException("Remote closed.");
                        stash.Write(buf, 0, n);

                        ExtractPacketsSyncDelimited(stash, packet =>
                        {
                            Header hdr; ushort[] dist; ushort[] inten; string err;
                            if (TryParseFrame(packet, out hdr, out dist, out inten, out err))
                            {
                                // === (A) 프레임 최소 평균 계산 ===
                                if (TryMinMovingAvg(dist, out double frameMinAvg))
                                {
                                    // 링버퍼에 최근 10프레임 누적
                                    _frameMinBuf[_frameMinPos] = frameMinAvg;
                                    _frameMinPos = (_frameMinPos + 1) % FRAME_WINDOW;
                                    if (_frameMinCount < FRAME_WINDOW) _frameMinCount++;

                                    // 가득 찼을 때만 판정
                                    if (_frameMinCount == FRAME_WINDOW)
                                    {
                                        int below = 0;
                                        for (int i = 0; i < FRAME_WINDOW; i++)
                                            if (_frameMinBuf[i] < DIST_THRESH) below++;

                                        if (!_warnActive && below >= REQUIRED_COUNT)
                                        {
                                            _warnActive = true;
                                            _clearStreak = 0;
                                            Logger.Instance.Print(
                                                Logger.LogLevel.INFO,
                                                $"{Enum.GetName(typeof(ModuleIndex), _ModuleIndex)} 경고: 너무 가깝습니다 " +
                                                $"(최근 {FRAME_WINDOW}프레임 중 {below}프레임 < {DIST_THRESH}mm, 현재={frameMinAvg:F1}mm)",
                                                true);
                                        }
                                        else if (_warnActive)
                                        {
                                            // 해제 히스테리시스: CLEAR_THRESH 초과가 n프레임 연속이면 OFF
                                            if (frameMinAvg > CLEAR_THRESH)
                                            {
                                                if (++_clearStreak >= CLEAR_FRAMES)
                                                {
                                                    _warnActive = false;
                                                    _clearStreak = 0;
                                                    Logger.Instance.Print(
                                                        Logger.LogLevel.INFO,
                                                        $"{Enum.GetName(typeof(ModuleIndex), _ModuleIndex)} 경고 해제 " +
                                                        $"(연속 {CLEAR_FRAMES}프레임 > {CLEAR_THRESH}mm, 현재={frameMinAvg:F1}mm)",
                                                        true);
                                                }
                                            }
                                            else
                                            {
                                                _clearStreak = 0; // 다시 낮아졌으면 해제 카운트 리셋
                                            }
                                        }
                                    }
                                }
                                
                                FrameReceived?.Invoke(_ModuleIndex, hdr, dist, inten);
                            }
                                
                            else
                                Logger.Instance.Print(Logger.LogLevel.WARN, $"{Enum.GetName(typeof(ModuleIndex), _ModuleIndex)} Parse fail {err}", true);
                        });
                    }
                    catch (ObjectDisposedException) { /* 소켓 종료 시 */ }
                    catch (IOException) { /* 원격 종료 또는 Stop()로 close됨 */ }
                    catch (Exception ex) { Logger.Instance.Print(Logger.LogLevel.WARN, $"{Enum.GetName(typeof(ModuleIndex), _ModuleIndex)} receive loop: {ex.Message}", true); }
                }
            }
        }

        // ===== Framer: SYNC ~ SYNC =====
        private void ExtractPacketsSyncDelimited(MemoryStream stash, Action<byte[]> onPacket)
        {
            byte[] buf = stash.GetBuffer();
            int len = (int)stash.Length;
            int p = 0;
            bool progressed = false;

            while (true)
            {
                // 시작 SYNC
                int s0 = IndexOf(buf, p, len - p, SYNC);
                if (s0 < 0)
                {
                    // 경계 복구용 꼬리 3바이트만 유지
                    int keep = Math.Min(3, len);
                    if (len > keep)
                    {
                        Buffer.BlockCopy(buf, len - keep, buf, 0, keep);
                        stash.SetLength(keep);
                        stash.Position = keep;
                    }
                    else
                    {
                        stash.Position = len;
                    }
                    break;
                }

                // 다음 SYNC
                int s1Search = s0 + SYNC.Length;
                int s1 = IndexOf(buf, s1Search, len - s1Search, SYNC);
                if (s1 < 0)
                {
                    // s0부터 꼬리 보존(부분 프레임)
                    int rem = len - s0;
                    if (s0 > 0)
                    {
                        Buffer.BlockCopy(buf, s0, buf, 0, rem);
                        stash.SetLength(rem);
                        stash.Position = rem;
                    }
                    else
                    {
                        stash.Position = len;
                    }
                    break;
                }

                int candidateLen = s1 - s0;
                if (candidateLen < MIN_PACKET)
                {
                    p = s1; // 너무 짧음 → 다음 SYNC에서 계속
                    continue;
                }

                // 헤더의 PacketSize를 신뢰할 수 있으면 그 길이로 절단(과잉 구간 방지)
                int takeLen = candidateLen;
                if (candidateLen >= 7) // PT(1) + Size(2)까지 확인 가능
                {
                    ushort declared = ReadU16BE(buf, s0 + 5);
                    if (declared >= MIN_PACKET && declared <= MAX_PACKET && declared <= candidateLen)
                        takeLen = declared;
                }

                var pkt = new byte[takeLen];
                Buffer.BlockCopy(buf, s0, pkt, 0, takeLen);
                onPacket(pkt);
                progressed = true;

                // 다음 프레임 탐색은 s1부터
                p = s1;
            }

            if (progressed)
            {
                int rem = len - p;
                if (rem > 0)
                    Buffer.BlockCopy(buf, p, buf, 0, rem);
                stash.SetLength(rem);
                stash.Position = rem;
            }
        }

        private int IndexOf(byte[] src, int start, int count, byte[] pattern)
        {
            if (pattern.Length == 0) return start;
            int end = start + count - pattern.Length;
            for (int i = start; i <= end; i++)
            {
                if (src[i] == pattern[0])
                {
                    int j = 1;
                    for (; j < pattern.Length; j++)
                        if (src[i + j] != pattern[j]) break;
                    if (j == pattern.Length) return i;
                }
            }
            return -1;
        }

        public bool TryParseFrame(byte[] packet, out Header header, out ushort[] distances, out ushort[] intensities, out string error)
        {
            header = new Header();
            distances = new ushort[0];
            intensities = new ushort[0];
            error = "";

            if (packet == null || packet.Length < MIN_PACKET) { error = "Too short"; return false; }
            if (!(packet[0] == 0x4C && packet[1] == 0x45 && packet[2] == 0x55 && packet[3] == 0x5A))
            { error = "Bad SYNC"; return false; }

            header.PacketType = packet[4];
            header.PacketSize = ReadU16BE(packet, 5); // BE
            if (header.PacketSize < MIN_PACKET || header.PacketSize > packet.Length)
            { error = "PacketSize invalid"; return false; }

            int off = 5 + 2 + 6; // Reserved 6B
            header.PacketNo = ReadU16BE(packet, off); off += 2;
            header.TotalNo = packet[off++];
            header.SubNo = packet[off++];
            header.ScanFreqHz = ReadU16BE(packet, off); off += 2;
            header.ScanSpots = ReadU16BE(packet, off); off += 2;
            header.FirstAngle_mdeg = ReadI32BE(packet, off); off += 4;
            header.DeltaAngle_mdeg = ReadI32BE(packet, off); off += 4;
            header.Timestamp_ms = ReadU16BE(packet, off); off += 2;

            if (off != HEADER_SIZE) { error = "Header size mismatch"; return false; }

            int effectiveLen = Math.Min(packet.Length, header.PacketSize);
            int msgLen = effectiveLen - HEADER_SIZE - CRC_SIZE;
            if (msgLen <= 0) { error = "No message"; return false; }

            if (header.PacketType == 0)
            {
                // Distance only: 2B * N (BE)
                int need = 2 * header.ScanSpots;
                if (msgLen < need) { error = "Msg too short(dist)"; return false; }
                distances = new ushort[header.ScanSpots];
                int m = HEADER_SIZE;
                for (int i = 0; i < header.ScanSpots; i++)
                    distances[i] = ReadU16BE(packet, m + 2 * i);
            }
            else
            {
                // Distance & Intensity (interleaved): [D1 I1 D2 I2 ...]
                int need = 4 * header.ScanSpots;
                if (msgLen < need) { error = "Msg too short(dist/int)"; return false; }
                distances = new ushort[header.ScanSpots];
                intensities = new ushort[header.ScanSpots];
                int m = HEADER_SIZE;
                for (int i = 0; i < header.ScanSpots; i++)
                {
                    int pos = m + 4 * i;
                    distances[i] = ReadU16BE(packet, pos);
                    intensities[i] = ReadU16BE(packet, pos + 2);
                }
            }

            // (옵션) CRC16 확인 – 불일치 시 경고만
            ushort crcFrame = ReadU16BE(packet, effectiveLen - 2);
            ushort crcCalc = ComputeCrc16_MsbFirst_0x90D9(packet, 0, effectiveLen - 2);
            if (crcFrame != crcCalc)
                System.Diagnostics.Debug.WriteLine($"[CRC WARN] frame=0x{crcFrame:X4}, calc=0x{crcCalc:X4}");

            return true;
        }

        // ===== BE readers =====
        private ushort ReadU16BE(byte[] b, int i)
            => (ushort)((b[i] << 8) | b[i + 1]);
        private int ReadI32BE(byte[] b, int i)
            => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];

        // ===== CRC16 (poly=0x90D9, init=0x0000, MSB-first) =====
        private ushort ComputeCrc16_MsbFirst_0x90D9(byte[] data, int offset, int length)
        {
            ushort crc = 0x0000;
            for (int i = 0; i < length; i++)
            {
                crc ^= (ushort)(data[offset + i] << 8); // feed MSB first
                for (int b = 0; b < 8; b++)
                {
                    if ((crc & 0x8000) != 0) crc = (ushort)((crc << 1) ^ 0x90D9);
                    else crc <<= 1;
                }
            }
            return crc;
        }
    }

}
