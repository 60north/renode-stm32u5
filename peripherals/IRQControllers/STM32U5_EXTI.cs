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
// Limitations: CPU events (EXTI_EMR1), security and privilege filtering (SECCFGR1, PRIVCFGR1,
// LOCKR) and wake-up from low-power modes are not modelled; those registers are storage.
//

using System.Collections.Generic;
using System.Collections.ObjectModel;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.IRQControllers
{
    public class STM32U5_EXTI : BasicDoubleWordPeripheral, IKnownSize, ILocalGPIOReceiver, INumberedGPIOOutput, IGPIOReceiver
    {
        public STM32U5_EXTI(IMachine machine, int numberOfLines = 24) : base(machine)
        {
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
                .WithValueField(0, numberOfLines, name: "SEC");
            Registers.PRIVCFGR1.Define(this, name: "EXTI_PRIVCFGR1")
                .WithValueField(0, numberOfLines, name: "PRIV");

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
                .WithFlag(0, name: "LOCK")
                .WithReservedBits(1, 31);
            Registers.IMR1.Define(this, name: "EXTI_IMR1")
                .WithValueField(0, numberOfLines, out interruptMask, name: "IM")
                .WithChangeCallback((_, __) => UpdateOutputs());
            Registers.EMR1.Define(this, name: "EXTI_EMR1")
                .WithValueField(0, numberOfLines, name: "EM");
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
