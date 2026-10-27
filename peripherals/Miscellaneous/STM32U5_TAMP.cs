//
// Copyright (c) 2026 Fedor Druzhinin
//
// This file is licensed under the MIT License.
//
// STM32U5 tamper and backup registers (TAMP), RM0456 Rev 7 section 64.
// Register layout: cmsis-device-u5 v1.4.3 stm32u575xx.h (TAMP_TypeDef).
//
// Only the backup registers TAMP_BKP0R-BKP31R are modelled. As the backup registers of
// STM32F4_RTC they have no logic and keep their value over a system reset (they are reset by a
// backup domain reset only, RM0456 section 64.4.10). Tamper detection, monotonic counter and
// the other registers are tags. Backup domain reset (RCC_BDCR.BDRST) and the erase of the
// backup registers on a tamper event are not modelled.
//

using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class STM32U5_TAMP : IDoubleWordPeripheral, IKnownSize
    {
        public STM32U5_TAMP(IMachine machine)
        {
            var registerMap = new Dictionary<long, DoubleWordRegister>
            {
                {(long)Registers.Control1, new DoubleWordRegister(this).WithTag("TAMP_CR1", 0, 32)},
                {(long)Registers.Control2, new DoubleWordRegister(this).WithTag("TAMP_CR2", 0, 32)},
                {(long)Registers.Control3, new DoubleWordRegister(this).WithTag("TAMP_CR3", 0, 32)},
                {(long)Registers.FilterControl, new DoubleWordRegister(this).WithTag("TAMP_FLTCR", 0, 32)},
                {(long)Registers.ActiveTamperControl1, new DoubleWordRegister(this, 0x00070000).WithTag("TAMP_ATCR1", 0, 32)},
                {(long)Registers.ActiveTamperSeed, new DoubleWordRegister(this).WithTag("TAMP_ATSEEDR", 0, 32)},
                {(long)Registers.ActiveTamperOutput, new DoubleWordRegister(this).WithTag("TAMP_ATOR", 0, 32)},
                {(long)Registers.ActiveTamperControl2, new DoubleWordRegister(this).WithTag("TAMP_ATCR2", 0, 32)},
                {(long)Registers.SecureConfiguration, new DoubleWordRegister(this).WithTag("TAMP_SECCFGR", 0, 32)},
                {(long)Registers.PrivilegeConfiguration, new DoubleWordRegister(this).WithTag("TAMP_PRIVCR", 0, 32)},
                {(long)Registers.InterruptEnable, new DoubleWordRegister(this).WithTag("TAMP_IER", 0, 32)},
                {(long)Registers.Status, new DoubleWordRegister(this).WithTag("TAMP_SR", 0, 32)},
                {(long)Registers.MaskedInterruptStatus, new DoubleWordRegister(this).WithTag("TAMP_MISR", 0, 32)},
                {(long)Registers.SecureMaskedInterruptStatus, new DoubleWordRegister(this).WithTag("TAMP_SMISR", 0, 32)},
                {(long)Registers.StatusClear, new DoubleWordRegister(this).WithTag("TAMP_SCR", 0, 32)},
                {(long)Registers.MonotonicCounter1, new DoubleWordRegister(this).WithTag("TAMP_COUNT1R", 0, 32)},
                {(long)Registers.ResetConfiguration, new DoubleWordRegister(this).WithTag("TAMP_ERCFGR", 0, 32)},
            };
            // These registers have no logic, they serve as scratchpad
            for(var reg = (long)Registers.BackupStart; reg <= (long)Registers.BackupEnd; reg += 4)
            {
                registerMap.Add(reg, new DoubleWordRegister(this, softResettable: false).WithValueField(0, 32));
            }
            registers = new DoubleWordRegisterCollection(this, registerMap);
        }

        public uint ReadDoubleWord(long offset)
        {
            return registers.Read(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            registers.Write(offset, value);
        }

        public void Reset()
        {
            registers.Reset();
        }

        public long Size => 0x400;

        private readonly DoubleWordRegisterCollection registers;

        private enum Registers
        {
            Control1 = 0x0,
            Control2 = 0x4,
            Control3 = 0x8,
            FilterControl = 0xc,
            ActiveTamperControl1 = 0x10,
            ActiveTamperSeed = 0x14,
            ActiveTamperOutput = 0x18,
            ActiveTamperControl2 = 0x1c,
            SecureConfiguration = 0x20,
            PrivilegeConfiguration = 0x24,
            InterruptEnable = 0x2c,
            Status = 0x30,
            MaskedInterruptStatus = 0x34,
            SecureMaskedInterruptStatus = 0x38,
            StatusClear = 0x3c,
            MonotonicCounter1 = 0x40,
            ResetConfiguration = 0x54,
            BackupStart = 0x100,
            BackupEnd = 0x17c
        }
    }
}
