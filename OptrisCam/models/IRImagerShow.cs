using Optris.OtcSDK;
using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using static System.Net.Mime.MediaTypeNames;


namespace CP.OptrisCam.models
{
    public sealed class CapturedFrame
    {
        public int FrameIndex { get; set; }
        public ThermalFrame Frame { get; set; }
        public FrameMetadata Meta { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public enum AcquisitionAngle
    {
        Angle_0 = 0,
        Angle_90 = 90,
        Angle_180 = 180,
        Angle_270 = 270
    }

    /// <summary>
    /// A more feature rich implementation of an IRImagerClient that converts thermal frames to false color images and
    /// displays them.
    /// </summary>
    /// 
    /// An IRImager acts as an observer to an IRImager implementation that retrieves and processes thermal data from
    /// Optris thermal cameras.
    public class IRImagerShow : IRImagerClient
    {
        public IRImager Imager { get; private set; }

        private ImageBuilder imageBuilder;
        private string flagState = "";
        private string _ConfigFilePath;
        private string _SaveFolderPath = "";
        private AcquisitionAngle _AcquisitionAngle = AcquisitionAngle.Angle_0;
        private ModuleIndex _CamIndex;
        private int _FrameCount = 80;


        public bool IsConnected { get; private set; }
        public bool IsConnectionLost { get; private set; }

        public int GrabCount { get; set; } = 0;


        //80프레임을 얻기 위한 변수
        private ConcurrentQueue<CapturedFrame> _FrameQueue = new ConcurrentQueue<CapturedFrame>();
        private int _RemainingFrameCount = 0;


        private CancellationTokenSource _GetThermalDataCts;
        private Task _GetThermalDataTask;

        // ===== Debug counters & completion signal =====
        private readonly ManualResetEventSlim _burstDone = new(false); // 80장 수집 완료 신호

        /// <summary>Constructor</summary>
        public IRImagerShow(ModuleIndex camIndex, string configPath)
        {
            // Instantiate an imager object. It will serve as the main interface to the SDK
            Imager = IRImagerFactory.getInstance().create("native");

            // Register this instance as client/observer
            Imager.addClient(this);

            /*
             * Create an image builder object that will convert thermal frame data to false color images
             * 
             * Its color format refers to the sequence of the bytes for the color values in the generated image array.
             * The color format of C# Bitmap class, however, refers to the significance of the color value bytes. Since
             * x64 is little-endian a C# Bitmap color format of RBG equals a BGR ImageBuilder color format.
             * 
             * Images are typically read line by line. To improve the performance of that operation this happens in bigger
             * byte chunks. The C# Bitmap class uses four byte chunks. Thus, the width alignment should be set to four 
             * bytes. This will ensure that each line has a size in bytes that is a multiple of four.
             * 
             * The temperature range decimal indicates the precision of the thermal data. The ImageBuilder requires this
             * information to correctly decode that data.
             */
            imageBuilder = new ImageBuilder(ColorFormat.BGR, WidthAlignment.FourBytes);
            imageBuilder.setPaletteScalingMethod(PaletteScalingMethod.MinMax);
            

            IsConnected = false;
            IsConnectionLost = false;
            _ConfigFilePath = configPath;
            _CamIndex = camIndex;

        }

        /// <summary>Connects to the device specified in the configuration file.</summary>
        /// 
        /// <param name="configFile">path to the configuration files of the device to connect to.</param>
        public void Connect()
        {
            if (IsConnected)
                return;

            // Read the configuration file and initialize the imager with it
            IRImagerConfig config = IRImagerConfigReader.read(_ConfigFilePath);
            Imager.connect(config);
;
            _GetThermalDataCts = new CancellationTokenSource();
            _GetThermalDataTask = Task.Run(() => GetThermalDataAsync(_GetThermalDataCts.Token));

            IsConnected = true;
            IsConnectionLost = false;
        }


        /// <summary>Disconnects from the currently connected device.</summary>
        public async void Disconnect()
        {
            if (IsConnected)
            {
                try
                {
                    // Stop camera if running
                    try { Imager.stopRunning(); } catch { /* ignore */ }
                    Imager.disconnect();
                }
                finally
                {
                    IsConnected = false;
                    IsConnectionLost = false;
                    if (_GetThermalDataCts != null)
                    {
                        _GetThermalDataCts.Cancel();
                        try { await _GetThermalDataTask; } catch { }
                        _GetThermalDataCts.Dispose();
                    }
                }
            }
        }

        public async void StartImageCapture(int frameCount, string savePath)
        {
            if (!IsConnected)
                return;
            
            _SaveFolderPath = savePath;
            _FrameCount = frameCount;
            Imager.runAsync();
            //Imager.setFocusMotorPosition(68);
            await Task.Delay(1000);
            // reset state
            _burstDone.Reset();
            _FrameQueue = new ConcurrentQueue<CapturedFrame>();
            
            Volatile.Write(ref _RemainingFrameCount, frameCount);
            CamLogger.Instance.Print(CamLogger.LogLevel.INFO, $"{Enum.GetName(typeof(ModuleIndex), _CamIndex)}Cam Start Capture", true);
            // Start camera acquisition

        }

        /// <summary>Returns the type of the device.</summary>
        /// 
        /// <return>type of the device.</summary>
        public string GetDeviceType()
        {
            return Imager.getDeviceType().ToString();
        }

        /// <summary>Returns the serial number of the device.</summary>
        /// 
        /// <return>serial number of the device.</return>
        public uint GetSerialNumber()
        {
            return Imager.getSerialNumber();
        }

        /// <summary>Returns the current flag state of the device.</summary>
        /// 
        /// <return>current flag state of the device.</return>
        public string GetFlagState()
        {
            lock (flagState)
            {
                return flagState;
            }
        }

        // Callbacks
        /// <summary>Callback method triggered by imager when a new thermal frame is available.</summary>
        /// 
        /// <param name="thermal">thermal frame data.</param>
        /// <param name="meta">data of the thermal frame.</param>
        public override void onThermalFrame(ThermalFrame thermal, FrameMetadata meta)
        {

            // Burst logic
            int before = Volatile.Read(ref _RemainingFrameCount);
            if (before > 0)
            {
                // Enqueue only while we still need frames
                var cloned = thermal.clone();
                _FrameQueue.Enqueue(new CapturedFrame
                {
                    FrameIndex = _FrameCount - before,
                    Frame = cloned,
                    Meta = meta,
                    Timestamp = DateTime.Now
                });

                // Count this frame toward the target
                int after = Interlocked.Decrement(ref _RemainingFrameCount);

                // If we've reached the target, stop the camera and signal completion
                if (after == 0)
                {
                    _burstDone.Set();
                    CamLogger.Instance.Print(CamLogger.LogLevel.INFO, $"{Enum.GetName(typeof(ModuleIndex), _CamIndex)}Cam Capture Done ({_FrameCount}Frame)", true);
                }
            }
        }
        private async Task GetThermalDataAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (_FrameQueue.TryDequeue(out var frame))
                {

                    string time = $"{frame.Timestamp:yyyy-MM-dd-HH-mm-ss-fff}";
                    string imageName = $"{frame.FrameIndex}_{time}.bmp";
                    string imageSavePath = Path.Combine(_SaveFolderPath, Enum.GetName(typeof(ModuleIndex), _CamIndex),"Image", imageName);
                    string rawName = $"{frame.FrameIndex}_{time}.raw";
                    string rawSavePath = Path.Combine(_SaveFolderPath, Enum.GetName(typeof(ModuleIndex), _CamIndex), "Raw", rawName);
                    string csvName = $"{frame.FrameIndex}_{time}.csv";
                    string csvSavePath= Path.Combine(_SaveFolderPath, Enum.GetName(typeof(ModuleIndex), _CamIndex), "csv", csvName);
                    int width = frame.Frame.getWidth();
                    int height = frame.Frame.getHeight();

                    ////온도 정보
                    TemperatureConverter converter = new TemperatureConverter();
                    converter.setPrecision(frame.Frame.getTemperaturePrecision());

                    ushort[] data = new ushort[frame.Frame.getSize()];
                    frame.Frame.copyDataTo(data, data.Length);

                    float[] temperature = new float[data.Length];
                    for (int i = 0; i < data.Length; i++)
                        temperature[i] = converter.toTemperature(data[i]);

                    SaveTemperatureCsv(csvSavePath, temperature, width, height);

                    var rotated = ThermalRotate.RotateTemperature(temperature, width, height, (int)_AcquisitionAngle);
                    SaveAsRawFloatBigEndian(rawSavePath, rotated);


                    // Process & save (example: BMP)
                    imageBuilder.setThermalFrame(frame.Frame);
                    imageBuilder.convertTemperatureToPaletteImage();

                    byte[] image = new byte[imageBuilder.getImageSizeInBytes()];
                    imageBuilder.copyImageDataTo(image, image.Length);
                    
                    using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                    var rectangle = new System.Drawing.Rectangle(0, 0, width, height);
                    var bitmapData = bitmap.LockBits(rectangle, ImageLockMode.ReadWrite, bitmap.PixelFormat);
                    System.Runtime.InteropServices.Marshal.Copy(image, 0, bitmapData.Scan0, image.Length);
                    bitmap.UnlockBits(bitmapData);

                    if(_AcquisitionAngle == AcquisitionAngle.Angle_90)
                        bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone);
                    else if(_AcquisitionAngle == AcquisitionAngle.Angle_180)
                        bitmap.RotateFlip(RotateFlipType.Rotate180FlipNone);
                    else if(_AcquisitionAngle == AcquisitionAngle.Angle_270)
                        bitmap.RotateFlip(RotateFlipType.Rotate270FlipNone);

                    Directory.CreateDirectory(Path.GetDirectoryName(imageSavePath)!);
                    bitmap.Save(imageSavePath, ImageFormat.Bmp);
                }
                else
                {
                    // If burst complete AND queue drained -> exit worker loop (optional)
                    if (_burstDone.IsSet && _FrameQueue.IsEmpty)
                    {
                        _burstDone.Reset();            // 다음 버스트를 위해 리셋
                        await Task.Delay(10, token);   // 잠깐 쉼
                        Imager.stopRunning();
                        continue;                      // 루프 유지
                    }
                    try
                    {
                        await Task.Delay(10, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break; // 정상 종료
                    }
                }
            }
            CamLogger.Instance.Print(CamLogger.LogLevel.INFO, $"{Enum.GetName(typeof(ModuleIndex), _CamIndex)}Cam Thread 종료");

        }

