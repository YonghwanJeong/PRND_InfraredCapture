using CommunityToolkit.Mvvm.Input;
using CP.Common.Util;
using PRND_InfraredCapture.Bases;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace PRND_InfraredCapture.ViewModels
{
    public class SettingViewModel : ViewModelBase
    {
        private static bool _IsFirstLoaded = true;


        public SettingViewModel()
        {
            if(_IsFirstLoaded) Initialize();


        }
        public void Initialize()
        {
            

            _IsFirstLoaded = false;
        }

        public override Task OnNavigatedFromAsync()
        {
            Logger.Instance.Print(Logger.LogLevel.INFO, "Escape SeetingView");
            return base.OnNavigatedFromAsync();
        }

        public override Task OnNavigatedToAsync()
        {
            Logger.Instance.Print(Logger.LogLevel.INFO, "Enter SeetingView");
            return base.OnNavigatedToAsync();
        }
        public override void Dispose()
        {
            Logger.Instance.Print(Logger.LogLevel.INFO, "SettingView Disposed");
            base.Dispose();
        }


    }
}
