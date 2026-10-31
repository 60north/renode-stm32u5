//
// Copyright (c) 2026 Fedor Druzhinin
//
// This file is licensed under the MIT License.
//
// STM32U5 low-power general-purpose I/Os (LPGPIO1), RM0456 Rev 7 section 14: 16 I/Os, each an
// input or an output (LPGPIO_MODER, one bit per I/O), LPGPIO_IDR, LPGPIO_ODR, LPGPIO_BSRR
// (set wins over reset) and LPGPIO_BRR.
//
// The I/Os are the outputs 0-15 (driven by LPGPIO_ODR when the I/O is an output) and the inputs
// 0-15 (sampled into LPGPIO_IDR). On the chip LPGPIO1_Py reaches a pin through the GPIO
// alternate function selected in GPIOx_MODER/AFR; the board description connects them. The
// security inherited from GPIOx_SECCFGR is not modelled.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.GPIOPort
{
    public class STM32U5_LPGPIO : BaseGPIOPort, IDoubleWordPeripheral, IProvidesRegisterCollection<DoubleWordRegisterCollection>, IKnownSize
    {
        public STM32U5_LPGPIO(IMachine machine) : base(machine, NumberOfPins)
        {
            input = new bool[NumberOfPins];
            RegistersCollection = new DoubleWordRegisterCollection(this);
            DefineRegisters();
        }

        public override void Reset()
        {
            base.Reset();
            RegistersCollection.Reset();
            for(var i = 0; i < NumberOfPins; i++)
            {
                input[i] = false;
            }
            UpdateOutputs();
        }

        public override void OnGPIO(int number, bool value)
        {
            if(number < 0 || number >= NumberOfPins)
            {
                this.Log(LogLevel.Warning, "Input {0} out of range", number);
                return;
            }
            input[number] = value;
        }

        public uint ReadDoubleWord(long offset)
        {
            return RegistersCollection.Read(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            RegistersCollection.Write(offset, value);
        }

        public long Size => 0x400;

        public DoubleWordRegisterCollection RegistersCollection { get; }

        private void DefineRegisters()
        {
            Registers.Mode.Define(this)
                .WithValueField(0, 16, out mode, name: "MODE", changeCallback: (_, __) => UpdateOutputs())
                .WithReservedBits(16, 16);

            // An output reads back its driven level.
            Registers.InputData.Define(this)
                .WithValueField(0, 16, FieldMode.Read, name: "ID", valueProviderCallback: _ =>
                {
                    var result = 0u;
                    for(var i = 0; i < NumberOfPins; i++)
                    {
                        var isOutput = (mode.Value & (1u << i)) != 0;
                        if(isOutput ? (output.Value & (1u << i)) != 0 : input[i])
                        {
                            result |= 1u << i;
                        }
                    }
                    return result;
                })
                .WithReservedBits(16, 16);

            Registers.OutputData.Define(this)
                .WithValueField(0, 16, out output, name: "OD", changeCallback: (_, __) => UpdateOutputs())
                .WithReservedBits(16, 16);

            Registers.BitSetReset.Define(this)
                .WithValueField(0, 32, FieldMode.Write, name: "BS/BR", writeCallback: (_, value) =>
                {
                    var set = (uint)value & 0xFFFF;
                    var reset = ((uint)value >> 16) & ~set;
                    output.Value = (output.Value | set) & ~reset;
                    UpdateOutputs();
                });

            Registers.BitReset.Define(this)
                .WithValueField(0, 16, FieldMode.Write, name: "BR", writeCallback: (_, value) =>
                {
                    output.Value &= ~value;
                    UpdateOutputs();
                })
                .WithReservedBits(16, 16);
        }

        private void UpdateOutputs()
        {
            for(var i = 0; i < NumberOfPins; i++)
            {
                var isOutput = (mode.Value & (1u << i)) != 0;
                var level = isOutput && (output.Value & (1u << i)) != 0;
                State[i] = level;
                Connections[i].Set(level);
            }
        }

        private IValueRegisterField mode;
        private IValueRegisterField output;

        private readonly bool[] input;

        private const int NumberOfPins = 16;

        private enum Registers
        {
            Mode = 0x00,
            InputData = 0x10,
            OutputData = 0x14,
            BitSetReset = 0x18,
            BitReset = 0x28,
        }
    }
}
