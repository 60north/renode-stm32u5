//
// Copyright (c) 2026 Fedor Druzhinin
// Copyright (c) 2010-2024 Antmicro
//
// This file is licensed under the MIT License.
//
// STM32U5 SPI, RM0456 Rev 7 section 68.
//
// Adapted from renode-infrastructure
//   src/Emulator/Peripherals/Peripherals/SPI/STM32H7_SPI.cs @ 7ea0541f8d9b
// (identical in Renode 1.17.0), using its WBA register layout, which matches stm32u575xx.h.
// Changes for STM32U5:
// - Reset values from RM0456 68.8: SPI_CFG1 0x0007 0007 (8-bit frames), SPI_CRCPOLY 0x0000 0107.
// - Fields written by STM32CubeU5 (SSI, MASRX, FTHLV, CRC settings, all of SPI_CFG2, the other
//   interrupt enables and clear flags) are stored instead of tagged. Clock polarity/phase, slave
//   select management and CRC do not change the data exchanged with the slave.
// - RXP follows the FIFO threshold FTHLV; RXWNE and RXPLVL report the RxFIFO occupancy
//   (RM0456 68.8.6); TXTF and DXP raise interrupts when enabled.
// - SPI_AUTOCR (0x1C), SPI_CRCPOLY, SPI_UDRDR are stored; SPI_TXCRC and SPI_RXCRC read 0.
// - Limited-feature instance (SPI3, RM0456 Table 696): TSIZE[15:10] and DSIZE[4] reserved,
//   DSIZE[2:0] fixed to 111 (8 or 16-bit frames).
// - The receive DMA request is a level: RXDMAEN set and data in the RxFIFO.
// - With software slave select (SSM = 1) the transaction of the slave ends when the chip select
//   input (GPIO 0, from the chip select pin) goes high, not at the end of each transfer.
// Limitations: master mode only; underrun, overrun, CRC errors, mode faults and the TI frame
// format are not reported; the FIFOs are not bounded; transfers complete immediately.
//
using System;
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.SPI
{
    public class STM32U5_SPI : NullRegistrationPointPeripheralContainer<ISPIPeripheral>, IKnownSize, IDoubleWordPeripheral, IWordPeripheral, IBytePeripheral, IGPIOReceiver
    {
        public STM32U5_SPI(IMachine machine, bool limitedFeatures = false) : base(machine)
        {
            this.limitedFeatures = limitedFeatures;
            registers = new DoubleWordRegisterCollection(this);
            IRQ = new GPIO();
            DMARecieve = new GPIO();

            transmitFifo = new Queue<uint>();
            receiveFifo = new Queue<uint>();

            DefineRegisters();
            Reset();
        }

        public override void Reset()
        {
            IRQ.Unset();
            DMARecieve.Unset();
            iolockValue = false;
            transmittedPackets = 0;
            transmitFifo.Clear();
            receiveFifo.Clear();
            registers.Reset();
        }

        // We can't use AllowedTranslations because then WriteByte/WriteWord will trigger
        // an additional read (see ReadWriteExtensions:WriteByteUsingDoubleWord).
        // We can't have this happenning for the data register.
        public byte ReadByte(long offset)
        {
            return (byte)ReadDoubleWord(offset);
        }

        public void WriteByte(long offset, byte value)
        {
            WriteDoubleWord(offset, value);
        }

        public ushort ReadWord(long offset)
        {
            return (ushort)ReadDoubleWord(offset);
        }

        public void WriteWord(long offset, ushort value)
        {
            WriteDoubleWord(offset, value);
        }

        public uint ReadDoubleWord(long offset)
        {
            return registers.Read(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(CanWriteToRegister((Registers)offset, value))
            {
                registers.Write(offset, value);
            }
        }

        public long Size => 0x400;

        public GPIO IRQ { get; }

        public GPIO DMARecieve { get; }

        // STM32U5 SPI has the register layout of the WBA variant of the upstream model.
        protected virtual bool IsWba { get; } = true;

        private void DefineRegisters()
        {
            Registers.Control1.Define(registers)
                .WithFlag(0, out peripheralEnabled, name: "SPE", changeCallback: (_, value) =>
                    {
                        if(value)
                        {
                            if(!masterMode.Value)
                            {
                                this.Log(LogLevel.Error, "Enabled in SPI slave mode. Only master mode is supported");
                            }
                            TryTransmitData();
                        }
                        else
                        {
                            ResetTransmissionState();
                            transmitFifo.Clear();
                            receiveFifo.Clear();
                            UpdateDmaRequest();
                            transmissionSize.Value = 0;
                        }
                    })
                .WithReservedBits(1, 7)
                .WithFlag(8, name: "MASRX")
                .WithFlag(9, out startTransmission, FieldMode.Read | FieldMode.Set, name: "CSTART", changeCallback: (_, value) =>
                    {
                        if(value)
                        {
                            endOfTransfer.Value = false;
                            TryTransmitData();
                        }
                    })
                .WithFlag(10, FieldMode.Set, name: "CSUSP", writeCallback: (_, value) =>
                    {
                        if(value)
                        {
                            if(!endOfTransfer.Value)
                            {
                                EndTransfer();
                            }
                            suspensionStatus.Value = true;
                            UpdateInterrupts();
                        }
                    })
                .WithFlag(11, name: "HDDIR")
                .WithFlag(12, name: "SSI")
                .WithFlag(13, name: "CRC33_17")
                .WithFlag(14, name: "RCRCINI")
                .WithFlag(15, name: "TCRCINI")
                .WithFlag(16, name: "IOLOCK", valueProviderCallback: _ => iolockValue, changeCallback: (_, value) =>
                    {
                        if(value && !peripheralEnabled.Value)
                        {
                            this.Log(LogLevel.Warning, "Attempted to set IOLOCK while peripheral is enabled");
                            return;
                        }

                        iolockValue = value;
                    })
                .WithReservedBits(17, 15);

            Registers.Control2.Define(registers)
                .WithValueField(0, limitedFeatures ? 10 : 16, out transmissionSize, name: "TSIZE")
                .If(limitedFeatures)
                    .Then(r => r.WithReservedBits(10, 6))
                    .Else(_ => { })
                .If(IsWba)
                    .Then(r => r.WithReservedBits(16, 16))
                    .Else(r => r.WithTag("TSER", 16, 16));

            Registers.Configuration1.Define(registers, 0x00070007)
                .WithValueField(0, 5, out packetSizeBits, writeCallback: (_, value) =>
                    {
                        if(limitedFeatures)
                        {
                            // DSIZE[4] reserved, DSIZE[2:0] fixed to 1 (RM0456 68.8.3).
                            packetSizeBits.Value = (value & 0x8) | 0x7;
                        }
                    }, name: "DSIZE")
                .WithValueField(5, limitedFeatures ? 2 : 4, out fifoThreshold, name: "FTHLV")
                .If(limitedFeatures)
                    .Then(r => r.WithReservedBits(7, 2))
                    .Else(_ => { })
                .If(IsWba)
                    .Then(r => r
                        .WithFlag(9, name: "UDRCFG")
                        .WithReservedBits(10, 3))
                    .Else(r => r
                        .WithTag("UDRCFG", 9, 2)
                        .WithTag("UDRDET", 11, 2))
                .WithReservedBits(13, 1)
                .WithFlag(14, out receiveDMAEnabled, changeCallback: (_, __) => UpdateDmaRequest(), name: "RXDMAEN")
                // Software expects this value to be as it was set. Transmitting with DMA doesn't require any special logic
                .WithFlag(15, name: "TXDMAEN")
                .WithValueField(16, 5, name: "CRCSIZE")
                .WithReservedBits(21, 1)
                .WithFlag(22, name: "CRCEN")
                .WithReservedBits(23, 5)
                .WithValueField(28, 3, name: "MBR")
                .If(IsWba)
                    .Then(r => r.WithFlag(31, name: "BPASS"))
                    .Else(r => r.WithReservedBits(31, 1));

            Registers.Configuration2.Define(registers)
                .WithValueField(0, 4, name: "MSSI")
                .WithValueField(4, 4, name: "MIDI")
                .WithReservedBits(8, 5)
                .If(IsWba)
                    .Then(r => r
                        .WithFlag(13, name: "RDIOM")
                        .WithFlag(14, name: "RDIOP"))
                    .Else(r => r.WithReservedBits(13, 2))
                .WithFlag(15, name: "IOSWP")
                .WithReservedBits(16, 1)
                .WithValueField(17, 2, name: "COMM")
                .WithValueField(19, 3, name: "SP")
                // Only master mode is supported
                .WithFlag(22, out masterMode, name: "MASTER")
                .WithFlag(23, out leastSignificantByteFirst, name: "LSBFRST")
                .WithFlag(24, name: "CPHA")
                .WithFlag(25, name: "CPOL")
                .WithFlag(26, out softwareSlaveManagement, name: "SSM")
                .WithReservedBits(27, 1)
                .WithFlag(28, name: "SSIOP")
                .WithFlag(29, name: "SSOE")
                .WithFlag(30, name: "SSOM")
                .WithFlag(31, name: "AFCNTR");

            Registers.InterruptEnable.Define(registers)
                .WithFlag(0, out receiveFifoThresholdInterruptEnable, name: "RXPIE")
                .WithFlag(1, out transmitFifoThresholdInterruptEnable, name: "TXPIE")
                .WithFlag(2, out duplexPacketInterruptEnable, name: "DXPIE")
                .WithFlag(3, out endOfTransferInterruptEnable, name: "EOTIE")
                .WithFlag(4, out transmissionFilledInterruptEnable, name: "TXTFIE")
                .WithFlag(5, name: "UDRIE")
                .WithFlag(6, name: "OVRIE")
                .WithFlag(7, name: "CRCEIE")
                .WithFlag(8, name: "TIFREIE")
                .WithFlag(9, name: "MODFIE")
                .If(IsWba)
                    .Then(r => r.WithReservedBits(10, 1))
                    .Else(r => r.WithTaggedFlag("TSERFIE", 10))
                .WithReservedBits(11, 21)
                .WithWriteCallback((_, __) =>
                {
                    UpdateInterrupts();
                });

            Registers.Status.Define(registers)
                .WithFlag(0, FieldMode.Read, name: "RXP", valueProviderCallback: _ => ReceivePacketAvailable)
                // We always report that there is space for additional packets
                .WithFlag(1, FieldMode.Read, name: "TXP", valueProviderCallback: _ => true)
                // This flag is equal to RXP && TXP. Since TXP is always true this flag is equal to RXP
                .WithFlag(2, FieldMode.Read, name: "DXP", valueProviderCallback: _ => ReceivePacketAvailable)
                .WithFlag(3, out endOfTransfer, FieldMode.Read, name: "EOT")
                .WithFlag(4, FieldMode.Read, name: "TXTF", valueProviderCallback: _ => TransmissionFilled)
                // Underrun, overrun, CRC errors, TI frame errors and mode faults never occur in this model
                .WithFlag(5, FieldMode.Read, name: "UDR", valueProviderCallback: _ => false)
                .WithFlag(6, FieldMode.Read, name: "OVR", valueProviderCallback: _ => false)
                .WithFlag(7, FieldMode.Read, name: "CRCE", valueProviderCallback: _ => false)
                .WithFlag(8, FieldMode.Read, name: "TIFRE", valueProviderCallback: _ => false)
                .WithFlag(9, FieldMode.Read, name: "MODF", valueProviderCallback: _ => false)
                .If(IsWba)
                    .Then(r => r.WithReservedBits(10, 1))
                    .Else(r => r.WithTaggedFlag("TSERF", 10))
                .WithFlag(11, out suspensionStatus, FieldMode.Read, name: "SUSP")
                .WithFlag(12, FieldMode.Read, name: "TXC",
                    valueProviderCallback: _ => transmissionSize.Value == 0 ? transmitFifo.Count == 0 : endOfTransfer.Value)
                .WithValueField(13, 2, FieldMode.Read, name: "RXPLVL", valueProviderCallback: _ => ReceivePackingLevel)
                .WithFlag(15, FieldMode.Read, name: "RXWNE", valueProviderCallback: _ => ReceiveWordNotEmpty)
                .WithValueField(16, 16, FieldMode.Read, name: "CTSIZE", valueProviderCallback: _ => RemainingFrames);

            Registers.InterruptStatusFlagsClear.Define(registers)
                .WithReservedBits(0, 3)
                .WithFlag(3, FieldMode.Write, name: "EOTC", writeCallback: (_, value) =>
                    {
                        if(value)
                        {
                            ResetTransmissionState();
                        }
                    })
                .WithFlag(4, FieldMode.Write, name: "TXTFC")
                .WithFlag(5, FieldMode.Write, name: "UDRC")
                .WithFlag(6, FieldMode.Write, name: "OVRC")
                .WithFlag(7, FieldMode.Write, name: "CRCEC")
                .WithFlag(8, FieldMode.Write, name: "TIFREC")
                .WithFlag(9, FieldMode.Write, name: "MODFC")
                .If(IsWba)
                    .Then(r => r.WithReservedBits(10, 1))
                    .Else(r => r.WithTaggedFlag("TSERFC", 10))
                .WithFlag(11, name: "SUSPC", writeCallback: (_, value) =>
                    {
                        if(value)
                        {
                            suspensionStatus.Value = false;
                        }
                    })
                .WithReservedBits(12, 20);

            Registers.AutonomousModeControl.Define(registers)
                .WithReservedBits(0, 16)
                .WithValueField(16, 4, name: "TRIGSEL")
                .WithFlag(20, name: "TRIGPOL")
                .WithFlag(21, name: "TRIGEN")
                .WithReservedBits(22, 10);

            Registers.CRCPolynomial.Define(registers, 0x00000107)
                .WithValueField(0, 32, name: "CRCPOLY");

            Registers.TransmitterCRC.Define(registers)
                .WithValueField(0, 32, FieldMode.Read, valueProviderCallback: _ => 0, name: "TXCRC");

            Registers.ReceiverCRC.Define(registers)
                .WithValueField(0, 32, FieldMode.Read, valueProviderCallback: _ => 0, name: "RXCRC");

            Registers.UnderrunData.Define(registers)
                .WithValueField(0, 32, name: "UDRDR");

            Registers.TransmitData.Define(registers)
                .WithValueField(0, 32, FieldMode.Write, name: "SPI_TXDR", writeCallback: (_, value) =>
                    {
                        transmitFifo.Enqueue((uint)value);
                        TryTransmitData();
                    });

            Registers.ReceiveData.Define(registers)
                .WithValueField(0, 32, FieldMode.Read, name: "SPI_RXDR", valueProviderCallback: _ =>
                    {
                        if(!receiveFifo.TryDequeue(out var value))
                        {
                            this.Log(LogLevel.Error, "Receive data FIFO is empty. Returning 0");
                            return 0;
                        }
                        UpdateDmaRequest();
                        UpdateInterrupts();
                        return value;
                    });

            if(!IsWba)
            {
                Registers.I2SConfiguration.Define(registers)
                    .WithFlag(0, name: "I2SMOD", valueProviderCallback: _ => false, writeCallback: (_, value) =>
                        {
                            if(value)
                            {
                                this.Log(LogLevel.Error, "Attempted to enable I2S. This mode is not supported");
                            }
                        })
                    .WithTag("I2SCFG[2:0]", 1, 3)
                    .WithTag("I2SSTD[1:0]", 4, 2)
                    .WithReservedBits(6, 1)
                    .WithTaggedFlag("PCMSYNC", 7)
                    .WithTag("DATLEN[1:0]", 8, 2)
                    .WithTaggedFlag("CHLEN", 10)
                    .WithTaggedFlag("CKPOL", 11)
                    .WithTaggedFlag("FIXCH", 12)
                    .WithTaggedFlag("WSINV", 13)
                    .WithTaggedFlag("DATFMT", 14)
                    .WithReservedBits(15, 1)
                    .WithTag("I2SDIV[7:0]", 16, 8)
                    .WithTaggedFlag("ODD", 24)
                    .WithTaggedFlag("MCKOE", 25)
                    .WithReservedBits(26, 6);
            }
        }

        private bool CanWriteToRegister(Registers reg, uint value)
        {
            if(peripheralEnabled.Value)
            {
                switch(reg)
                {
                case Registers.Configuration1:
                case Registers.Configuration2:
                case Registers.CRCPolynomial:
                case Registers.UnderrunData:
                    this.Log(LogLevel.Error, "Attempted to write 0x{0:X} to {0} register while peripheral is enabled", value, reg);
                    return false;
                }
            }

            return true;
        }

        private void TryTransmitData()
        {
            if(!peripheralEnabled.Value || !startTransmission.Value || transmitFifo.Count == 0)
            {
                return;
            }

            // This many bytes are needed to hold all of the packet bits (using ceiling division)
            // The value of the register is one less that the amount of required bits
            var byteCount = (int)packetSizeBits.Value / 8 + 1;
            var bytes = new byte[MaxPacketBytes];
            var reverseBytes = BitConverter.IsLittleEndian && !leastSignificantByteFirst.Value;

            while(transmitFifo.Count != 0)
            {
                var value = transmitFifo.Dequeue();
                BitHelper.GetBytesFromValue(bytes, 0, value, byteCount, reverseBytes);

                for(var i = 0; i < byteCount; i++)
                {
                    bytes[i] = RegisteredPeripheral?.Transmit(bytes[i]) ?? 0;
                }

                receiveFifo.Enqueue(BitHelper.ToUInt32(bytes, 0, byteCount, reverseBytes));

                UpdateDmaRequest();
                transmittedPackets++;
            }

            if(transmissionSize.Value == 0)
            {
                // [Transaction handling, p. 1621](https://www.st.com/resource/en/reference_manual/rm0493-multiprotocol-wireless-bluetooth-lowenergy-and-ieee802154-stm32wba5xxx-armbased-32bit-mcus-stmicroelectronics.pdf)
                // SPI operates in endless transaction mode, which means that the SPI peripheral is not able to detect when a transaction completes.
                // CSTART flag is not reset automatically and we don't call FinishTransmission().
                // To reset CSTART and de-assert CS pin, CSUSP (master suspend request) flag should be set.
            }
            else if(transmittedPackets == transmissionSize.Value)
            {
                EndTransfer();
            }

            UpdateInterrupts();
        }

        private void EndTransfer()
        {
            if(!softwareSlaveManagement.Value)
            {
                RegisteredPeripheral?.FinishTransmission();
            }
            endOfTransfer.Value = true;
            startTransmission.Value = false;
        }

        // GPIO 0: the chip select line of the slave, driven by a GPIO pin of the firmware (active
        // low). With SSM = 1 the transaction of the slave ends when it goes high.
        public void OnGPIO(int number, bool value)
        {
            if(number != ChipSelectInput)
            {
                this.Log(LogLevel.Warning, "Unexpected GPIO input {0}", number);
                return;
            }
            if(value && !chipSelectReleased && softwareSlaveManagement.Value)
            {
                RegisteredPeripheral?.FinishTransmission();
            }
            chipSelectReleased = value;
        }

        private void UpdateDmaRequest()
        {
            DMARecieve.Set(receiveDMAEnabled.Value && receiveFifo.Count > 0);
        }

        private void UpdateInterrupts()
        {
            var rxp = ReceivePacketAvailable && receiveFifoThresholdInterruptEnable.Value;
            var dxp = ReceivePacketAvailable && duplexPacketInterruptEnable.Value;
            var txtf = TransmissionFilled && transmissionFilledInterruptEnable.Value;
            var eot = (endOfTransfer.Value || suspensionStatus.Value) && endOfTransferInterruptEnable.Value;

            var irqValue = transmitFifoThresholdInterruptEnable.Value || rxp || dxp || txtf || eot;
            this.Log(LogLevel.Debug, "Setting IRQ to {0}", irqValue);
            IRQ.Set(irqValue);
        }

        private void ResetTransmissionState()
        {
            endOfTransfer.Value = false;
            startTransmission.Value = false;
            transmittedPackets = 0;
            UpdateInterrupts();
        }

        // RXP: at least FTHLV + 1 frames in the RxFIFO (RM0456 68.8.6).
        private bool ReceivePacketAvailable => receiveFifo.Count > 0 && receiveFifo.Count >= (int)fifoThreshold.Value + 1;

        // Frames of up to 8, 16 and 32 bits occupy 1, 2 and 4 bytes of the RxFIFO.
        private int FrameBytes => (int)packetSizeBits.Value < 8 ? 1 : (int)packetSizeBits.Value < 16 ? 2 : 4;

        // RXWNE: at least four bytes in the RxFIFO, whatever DSIZE (RM0456 68.8.6).
        private bool ReceiveWordNotEmpty => receiveFifo.Count * FrameBytes >= 4;

        // RXPLVL: frames in the RxFIFO when RXWNE = 0 and frames have up to 16 bits, else 0.
        private ulong ReceivePackingLevel => ReceiveWordNotEmpty || (int)packetSizeBits.Value >= 16 ? 0 : (ulong)Math.Min(receiveFifo.Count, 3);

        private ulong RemainingFrames => limitedFeatures || transmittedPackets >= transmissionSize.Value ? 0 : transmissionSize.Value - transmittedPackets;

        // TXTF: all TSIZE frames have been written to the transmit FIFO.
        private bool TransmissionFilled => transmissionSize.Value != 0 && transmittedPackets >= transmissionSize.Value;

        private bool iolockValue;
        private bool chipSelectReleased = true;
        private IFlagRegisterField softwareSlaveManagement;
        private const int ChipSelectInput = 0;
        private readonly bool limitedFeatures;
        private IValueRegisterField fifoThreshold;
        private IFlagRegisterField duplexPacketInterruptEnable;
        private IFlagRegisterField transmissionFilledInterruptEnable;
        private IFlagRegisterField receiveFifoThresholdInterruptEnable;
        private IFlagRegisterField transmitFifoThresholdInterruptEnable;
        private IFlagRegisterField endOfTransferInterruptEnable;
        private IFlagRegisterField endOfTransfer;
        private IFlagRegisterField peripheralEnabled;
        private IFlagRegisterField masterMode;
        private IFlagRegisterField receiveDMAEnabled;
        private IFlagRegisterField startTransmission;
        private IFlagRegisterField leastSignificantByteFirst;

        private IValueRegisterField transmissionSize;
        private IValueRegisterField packetSizeBits;
        private IFlagRegisterField suspensionStatus;

        private ulong transmittedPackets;

        private readonly DoubleWordRegisterCollection registers;
        private readonly Queue<uint> transmitFifo;
        private readonly Queue<uint> receiveFifo;

        private const int MaxPacketBytes = 4;

        private enum Registers
        {
            Control1 = 0x00,                    // SPI_CR1
            Control2 = 0x04,                    // SPI_CR2
            Configuration1 = 0x08,              // SPI_CFG1
            Configuration2 = 0x0C,              // SPI_CFG2
            InterruptEnable = 0x10,             // SPI_IER
            Status = 0x14,                      // SPI_SR
            InterruptStatusFlagsClear = 0x18,   // SPI_IFCR
            AutonomousModeControl = 0x1C,       // SPI_AUTOCR
            TransmitData = 0x20,                // SPI_TXDR
            ReceiveData = 0x30,                 // SPI_RXDR
            CRCPolynomial = 0x40,               // SPI_CRCPOLY
            TransmitterCRC = 0x44,              // SPI_TXCRC
            ReceiverCRC = 0x48,                 // SPI_RXCRC
            UnderrunData = 0x4C,                // SPI_UDRDR
            I2SConfiguration = 0x50,            // SPI_I2SCFGR (non-WBA only)
        }
    }
}