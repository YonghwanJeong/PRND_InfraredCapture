using CP.OptrisCam.models;
using System.Drawing;

using Optris.OtcSDK;

namespace CP.OptrisCam
{
    public enum ModuleIndex
    {
        Module1 = 0,
        Module2 = 1,
        Module3 = 2,
        Module4 = 3,
    }

    public class CamController : IDisposable
    {
        private CancellationTokenSource _imageLoopCts;
        private Task _imageLoopTask;
        private DateTime _lastUpdateTime;
        private IRImagerShow[] _OptrisCams;
        

        public Action<Bitmap> OnReceiveImageAction { get; set; }
        public Action<int> OnUpdateGrabCount{ get; set; }

        public CamController(List<string> camFileList)
        {
            _OptrisCams = new IRImagerShow[camFileList.Count];

            for (int i = 0; i < camFileList.Count; i++)
            {
                _OptrisCams[i] = new IRImagerShow((ModuleIndex)i, camFileList[i]);
            }
        }

        public void Connect(ModuleIndex camIndex)
        {
            try
            {
                _OptrisCams[(int)camIndex].Disconnect();
                _OptrisCams[(int)camIndex].Connect();
                CamLogger.Instance.Print(CamLogger.LogLevel.INFO, $"{Enum.GetName(typeof(ModuleIndex), camIndex)} Cam Connected", true);
            }
            catch (SDKException ex)
            {
                CamLogger.Instance.Print(CamLogger.LogLevel.ERROR, $"{Enum.GetName(typeof(ModuleIndex), camIndex)} Cam Connect Error: {ex.Message}", true);
            }
        }

        public void ReadyCapture(ModuleIndex index, float focus)
        {
            if (_OptrisCams[(int)index].IsConnected)
            {
                _OptrisCams[(int)index].ReadyCapture(focus);
                CamLogger.Instance.Print(CamLogger.LogLevel.INFO, $"{Enum.GetName(typeof(ModuleIndex), index)} Cam Ready Capture");
            }
        }
        public void CaptureImage(ModuleIndex index, int framecnt, string savePath,  AcquisitionAngle angle, string positionName = "")
        {
            if (_OptrisCams[(int)index].IsConnected)
                _OptrisCams[(int)index].StartImageCapture(framecnt, savePath, angle, positionName);
        }
        public void Disconnect(ModuleIndex camIndex)
        {
            if (!_OptrisCams[(int)camIndex].IsConnected)
                return;
            _OptrisCams[(int)camIndex].Disconnect();
            CamLogger.Instance.Print(CamLogger.LogLevel.INFO, $"{Enum.GetName(typeof(ModuleIndex), camIndex)} Cam Disconnected", true);
        }
        public void DisconnectAll()
        {
            for(int i = 0; i < _OptrisCams.Length; i++)
                Disconnect((ModuleIndex)i);
        }

        public void StartImageLoop()
        {
            StopImageLoop(); // 혹시 기존 루프가 돌고 있으면 정리

            _imageLoopCts = new CancellationTokenSource();
            var ct = _imageLoopCts.Token;

            _imageLoopTask = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    // 1ms 주기
                    try
                    {
                        //await Task.Delay(1, ct);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                }
            }, ct);
        }

        public void StopImageLoop()
        {
            try
            {
                _imageLoopCts?.Cancel();
                _imageLoopTask?.Wait(200); // 너무 오래 기다리지 않도록 제한
            }
            catch { /* 무시 */ }
            finally
            {
                _imageLoopTask = null;
                _imageLoopCts?.Dispose();
                _imageLoopCts = null;
            }
        }

        public void Dispose()
        {
            DisconnectAll();
        }
    }
}
