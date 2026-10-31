//
// Copyright (c) 2026 Fedor Druzhinin
//
// This file is licensed under the MIT License.
//
// STM32U5 tamper and backup registers (TAMP), RM0456 Rev 7 section 64.
// Register layout: cmsis-device-u5 v1.4.3 stm32u575xx.h (TAMP_TypeDef).
//
// The backup registers TAMP_BKP0R-BKP31R and the protection configuration are modelled. As the
// backup registers of STM32F4_RTC the backup registers have no logic and keep their value over
// a system reset (they are reset by a backup domain reset only, RM0456 section 64.4.10).
// Tamper detection, monotonic counter and the other registers are tags. Backup domain reset
// (RCC_BDCR.BDRST) and the erase of the backup registers on a tamper event are not modelled.
//
// Protection (sections 64.4.5-64.4.7, 64.7.9, 64.7.10). TAMP_SECCFGR and TAMP_PRIVCFGR are not
// affected by a system reset. A denied access reads as 0 and its write is ignored:
// - Backup registers: zone 1 (BKP0R up to BKPRWSEC - 1) is read and written by secure accesses
//   only, zone 2 (BKPRWSEC up to BKPWSEC - 1) is written by secure accesses only, zone 3 is
//   open. BKPRWPRIV and BKPWPRIV add the same rules for unprivileged accesses. BHKLOCK denies
//   BKP0R-BKP7R to every access.
// - TAMPSEC: the tamper registers are written by secure accesses only, and read by secure
//   accesses only except TAMP_SECCFGR, TAMP_PRIVCFGR and TAMP_MISR. TAMPPRIV: the same for
//   unprivileged accesses, reads allowed for TAMP_SECCFGR and TAMP_PRIVCFGR only. CNT1SEC
//   (CNT1PRIV) protects TAMP_COUNT1R.
// - TAMP_SECCFGR is written by secure accesses only, TAMP_PRIVCFGR by privileged accesses only;
//   BKPRWPRIV and BKPWPRIV by secure accesses only. The privilege rules of BKPRWSEC/BKPWSEC
//   follow the register description (64.7.9: BKPWPRIV protects BKPWSEC, either bit protects
//   BKPRWSEC); section 64.4.7 states BKPRWPRIV protects BKPWSEC.
// A denied non-secure access is reported to the TZIC (section 64.4.5), also a write to
// TAMP_PRIVCFGR that only touches its secure-only bits; not checked on hardware. With trustZoneEnabled = false (TZEN = 0)
// every access is non-secure: the security rules do not apply, and TAMPSEC and CNT1SEC cannot
// be set.
//

