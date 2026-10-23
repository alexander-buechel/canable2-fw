//
// Minimal STM32G4 reset and clock control model (RM0440, chapter "Reset and clock control").
//
// Behaves like a register file with reset values, plus the handshakes firmware busy-waits on: the oscillator and
// PLL "ready" flags follow their "on" bits and the system clock switch status follows the switch.
// No clock tree is computed, peripherals use the frequencies configured in the platform description.
//
using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    [AllowedTranslations(AllowedTranslation.WordToDoubleWord | AllowedTranslation.ByteToDoubleWord)]
    public class STM32G4_RCC : IDoubleWordPeripheral, IKnownSize
    {
        public STM32G4_RCC(IMachine machine)
        {
            registers = new uint[Size / 4];
            Reset();
        }

        public void Reset()
        {
            Array.Clear(registers, 0, registers.Length);
            registers[Cr / 4] = 0x00000500;       // HSION | HSIRDY
            registers[Cfgr / 4] = 0x00000005;     // SW = SWS = HSI16
            registers[Pllcfgr / 4] = 0x00001000;
            registers[Ahb1enr / 4] = 0x00000100;  // FLASH clock
            registers[Apb1enr1 / 4] = 0x00000000;
            registers[Csr / 4] = 0x0C000000;
        }

        public uint ReadDoubleWord(long offset)
        {
            var value = registers[offset / 4];
            switch(offset)
            {
            case Cr:
                value = Follow(value, 8, 10);   // HSION  -> HSIRDY
                value = Follow(value, 16, 17);  // HSEON  -> HSERDY
                value = Follow(value, 24, 25);  // PLLON  -> PLLRDY
                break;
            case Cfgr:
                value = (value & ~0xCu) | ((value & 0x3u) << 2); // SW -> SWS
                break;
            case Bdcr:
                value = Follow(value, 0, 1);    // LSEON  -> LSERDY
                break;
            case Csr:
                value = Follow(value, 0, 1);    // LSION  -> LSIRDY
                break;
            case Crrcr:
                value = Follow(value, 0, 1);    // HSI48ON -> HSI48RDY
                break;
            }
            return value;
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset < 0 || offset >= Size)
            {
                this.WarningLog("Write outside of the register file: 0x{0:X}", offset);
                return;
            }
            registers[offset / 4] = value;
        }

        public long Size => 0x400;

        private static uint Follow(uint value, int onBit, int readyBit)
        {
            return ((value >> onBit) & 1) != 0 ? (value | (1u << readyBit)) : (value & ~(1u << readyBit));
        }

        private readonly uint[] registers;

        private const long Cr = 0x00;
        private const long Cfgr = 0x08;
        private const long Pllcfgr = 0x0C;
        private const long Ahb1enr = 0x48;
        private const long Apb1enr1 = 0x58;
        private const long Bdcr = 0x90;
        private const long Csr = 0x94;
        private const long Crrcr = 0x98;
    }
}
