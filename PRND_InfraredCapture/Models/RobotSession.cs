using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PRND_InfraredCapture.Models
{
    public sealed class RobotSession : IDisposable
    {
        private const string NewLine = "\n"; // 필요 시 "\r\n"로 변경
        private readonly TcpClient _client;
        private readonly NetworkStream _ns;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        // RX 큐 & 신호
        private readonly ConcurrentQueue<string> _rxQueue = new ConcurrentQueue<string>();
        private readonly SemaphoreSlim _rxSignal = new SemaphoreSlim(0, int.MaxValue);

        // TX 큐 & 신호
        private readonly ConcurrentQueue<string> _txQueue = new ConcurrentQueue<string>();
        private readonly SemaphoreSlim _txSignal = new SemaphoreSlim(0, int.MaxValue);

        public int RobotIndex { get; private set; }

        public RobotSession(int robotIndex, TcpClient client)
        {
            RobotIndex = robotIndex;
            _client = client;
            _ns = client.GetStream();
        }

        public async Task RunAsync()
        {
            var reader = Task.Run(ReadLoopAsync);
            var writer = Task.Run(WriteLoopAsync);

            await Task.WhenAny(reader, writer).ConfigureAwait(false);
            _cts.Cancel();

            try { await Task.WhenAll(reader, writer).ConfigureAwait(false); }
            catch { }
        }

        public Task SendLineAsync(string line, CancellationToken ct)
        {
            if (ct.IsCancellationRequested || _cts.IsCancellationRequested)
                return Task.FromCanceled(ct.IsCancellationRequested ? ct : _cts.Token);

            _txQueue.Enqueue(line);
            _txSignal.Release();
            return Task.CompletedTask;
        }

        public async Task<string> WaitForMessageAsync(Func<string, bool> predicate, TimeSpan timeout,CancellationToken externalCt)
        {
            // 1) 타임아웃 전용 CTS
            using (var timeoutCts = new CancellationTokenSource(timeout))
            // 2) 내부 종료용 _cts, 외부 취소, 타임아웃을 링크
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, externalCt, timeoutCts.Token))
            {
                try
                {
                    // 먼저 큐를 비우며 매칭 확인 (레이스 방지)
                    string pre;
                    while (_rxQueue.TryDequeue(out pre))
                    {
                        var norm = NormalizeLine(pre);
                        if (predicate(norm)) return norm;
                    }

                    while (true)
                    {
                        // 수신 신호 대기 (신호는 Enqueue 시 Release로 올려줘야 함)
                        await _rxSignal.WaitAsync(linked.Token).ConfigureAwait(false);

                        string line;
                        while (_rxQueue.TryDequeue(out line))
                        {
                            var norm = NormalizeLine(line);
                            if (predicate(norm)) return norm;
                            // 필요 시 로그: 매칭 안 되는 라인 스킵
                        }
                        // 큐가 비면 다음 신호 대기
                    }
                }
                catch (OperationCanceledException)
                {
                    // 3) 원인 구분: 타임아웃 vs 외부/내부 취소
                    if (timeoutCts.IsCancellationRequested &&
                        !externalCt.IsCancellationRequested &&
                        !_cts.IsCancellationRequested)
                    {
                        // 타임아웃 → 재시도 유도
                        throw new TimeoutException("수신 타임아웃");
                    }

                    // 외부/내부 취소는 그대로 상위에서 처리
                    throw;
                }
            }
        }

        private async Task ReadLoopAsync()
        {
            var buf = new byte[4096];
            var sb = new StringBuilder(256);

            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    int n = await _ns.ReadAsync(buf, 0, buf.Length, _cts.Token).ConfigureAwait(false);
                    if (n <= 0) throw new Exception("Remote closed");

                    for (int i = 0; i < n; i++)
                    {
                        char ch = (char)buf[i]; // ASCII 기준
                        if (ch == '\n')
                        {
                            var line = sb.ToString().TrimEnd('\r');
                            sb.Length = 0;
                            _rxQueue.Enqueue(line);
                            _rxSignal.Release();
                        }
                        else
                        {
                            sb.Append(ch);
                            if (sb.Length > 8192)
                                throw new Exception("Line too long");
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Instance.Print(Logger.LogLevel.WARN, $"[{Enum.GetName(typeof(RobotIndex), RobotIndex)} ReadLoop stop: {ex.Message}", true);
            }
        }

        private async Task WriteLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await _txSignal.WaitAsync(_cts.Token).ConfigureAwait(false);

                    string line;
                    while (_txQueue.TryDequeue(out line))
                    {
                        var data = Encoding.ASCII.GetBytes(line + NewLine);
                        await _ns.WriteAsync(data, 0, data.Length, _cts.Token).ConfigureAwait(false);
                        await _ns.FlushAsync(_cts.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Instance.Print(Logger.LogLevel.WARN, $"{Enum.GetName(typeof(RobotIndex), RobotIndex)} WriteLoop stop: {ex.Message}", true);
            }
        }
        private static string NormalizeLine(string s)
        {
            if (s == null) return string.Empty;
            // BOM 제거
            if (s.Length > 0 && s[0] == '\uFEFF') s = s.Substring(1);
            // 널/제어문자 제거
            s = s.Replace("\0", string.Empty);
            // 개행/공백 트림
            return s.Trim(); // \r\n 포함 모든 공백/개행 제거
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _ns.Close(); } catch { }
            try { _client.Close(); } catch { }
            try { _rxSignal.Dispose(); } catch { }
            try { _txSignal.Dispose(); } catch { }
        }
    }
}