using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class STM32U5_TAMP : IDoubleWordPeripheral, IKnownSize, ISTM32U5_TrustZoneAware
    {
        public STM32U5_TAMP(IMachine machine, bool trustZoneEnabled = false, STM32U5_GTZC_TZIC illegalAccessController = null)
        {
            sysbus = machine.GetSystemBus(this);
            this.trustZoneEnabled = trustZoneEnabled;
            this.illegalAccessController = illegalAccessController;

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
                {(long)Registers.SecureConfiguration, new DoubleWordRegister(this, softResettable: false)
                    .WithValueField(0, 8, out backupReadWriteSecureStart, name: "BKPRWSEC")
                    .WithReservedBits(8, 7)
                    .WithFlag(15, name: "CNT1SEC")
                    .WithValueField(16, 8, out backupWriteSecureStart, name: "BKPWSEC")
                    .WithReservedBits(24, 6)
                    .WithFlag(30, out bootHardwareKeyLock, FieldMode.Read | FieldMode.Set, name: "BHKLOCK")
                    .WithFlag(31, name: "TAMPSEC")
                },
                {(long)Registers.PrivilegeConfiguration, new DoubleWordRegister(this, softResettable: false)
                    .WithReservedBits(0, 15)
                    .WithFlag(15, name: "CNT1PRIV")
                    .WithReservedBits(16, 13)
                    .WithFlag(29, name: "BKPRWPRIV")
                    .WithFlag(30, name: "BKPWPRIV")
                    .WithFlag(31, name: "TAMPPRIV")
                },
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
            var denied = DeniedBits(offset, false);
            return denied == AllBits ? 0 : registers.Read(offset) & ~denied;
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            var denied = DeniedBits(offset, true);
            if(denied == AllBits)
            {
                return;
            }
            if(denied != 0)
            {
                // The configuration registers keep their protected bits.
                value = (value & ~denied) | (registers.Read(offset) & denied);
            }
            registers.Write(offset, value);
        }

        public void Reset()
        {
            registers.Reset();
        }

        public long Size => 0x400;

        // RM0456 section 64.4.5: the TAMP clock and reset bits of the RCC are secure as soon as one
        // TAMP function is secure: the tamper configuration, the monotonic counter or a backup
        // register zone 1 or 2.
        public bool HasSecureFunction
        {
            get
            {
                if(!trustZoneEnabled)
                {
                    return false;
                }
                var security = registers.Read((long)Registers.SecureConfiguration);
                return (security & (TamperProtection | CounterProtection)) != 0
                    || backupReadWriteSecureStart.Value > 0 || backupWriteSecureStart.Value > backupReadWriteSecureStart.Value;
            }
        }

        // The bits of the register at offset denied to the current access.
        private uint DeniedBits(long offset, bool write)
        {
            STM32U5_TrustZoneContext.TryGetTransaction(sysbus, out var secure, out var privileged);
            var security = registers.Read((long)Registers.SecureConfiguration);
            var privilege = registers.Read((long)Registers.PrivilegeConfiguration);
            var denied = 0u;
            if(IsBackupRegister(offset) && bootHardwareKeyLock.Value && BackupIndex(offset) < BootHardwareKeyRegisters)
            {
                denied = AllBits;
            }
            if(!trustZoneEnabled)
            {
                if(write && offset == (long)Registers.SecureConfiguration)
                {
                    denied |= TamperProtection | CounterProtection;
                }
            }
            else if(!secure)
            {
                var nonSecureDenied = SecureProtectedBits(offset, write, security);
                if(nonSecureDenied != 0)
                {
                    this.Log(LogLevel.Debug, "Non-secure {0} at 0x{1:X} denied", write ? "write" : "read", offset);
                    illegalAccessController?.ReportIllegalAccess(IllegalAccessRegister, IllegalAccessBit);
                }
                denied |= nonSecureDenied;
            }
            if(!privileged)
            {
                denied |= PrivilegeProtectedBits(offset, write, privilege);
            }
            return denied;
        }

        private uint SecureProtectedBits(long offset, bool write, uint security)
        {
            switch((Registers)offset)
            {
            case Registers.SecureConfiguration:
                return write ? AllBits : 0;
            case Registers.PrivilegeConfiguration:
                if(!write)
                {
                    return 0;
                }
                if((security & TamperProtection) != 0)
                {
                    return AllBits;
                }
                return BackupReadWritePrivilege | BackupWritePrivilege | ((security & CounterProtection) != 0 ? CounterProtection : 0);
            case Registers.MonotonicCounter1:
                return (security & CounterProtection) != 0 ? AllBits : 0;
            case Registers.MaskedInterruptStatus:
                return write && (security & TamperProtection) != 0 ? AllBits : 0;
            }
            if(IsBackupRegister(offset))
            {
                var zone = BackupZone(BackupIndex(offset), backupReadWriteSecureStart.Value, backupWriteSecureStart.Value);
                return zone == 1 || (zone == 2 && write) ? AllBits : 0;
            }
            return (security & TamperProtection) != 0 ? AllBits : 0;
        }

        private uint PrivilegeProtectedBits(long offset, bool write, uint privilege)
        {
            switch((Registers)offset)
            {
            case Registers.PrivilegeConfiguration:
                return write ? AllBits : 0;
            case Registers.SecureConfiguration:
                if(!write)
                {
                    return 0;
                }
                if((privilege & TamperProtection) != 0)
                {
                    return AllBits;
                }
                var bits = 0u;
                bits |= (privilege & CounterProtection) != 0 ? CounterProtection : 0;
                // BKPRWSEC with BKPRWPRIV or BKPWPRIV, BKPWSEC with BKPWPRIV, BHKLOCK with
                // BKPRWPRIV (section 64.7.9).
                bits |= (privilege & (BackupReadWritePrivilege | BackupWritePrivilege)) != 0 ? 0xFFu : 0;
                bits |= (privilege & BackupWritePrivilege) != 0 ? 0xFF0000u : 0;
                bits |= (privilege & BackupReadWritePrivilege) != 0 ? 1u << 30 : 0;
                return bits;
            case Registers.MonotonicCounter1:
                return (privilege & CounterProtection) != 0 ? AllBits : 0;
            }
            if(IsBackupRegister(offset))
            {
                var zone = BackupZone(BackupIndex(offset), backupReadWriteSecureStart.Value, backupWriteSecureStart.Value);
                var readWriteProtected = zone == 1 && (privilege & BackupReadWritePrivilege) != 0;
                var writeProtected = zone == 2 && write && (privilege & BackupWritePrivilege) != 0;
                return readWriteProtected || writeProtected ? AllBits : 0;
            }
            return (privilege & TamperProtection) != 0 ? AllBits : 0;
        }

        // RM0456 Figure 775: zone 1 below BKPRWSEC, zone 2 from BKPRWSEC below BKPWSEC, zone 3.
        private static int BackupZone(long index, ulong readWriteStart, ulong writeStart)
        {
            if(index < (long)readWriteStart)
            {
                return 1;
            }
            return index < (long)writeStart ? 2 : 3;
        }

        private static bool IsBackupRegister(long offset)
        {
            return offset >= (long)Registers.BackupStart && offset <= (long)Registers.BackupEnd;
        }

        private static long BackupIndex(long offset)
        {
            return (offset - (long)Registers.BackupStart) / 4;
        }

        private readonly IBusController sysbus;
        private readonly bool trustZoneEnabled;
        private readonly STM32U5_GTZC_TZIC illegalAccessController;
        private readonly DoubleWordRegisterCollection registers;
        private readonly IValueRegisterField backupReadWriteSecureStart;
        private readonly IValueRegisterField backupWriteSecureStart;
        private readonly IFlagRegisterField bootHardwareKeyLock;

        private const uint AllBits = 0xFFFFFFFF;
        // TAMP_SECCFGR TAMPSEC / TAMP_PRIVCFGR TAMPPRIV, CNT1SEC / CNT1PRIV.
        private const uint TamperProtection = 1u << 31;
        private const uint CounterProtection = 1u << 15;
        // TAMP_PRIVCFGR.
        private const uint BackupReadWritePrivilege = 1u << 29;
        private const uint BackupWritePrivilege = 1u << 30;
        private const int BootHardwareKeyRegisters = 8;
        // GTZC2_TZIC_IER2.TAMPIE (RM0456 section 5.8.11).
        private const int IllegalAccessRegister = 2;
        private const int IllegalAccessBit = 2;

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
