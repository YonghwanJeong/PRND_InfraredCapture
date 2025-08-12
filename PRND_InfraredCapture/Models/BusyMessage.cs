using CommunityToolkit.Mvvm.Messaging.Messages;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PRND_InfraredCapture.Models
{
    public class BusyMessage : ValueChangedMessage<bool>
    {
        /// <summary>
        /// BusyId
        /// </summary>
        public string BusyId { get; set; }
        /// <summary>
        /// BusyText
        /// </summary>
        public string BusyText { get; set; }
        /// <summary>
        /// todtjdwk
        /// </summary>
        /// <param name="value"></param>
        public BusyMessage(bool value) : base(value)
        {
        }
    }
}
