using CommunityToolkit.Mvvm.DependencyInjection;
using CP.Common.Util;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace PRND_InfraredCapture
{
    /// <summary>
    /// App.xaml에 대한 상호 작용 논리
    /// </summary>
    public partial class App : Application
    {
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
        [DllImport("winmm.dll")] static extern int timeGetDevCaps(out TimeCaps caps, int size);

        [StructLayout(LayoutKind.Sequential)]
        public struct TimeCaps { public uint wPeriodMin; public uint wPeriodMax; }

        private readonly Bootstrapper _bootstrapper;

        public IServiceProvider Services => _bootstrapper.Services;

        public App()
        {
            _bootstrapper = new Bootstrapper();

            // IoC 컨테이너 설정
            Ioc.Default.ConfigureServices(_bootstrapper.Services);

            Logger.Instance.Print(Logger.LogLevel.INFO,"Program 실행 됨.");
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var rc = timeBeginPeriod(1);   // 0이면 성공
            timeGetDevCaps(out var caps, Marshal.SizeOf<TimeCaps>());
        }

        protected override void OnExit(ExitEventArgs e)
        {
            timeEndPeriod(1);
            base.OnExit(e);
            // IServiceProvider Dispose 호출
            _bootstrapper.Dispose();
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Logger.Instance.Print(Logger.LogLevel.FATAL, $"처리되지 않은 예외 {e.Exception}");
            MessageBox.Show("예기치 않은 오류로 인해 프로그램이 종료됩니다: " + e.Exception.Message);
            e.Handled = true; // 프로그램 강제종료 방지
        }
    }
}
