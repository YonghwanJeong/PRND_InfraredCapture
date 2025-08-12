using CommunityToolkit.Mvvm.Input;
using CP.Common;
using CP.Common.Util;
using PRND_InfraredCapture.Bases;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace PRND_InfraredCapture.ViewModels
{
    public class HomeViewModel : ViewModelBase
    {

        public static ObservableCollection<string> ProgramLogs { get; set; }

        private BitmapSource _TestImage;
        public BitmapSource TestImage
        {
            get { return _TestImage; }
            set { SetProperty(ref _TestImage, value); }
        }


        private int _CurrentXoffset;
        public int CurrentXoffset
        {
            get { return _CurrentXoffset; }
            set { SetProperty(ref _CurrentXoffset, value); }
        }


        private int _CurrentYoffset;
        public int CurrentYoffset
        {
            get { return _CurrentYoffset; }
            set { SetProperty(ref _CurrentYoffset, value); }
        }


        private double _CurrentScale;
        public double CurrentScale
        {
            get { return _CurrentScale; }
            set { SetProperty(ref _CurrentScale, value); }
        }


        public ICommand TestCommand { get; set; }
        public ICommand PageLoadedCommmand { get; set; }

        
        private static bool _IsFirstLoaded = true;


        public HomeViewModel()
        {
            Title = "Home";
            TestCommand = new RelayCommand(OnTestCommand);
            PageLoadedCommmand = new RelayCommand(OnPageLoaded);

            if (_IsFirstLoaded) Initialize();

        }
        /// <summary>
        /// 한번만 실행되야 하는 로직 추가
        /// </summary>
        public void Initialize()
        {
            

            _IsFirstLoaded = false;
        }

        private void OnPageLoaded()
        {
            TestImage = BitmapController.LoadBitmapImage(@"C:\Users\jijon\Work\2. Development\PRND_InfraredCapture\PRND_InfraredCapture\Resources\CustomAir_Cylinder.bmp");
        }

        private void OnTestCommand()
        {

        }

        public override Task OnNavigatedFromAsync()
        {
            Logger.Instance.Print(Logger.LogLevel.INFO, "Escape HomeView");
            return base.OnNavigatedFromAsync();
        }

        public override Task OnNavigatedToAsync()
        {
            Logger.Instance.Print(Logger.LogLevel.INFO, "Enter HomeView");
            return base.OnNavigatedToAsync();
        }

        public override void Dispose()
        {
            Logger.Instance.Print(Logger.LogLevel.INFO, "HomeView Disposed");
            base.Dispose();
        }
    }
}
