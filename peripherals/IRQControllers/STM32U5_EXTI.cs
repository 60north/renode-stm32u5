//
// Copyright (c) 2026 Fedor Druzhinin
// Copyright (c) 2010-2024 Antmicro
//
// This file is licensed under the MIT License.
//
// STM32U5 extended interrupt and event controller (EXTI), RM0456 Rev 7 section 23.
//
// The GPIO multiplexer (one local receiver per GPIO port, line source selected by
// EXTI_EXTICRm, re-evaluation when the selection changes) is adapted from renode-infrastructure
//   src/Emulator/Peripherals/Peripherals/IRQControllers/STM32WBA_EXTI.cs @ 7ea0541f8d9b
// The event logic follows RM0456 section 23.3 instead of STM32_EXTICore, because the WBA model
// does not apply EXTI_IMR1 and supports only 17 configurable lines:
// - an enabled rising (EXTI_RTSR1) or falling (EXTI_FTSR1) edge, or a write of 1 to
//   EXTI_SWIER1, sets the line's bit in EXTI_RPR1 or EXTI_FPR1;
// - the line's interrupt output is active while a pending bit is set and EXTI_IMR1 enables it;
// - writing 1 to a pending bit clears it.
// All lines of STM32U575 are configurable (RM0456 Table 189). Lines 0-15 come from the GPIO
// ports; the others are driven through OnGPIO by the peripherals connected to them.
//
// TrustZone and privilege (RM0456 section 23.5, Table 191): with trustZoneEnabled, the bits and
// EXTI_EXTICRm fields of the lines set in EXTI_SECCFGR1 read as 0 and ignore writes for
// non-secure accesses, without an event; EXTI_PRIVCFGR1 does the same for unprivileged
// accesses. EXTI_SECCFGR1 is written by secure accesses only (a non-secure write is reported to
// the TZIC), EXTI_PRIVCFGR1 by privileged ones only; EXTI_LOCKR is secure (a non-secure access
// reads 0 and is reported) and LOCK freezes both configuration registers until reset. With
// TZEN = 0 EXTI_SECCFGR1 and EXTI_LOCKR are RAZ/WI.
//
// Limitations: CPU events (EXTI_EMR1) and wake-up from low-power modes are not modelled.
//

using System.Collections.Generic;
using System.Collections.ObjectModel;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Miscellaneous;

namespace Antmicro.Renode.Peripherals.IRQControllers
{
    public class STM32U5_EXTI : BasicDoubleWordPeripheral, IKnownSize, ILocalGPIOReceiver, INumberedGPIOOutput, IGPIOReceiver
    {
        public STM32U5_EXTI(IMachine machine, int numberOfLines = 24, bool trustZoneEnabled = false,
            STM32U5_GTZC_TZIC illegalAccessController = null) : base(machine)
        {
            sysbus = machine.GetSystemBus(this);
            this.trustZoneEnabled = trustZoneEnabled;
            this.illegalAccessController = illegalAccessController;
            if(numberOfLines < GpioPins || numberOfLines > 32)
            {
                throw new ConstructionException($"Number of lines must be between {GpioPins} and 32, got {numberOfLines}");
            }
            this.numberOfLines = numberOfLines;
            var connections = new Dictionary<int, IGPIO>();
            for(var line = 0; line < numberOfLines; line++)
            {
                connections[line] = new GPIO();
            }
            Connections = new ReadOnlyDictionary<int, IGPIO>(connections);
            lineLevel = new bool[numberOfLines];
            receivers = new Dictionary<int, PortReceiver>();
            DefineRegisters();
            Reset();
        }

        public override void Reset()
        {
            base.Reset();
            for(var line = 0; line < numberOfLines; line++)
            {
                lineLevel[line] = false;
                Connections[line].Unset();
            }
            // Input levels belong to the connected ports; take them over for the reset selection.
            for(var pin = 0; pin < GpioPins; pin++)
            {
                lineLevel[pin] = SelectedPortLevel(pin);
            }
        }

