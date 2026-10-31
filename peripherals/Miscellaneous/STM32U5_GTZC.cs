//
// Copyright (c) 2026 Fedor Druzhinin
//
// This file is licensed under the MIT License.
//
// STM32U5 global TrustZone controller (GTZC): TrustZone security controller (TZSC), TrustZone
// illegal access controller (TZIC), block-based memory protection controller (MPCBB) and the
// firewalls of the securable peripherals and of the SRAMs, RM0456 Rev 7 section 5. Bit
// positions and register masks: cmsis-device-u5 v1.4.3 stm32u575xx.h (GTZC_CFGRx_*_Pos for
// GTZC1 and GTZC2).
//
// New model. The firewalls are bus registrations placed around each securable peripheral
// (STM32U5_GTZCFirewallRegistration, asks the TZSC) and around each SRAM
// (STM32U5_MPCBBRegistration, asks the MPCBB for the 512-byte block) in the TrustZone platform
// description; they decide on every access whether the transaction may reach the model. The
// security of a CPU transaction is the security of the core and of the attribution of the
// accessed address (a secure core accessing a non-secure alias issues a non-secure transaction).
//
// Behaviour (RM0456 sections 5.4.2 to 5.8):
// - A non-secure access to a secure resource reads as zero, ignores writes and raises an
//   illegal access event in the TZIC (reads and writes, as section 3.5.4 describes for the
//   TrustZone-aware RTC). An unprivileged access to a privileged resource reads as zero and
//   ignores writes silently.
// - TZSC_SECCFGRx: read by any transaction; written by a secure transaction, a secure
//   privileged one for the bits of privileged peripherals. TZSC_PRIVCFGRx: read by any
//   transaction; written by a privileged transaction, a secure privileged one for the bits of
//   secure peripherals. TZSC_CR (LCK) and the TZIC: secure privileged only. LCK freezes
//   SECCFGRx and PRIVCFGRx until reset.
// - An illegal access event sets its TZIC_SRx flag only when enabled in TZIC_IERx ("register
//   masks filter unwanted events"); the interrupt is the OR of the flags. TZIC_FCRx clears.
// - MPCBB: a non-secure transaction to a secure block and a secure transaction to a non-secure
//   block (unless MPCBBz_CR.SRWILADIS) read as zero, ignore writes and raise the SRAM event; an
//   unprivileged transaction to a privileged block is silent. SECCFGRx and PRIVCFGRx are
//   0xFFFF FFFF after reset (TZEN = 1, section 5.4.6). MPCBBz_CR and CFGLOCKRx are secure
//   privileged; SECCFGRx is written by secure, PRIVCFGRx by privileged transactions, with the
//   per-block rules of sections 5.8.4 and 5.8.5; SPLCKy and GLOCK lock until reset.
//
// Limitations:
// - Accesses without a CPU context (the Renode Monitor, ELF loading, models that master the bus
//   without their own transaction state) pass the firewalls and are treated as secure
//   privileged by the configuration registers.
// - MPCWM1-5 (GTZC1 TZSC, RM0456 Table 31: OCTOSPI1, FSMC NOR bank, FSMC NAND bank, BKPSRAM,
//   OCTOSPI2): sub-regions A and B (B for MPCWM1, 2, 5) with SREN, SEC, PRIV and SRLOCK; an
//   address outside every enabled sub-region is secure privileged, an overlap is secure
//   (privileged) only when both sub-regions are (Tables 36 and 37). Illegal accesses as for the
//   MPCBB, events in TZIC1_SR4 (OCTOSPI1_MEM 16, FSMC_MEM 17, BKPSRAM 18, OCTOSPI2_MEM 19). The
//   registers are secure privileged. The external memories are taken as 256 MB regions for the
//   length saturation. A memory is put behind its watermark controller with
//   STM32U5_MPCWMRegistration.
// - Execute accesses are not distinguished from data accesses: no bus error on a fetch, and a
//   secure fetch from a non-secure block passes when SRWILADIS is set.
// - MPCBBz_CR.INVSECSTATE is stored only (it controls the security of the SRAM clocks in RCC).
//

