using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using PRND_InfraredCapture.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PRND_InfraredCapture.Locators
{
    public sealed class ViewModelLocator
    {
        public MainViewModel MainViewModel => Ioc.Default.GetService<MainViewModel>();
        public HomeViewModel HomeViewModel => Ioc.Default.GetService<HomeViewModel>();
        public SettingViewModel SettingViewModel => Ioc.Default.GetService<SettingViewModel>();

    }
}
