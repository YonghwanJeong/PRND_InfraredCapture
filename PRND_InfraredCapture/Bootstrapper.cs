using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using PRND_InfraredCapture.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PRND_InfraredCapture
{
    public class Bootstrapper : IDisposable
    {
        public IServiceProvider Services { get; }

        public Bootstrapper()
        {
            Services = ConfigureServices();
        }

        private IServiceProvider ConfigureServices()
        {
            var services = new ServiceCollection();

            // ViewModels 등록
            services.AddTransient<MainViewModel>();
            services.AddTransient<HomeViewModel>();
            services.AddTransient<SettingViewModel>();

            return services.BuildServiceProvider();
        }

        public void Dispose()
        {
            if (Services is IDisposable disposable)
                disposable.Dispose();
        }
    }
}
