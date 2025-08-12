using CommunityToolkit.Mvvm.DependencyInjection;
using CP.Common.Util;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace PRND_InfraredCapture
{
    /// <summary>
    /// App.xaml에 대한 상호 작용 논리
    /// </summary>
    public partial class App : Application
    {
        private readonly Bootstrapper _bootstrapper;

        public IServiceProvider Services => _bootstrapper.Services;

        public App()
        {
            _bootstrapper = new Bootstrapper();

            // IoC 컨테이너 설정
            Ioc.Default.ConfigureServices(_bootstrapper.Services);

            Logger.Instance.Print(Logger.LogLevel.INFO,"Program 실행 됨.");
        }

        protected override void OnExit(ExitEventArgs e)
        {
            base.OnExit(e);
            // IServiceProvider Dispose 호출
            _bootstrapper.Dispose();
        }

    }
}
