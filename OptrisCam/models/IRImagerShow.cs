using Optris.OtcSDK;
using System.Drawing;
using System.Drawing.Imaging;


namespace OptrisCam.models
{
    public sealed class ByteChangeDetector
    {
        private byte[] _prev = Array.Empty<byte>();
        private ulong _prevHash = 0UL;

        // 변경되었으면 onChanged(data) 호출하고 true 반환
        public bool TryHandleIfChanged(byte[] data, Action<byte[]> onChanged)
        {
            if (data == null) return false;

            // 길이 다르면 무조건 변경
            if (data.Length != _prev.Length)
            {
                onChanged(data);
                SaveSnapshot(data);
                return true;
            }

            // 해시 계산(빠름)
            ulong h = Fnv1a64(data);

            if (h != _prevHash)
            {
                onChanged(data);
                SaveSnapshot(data, h);
                return true;
            }

            // 드물지만 해시 충돌/동일참조 대비: 실제 바이트 비교
            if (!_prev.SequenceEqual(data))
            {
                onChanged(data);
                SaveSnapshot(data, h);
                return true;
            }

            return false; // 완전히 동일 → 스킵
        }

        private void SaveSnapshot(byte[] src, ulong? hashOpt = null)
        {
            if (_prev.Length != src.Length) _prev = new byte[src.Length];
            Buffer.BlockCopy(src, 0, _prev, 0, src.Length);
            _prevHash = hashOpt ?? Fnvlazy(src);
        }

        private ulong Fnvlazy(byte[] data) => Fnv1a64(data);

        // 64-bit FNV-1a (빠르고 단순)
        private static ulong Fnv1a64(byte[] data)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;
            for (int i = 0; i < data.Length; i++)
            {
                hash ^= data[i];
                hash *= prime;
            }
            return hash;
        }
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
        private ThermalFrame thermalFrame = new();
        private FramerateCounter counter = new();
        private string flagState = "";
        private string _ConfigFilePath;

        public bool IsConnected { get; private set; }
        public bool IsConnectionLost { get; private set; }

        public int GrabCount { get; set; } = 0;

        private readonly ByteChangeDetector detector = new ByteChangeDetector();

        /// <summary>Constructor</summary>
        public IRImagerShow(string configFilePath)
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
            _ConfigFilePath = configFilePath;
        }

        /// <summary>Connects to the device specified in the configuration file.</summary>
        /// 
        /// <param name="configFile">path to the configuration files of the device to connect to.</param>
        public void Connect()
        {
            if (IsConnected)
            {
                return;
            }

            // Read the configuration file and initialize the imager with it
            IRImagerConfig config = IRImagerConfigReader.read(_ConfigFilePath);
            Imager.connect(config);

            // Start processing
            Imager.runAsync();

            IsConnected = true;
            IsConnectionLost = false;
        }


        /// <summary>Disconnects from the currently connected device.</summary>
        public void Disconnect()
        {
            if (IsConnected)
            {
                Imager.disconnect();

                IsConnected = false;
                IsConnectionLost = false;
            }
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

        /// <summary>Returns the current frame rate in Hz.</summary>
        /// 
        /// <return> current frame rate in Hz.</return>
        public double GetFPS()
        {
            lock (thermalFrame)
            {
                return Math.Round(counter.getFps(), 1);
            }
        }

        /// <summary>Converts the latest thermal frame into a false color image and returns it.</summary>
        /// 
        /// <return>converted false color image.</return>
        public Bitmap? GetImage()
        {
            lock (thermalFrame)
            {
                if (thermalFrame.isEmpty())
                {
                    return null;
                }

                imageBuilder.setThermalFrame(thermalFrame);
            }

            // Convert the thermal frame to a false color image
            imageBuilder.convertTemperatureToPaletteImage();

            // Extract the image data...
            int width = imageBuilder.getWidth();
            int height = imageBuilder.getHeight();
            

            // The image size in bytes may not equal width * height due to width padding
            byte[] image = new byte[imageBuilder.getImageSizeInBytes()];
            imageBuilder.copyImageDataTo(image, image.Length);

            Bitmap result = null;

            detector.TryHandleIfChanged(image, changed =>
            {
                // 데이터가 바뀐 경우에만 실행할 로직
                // 예: CSV 저장, 처리 파이프라인 투입, 화면 갱신 등
                
                // .. and create a bitmap
                System.Drawing.Rectangle rectangle = new System.Drawing.Rectangle(0, 0, width, height);
                Bitmap bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);

                BitmapData bitmapData = bitmap.LockBits(rectangle, ImageLockMode.ReadWrite, bitmap.PixelFormat);
                System.Runtime.InteropServices.Marshal.Copy(image, 0, bitmapData.Scan0, image.Length);
                bitmap.UnlockBits(bitmapData);
                result = bitmap;
            });

            return result;
        }


        // Callbacks
        /// <summary>Callback method triggered by imager when a new thermal frame is available.</summary>
        /// 
        /// <param name="thermal">thermal frame data.</param>
        /// <param name="meta">data of the thermal frame.</param>
        public override void onThermalFrame(ThermalFrame thermal, FrameMetadata meta)
        {
            lock (thermalFrame)
            {
                thermalFrame = thermal.clone();
                counter.trigger();
                GrabCount++;
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
            }
        }

        /// <summary>Called when the connection to the camera is lost and can not be recovered.</summary>
        //public override void onConnectionLost()
        //{
        //    MessageBox.Show("Lost connection to device.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);

        //    IsConnectionLost = true;
        //}

        /// <summary>Called when the SDK has not received frames from the camera for a while.</summary>
        //public override void onConnectionTimeout()
        //{
        //    DialogResult dialogResult = MessageBox.Show("Connection to the device timed out. Disconnect?", "Connection Timeout", MessageBoxButtons.YesNo);

        //    IsConnectionLost = (dialogResult == DialogResult.Yes);
        //}
    }
}
