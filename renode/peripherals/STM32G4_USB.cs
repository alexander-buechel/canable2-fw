//
// STM32G4 USB full-speed device peripheral (RM0440, chapter "Universal serial bus full-speed device interface")
//
// This model has two halves:
//
//  1. The *device* side: the register block (USB_EPnR, CNTR, ISTR, FNR, DADDR, BTABLE, LPMCSR, BCDR) and the
//     packet memory area (PMA, a separate ArrayMemory) as seen by the firmware / ST HAL (stm32g4xx_hal_pcd).
//
//  2. A small built-in *virtual host* standing in for the PC on the other end of the cable. It reacts to the
//     firmware pulling up D+ (BCDR.DPPU), performs bus reset and a Linux-like enumeration (device/config/string
//     descriptors, SET_ADDRESS, SET_CONFIGURATION, CDC SET_LINE_CODING / SET_CONTROL_LINE_STATE) and afterwards
//     bridges the CDC bulk endpoints to the Renode UART interface (IUART), so that the usual terminal / pty /
//     socket / UART tester tooling can talk to the firmware's virtual COM port.
//
// The host talks to the device exclusively through the same registers and packet memory the real USB core
// exposes (EPnR STAT/DTOG/CTR bits, buffer descriptor table, COUNT_RX/COUNT_TX), so it exercises the HAL
// code paths in the firmware exactly as the real hardware does.
//
// Not modelled: double-buffered / isochronous endpoints, suspend/resume, LPM, battery charging detection.
//
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Antmicro.Migrant;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Peripherals.UART;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.USB
{
    [AllowedTranslations(AllowedTranslation.WordToDoubleWord | AllowedTranslation.ByteToDoubleWord)]
    public class STM32G4_USB : IDoubleWordPeripheral, IKnownSize, IUART
    {
        public STM32G4_USB(IMachine machine, ArrayMemory pma, bool autoEnumerate = true, byte assignedAddress = 2)
        {
            this.machine = machine;
            this.pma = pma;
            this.assignedAddress = assignedAddress;
            AutoEnumerate = autoEnumerate;

            IRQ = new GPIO();
            HighPriorityIRQ = new GPIO();
            locker = new object();
            txQueue = new Queue<byte>();
            endpoints = new ushort[EndpointCount];

            // The host controller schedules one "frame" every millisecond, like a real full-speed host.
            frameTimer = new LimitTimer(machine.ClockSource, 1000, this, nameof(frameTimer), limit: 1,
                direction: Direction.Descending, enabled: true, workMode: WorkMode.Periodic, eventEnabled: true);
            frameTimer.LimitReached += HostFrame;

            Reset();
        }

        public void Reset()
        {
            lock(locker)
            {
                for(var i = 0; i < EndpointCount; i++)
                {
                    endpoints[i] = 0;
                }
                cntr = CntrPowerDown | CntrForceReset;
                istrFlags = 0;
                daddr = 0;
                btable = 0;
                lpmcsr = 0;
                bcdr = 0;
                frameNumber = 0;
                ResetHost();
                IRQ.Unset();
                HighPriorityIRQ.Unset();
            }
        }

        public uint ReadDoubleWord(long offset)
        {
            lock(locker)
            {
                if(offset >= 0 && offset < EndpointCount * 4 && (offset & 3) == 0)
                {
                    return endpoints[offset / 4];
                }
                switch(offset)
                {
                case RegCntr:
                    return cntr;
                case RegIstr:
                    return ReadIstr();
                case RegFnr:
                    return (uint)(frameNumber & 0x7FF);
                case RegDaddr:
                    return daddr;
                case RegBtable:
                    return btable;
                case RegLpmcsr:
                    return lpmcsr;
                case RegBcdr:
                    return bcdr;
                default:
                    this.WarningLog("Read from unimplemented register 0x{0:X}", offset);
                    return 0;
                }
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            lock(locker)
            {
                if(offset >= 0 && offset < EndpointCount * 4 && (offset & 3) == 0)
                {
                    WriteEndpointRegister((int)(offset / 4), (ushort)value);
                    UpdateInterrupts();
                    return;
                }
                switch(offset)
                {
                case RegCntr:
                    WriteCntr((ushort)value);
                    break;
                case RegIstr:
                    // Event bits are cleared by writing 0, writing 1 has no effect. CTR/DIR/EP_ID are read-only.
                    istrFlags &= (ushort)((ushort)value | (~IstrEventMask & 0xFFFF));
                    break;
                case RegFnr:
                    break;
                case RegDaddr:
                    daddr = (ushort)(value & 0xFF);
                    break;
                case RegBtable:
                    btable = (ushort)(value & 0xFFF8);
                    break;
                case RegLpmcsr:
                    lpmcsr = (ushort)value;
                    break;
                case RegBcdr:
                    WriteBcdr((ushort)value);
                    break;
                default:
                    this.WarningLog("Write to unimplemented register 0x{0:X} = 0x{1:X}", offset, value);
                    break;
                }
                UpdateInterrupts();
            }
        }

        public long Size => 0x400;

        // Low priority interrupt (USB_LP, NVIC 20) and high priority one (USB_HP, NVIC 19, unused by this model).
        public GPIO IRQ { get; }
        public GPIO HighPriorityIRQ { get; }

        // ------------------------------------------------------------------------------------------------
        // IUART: the CDC virtual COM port as seen by the host.
        // Bytes written here are sent to the device on the CDC bulk OUT endpoint, bytes the device sends on
        // the bulk IN endpoint are emitted through CharReceived.
        // ------------------------------------------------------------------------------------------------
        [field: Transient]
        public event Action<byte> CharReceived;

        public void WriteChar(byte value)
        {
            lock(locker)
            {
                if(txQueue.Count >= MaxHostQueue)
                {
                    this.WarningLog("Host transmit queue full, dropping byte 0x{0:X2}", value);
                    return;
                }
                txQueue.Enqueue(value);
            }
        }

        public uint BaudRate => lineCodingBaud;

        public Bits StopBits => lineCodingStopBits == 0 ? Bits.One : (lineCodingStopBits == 1 ? Bits.OneAndAHalf : Bits.Two);

        public Parity ParityBit
        {
            get
            {
                switch(lineCodingParity)
                {
                case 0: return Parity.None;
                case 1: return Parity.Odd;
                case 2: return Parity.Even;
                case 3: return Parity.Forced1;
                case 4: return Parity.Forced0;
                default: return Parity.Unsupported;
                }
            }
        }

        // ------------------------------------------------------------------------------------------------
        // Host-visible state, handy from the Renode monitor and from Robot tests.
        // ------------------------------------------------------------------------------------------------

        // When false the host only attaches and waits for Enumerate() / BusReset() to be called.
        public bool AutoEnumerate { get; set; }

        public string HostState => hostState.ToString();
        public bool Connected => (bcdr & BcdrDppu) != 0;
        public bool Configured => hostState == HostStates.Configured;
        public int EnumerationCount { get; private set; }
        public string LastError { get; private set; } = "";

        public byte DeviceAddress => (byte)(daddr & 0x7F);
        public bool FunctionEnabled => (daddr & DaddrEf) != 0;
        public byte MaxPacketSize0 => maxPacketSize0;
        public ushort VendorId { get; private set; }
        public ushort ProductId { get; private set; }
        public ushort DeviceBcd { get; private set; }
        public byte DeviceClass { get; private set; }
        public string Manufacturer { get; private set; } = "";
        public string Product { get; private set; } = "";
        public string SerialNumber { get; private set; } = "";
        public string DeviceDescriptor => ToHex(deviceDescriptor);
        public string ConfigurationDescriptor => ToHex(configDescriptor);
        public byte ConfigurationValue { get; private set; }
        public int InterfaceCount { get; private set; }
        public byte BulkInEndpoint => bulkIn;
        public byte BulkOutEndpoint => bulkOut;
        public byte NotificationEndpoint => notifyIn;
        public ushort BulkMaxPacketSize => bulkMaxPacket;
        public bool HasCdcInterface => cdcCommInterface >= 0;
        public ushort ControlLineState { get; private set; }
        public ulong BytesToDevice { get; private set; }
        public ulong BytesFromDevice { get; private set; }

        // Pretend the cable was re-plugged (or the hub issued a port reset): reset the bus and re-enumerate.
        public void BusReset()
        {
            lock(locker)
            {
                if(!Connected)
                {
                    this.WarningLog("Bus reset requested while the device is not connected (DPPU is off)");
                    return;
                }
                hostState = HostStates.Attached;
                hostStateTicks = AttachDelayFrames; // reset on the next frame
            }
        }

        // Start enumeration manually (only needed when AutoEnumerate is false).
        public void Enumerate()
        {
            lock(locker)
            {
                if(!Connected)
                {
                    this.WarningLog("Cannot enumerate, the device is not connected (DPPU is off)");
                    return;
                }
                if(hostState != HostStates.Enumerating)
                {
                    StartEnumeration();
                }
            }
        }

        // ------------------------------------------------------------------------------------------------
        // Device side register behaviour
        // ------------------------------------------------------------------------------------------------

        private void WriteCntr(ushort value)
        {
            var oldCntr = cntr;
            cntr = (ushort)((ushort)value & ~CntrReservedMask);
            if((cntr & CntrForceReset) != 0 && (oldCntr & CntrForceReset) == 0)
            {
                // FRES: behave as if a USB reset had been received, but without the RESET event.
                ResetEndpointRegisters();
                daddr = 0;
            }
        }

        private void WriteBcdr(ushort value)
        {
            var wasConnected = Connected;
            bcdr = (ushort)(value & (BcdrDppu | 0x000F));
            if(!wasConnected && Connected)
            {
                this.InfoLog("D+ pull-up enabled, host sees the device");
                hostState = HostStates.Attached;
                hostStateTicks = 0;
            }
            else if(wasConnected && !Connected)
            {
                this.InfoLog("D+ pull-up disabled, host sees a disconnect");
                ResetHost();
            }
        }

        private uint ReadIstr()
        {
            var istr = (uint)(istrFlags & IstrEventMask);
            for(var i = 0; i < EndpointCount; i++)
            {
                var reg = endpoints[i];
                if((reg & EpCtrRx) != 0)
                {
                    return istr | IstrCtr | IstrDir | (uint)i;
                }
                if((reg & EpCtrTx) != 0)
                {
                    return istr | IstrCtr | (uint)i;
                }
            }
            return istr;
        }

        // Write semantic of USB_EPnR: CTR bits are cleared by writing 0, DTOG and STAT bits toggle when written
        // with 1, SETUP is read-only, the rest (EP_TYPE, EP_KIND, EA) is plain read/write.
        private void WriteEndpointRegister(int index, ushort value)
        {
            var reg = endpoints[index];
            var next = reg;
            const int ctrMask = EpCtrRx | EpCtrTx;
            const int toggleMask = EpDtogRx | EpDtogTx | EpRxStat | EpTxStat;
            const int plainMask = EpType | EpKind | EpAddress;
            next = (ushort)(next & ~ctrMask);
            next |= (ushort)(reg & value & ctrMask);
            next ^= (ushort)(value & toggleMask);
            next = (ushort)((next & ~plainMask) | (value & plainMask));
            endpoints[index] = next;
        }

        private void ResetEndpointRegisters()
        {
            for(var i = 0; i < EndpointCount; i++)
            {
                // CTR_RX and CTR_TX are kept so that a completed transaction is not lost on reset.
                endpoints[i] &= (ushort)(EpCtrRx | EpCtrTx);
            }
        }

        private void UpdateInterrupts()
        {
            var ctr = false;
            for(var i = 0; i < EndpointCount; i++)
            {
                if((endpoints[i] & (EpCtrRx | EpCtrTx)) != 0)
                {
                    ctr = true;
                    break;
                }
            }
            var irq = (ctr && (cntr & CntrCtrm) != 0) || ((istrFlags & cntr & IstrEventMask) != 0);
            if((cntr & (CntrPowerDown | CntrForceReset)) != 0)
            {
                irq = false;
            }
            IRQ.Set(irq);
        }

        private ushort ReadPma16(int offset)
        {
            var b = pma.ReadBytes(offset, 2);
            return (ushort)(b[0] | (b[1] << 8));
        }

        private void WritePma16(int offset, ushort value)
        {
            pma.WriteBytes(offset, new[] { (byte)value, (byte)(value >> 8) }, 0, 2);
        }

        // ------------------------------------------------------------------------------------------------
        // Bus transactions, as the USB core executes them. Called by the virtual host.
        // ------------------------------------------------------------------------------------------------

        private int FindEndpoint(int number, bool tx)
        {
            for(var i = 0; i < EndpointCount; i++)
            {
                var reg = endpoints[i];
                if((reg & EpAddress) != number)
                {
                    continue;
                }
                var stat = tx ? TxStat(reg) : RxStat(reg);
                if(stat != StatDisabled)
                {
                    return i;
                }
            }
            return -1;
        }

        private TransactionResult DeviceReceive(int number, byte[] data, bool setup)
        {
            if(!FunctionEnabled)
            {
                return TransactionResult.NoResponse;
            }
            var index = FindEndpoint(number, false);
            if(index < 0)
            {
                return TransactionResult.NoResponse;
            }
            var reg = endpoints[index];
            if(setup)
            {
                // SETUP is accepted by a control endpoint independently of the RX status being NAK/STALL.
                if((reg & EpType) != EpTypeControl)
                {
                    return TransactionResult.NoResponse;
                }
            }
            else
            {
                switch(RxStat(reg))
                {
                case StatStall:
                    return TransactionResult.Stall;
                case StatNak:
                    return TransactionResult.Nak;
                }
            }

            var entry = (btable & 0xFFF8) + index * 8;
            var address = ReadPma16(entry + 4) & 0xFFFE;
            var countReg = ReadPma16(entry + 6);
            var blocks = (countReg >> 10) & 0x1F;
            var capacity = (countReg & 0x8000) != 0 ? (blocks + 1) * 32 : blocks * 2;
            if(data.Length > capacity)
            {
                this.WarningLog("EP{0}: host packet of {1} bytes does not fit the {2} byte RX buffer, PMA overrun", number, data.Length, capacity);
                istrFlags |= IstrPmaOvr;
                UpdateInterrupts();
                return TransactionResult.NoResponse;
            }

            if(data.Length > 0)
            {
                pma.WriteBytes(address, data, 0, data.Length);
            }
            WritePma16(entry + 6, (ushort)((countReg & 0xFC00) | data.Length));

            reg |= EpCtrRx;
            if(setup)
            {
                reg |= EpSetup;
                if(TxStat(reg) != StatDisabled)
                {
                    reg = WithStat(reg, EpTxStatShift, StatNak);
                }
            }
            else
            {
                reg = (ushort)(reg & ~EpSetup);
                if((reg & EpType) != EpTypeIso)
                {
                    reg ^= EpDtogRx;
                }
            }
            reg = WithStat(reg, EpRxStatShift, StatNak);
            endpoints[index] = reg;
            this.NoisyLog("{0} to EP{1}: {2} bytes{3}", setup ? "SETUP" : "OUT", number, data.Length, data.Length > 0 && data.Length <= 16 ? " [" + ToHex(data) + "]" : "");
            UpdateInterrupts();
            return TransactionResult.Ack;
        }

        private TransactionResult DeviceSend(int number, out byte[] data)
        {
            data = null;
            if(!FunctionEnabled)
            {
                return TransactionResult.NoResponse;
            }
            var index = FindEndpoint(number, true);
            if(index < 0)
            {
                return TransactionResult.NoResponse;
            }
            var reg = endpoints[index];
            switch(TxStat(reg))
            {
            case StatStall:
                return TransactionResult.Stall;
            case StatNak:
                return TransactionResult.Nak;
            }

            var entry = (btable & 0xFFF8) + index * 8;
            var address = ReadPma16(entry) & 0xFFFE;
            var count = ReadPma16(entry + 2) & 0x3FF;
            data = count > 0 ? pma.ReadBytes(address, count) : new byte[0];

            reg |= EpCtrTx;
            reg = WithStat(reg, EpTxStatShift, StatNak);
            if((reg & EpType) != EpTypeIso)
            {
                reg ^= EpDtogTx;
            }
            endpoints[index] = reg;
            this.NoisyLog("IN from EP{0}: {1} bytes{2}", number, data.Length, data.Length > 0 && data.Length <= 16 ? " [" + ToHex(data) + "]" : "");
            UpdateInterrupts();
            return TransactionResult.Ack;
        }

        // ------------------------------------------------------------------------------------------------
        // Virtual host
        // ------------------------------------------------------------------------------------------------

        private void ResetHost()
        {
            hostState = HostStates.Idle;
            hostStateTicks = 0;
            control = null;
            enumeration = null;
            maxPacketSize0 = 8;
            bulkIn = 0;
            bulkOut = 0;
            notifyIn = 0;
            bulkMaxPacket = 64;
            cdcCommInterface = -1;
            lineCodingBaud = 115200;
            lineCodingStopBits = 0;
            lineCodingParity = 0;
            ControlLineState = 0;
            ConfigurationValue = 0;
            txQueue.Clear();
            deviceDescriptor = new byte[0];
            configDescriptor = new byte[0];
        }

        private void HostFrame()
        {
            lock(locker)
            {
                frameNumber = (frameNumber + 1) & 0x7FF;
                if(!Connected || (cntr & (CntrPowerDown | CntrForceReset)) != 0)
                {
                    return;
                }

                switch(hostState)
                {
                case HostStates.Attached:
                    if(++hostStateTicks > AttachDelayFrames)
                    {
                        HostBusReset();
                    }
                    break;
                case HostStates.Resetting:
                    if(++hostStateTicks > ResetRecoveryFrames && AutoEnumerate)
                    {
                        StartEnumeration();
                    }
                    break;
                case HostStates.Enumerating:
                    RunControl();
                    break;
                case HostStates.Configured:
                    RunBulk();
                    break;
                }

                if(hostState == HostStates.Resetting || hostState == HostStates.Enumerating || hostState == HostStates.Configured)
                {
                    istrFlags |= IstrSof;
                }
                UpdateInterrupts();
            }
        }

        private void HostBusReset()
        {
            this.InfoLog("Host: bus reset");
            ResetEndpointRegisters();
            daddr = 0;
            control = null;
            enumeration = null;
            bulkIn = 0;
            bulkOut = 0;
            istrFlags |= IstrReset;
            hostState = HostStates.Resetting;
            hostStateTicks = 0;
            UpdateInterrupts();
        }

        private void StartEnumeration()
        {
            if(hostState == HostStates.Attached || hostState == HostStates.Idle || hostState == HostStates.Failed)
            {
                // Manual enumeration of a device that was not reset yet.
                HostBusReset();
            }
            this.InfoLog("Host: starting enumeration");
            LastError = "";
            hostState = HostStates.Enumerating;
            hostStateTicks = 0;
            enumeration = EnumerationSequence().GetEnumerator();
            control = null;
        }

        private void Fail(string message)
        {
            LastError = message;
            this.ErrorLog("Host: enumeration failed: {0}", message);
            hostState = HostStates.Failed;
            control = null;
            enumeration = null;
        }

        private void RunControl()
        {
            for(var guard = 0; guard < 64; guard++)
            {
                if(control == null)
                {
                    if(enumeration == null || !enumeration.MoveNext())
                    {
                        enumeration = null;
                        if(hostState == HostStates.Enumerating)
                        {
                            hostState = HostStates.Configured;
                            EnumerationCount++;
                            this.InfoLog("Host: device configured (VID:PID {0:X4}:{1:X4}, address {2})", VendorId, ProductId, DeviceAddress);
                        }
                        return;
                    }
                    control = enumeration.Current;
                }

                if(!StepControl(control))
                {
                    // Blocked on a NAK / missing response: retry in the next frame.
                    if(control.Stage != ControlStage.Delay && ++control.BlockedFrames > ControlTimeoutFrames)
                    {
                        Fail($"{control.Name}: timeout in {control.Stage} stage ({control.LastResult})");
                    }
                    return;
                }
                control = null;
                return; // one control transfer per frame at most, as a real host with 1 ms frames would do
            }
        }

        // Advances a control transfer as far as possible. Returns true when the transfer is finished (successfully or
        // stalled), false when the device does not respond yet (NAK / not ready) and the host has to retry next frame.
        private bool StepControl(ControlTransfer t)
        {
            while(true)
            {
                byte[] data;
                switch(t.Stage)
                {
                case ControlStage.Delay:
                    if(++t.BlockedFrames < 0)
                    {
                        return false;
                    }
                    t.Done = true;
                    return true;

                case ControlStage.Setup:
                    if(!Transact(t, DeviceReceive(0, t.Setup, true)))
                    {
                        return t.Done;
                    }
                    t.Stage = t.Length == 0 ? ControlStage.StatusIn : (t.In ? ControlStage.DataIn : ControlStage.DataOut);
                    break;

                case ControlStage.DataIn:
                    if(!Transact(t, DeviceSend(0, out data)))
                    {
                        return t.Done;
                    }
                    t.InData.AddRange(data);
                    if(data.Length < maxPacketSize0 || t.InData.Count >= t.Length)
                    {
                        t.Stage = ControlStage.StatusOut;
                    }
                    break;

                case ControlStage.DataOut:
                    {
                        var chunk = Math.Min((int)maxPacketSize0, t.OutData.Length - t.OutOffset);
                        data = t.OutData.Skip(t.OutOffset).Take(chunk).ToArray();
                        if(!Transact(t, DeviceReceive(0, data, false)))
                        {
                            return t.Done;
                        }
                        t.OutOffset += chunk;
                        if(t.OutOffset >= t.OutData.Length)
                        {
                            t.Stage = ControlStage.StatusIn;
                        }
                    }
                    break;

                case ControlStage.StatusOut:
                    if(!Transact(t, DeviceReceive(0, new byte[0], false)))
                    {
                        return t.Done;
                    }
                    t.Done = true;
                    return true;

                case ControlStage.StatusIn:
                    if(!Transact(t, DeviceSend(0, out data)))
                    {
                        return t.Done;
                    }
                    t.Done = true;
                    return true;
                }
                t.BlockedFrames = 0;
            }
        }

        // Returns true when the stage was ACKed. A STALL ends the transfer (Done + Stalled), NAK / no response
        // leave it pending so that it is retried in the next frame.
        private bool Transact(ControlTransfer t, TransactionResult result)
        {
            t.LastResult = result;
            if(result == TransactionResult.Ack)
            {
                return true;
            }
            if(result == TransactionResult.Stall)
            {
                this.WarningLog("Host: {0} stalled by the device in {1} stage", t.Name, t.Stage);
                t.Stalled = true;
                t.Done = true;
            }
            return false;
        }

        private ControlTransfer NewControl(string name, byte requestType, byte request, ushort value, ushort index, ushort length, byte[] outData = null)
        {
            return new ControlTransfer
            {
                Name = name,
                Setup = new[]
                {
                    requestType, request,
                    (byte)value, (byte)(value >> 8),
                    (byte)index, (byte)(index >> 8),
                    (byte)length, (byte)(length >> 8),
                },
                OutData = outData ?? new byte[0],
            };
        }

        // The enumeration sequence is written as an iterator: every `yield return` hands one control transfer to
        // the host engine and resumes once it has completed, so the code reads like a normal host driver.
        private IEnumerable<ControlTransfer> EnumerationSequence()
        {
            ControlTransfer t;

            // 1. first 8 bytes of the device descriptor to learn bMaxPacketSize0
            t = NewControl("GET_DESCRIPTOR(device, 8)", 0x80, 0x06, 0x0100, 0, 8);
            yield return t;
            if(!Ok(t, 8))
            {
                yield break;
            }
            maxPacketSize0 = t.InData[7];

            // 2. SET_ADDRESS
            t = NewControl("SET_ADDRESS", 0x00, 0x05, assignedAddress, 0, 0);
            yield return t;
            if(!Ok(t, 0))
            {
                yield break;
            }
            yield return Delay(2); // SetAddress recovery time

            // 3. full device descriptor
            t = NewControl("GET_DESCRIPTOR(device)", 0x80, 0x06, 0x0100, 0, 18);
            yield return t;
            if(!Ok(t, 18))
            {
                yield break;
            }
            deviceDescriptor = t.InData.ToArray();
            DeviceBcd = (ushort)(deviceDescriptor[12] | (deviceDescriptor[13] << 8));
            DeviceClass = deviceDescriptor[4];
            VendorId = (ushort)(deviceDescriptor[8] | (deviceDescriptor[9] << 8));
            ProductId = (ushort)(deviceDescriptor[10] | (deviceDescriptor[11] << 8));
            var iManufacturer = deviceDescriptor[14];
            var iProduct = deviceDescriptor[15];
            var iSerial = deviceDescriptor[16];

            // 4. configuration descriptor: header first, then the whole thing
            t = NewControl("GET_DESCRIPTOR(configuration, 9)", 0x80, 0x06, 0x0200, 0, 9);
            yield return t;
            if(!Ok(t, 9))
            {
                yield break;
            }
            var totalLength = t.InData[2] | (t.InData[3] << 8);
            t = NewControl("GET_DESCRIPTOR(configuration)", 0x80, 0x06, 0x0200, 0, (ushort)totalLength);
            yield return t;
            if(!Ok(t, totalLength))
            {
                yield break;
            }
            configDescriptor = t.InData.ToArray();
            ParseConfiguration(configDescriptor);

            // 5. strings
            t = NewControl("GET_DESCRIPTOR(string 0)", 0x80, 0x06, 0x0300, 0, 255);
            yield return t;
            ushort langId = 0x0409;
            if(!t.Failed && t.InData.Count >= 4)
            {
                langId = (ushort)(t.InData[2] | (t.InData[3] << 8));
            }
            foreach(var entry in new[] { Tuple.Create(iManufacturer, 0), Tuple.Create(iProduct, 1), Tuple.Create(iSerial, 2) })
            {
                if(entry.Item1 == 0)
                {
                    continue;
                }
                t = NewControl($"GET_DESCRIPTOR(string {entry.Item1})", 0x80, 0x06, (ushort)(0x0300 | entry.Item1), langId, 255);
                yield return t;
                if(t.Failed || t.InData.Count < 2)
                {
                    this.WarningLog("Host: could not read string descriptor {0}", entry.Item1);
                    continue;
                }
                var text = Encoding.Unicode.GetString(t.InData.Skip(2).Take(Math.Max(0, Math.Min(t.InData[0], t.InData.Count) - 2)).ToArray());
                switch(entry.Item2)
                {
                case 0: Manufacturer = text; break;
                case 1: Product = text; break;
                default: SerialNumber = text; break;
                }
            }

            // 6. SET_CONFIGURATION
            var configurationValue = configDescriptor[5];
            t = NewControl("SET_CONFIGURATION", 0x00, 0x09, configurationValue, 0, 0);
            yield return t;
            if(!Ok(t, 0))
            {
                yield break;
            }
            ConfigurationValue = configurationValue;

            // 7. CDC ACM class requests, as sent by the Linux cdc_acm driver / any terminal opening the port
            if(cdcCommInterface >= 0)
            {
                var coding = new byte[] { 0x00, 0xC2, 0x01, 0x00, 0x00, 0x00, 0x08 }; // 115200 8N1
                t = NewControl("SET_LINE_CODING", 0x21, 0x20, 0, (ushort)cdcCommInterface, 7, coding);
                yield return t;
                t = NewControl("GET_LINE_CODING", 0xA1, 0x21, 0, (ushort)cdcCommInterface, 7);
                yield return t;
                if(!t.Failed && t.InData.Count >= 7)
                {
                    lineCodingBaud = (uint)(t.InData[0] | (t.InData[1] << 8) | (t.InData[2] << 16) | (t.InData[3] << 24));
                    lineCodingStopBits = t.InData[4];
                    lineCodingParity = t.InData[5];
                }
                t = NewControl("SET_CONTROL_LINE_STATE", 0x21, 0x22, 0x0003, (ushort)cdcCommInterface, 0); // DTR | RTS
                yield return t;
                if(!t.Failed)
                {
                    ControlLineState = 0x0003;
                }
            }
        }

        private ControlTransfer Delay(int frames)
        {
            return new ControlTransfer { Name = "delay", Stage = ControlStage.Delay, BlockedFrames = -frames };
        }

        private bool Ok(ControlTransfer t, int expectedLength)
        {
            if(t.Failed)
            {
                Fail($"{t.Name} was stalled by the device");
                return false;
            }
            if(t.In && t.InData.Count < Math.Min(expectedLength, t.Length) && expectedLength > 0)
            {
                Fail($"{t.Name} returned {t.InData.Count} bytes, expected {expectedLength}");
                return false;
            }
            return true;
        }

        private void ParseConfiguration(byte[] descriptor)
        {
            InterfaceCount = descriptor.Length > 4 ? descriptor[4] : 0;
            var currentInterface = -1;
            var currentClass = 0;
            for(var i = 0; i + 1 < descriptor.Length && descriptor[i] != 0; i += descriptor[i])
            {
                var length = descriptor[i];
                var type = descriptor[i + 1];
                if(type == 0x04 && i + 5 <= descriptor.Length)
                {
                    currentInterface = descriptor[i + 2];
                    currentClass = descriptor[i + 5];
                    if(currentClass == 0x02 && cdcCommInterface < 0)
                    {
                        cdcCommInterface = currentInterface;
                    }
                }
                else if(type == 0x05 && i + 7 <= descriptor.Length)
                {
                    var address = descriptor[i + 2];
                    var attributes = descriptor[i + 3] & 0x03;
                    var maxPacket = (ushort)(descriptor[i + 4] | (descriptor[i + 5] << 8));
                    if(attributes == 2 && bulkIn == 0 && (address & 0x80) != 0)
                    {
                        bulkIn = address;
                        bulkMaxPacket = maxPacket;
                    }
                    else if(attributes == 2 && bulkOut == 0 && (address & 0x80) == 0)
                    {
                        bulkOut = address;
                        bulkMaxPacket = maxPacket;
                    }
                    else if(attributes == 3 && notifyIn == 0 && (address & 0x80) != 0)
                    {
                        notifyIn = address;
                    }
                }
            }
            this.InfoLog("Host: configuration has {0} interface(s), bulk IN 0x{1:X2}, bulk OUT 0x{2:X2}, notification 0x{3:X2}", InterfaceCount, bulkIn, bulkOut, notifyIn);
        }

        // Bulk traffic: what a CDC ACM driver does once the port is open. A full-speed frame fits ~19 packets of 64 bytes.
        private void RunBulk()
        {
            if(bulkOut != 0)
            {
                for(var n = 0; n < MaxPacketsPerFrame && txQueue.Count > 0; n++)
                {
                    var chunk = txQueue.Take(bulkMaxPacket).ToArray();
                    if(DeviceReceive(bulkOut & 0x0F, chunk, false) != TransactionResult.Ack)
                    {
                        break;
                    }
                    for(var i = 0; i < chunk.Length; i++)
                    {
                        txQueue.Dequeue();
                    }
                    BytesToDevice += (ulong)chunk.Length;
                }
            }

            if(bulkIn != 0)
            {
                var received = new List<byte>();
                for(var n = 0; n < MaxPacketsPerFrame; n++)
                {
                    byte[] data;
                    if(DeviceSend(bulkIn & 0x0F, out data) != TransactionResult.Ack)
                    {
                        break;
                    }
                    received.AddRange(data);
                }
                if(received.Count > 0)
                {
                    BytesFromDevice += (ulong)received.Count;
                    var handler = CharReceived;
                    if(handler != null)
                    {
                        foreach(var b in received)
                        {
                            handler(b);
                        }
                    }
                }
            }
        }

        private static string ToHex(byte[] data)
        {
            return string.Concat(data.Select(b => b.ToString("X2")));
        }

        private static int RxStat(ushort reg) => (reg >> EpRxStatShift) & 3;
        private static int TxStat(ushort reg) => (reg >> EpTxStatShift) & 3;

        private static ushort WithStat(ushort reg, int shift, int stat)
        {
            return (ushort)((reg & ~(3 << shift)) | (stat << shift));
        }

        private readonly IMachine machine;
        private readonly ArrayMemory pma;
        private readonly object locker;
        private readonly ushort[] endpoints;
        private readonly Queue<byte> txQueue;
        private readonly LimitTimer frameTimer;
        private readonly byte assignedAddress;

        private ushort cntr;
        private ushort istrFlags;
        private ushort daddr;
        private ushort btable;
        private ushort lpmcsr;
        private ushort bcdr;
        private int frameNumber;

        private HostStates hostState;
        private int hostStateTicks;
        private ControlTransfer control;
        private IEnumerator<ControlTransfer> enumeration;
        private byte maxPacketSize0;
        private byte bulkIn;
        private byte bulkOut;
        private byte notifyIn;
        private ushort bulkMaxPacket;
        private int cdcCommInterface;
        private uint lineCodingBaud;
        private byte lineCodingStopBits;
        private byte lineCodingParity;
        private byte[] deviceDescriptor = new byte[0];
        private byte[] configDescriptor = new byte[0];

        private enum HostStates
        {
            Idle,         // not connected (no D+ pull-up)
            Attached,     // pull-up seen, waiting for the connect debounce time
            Resetting,    // bus reset issued, waiting for reset recovery
            Enumerating,
            Configured,
            Failed,
        }

        private enum TransactionResult
        {
            Ack,
            Nak,
            Stall,
            NoResponse,
        }

        private enum ControlStage
        {
            Setup,
            DataIn,
            DataOut,
            StatusIn,
            StatusOut,
            Delay,
        }

        private class ControlTransfer
        {
            public string Name;
            public byte[] Setup = new byte[8];
            public byte[] OutData = new byte[0];
            public int OutOffset;
            public List<byte> InData = new List<byte>();
            public ControlStage Stage;
            public bool Done;
            public bool Stalled;
            public int BlockedFrames;
            public TransactionResult LastResult;

            public bool In => (Setup[0] & 0x80) != 0;
            public int Length => Setup[6] | (Setup[7] << 8);
            public bool Failed => Stalled;
        }

        // Frames (ms) the host waits after connect before resetting, and after reset before talking to the device.
        private const int AttachDelayFrames = 10;
        private const int ResetRecoveryFrames = 10;
        private const int ControlTimeoutFrames = 2000;
        private const int MaxPacketsPerFrame = 16;
        private const int MaxHostQueue = 1 << 16;

        private const int EndpointCount = 8;

        private const long RegCntr = 0x40;
        private const long RegIstr = 0x44;
        private const long RegFnr = 0x48;
        private const long RegDaddr = 0x4C;
        private const long RegBtable = 0x50;
        private const long RegLpmcsr = 0x54;
        private const long RegBcdr = 0x58;

        private const ushort CntrCtrm = 0x8000;
        private const ushort CntrPowerDown = 0x0002;
        private const ushort CntrForceReset = 0x0001;
        private const ushort CntrReservedMask = 0x00E0;

        private const uint IstrCtr = 0x8000;
        private const ushort IstrPmaOvr = 0x4000;
        private const ushort IstrReset = 0x0400;
        private const ushort IstrSof = 0x0200;
        private const uint IstrDir = 0x0010;
        private const ushort IstrEventMask = 0x7F00;

        private const ushort DaddrEf = 0x0080;
        private const ushort BcdrDppu = 0x8000;

        private const ushort EpCtrRx = 0x8000;
        private const ushort EpDtogRx = 0x4000;
        private const ushort EpRxStat = 0x3000;
        private const ushort EpSetup = 0x0800;
        private const ushort EpType = 0x0600;
        private const ushort EpKind = 0x0100;
        private const ushort EpCtrTx = 0x0080;
        private const ushort EpDtogTx = 0x0040;
        private const ushort EpTxStat = 0x0030;
        private const ushort EpAddress = 0x000F;
        private const ushort EpTypeControl = 0x0200;
        private const ushort EpTypeIso = 0x0400;
        private const int EpRxStatShift = 12;
        private const int EpTxStatShift = 4;

        private const int StatDisabled = 0;
        private const int StatStall = 1;
        private const int StatNak = 2;
        private const int StatValid = 3;
    }
}