        public override uint ReadDoubleWord(long offset)
        {
            var denied = DeniedBits(offset, false);
            return denied == AllBits ? 0 : base.ReadDoubleWord(offset) & ~denied;
        }

        public override void WriteDoubleWord(long offset, uint value)
        {
            var denied = DeniedBits(offset, true);
            if(denied == AllBits)
            {
                return;
            }
            if(denied != 0)
            {
                // The pending registers clear on written ones and EXTI_SWIER1 triggers on them;
                // the other registers keep the protected bits.
                var actOnOnes = offset == (long)Registers.RPR1 || offset == (long)Registers.FPR1 || offset == (long)Registers.SWIER1;
                value = (value & ~denied) | (actOnOnes ? 0 : base.ReadDoubleWord(offset) & denied);
            }
            base.WriteDoubleWord(offset, value);
        }

        public long Size => 0x400;

        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        // Local receiver per GPIO port; the index is the EXTI_EXTICRm port code (0 = PA ... 8 = PI).
        public IGPIOReceiver GetLocalReceiver(int index)
        {
            if(!receivers.TryGetValue(index, out var receiver))
            {
                receiver = new PortReceiver(this, index);
                receivers.Add(index, receiver);
            }
            return receiver;
        }

        // Direct connection of the non-GPIO lines (16 and above).
        public void OnGPIO(int number, bool value)
        {
            if(number < GpioPins || number >= numberOfLines)
            {
                this.Log(LogLevel.Warning, "Line {0} cannot be driven directly; lines {1}-{2} are available", number, GpioPins, numberOfLines - 1);
                return;
            }
            SetLineLevel(number, value);
        }

        private void DefineRegisters()
        {
            // RM0456 sections 23.6.1-23.6.5, reset 0x0000 0000.
            Registers.RTSR1.Define(this, name: "EXTI_RTSR1")
                .WithValueField(0, numberOfLines, out risingTrigger, name: "RT");
            Registers.FTSR1.Define(this, name: "EXTI_FTSR1")
                .WithValueField(0, numberOfLines, out fallingTrigger, name: "FT");
            Registers.SWIER1.Define(this, name: "EXTI_SWIER1")
                .WithValueField(0, numberOfLines, FieldMode.Read | FieldMode.Write, valueProviderCallback: _ => 0,
                    writeCallback: (_, value) =>
                    {
                        // A software event is a rising edge whatever the edge selection.
                        risingPending.Value |= value;
                        UpdateOutputs();
                    }, name: "SWI");
            Registers.RPR1.Define(this, name: "EXTI_RPR1")
                .WithValueField(0, numberOfLines, out risingPending, FieldMode.Read | FieldMode.WriteOneToClear, name: "RPIF")
                .WithWriteCallback((_, __) => UpdateOutputs());
            Registers.FPR1.Define(this, name: "EXTI_FPR1")
                .WithValueField(0, numberOfLines, out fallingPending, FieldMode.Read | FieldMode.WriteOneToClear, name: "FPIF")
                .WithWriteCallback((_, __) => UpdateOutputs());

            // RM0456 sections 23.6.6 and 23.6.7.
            Registers.SECCFGR1.Define(this, name: "EXTI_SECCFGR1")
                .WithValueField(0, numberOfLines, out secureLines, name: "SEC");
            Registers.PRIVCFGR1.Define(this, name: "EXTI_PRIVCFGR1")
                .WithValueField(0, numberOfLines, out privilegedLines, name: "PRIV");

            // RM0456 section 23.6.8: four 8-bit fields per register, port code 0x00 = PA ...
            for(var index = 0; index < InterruptSelectionRegisters; index++)
            {
                var register = (Registers.EXTICR1 + index * 4).Define(this, name: $"EXTI_EXTICR{index + 1}");
                for(var field = 0; field < PinsPerSelectionRegister; field++)
                {
                    var pin = index * PinsPerSelectionRegister + field;
                    portSelection[pin] = register.DefineValueField(field * 8, 8, name: $"EXTI{pin}",
                        changeCallback: (_, __) =>
                        {
                            // Changing the source is not an edge: take over the new source level.
                            lineLevel[pin] = SelectedPortLevel(pin);
                        });
                }
            }

            // RM0456 sections 23.6.9-23.6.11.
            Registers.LOCKR.Define(this, name: "EXTI_LOCKR")
                .WithFlag(0, out configurationLocked, FieldMode.Read | FieldMode.Set, name: "LOCK")
                .WithReservedBits(1, 31);
            Registers.IMR1.Define(this, name: "EXTI_IMR1")
                .WithValueField(0, numberOfLines, out interruptMask, name: "IM")
                .WithChangeCallback((_, __) => UpdateOutputs());
            Registers.EMR1.Define(this, name: "EXTI_EMR1")
                .WithValueField(0, numberOfLines, name: "EM");
        }