using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.Miscellaneous;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class STM32U5_GTZC_TZIC : IDoubleWordPeripheral, IKnownSize
    {
        public STM32U5_GTZC_TZIC(IMachine machine, int instance)
        {
            sysbus = machine.GetSystemBus(this);
            masks = STM32U5_GTZC.EventMasks(instance);
            ownEvent = STM32U5_GTZC.TzicEvent(instance);
            interruptEnable = new uint[masks.Length];
            status = new uint[masks.Length];
            IRQ = new GPIO();
        }

        public void Reset()
        {
            Array.Clear(interruptEnable, 0, interruptEnable.Length);
            Array.Clear(status, 0, status.Length);
            IRQ.Unset();
        }

        public uint ReadDoubleWord(long offset)
        {
            if(!AccessAllowed())
            {
                return 0;
            }
            var index = (int)(offset & 0xF) / 4;
            if(index >= masks.Length)
            {
                return 0;
            }
            switch(offset & ~0xFL)
            {
            case InterruptEnableOffset:
                return interruptEnable[index];
            case StatusOffset:
                return status[index];
            default:
                // Flag clear registers are write-only.
                return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(!AccessAllowed())
            {
                return;
            }
            var index = (int)(offset & 0xF) / 4;
            if(index >= masks.Length)
            {
                return;
            }
            switch(offset & ~0xFL)
            {
            case InterruptEnableOffset:
                interruptEnable[index] = value & masks[index];
                break;
            case FlagClearOffset:
                status[index] &= ~(value & masks[index]);
                UpdateInterrupt();
                break;
            default:
                this.Log(LogLevel.Warning, "Write to the read-only TZIC_SR{0}", index + 1);
                break;
            }
        }

        // configurationRegister is 1-based, as the GTZC_CFGRx bit definitions.
        public void ReportIllegalAccess(int configurationRegister, int bit)
        {
            var index = configurationRegister - 1;
            var flag = 1u << bit;
            this.Log(LogLevel.Debug, "Illegal access event CFGR{0} bit {1}", configurationRegister, bit);
            if((interruptEnable[index] & flag) == 0)
            {
                return;
            }
            status[index] |= flag;
            UpdateInterrupt();
        }

        public GPIO IRQ { get; }

        public long Size => 0x400;

        // The TZIC is a secure privileged peripheral; a non-secure access is an illegal access.
        private bool AccessAllowed()
        {
            STM32U5_TrustZoneContext.TryGetTransaction(sysbus, out var secure, out var privileged);
            if(!secure)
            {
                ReportIllegalAccess(ownEvent.Item1, ownEvent.Item2);
                return false;
            }
            return privileged;
        }

        private void UpdateInterrupt()
        {
            var pending = false;
            for(var index = 0; index < status.Length; index++)
            {
                pending |= (status[index] & interruptEnable[index]) != 0;
            }
            IRQ.Set(pending);
        }

        private readonly IBusController sysbus;
        private readonly uint[] masks;
        private readonly Tuple<int, int> ownEvent;
        private readonly uint[] interruptEnable;
        private readonly uint[] status;

        private const long InterruptEnableOffset = 0x00;
        private const long StatusOffset = 0x10;
        private const long FlagClearOffset = 0x20;
    }

    public class STM32U5_GTZC_TZSC : IDoubleWordPeripheral, IKnownSize
    {
        public STM32U5_GTZC_TZSC(IMachine machine, STM32U5_GTZC_TZIC tzic, int instance)
        {
            sysbus = machine.GetSystemBus(this);
            this.tzic = tzic;
            var eventMasks = STM32U5_GTZC.EventMasks(instance);
            // SECCFGRx and PRIVCFGRx cover the securable peripherals: all CFGRx but the last one.
            masks = new uint[eventMasks.Length - 1];
            Array.Copy(eventMasks, masks, masks.Length);
            ownEvent = STM32U5_GTZC.TzscEvent(instance);
            secure = new uint[masks.Length];
            privileged = new uint[masks.Length];
            // The watermark controllers are in GTZC1.
            watermarks = instance == 1 ? new uint[NumberOfWatermarkControllers, 4] : new uint[0, 4];
        }

        public void Reset()
        {
            Array.Clear(secure, 0, secure.Length);
            Array.Clear(privileged, 0, privileged.Length);
            Array.Clear(watermarks, 0, watermarks.Length);
            locked = false;
        }

        public uint ReadDoubleWord(long offset)
        {
            STM32U5_TrustZoneContext.TryGetTransaction(sysbus, out var secureTransaction, out var privilegedTransaction);
            if(offset == ControlOffset)
            {
                return ControlAccessAllowed(secureTransaction, privilegedTransaction) && locked ? LockBit : 0u;
            }
            if(TryGetIndex(offset, SecureConfigurationOffset, out var index))
            {
                return secure[index];
            }
            if(TryGetIndex(offset, PrivilegeConfigurationOffset, out index))
            {
                return privileged[index];
            }
            if(TryGetWatermarkRegister(offset, out var controller, out var register))
            {
                return ControlAccessAllowed(secureTransaction, privilegedTransaction) ? watermarks[controller, register] : 0u;
            }
            this.Log(LogLevel.Warning, "Unhandled read at 0x{0:X}", offset);
            return 0;
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            STM32U5_TrustZoneContext.TryGetTransaction(sysbus, out var secureTransaction, out var privilegedTransaction);
            if(offset == ControlOffset)
            {
                if(ControlAccessAllowed(secureTransaction, privilegedTransaction) && (value & LockBit) != 0)
                {
                    locked = true;
                }
                return;
            }
            if(TryGetIndex(offset, SecureConfigurationOffset, out var index))
            {
                WriteSecureConfiguration(index, value, secureTransaction, privilegedTransaction);
                return;
            }
            if(TryGetIndex(offset, PrivilegeConfigurationOffset, out index))
            {
                WritePrivilegeConfiguration(index, value, secureTransaction, privilegedTransaction);
                return;
            }
            if(TryGetWatermarkRegister(offset, out var controller, out var register))
            {
                if(ControlAccessAllowed(secureTransaction, privilegedTransaction))
                {
                    WriteWatermarkRegister(controller, register, value);
                }
                return;
            }
            this.Log(LogLevel.Warning, "Unhandled write at 0x{0:X}, value 0x{1:X}", offset, value);
        }

        // Called by the watermark firewall on every access; controller is 1-based (MPCWMx) and
        // offset is the offset in the protected memory.
        public bool AllowsWatermarkAccess(int controller, long offset)
        {
            if(!STM32U5_TrustZoneContext.TryGetTransaction(sysbus, out var secureTransaction, out var privilegedTransaction))
            {
                return true;
            }
            var index = controller - 1;
            var covered = false;
            var secureArea = true;
            var privilegedArea = true;
            for(var subRegion = 0; subRegion < 2; subRegion++)
            {
                var configuration = watermarks[index, 2 * subRegion];
                var range = watermarks[index, 2 * subRegion + 1];
                var length = (range >> 16) & WatermarkLengthMask;
                if((configuration & SubRegionEnable) == 0 || length == 0)
                {
                    continue;
                }
                var start = (long)(range & WatermarkStartMask) * WatermarkGranularity[index];
                var end = Math.Min(start + (long)length * WatermarkGranularity[index], WatermarkRegionSize[index]);
                if(offset < start || offset >= end)
                {
                    continue;
                }
                covered = true;
                secureArea &= (configuration & SubRegionSecure) != 0;
                privilegedArea &= (configuration & SubRegionPrivileged) != 0;
            }
            if(!covered)
            {
                secureArea = true;
                privilegedArea = true;
            }
            if(secureArea != secureTransaction)
            {
                tzic.ReportIllegalAccess(WatermarkEventRegister, WatermarkEventBit[index]);
                return false;
            }
            return !privilegedArea || privilegedTransaction;
        }

        // Called by the firewall on every access to a securable peripheral; configurationRegister
        // is 1-based, as the GTZC_CFGRx bit definitions.
        public bool AllowsAccess(int configurationRegister, int bit)
        {
            if(!STM32U5_TrustZoneContext.TryGetTransaction(sysbus, out var secureTransaction, out var privilegedTransaction))
            {
                return true;
            }
            var index = configurationRegister - 1;
            var flag = 1u << bit;
            if((secure[index] & flag) != 0 && !secureTransaction)
            {
                tzic.ReportIllegalAccess(configurationRegister, bit);
                return false;
            }
            return (privileged[index] & flag) == 0 || privilegedTransaction;
        }

        // The SECCFGRx bit of a securable peripheral (configurationRegister 1-based).
        public bool IsSecure(int configurationRegister, int bit)
        {
            return (secure[configurationRegister - 1] & (1u << bit)) != 0;
        }

        public long Size => 0x400;

        private bool ControlAccessAllowed(bool secureTransaction, bool privilegedTransaction)
        {
            if(!secureTransaction)
            {
                tzic.ReportIllegalAccess(ownEvent.Item1, ownEvent.Item2);
                return false;
            }
            return privilegedTransaction;
        }

        private void WriteSecureConfiguration(int index, uint value, bool secureTransaction, bool privilegedTransaction)
        {
            if(!secureTransaction)
            {
                tzic.ReportIllegalAccess(ownEvent.Item1, ownEvent.Item2);
                return;
            }
            if(locked)
            {
                return;
            }
            // The SEC bit of a privileged peripheral needs a secure privileged transaction.
            var writable = privilegedTransaction ? masks[index] : masks[index] & ~privileged[index];
            secure[index] = (secure[index] & ~writable) | (value & writable);
        }

        private void WritePrivilegeConfiguration(int index, uint value, bool secureTransaction, bool privilegedTransaction)
        {
            if(!privilegedTransaction || locked)
            {
                return;
            }
            // The PRIV bit of a secure peripheral needs a secure privileged transaction; a
            // non-secure attempt to change one is an illegal access.
            var writable = secureTransaction ? masks[index] : masks[index] & ~secure[index];
            if(!secureTransaction && (value & secure[index] & masks[index]) != (privileged[index] & secure[index]))
            {
                tzic.ReportIllegalAccess(ownEvent.Item1, ownEvent.Item2);
            }
            privileged[index] = (privileged[index] & ~writable) | (value & writable);
        }

        private bool TryGetIndex(long offset, long first, out int index)
        {
            index = (int)(offset - first) / 4;
            return offset >= first && offset < first + 4 * masks.Length && (offset & 3) == 0;
        }

        // register: 0 MPCWMxACFGR, 1 MPCWMxAR, 2 MPCWMxBCFGR, 3 MPCWMxBR.
        private bool TryGetWatermarkRegister(long offset, out int controller, out int register)
        {
            controller = (int)(offset - WatermarkOffset) / 0x10;
            register = (int)(offset & 0xF) / 4;
            if(offset < WatermarkOffset || controller >= watermarks.GetLength(0) || (offset & 3) != 0)
            {
                return false;
            }
            return register < 2 || WatermarkHasSubRegionB[controller];
        }

        // SRLOCK freezes the configuration and the range of its sub-region until reset.
        private void WriteWatermarkRegister(int controller, int register, uint value)
        {
            var subRegion = register / 2;
            if((watermarks[controller, 2 * subRegion] & SubRegionLock) != 0)
            {
                return;
            }
            if(register % 2 == 0)
            {
                watermarks[controller, register] = value & (SubRegionPrivileged | SubRegionSecure | SubRegionLock | SubRegionEnable);
            }
            else
            {
                watermarks[controller, register] = value & ((WatermarkLengthMask << 16) | WatermarkStartMask);
            }
        }

        private readonly IBusController sysbus;
        private readonly STM32U5_GTZC_TZIC tzic;
        private readonly uint[,] watermarks;
        private readonly uint[] masks;
        private readonly Tuple<int, int> ownEvent;
        private readonly uint[] secure;
        private readonly uint[] privileged;
        private bool locked;

        private const long ControlOffset = 0x00;
        private const long SecureConfigurationOffset = 0x10;
        private const long PrivilegeConfigurationOffset = 0x20;
        private const uint LockBit = 1u << 0;

        private const int NumberOfWatermarkControllers = 5;
        private const long WatermarkOffset = 0x40;
        private const int WatermarkEventRegister = 4;
        private const uint WatermarkLengthMask = 0xFFF;
        private const uint WatermarkStartMask = 0x7FF;
        private const uint SubRegionEnable = 1u << 0;
        private const uint SubRegionLock = 1u << 1;
        private const uint SubRegionSecure = 1u << 8;
        private const uint SubRegionPrivileged = 1u << 9;
        // MPCWM1-5: OCTOSPI1, FSMC NOR bank, FSMC NAND bank, BKPSRAM, OCTOSPI2 (RM0456 Table 31,
        // TZIC1_IER4 bits).
        private static readonly long[] WatermarkGranularity = { 0x20000, 0x20000, 0x20000, 32, 0x20000 };
        private static readonly long[] WatermarkRegionSize = { 0x10000000, 0x10000000, 0x10000000, 0x800, 0x10000000 };
        private static readonly int[] WatermarkEventBit = { 16, 17, 17, 18, 19 };
        private static readonly bool[] WatermarkHasSubRegionB = { true, true, false, false, true };
    }

    public class STM32U5_GTZC_MPCBB : IDoubleWordPeripheral, IKnownSize
    {
        // eventRegister, memoryEventBit and registerEventBit locate the SRAMz and MPCBBz_REG
        // illegal access events in the TZIC (GTZC_CFGRx bit positions).
        public STM32U5_GTZC_MPCBB(IMachine machine, STM32U5_GTZC_TZIC tzic, long memorySize, int eventRegister,
            int memoryEventBit, int registerEventBit)
        {
            if(memorySize <= 0 || memorySize % SuperBlockSize != 0 || memorySize / SuperBlockSize > MaxSuperBlocks)
            {
                throw new ArgumentException($"The memory size must be a multiple of {SuperBlockSize} bytes, up to {MaxSuperBlocks} super-blocks");
            }
            sysbus = machine.GetSystemBus(this);
            this.tzic = tzic;
            this.eventRegister = eventRegister;
            this.memoryEventBit = memoryEventBit;
            this.registerEventBit = registerEventBit;
            superBlocks = (int)(memorySize / SuperBlockSize);
            secure = new uint[superBlocks];
            privileged = new uint[superBlocks];
            Reset();
        }

        public void Reset()
        {
            for(var index = 0; index < superBlocks; index++)
            {
                secure[index] = uint.MaxValue;
                privileged[index] = uint.MaxValue;
            }
            superBlockLocks = 0;
            controlLocked = false;
            secureAccessToNonSecureAllowed = false;
            invertedClockSecurity = false;
        }

        public uint ReadDoubleWord(long offset)
        {
            STM32U5_TrustZoneContext.TryGetTransaction(sysbus, out var secureTransaction, out var privilegedTransaction);
            if(TryGetIndex(offset, SecureConfigurationOffset, out var index))
            {
                return secure[index];
            }
            if(TryGetIndex(offset, PrivilegeConfigurationOffset, out index))
            {
                return privileged[index];
            }
            if(!SecurePrivilegedRegisterAccess(secureTransaction, privilegedTransaction))
            {
                return 0;
            }
            switch(offset)
            {
            case ControlOffset:
                return (secureAccessToNonSecureAllowed ? SecureReadWriteIllegalAccessDisable : 0u)
                    | (invertedClockSecurity ? InvertSecureState : 0u) | (controlLocked ? GlobalLock : 0u);
            case LockOffset1:
                return (uint)superBlockLocks;
            case LockOffset2:
                return (uint)(superBlockLocks >> 32);
            default:
                this.Log(LogLevel.Warning, "Unhandled read at 0x{0:X}", offset);
                return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            STM32U5_TrustZoneContext.TryGetTransaction(sysbus, out var secureTransaction, out var privilegedTransaction);
            if(TryGetIndex(offset, SecureConfigurationOffset, out var index))
            {
                WriteSecureConfiguration(index, value, secureTransaction, privilegedTransaction);
                return;
            }
            if(TryGetIndex(offset, PrivilegeConfigurationOffset, out index))
            {
                WritePrivilegeConfiguration(index, value, secureTransaction, privilegedTransaction);
                return;
            }
            if(!SecurePrivilegedRegisterAccess(secureTransaction, privilegedTransaction))
            {
                return;
            }
            switch(offset)
            {
            case ControlOffset:
                if(!controlLocked)
                {
                    secureAccessToNonSecureAllowed = (value & SecureReadWriteIllegalAccessDisable) != 0;
                    invertedClockSecurity = (value & InvertSecureState) != 0;
                    controlLocked = (value & GlobalLock) != 0;
                }
                break;
            case LockOffset1:
                superBlockLocks |= value & SuperBlockMask(0);
                break;
            case LockOffset2:
                superBlockLocks |= ((ulong)value << 32) & SuperBlockMask(32);
                break;
            default:
                this.Log(LogLevel.Warning, "Unhandled write at 0x{0:X}, value 0x{1:X}", offset, value);
                break;
            }
        }

        // Called by the SRAM firewall on every access; offset is the offset in the memory.
        public bool AllowsAccess(long offset)
        {
            if(!STM32U5_TrustZoneContext.TryGetTransaction(sysbus, out var secureTransaction, out var privilegedTransaction))
            {
                return true;
            }
            var block = offset / BlockSize;
            var index = (int)(block / BlocksPerSuperBlock);
            var flag = 1u << (int)(block % BlocksPerSuperBlock);
            var secureBlock = (secure[index] & flag) != 0;
            if(secureBlock != secureTransaction && (secureBlock || !secureAccessToNonSecureAllowed))
            {
                tzic.ReportIllegalAccess(eventRegister, memoryEventBit);
                return false;
            }
            return (privileged[index] & flag) == 0 || privilegedTransaction;
        }

        public long Size => 0x400;

        private bool SecurePrivilegedRegisterAccess(bool secureTransaction, bool privilegedTransaction)
        {
            if(!secureTransaction)
            {
                tzic.ReportIllegalAccess(eventRegister, registerEventBit);
                return false;
            }
            return privilegedTransaction;
        }

        private void WriteSecureConfiguration(int index, uint value, bool secureTransaction, bool privilegedTransaction)
        {
            if(!secureTransaction)
            {
                tzic.ReportIllegalAccess(eventRegister, registerEventBit);
                return;
            }
            if(SuperBlockLocked(index))
            {
                return;
            }
            // An unprivileged write cannot change the SEC bit of a privileged block.
            var writable = privilegedTransaction ? uint.MaxValue : ~privileged[index];
            secure[index] = (secure[index] & ~writable) | (value & writable);
        }

        private void WritePrivilegeConfiguration(int index, uint value, bool secureTransaction, bool privilegedTransaction)
        {
            if(!privilegedTransaction || SuperBlockLocked(index))
            {
                return;
            }
            // A non-secure write cannot change the PRIV bit of a secure block.
            var writable = secureTransaction ? uint.MaxValue : ~secure[index];
            privileged[index] = (privileged[index] & ~writable) | (value & writable);
        }

        private bool SuperBlockLocked(int index) => (superBlockLocks & (1UL << index)) != 0;

        private ulong SuperBlockMask(int first)
        {
            var mask = 0UL;
            for(var index = first; index < superBlocks && index < first + 32; index++)
            {
                mask |= 1UL << index;
            }
            return mask;
        }

        private bool TryGetIndex(long offset, long first, out int index)
        {
            index = (int)(offset - first) / 4;
            return offset >= first && offset < first + 4 * superBlocks && (offset & 3) == 0;
        }

        private readonly IBusController sysbus;
        private readonly STM32U5_GTZC_TZIC tzic;
        private readonly int eventRegister;
        private readonly int memoryEventBit;
        private readonly int registerEventBit;
        private readonly int superBlocks;
        private readonly uint[] secure;
        private readonly uint[] privileged;
        private ulong superBlockLocks;
        private bool controlLocked;
        private bool secureAccessToNonSecureAllowed;
        private bool invertedClockSecurity;

        private const long BlockSize = 512;
        private const long BlocksPerSuperBlock = 32;
        private const long SuperBlockSize = BlockSize * BlocksPerSuperBlock;
        private const int MaxSuperBlocks = 52;
        private const long ControlOffset = 0x00;
        private const long LockOffset1 = 0x10;
        private const long LockOffset2 = 0x14;
        private const long SecureConfigurationOffset = 0x100;
        private const long PrivilegeConfigurationOffset = 0x200;
        private const uint SecureReadWriteIllegalAccessDisable = 1u << 31;
        private const uint InvertSecureState = 1u << 30;
        private const uint GlobalLock = 1u << 0;
    }

    public static class STM32U5_GTZC
    {
        // Implemented bits of GTZC_CFGR1..n, also the TZIC_IERx/SRx/FCRx masks; the last
        // register lists the TrustZone-aware peripherals and memories (events only).
        public static uint[] EventMasks(int instance)
        {
            switch(instance)
            {
            case 1:
                return new uint[] { 0x000FFFFF, 0x000001FF, 0x007F37FF, 0x3F0FC007 };
            case 2:
                return new uint[] { 0x00001BFF, 0x0300C07F };
            default:
                throw new ArgumentException($"GTZC{instance} does not exist, the instances are 1 and 2");
            }
        }

        // GTZC1: CFGR4 TZSC1 = 14, TZIC1 = 15; GTZC2: CFGR2 TZSC2 = 14, TZIC2 = 15.
        public static Tuple<int, int> TzscEvent(int instance) => Tuple.Create(instance == 1 ? 4 : 2, 14);

        public static Tuple<int, int> TzicEvent(int instance) => Tuple.Create(instance == 1 ? 4 : 2, 15);
    }
}

namespace Antmicro.Renode.Peripherals.Bus
{
    // A registration that lets an access reach the peripheral only when the GTZC allows it.
    // Every access width the peripheral implements is wrapped; the system bus builds the
    // translated widths on top. A blocked access reads as zero and ignores writes.
    public abstract class STM32U5_GTZCGateRegistration : BusParametrizedRegistration
    {
        protected STM32U5_GTZCGateRegistration(ulong address, ulong size, IPeripheral visibleTo)
            : base(address, size, visibleTo)
        {
        }

        public override Func<long, ulong> GetReadQuadWordMethod(IBusPeripheral peripheral)
        {
            return peripheral is IQuadWordPeripheral target ? offset => Allowed(offset) ? target.ReadQuadWord(offset) : 0UL : (Func<long, ulong>)null;
        }

        public override Action<long, ulong> GetWriteQuadWordMethod(IBusPeripheral peripheral)
        {
            return peripheral is IQuadWordPeripheral target ? (offset, value) => { if(Allowed(offset)) { target.WriteQuadWord(offset, value); } } : (Action<long, ulong>)null;
        }

        public override Func<long, uint> GetReadDoubleWordMethod(IBusPeripheral peripheral)
        {
            return peripheral is IDoubleWordPeripheral target ? offset => Allowed(offset) ? target.ReadDoubleWord(offset) : 0u : (Func<long, uint>)null;
        }

        public override Action<long, uint> GetWriteDoubleWordMethod(IBusPeripheral peripheral)
        {
            return peripheral is IDoubleWordPeripheral target ? (offset, value) => { if(Allowed(offset)) { target.WriteDoubleWord(offset, value); } } : (Action<long, uint>)null;
        }

        public override Func<long, ushort> GetReadWordMethod(IBusPeripheral peripheral)
        {
            return peripheral is IWordPeripheral target ? offset => Allowed(offset) ? target.ReadWord(offset) : (ushort)0 : (Func<long, ushort>)null;
        }

        public override Action<long, ushort> GetWriteWordMethod(IBusPeripheral peripheral)
        {
            return peripheral is IWordPeripheral target ? (offset, value) => { if(Allowed(offset)) { target.WriteWord(offset, value); } } : (Action<long, ushort>)null;
        }

        public override Func<long, byte> GetReadByteMethod(IBusPeripheral peripheral)
        {
            return peripheral is IBytePeripheral target ? offset => Allowed(offset) ? target.ReadByte(offset) : (byte)0 : (Func<long, byte>)null;
        }

        public override Action<long, byte> GetWriteByteMethod(IBusPeripheral peripheral)
        {
            return peripheral is IBytePeripheral target ? (offset, value) => { if(Allowed(offset)) { target.WriteByte(offset, value); } } : (Action<long, byte>)null;
        }

        protected abstract bool Allowed(long offset);
    }

    // A securable peripheral behind the TZSC firewall.
    public class STM32U5_GTZCFirewallRegistration : STM32U5_GTZCGateRegistration
    {
        public STM32U5_GTZCFirewallRegistration(ulong address, ulong size, STM32U5_GTZC_TZSC tzsc,
            int configurationRegister, int bit, IPeripheral visibleTo = null)
            : base(address, size, visibleTo)
        {
            this.tzsc = tzsc;
            this.configurationRegister = configurationRegister;
            this.bit = bit;
        }

        public override string ToString()
        {
            return $"{base.ToString()} behind the GTZC firewall (CFGR{configurationRegister} bit {bit})";
        }

        protected override bool Allowed(long offset) => tzsc.AllowsAccess(configurationRegister, bit);

        private readonly STM32U5_GTZC_TZSC tzsc;
        private readonly int configurationRegister;
        private readonly int bit;
    }

    // A memory behind its watermark controller (MPCWMx of GTZC1, controller 1-based).
    public class STM32U5_MPCWMRegistration : STM32U5_GTZCGateRegistration
    {
        public STM32U5_MPCWMRegistration(ulong address, ulong size, STM32U5_GTZC_TZSC tzsc, int controller, IPeripheral visibleTo = null)
            : base(address, size, visibleTo)
        {
            this.tzsc = tzsc;
            this.controller = controller;
        }

        public override string ToString()
        {
            return $"{base.ToString()} behind MPCWM{controller}";
        }

        protected override bool Allowed(long offset) => tzsc.AllowsWatermarkAccess(controller, offset);

        private readonly STM32U5_GTZC_TZSC tzsc;
        private readonly int controller;
    }

    // An SRAM behind its MPCBB.
    public class STM32U5_MPCBBRegistration : STM32U5_GTZCGateRegistration
    {
        public STM32U5_MPCBBRegistration(ulong address, ulong size, STM32U5_GTZC_MPCBB mpcbb, IPeripheral visibleTo = null)
            : base(address, size, visibleTo)
        {
            this.mpcbb = mpcbb;
        }

        public override string ToString()
        {
            return $"{base.ToString()} behind its MPCBB";
        }

        protected override bool Allowed(long offset) => mpcbb.AllowsAccess(offset);

        private readonly STM32U5_GTZC_MPCBB mpcbb;
    }
}
