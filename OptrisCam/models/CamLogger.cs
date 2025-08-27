using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace CP.OptrisCam.models
{
    public class CamLogger
    {
        #region Data

        public enum LogLevel
        {
            TRACE,  // 추적 레벨은 Debug보다 좀 더 상세한 정보를 나타냄
            DEBUG,  // 프로그램을 디버깅하기 위한 정보 지정
            INFO,   // 상태변경과 같은 정보성 메시지를 나타냄
            WARN,   // 처리 가능한 문제, 향후 시스템 에러의 원인이 될 수 있는 경고성 메시지를 나타냄
            ERROR,  // 요청을 처리하는 중 문제가 발생한 경우
            FATAL   // 아주 심각한 에러가 발생한 상태, 시스템적으로 심각한 문제가 발생해서 어플리케이션 작동이 불가능할 경우
        }

        public enum LogInterval
        {
            Hour,
            Day,
            Month
        }

        private struct LogData
        {
            public LogLevel Level;
            public DateTime DateTime;
            public string Message;
            public bool IsRaiseEvent;

            public LogData(LogLevel logLevel, string msg, bool isRaiseEvent)
            {
                Level = logLevel;
                DateTime = DateTime.Now;
                Message = msg;
                IsRaiseEvent = isRaiseEvent;
            }
        }

        #endregion Data

        #region Field

        private object _LockObject = new object();
        private string _SaveDirectoryPath = @".\CamLog";
        private ConcurrentQueue<LogData> _LogProcessQueue = new ConcurrentQueue<LogData>();
        private string _FilePath = string.Empty;
        private LogInterval _LogInterval = LogInterval.Hour;

        #endregion Field

        #region Event
        public Action<string> OnLogSavedAction { get; set; }
        #endregion Event

        private static readonly Lazy<CamLogger> _Instance =
            new Lazy<CamLogger>(() => new CamLogger());

        public static CamLogger Instance
        {
            get { return _Instance.Value; }
        }

        private CamLogger()
        {
            CheckSaveDirectory();
            Task.Run(() => { WriteLogWithLockThread(); });
        }

        public void Print(LogLevel level = LogLevel.INFO, string message = null, bool isRaiseEvent = false)
        {
            _LogProcessQueue.Enqueue(new LogData(level, message, isRaiseEvent));
        }

        public void PrintException(Exception ex, bool isRaiseEvent = false)
        {
            string message = $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
            Print(LogLevel.ERROR, message, isRaiseEvent);
        }

        private void WriteLogWithLockThread()
        {
            while (true)
            {
                if (_LogProcessQueue.IsEmpty)
                {
                    Thread.Sleep(100);
                    continue;
                }

                LogData logData;
                _LogProcessQueue.TryDequeue(out logData);

                lock (_LockObject)
                {
                    WriteLog(logData.Level, logData.DateTime, logData.Message, logData.IsRaiseEvent);
                }
            }
        }

        public void CheckSaveDirectory()
        {
            if (!Directory.Exists(_SaveDirectoryPath))
                Directory.CreateDirectory(_SaveDirectoryPath);
        }

        public string GetFilePath()
        {
            return _FilePath;
        }

        public void SetSaveLogPath(string path)
        {
            _SaveDirectoryPath = path;
            CheckSaveDirectory();
        }

        public void SetLogInterval(LogInterval interval)
        {
            _LogInterval = interval;
        }

        private void WriteLog(LogLevel level, DateTime dt, string msg, bool isRaiseEvent = false)
        {
            try
            {
                // 날짜별 하위 폴더 생성
                string dateFolder = dt.ToString("yyyyMMdd");
                string fullDirPath = Path.Combine(_SaveDirectoryPath, dateFolder);
                if (!Directory.Exists(fullDirPath))
                    Directory.CreateDirectory(fullDirPath);

                string fileName = dt.ToString("yyyyMMdd_HH"); // 기본 Hour
                if (_LogInterval == LogInterval.Day)
                    fileName = dt.ToString("yyyyMMdd");
                else if (_LogInterval == LogInterval.Month)
                    fileName = dt.ToString("yyyyMM");

                _FilePath = Path.Combine(fullDirPath, $"{fileName}.log");
                var sb = GetLogData(level, dt.ToString("yyyy-MM-dd HH:mm:ss.fff"), msg);

                using (FileStream fs = new FileStream(_FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (StreamWriter sw = new StreamWriter(fs))
                {
                    sw.WriteLine(sb.ToString());
                }

                if (isRaiseEvent)
                    OnLogSavedAction?.Invoke($"{dt:MM-dd HH:mm:ss.fff} [{level}] {msg}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Logger Error] {ex.Message}");
            }
        }

        private StringBuilder GetLogData(LogLevel level, string time, string message)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(time);
            sb.Append(" | ");
            sb.Append(level.ToString());
            sb.Append(" | ");
            sb.Append(message);
            return sb;
        }
    }
}