        // RM0456 section 23.5: the bits of the register at offset denied to the current access.
        private uint DeniedBits(long offset, bool write)
        {
            STM32U5_TrustZoneContext.TryGetTransaction(sysbus, out var secure, out var privileged);
            secure &= trustZoneEnabled;
            var secureMask = trustZoneEnabled ? (uint)secureLines.Value : 0;
            var privilegedMask = (uint)privilegedLines.Value;
            switch((Registers)offset)
            {
            case Registers.SECCFGR1:
                if(!trustZoneEnabled)
                {
                    return AllBits;
                }
                if(!write)
                {
                    return 0;
                }
                if(!secure)
                {
                    ReportIllegalAccess(offset, write);
                    return AllBits;
                }
                // SECx of a privileged line is written by privileged accesses only.
                return configurationLocked.Value ? AllBits : privileged ? 0 : privilegedMask;
            case Registers.PRIVCFGR1:
                if(!write)
                {
                    return 0;
                }
                if(!privileged || configurationLocked.Value)
                {
                    return AllBits;
                }
                // PRIVx of a secure line is written by secure accesses only.
                return secure ? 0 : secureMask;
            case Registers.LOCKR:
                if(!trustZoneEnabled)
                {
                    return AllBits;
                }
                if(!secure)
                {
                    ReportIllegalAccess(offset, write);
                    return AllBits;
                }
                return 0;
            }
            var lineBits = LineBits(offset, secure ? 0 : secureMask);
            lineBits |= privileged ? 0 : LineBits(offset, privilegedMask);
            return lineBits;
        }

        // The bits of the register at offset that belong to the lines in mask.
        private static uint LineBits(long offset, uint lines)
        {
            if(lines == 0)
            {
                return 0;
            }
            if(offset >= (long)Registers.EXTICR1 && offset <= (long)Registers.EXTICR4)
            {
                // Four 8-bit fields, for lines 4m to 4m + 3.
                var firstLine = (int)(offset - (long)Registers.EXTICR1) / 4 * PinsPerSelectionRegister;
                var bits = 0u;
                for(var field = 0; field < PinsPerSelectionRegister; field++)
                {
                    bits |= (lines & (1u << (firstLine + field))) != 0 ? 0xFFu << (8 * field) : 0;
                }
                return bits;
            }
            switch((Registers)offset)
            {
            case Registers.RTSR1:
            case Registers.FTSR1:
            case Registers.SWIER1:
            case Registers.RPR1:
            case Registers.FPR1:
            case Registers.IMR1:
            case Registers.EMR1:
                return lines;
            default:
                return 0;
            }
        }

        private void ReportIllegalAccess(long offset, bool write)
        {
            this.Log(LogLevel.Debug, "Non-secure {0} of {1} denied", write ? "write" : "read", (Registers)offset);
            illegalAccessController?.ReportIllegalAccess(IllegalAccessRegister, IllegalAccessBit);
        }

