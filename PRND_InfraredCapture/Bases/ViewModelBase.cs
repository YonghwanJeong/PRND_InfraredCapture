using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Wpf.Ui.Abstractions.Controls;

namespace PRND_InfraredCapture.Bases
{
    public abstract class ViewModelBase : ObservableObject, INavigationAware, IDisposable
    {
        private string _title;
        private string _message;

        public string Title { get => _title; set => SetProperty(ref _title, value); }
        public string Message { get => _message; set => SetProperty(ref _message, value); }

        public virtual void Dispose() { }


        /// <summary>
        /// Frame에서 벗어날 때
        /// </summary>
        /// <returns></returns>
        public virtual Task OnNavigatedFromAsync()
        {
            return Task.CompletedTask;
        }
        /// <summary>
        /// Frame에 진입할 때
        /// </summary>
        /// <returns></returns>
        public virtual Task OnNavigatedToAsync()
        {
            return Task.CompletedTask;
        }

    }
}
