using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PRND_InfraredCapture.Models
{


    public enum BitIndex
    {
        Bit0 = 0,
        Bit1 = 1,
        Bit2 = 2,
        Bit3 = 3,
        Bit4 = 4,
        Bit5 = 5,
        Bit6 = 6,
        Bit7 = 7,
        Bit8 = 8,
        Bit9 = 9,
        Bit10 = 10,
        Bit11 = 11,
        Bit12 = 12,
        Bit13 = 13,
        Bit14 = 14,
        Bit15 = 15
    }
    public class EdgeDetector
    {
        private readonly Dictionary<BitIndex, bool> _prevStates = new Dictionary<BitIndex, bool>();
        private readonly Dictionary<BitIndex, bool> _currStates = new Dictionary<BitIndex, bool>();

        public EdgeDetector()
        {
            foreach (BitIndex cmd in Enum.GetValues(typeof(BitIndex)))
            {
                _prevStates[cmd] = false;
                _currStates[cmd] = false;
            }
        }
        /// <summary>
        /// 100ms마다 한 번 호출하여 현재 상태를 갱신
        /// </summary>
        public void Update(int currentValue)
        {
            foreach (BitIndex cmd in Enum.GetValues(typeof(BitIndex)))
            {
                _prevStates[cmd] = _currStates[cmd];
                _currStates[cmd] = (currentValue & (1 << (int)cmd)) != 0;
            }
        }

        public bool IsRisingEdge(BitIndex command)
        {
            return !_prevStates[command] && _currStates[command];
        }

        public bool IsFallingEdge(BitIndex command)
        {
            return _prevStates[command] && !_currStates[command];
        }
    }
}
