//
// Minimal STM32G4 flash interface (RM0440, "Embedded flash memory", category 2/3 devices).
//
// Provides the access control register (latency read-back), the key based unlock sequences and the option
// register. The option bytes are loaded with BOR_LEV = 4 so the firmware's option byte programming
// (see system.c) is skipped, just as on a CANable that has been booted before. Programming and erasing of the
// flash array is not modelled.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.MTD
{
    [AllowedTranslations(AllowedTranslation.WordToDoubleWord | AllowedTranslation.ByteToDoubleWord)]
    public class STM32G4_FlashController : IDoubleWordPeripheral, IKnownSize
    {
        public STM32G4_FlashController(IMachine machine)
        {
            Reset();
        }

        public void Reset()
        {
            acr = 0x00040601;
            cr = 0xC0000000; // LOCK | OPTLOCK
            sr = 0;
            eccr = 0;
            optr = 0xFFEFF4AA; // RDP = 0xAA (level 0), BOR_LEV = 4 (2.8 V)
            keyState = 0;
            optKeyState = 0;
        }

        public uint ReadDoubleWord(long offset)
        {
            switch(offset)
            {
            case Acr: return acr;
            case Sr: return sr;
            case Cr: return cr;
            case Eccr: return eccr;
            case Optr: return optr;
            default:
                return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            switch(offset)
            {
            case Acr:
                acr = value;
                break;
            case Keyr:
                keyState = Advance(keyState, value);
                if(keyState == 2)
                {
                    cr &= ~LockBit;
                    keyState = 0;
                }
                break;
            case OptKeyr:
                optKeyState = Advance(optKeyState, value);
                if(optKeyState == 2)
                {
                    cr &= ~OptLockBit;
                    optKeyState = 0;
                }
                break;
            case Sr:
                sr &= ~value; // flags are cleared by writing 1
                break;
            case Cr:
                if((value & LockBit) != 0)
                {
                    cr |= LockBit;
                }
                if((value & OptLockBit) != 0)
                {
                    cr |= OptLockBit;
                }
                if((cr & LockBit) == 0)
                {
                    cr = (cr & (LockBit | OptLockBit)) | (value & ~(LockBit | OptLockBit));
                }
                break;
            case Eccr:
                eccr = value;
                break;
            case Optr:
                if((cr & OptLockBit) == 0)
                {
                    optr = value;
                }
                break;
            default:
                this.NoisyLog("Write to unimplemented register 0x{0:X} = 0x{1:X}", offset, value);
                break;
            }
        }

        public long Size => 0x400;

        private static int Advance(int state, uint key)
        {
            if(state == 0)
            {
                return key == Key1 ? 1 : 0;
            }
            return key == Key2 ? 2 : 0;
        }

        private uint acr;
        private uint cr;
        private uint sr;
        private uint eccr;
        private uint optr;
        private int keyState;
        private int optKeyState;

        private const long Acr = 0x00;
        private const long Keyr = 0x08;
        private const long OptKeyr = 0x0C;
        private const long Sr = 0x10;
        private const long Cr = 0x14;
        private const long Eccr = 0x18;
        private const long Optr = 0x20;

        private const uint LockBit = 1u << 31;
        private const uint OptLockBit = 1u << 30;
        private const uint Key1 = 0x45670123;
        private const uint Key2 = 0xCDEF89AB;
    }
}
