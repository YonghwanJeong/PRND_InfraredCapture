using PRND_InfraredCapture.Bases;
using PRND_InfraredCapture.Views;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PRND_InfraredCapture.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        public MainViewModel()
        {
            Title = "Main ViewModel";
            
        }

        public override void Dispose()
        {
            base.Dispose();
        } 

    }
}