        public void SaveTemperatureCsv(string filePath, float[] temperature, int width, int height, int decimals = 2)
        {
            if (temperature == null) throw new ArgumentNullException(nameof(temperature));
            if (temperature.Length != width * height)
                throw new ArgumentException("temperature.Length != width*height");

            var inv = CultureInfo.InvariantCulture;
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // 큰 파일을 고려해 한 줄씩 바로바로 기록
            using (var sw = new StreamWriter(filePath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                // 필요하면 헤더(옵션)
                // sw.WriteLine($"WIDTH={width},HEIGHT={height}");

                var sb = new StringBuilder(capacity: Math.Max(16 * width, 1024));
                string fmt = "F" + Math.Max(0, decimals);

                for (int y = 0; y < height; y++)
                {
                    sb.Clear();
                    int rowStart = y * width;

                    // 첫 값
                    sb.Append(temperature[rowStart].ToString(fmt, inv));

                    // 나머지 값들
                    for (int x = 1; x < width; x++)
                    {
                        float t = temperature[rowStart + x];
                        sb.Append(',');
                        sb.Append(t.ToString(fmt, inv));
                    }

                    sw.WriteLine(sb.ToString());
                }
            }
        }


        public void SaveAsRawFloatBigEndian(string path, float[] data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using (var fs = File.Create(path))
            using (var bw = new BinaryWriter(fs))
            {
                for (int i = 0; i < data.Length; i++)
                {
                    byte[] b = BitConverter.GetBytes(data[i]);  // 머신 엔디안(대부분 little)
                    if (BitConverter.IsLittleEndian) Array.Reverse(b); // 파일은 big-endian
                    bw.Write(b);
                }
            }
        }

        /// <summary>Callback method triggered by imager when the state of the shutter flag changes.</summary>
        /// 
        /// <param name="flagState">of the shutter flag.</param>
        public override void onFlagStateChange(FlagState flagStateIn)
        {
            lock (flagState)
            {
                flagState = flagStateIn.ToString();
                CamLogger.Instance.Print(CamLogger.LogLevel.INFO, $"{Enum.GetName(typeof(ModuleIndex), _CamIndex)}Cam Flag State {flagState}");
            }
        }

        // <summary>Called when the connection to the camera is lost and can not be recovered.</summary>
        public override void onConnectionLost()
        {
         
            IsConnectionLost = true;
            CamLogger.Instance.Print(CamLogger.LogLevel.WARN, $"{_CamIndex} Connection Lost", true);
        }

        // <summary>Called when the SDK has not received frames from the camera for a while.</summary>
        public override void onConnectionTimeout()
        {
            IsConnectionLost = true;
            CamLogger.Instance.Print(CamLogger.LogLevel.WARN, $"{Enum.GetName(typeof(ModuleIndex), _CamIndex)}Cam Connection Timeout. Connection Lost",true);
        }
    }
}
