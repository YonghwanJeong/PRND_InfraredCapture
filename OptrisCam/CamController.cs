using OptrisCam.models;
using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Drawing;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using Optris.OtcSDK;

namespace OptrisCam
{
    public class CamController
    {
        private CancellationTokenSource _imageLoopCts;
        private Task _imageLoopTask;
        private DateTime _lastUpdateTime;
        private IRImagerShow imagerShow = new();

        int tickCount = 0;

        public Action<Bitmap> OnReceiveImageAction { get; set; }
        public Action<int> OnUpdateGrabCount{ get; set; }

        public CamController()
        {

        }

        public void Connect()
        {
            try
            {
                imagerShow.Disconnect();
                imagerShow.Connect(@"C:\Users\jijon\AppData\Roaming\Imager\Configs\25074286.xml");
            }
            catch (SDKException ex)
            {
                Console.WriteLine($"error {ex.Message}");
            }
        }
        private void Disconnect()
        {
            if (!imagerShow.IsConnected)
            {
                return;
            }
            imagerShow.Disconnect();
            StopImageLoop();
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
                    tickCount++;

                    // 연결 상태 확인
                    if (!imagerShow.IsConnected || imagerShow.IsConnectionLost)
                    {
                        Disconnect();
                    }

                    //빠른 주기 작업: 이미지 받아와서 PictureBox 갱신
                    Bitmap? image = null;
                    try
                    {
                        image = imagerShow.GetImage();
                        OnReceiveImageAction?.Invoke(image);
                    }
                    catch
                    {
                        // 필요 시 로깅
                    }

                    // 상태바 등 빠른 주기 UI 갱신
                    imagerShow.GetFlagState();


                    // 1초마다 실행되는 블록
                    var now = DateTime.UtcNow;
                    if ((now - _lastUpdateTime).TotalSeconds >= 1)
                    {
                        var ticksInSec = tickCount;
                        tickCount = 0;
                        _lastUpdateTime = now;
                        try
                        {
                            //sbFPS.Text = $"초당 tick : {ticksInSec}";
                            OnUpdateGrabCount?.Invoke(imagerShow.GrabCount);
                            //OnUpdateGrabCount?.Invoke(ticksInSec);
                            // 필요 시 GrabCount 등도 여기서 초기화/표시
                            imagerShow.GrabCount = 0;
                        }
                        catch { /* 폼 종료 중 예외 무시 */ }
                        
                    }

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

        


    }
}
