using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PRND_InfraredCapture.Models
{
    public sealed class RobotServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        // IP -> RobotIndex 매핑
        private readonly Dictionary<string, int> _ipToRobotIndex = new Dictionary<string, int>();

        // RobotIndex -> Session
        private readonly ConcurrentDictionary<int, RobotSession> _sessions =
            new ConcurrentDictionary<int, RobotSession>();

        // HandleClient 백그라운드 작업 추적
        private readonly ConcurrentDictionary<Guid, Task> _clientTasks =
            new ConcurrentDictionary<Guid, Task>();

        // 중복 Stop/Dispose 방지
        private int _stopped = 0;
        private bool _disposed;

        public RobotServer(IPAddress bindIp, int port, List<TCPConnectionPoint> robotConnectionList)
        {
            _listener = new TcpListener(bindIp, port);

            // IndexOf 대신 인덱스 루프(성능/안전)
            for (int i = 0; i < robotConnectionList.Count; i++)
            {
                var point = robotConnectionList[i];
                // 중복 키 대비: 마지막 값으로 덮어씀
                _ipToRobotIndex[point.IPAddress] = i + 1;
            }
        }

        public async Task StartAsync()
        {
            ThrowIfDisposed();
            _listener.Start();
            Logger.Instance.Print(Logger.LogLevel.INFO, "RobotServer started.", true);

            while (!_cts.IsCancellationRequested)
            {
                TcpClient client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    HandleClient(client); // fire-and-forget (추적함)
                }
                catch (ObjectDisposedException)
                {
                    break; // listener.Stop() 이후
                }
                catch (Exception ex)
                {
                    Logger.Instance.Print(Logger.LogLevel.ERROR, $"Accept error: {ex}", true);
                    if (client != null) { try { client.Close(); } catch { } }
                }
            }
        }

        private void HandleClient(TcpClient client)
        {
            var id = Guid.NewGuid();
            var task = Task.Run(async () =>
            {
                try
                {
                    client.NoDelay = true;
                    var remote = (IPEndPoint)client.Client.RemoteEndPoint;
                    string ip = remote.Address.ToString();

                    int robotIndex;
                    if (!_ipToRobotIndex.TryGetValue(ip, out robotIndex))
                    {
                        Logger.Instance.Print(Logger.LogLevel.WARN, $"Unknown robot IP {ip}, closing.", true);
                        client.Close();
                        return;
                    }

                    var session = new RobotSession(robotIndex, client);
                    RobotSession old;
                    if (_sessions.TryGetValue(robotIndex, out old))
                    {
                        try { old.Dispose(); } catch { }
                        RobotSession removed;
                        _sessions.TryRemove(robotIndex, out removed);
                    }

                    _sessions[robotIndex] = session;
                    Logger.Instance.Print(Logger.LogLevel.INFO, $"Robot {robotIndex} connected from {ip}", true);

                    try
                    {
                        await session.RunAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        RobotSession removed;
                        _sessions.TryRemove(robotIndex, out removed);
                        Logger.Instance.Print(Logger.LogLevel.INFO, $"Robot {robotIndex} disconnected", true);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Instance.Print(Logger.LogLevel.ERROR, $"HandleClient error: {ex}", true);
                }
            });

            _clientTasks[id] = task;
            task.ContinueWith(_ => { Task _out; _clientTasks.TryRemove(id, out _out); },
                TaskContinuationOptions.ExecuteSynchronously);
        }

        public RobotSession GetSession(int robotIndex)
        {
            ThrowIfDisposed();
            RobotSession s;
            return _sessions.TryGetValue(robotIndex, out s) ? s : null;
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;

            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }

            // 세션 종료
            foreach (var kv in _sessions)
            {
                try { kv.Value.Dispose(); } catch { }
            }
            _sessions.Clear();

            // 클라이언트 핸들링 작업이 자연 종료될 시간을 조금 준다(선택)
            try
            {
                var tasks = _clientTasks.Values;
                Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(2));
            }
            catch { }
            _clientTasks.Clear();

            Logger.Instance.Print(Logger.LogLevel.INFO, "RobotServer stopped.", true);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RobotServer));
        }

        // IDisposable
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();                // idempotent
            try { _cts.Dispose(); } catch { }

            GC.SuppressFinalize(this);
        }
    }
}