        private bool SelectedPortLevel(int pin)
        {
            var port = (int)portSelection[pin].Value;
            return receivers.TryGetValue(port, out var receiver) && receiver.Level(pin);
        }

        private void OnPortInput(int port, int pin, bool value)
        {
            if((int)portSelection[pin].Value == port)
            {
                SetLineLevel(pin, value);
            }
        }

        private void SetLineLevel(int line, bool value)
        {
            var previous = lineLevel[line];
            lineLevel[line] = value;
            var bit = 1UL << line;
            if(!previous && value && (risingTrigger.Value & bit) != 0)
            {
                risingPending.Value |= bit;
            }
            if(previous && !value && (fallingTrigger.Value & bit) != 0)
            {
                fallingPending.Value |= bit;
            }
            UpdateOutputs();
        }

        private void UpdateOutputs()
        {
            var active = (risingPending.Value | fallingPending.Value) & interruptMask.Value;
            for(var line = 0; line < numberOfLines; line++)
            {
                Connections[line].Set((active & (1UL << line)) != 0);
            }
        }

        private readonly IBusController sysbus;
        private readonly bool trustZoneEnabled;
        private readonly STM32U5_GTZC_TZIC illegalAccessController;
        private IValueRegisterField secureLines;
        private IValueRegisterField privilegedLines;
        private IFlagRegisterField configurationLocked;

        private const uint AllBits = 0xFFFFFFFF;
        // GTZC2_TZIC_IER2.EXTIIE (stm32u575xx.h GTZC_CFGR2_EXTI_Pos).
        private const int IllegalAccessRegister = 2;
        private const int IllegalAccessBit = 6;

        private IValueRegisterField risingTrigger;
        private IValueRegisterField fallingTrigger;
        private IValueRegisterField risingPending;
        private IValueRegisterField fallingPending;
        private IValueRegisterField interruptMask;
        private readonly IValueRegisterField[] portSelection = new IValueRegisterField[GpioPins];
        private readonly bool[] lineLevel;
        private readonly Dictionary<int, PortReceiver> receivers;
        private readonly int numberOfLines;

        private const int GpioPins = 16;
        private const int InterruptSelectionRegisters = 4;
        private const int PinsPerSelectionRegister = GpioPins / InterruptSelectionRegisters;

        private class PortReceiver : IGPIOReceiver
        {
            public PortReceiver(STM32U5_EXTI parent, int port)
            {
                this.parent = parent;
                this.port = port;
                state = new bool[GpioPins];
            }

            public void OnGPIO(int number, bool value)
            {
                if(number < 0 || number >= GpioPins)
                {
                    parent.Log(LogLevel.Error, "GPIO port {0}: pin {1} out of range", port, number);
                    return;
                }
                state[number] = value;
                parent.OnPortInput(port, number, value);
            }

            public bool Level(int pin) => state[pin];

            public void Reset()
            {
                // The input levels are owned by the GPIO port driving this receiver.
            }

            private readonly STM32U5_EXTI parent;
            private readonly int port;
            private readonly bool[] state;
        }

        private enum Registers
        {
            RTSR1 = 0x00, // EXTI_RTSR1
            FTSR1 = 0x04, // EXTI_FTSR1
            SWIER1 = 0x08, // EXTI_SWIER1
            RPR1 = 0x0C, // EXTI_RPR1
            FPR1 = 0x10, // EXTI_FPR1
            SECCFGR1 = 0x14, // EXTI_SECCFGR1
            PRIVCFGR1 = 0x18, // EXTI_PRIVCFGR1
            EXTICR1 = 0x60, // EXTI_EXTICR1
            EXTICR2 = 0x64, // EXTI_EXTICR2
            EXTICR3 = 0x68, // EXTI_EXTICR3
            EXTICR4 = 0x6C, // EXTI_EXTICR4
            LOCKR = 0x70, // EXTI_LOCKR
            IMR1 = 0x80, // EXTI_IMR1
            EMR1 = 0x84, // EXTI_EMR1
        }
    }
}
