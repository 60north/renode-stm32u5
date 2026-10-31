//
// Copyright (c) 2026 Fedor Druzhinin
//
// This file is licensed under the MIT License.
//
// STM32U5 implementation-defined attribution unit (IDAU), RM0456 Rev 7 section 2.2,
// Table 3. The IDAU partition is fixed by hardware: the secure aliases of the flash memory,
// SRAMs and peripherals are non-secure callable, the rest of the attributed address space is
// non-secure. The Cortex-M33 combines it with the SAU and the stricter attribution applies.
//
// New model. It answers the attribution requests of the core on every access, so the table is
// not cleared by a reset of the core, unlike the regions of CortexM.SetIDAURegion. A core
// reset disables the IDAU and the external attribution handler of the core; both are enabled
// again when the machine reset has completed.
// Addresses outside the table, such as the private peripheral bus, are left to the core.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.CPU;

namespace Antmicro.Renode.Peripherals.CPU
{
    public class STM32U5_IDAU : CortexMImplementationDefinedAttributionUnit, IPeripheral
    {
        public STM32U5_IDAU(IMachine machine, CortexM cpu) : base(cpu)
        {
            this.cpu = cpu;
            machine.MachineReset += _ => EnableInCore();
            EnableInCore();
        }

        public void Reset()
        {
        }

        public override bool AttributionCheckCallback(uint address, bool secure, AccessType type, int accessWidth,
            out int region, out SecurityAttribution attribution)
        {
            for(var index = 0; index < Regions.Length; index++)
            {
                if(address >= Regions[index].Start && address <= Regions[index].End)
                {
                    region = index;
                    attribution = Regions[index].Attribution;
                    return true;
                }
            }
            region = 0;
            attribution = SecurityAttribution.Secure;
            return false;
        }

        private void EnableInCore()
        {
            cpu.ImplementationDefinedAttributionUnit = this;
            cpu.IDAUEnabled = true;
        }

        private readonly CortexM cpu;

        private static readonly Region[] Regions =
        {
            new Region(0x00000000, 0x0BFFFFFF, SecurityAttribution.NonSecure),
            new Region(0x0C000000, 0x0FFFFFFF, SecurityAttribution.NonSecureCallable),
            new Region(0x10000000, 0x2FFFFFFF, SecurityAttribution.NonSecure),
            new Region(0x30000000, 0x3FFFFFFF, SecurityAttribution.NonSecureCallable),
            new Region(0x40000000, 0x4FFFFFFF, SecurityAttribution.NonSecure),
            new Region(0x50000000, 0x5FFFFFFF, SecurityAttribution.NonSecureCallable),
            new Region(0x60000000, 0xDFFFFFFF, SecurityAttribution.NonSecure),
        };

        private struct Region
        {
            public Region(uint start, uint end, SecurityAttribution attribution)
            {
                Start = start;
                End = end;
                Attribution = attribution;
            }

            public uint Start { get; }
            public uint End { get; }
            public SecurityAttribution Attribution { get; }
        }
    }
}
