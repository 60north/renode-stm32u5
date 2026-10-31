//
// Copyright (c) 2026 Fedor Druzhinin
//
// This file is licensed under the MIT License.
//
// Security and privilege of the current bus transaction, for the STM32U5 TrustZone models, and
// the interface of the TrustZone-aware models.
//
// New file. A CPU transaction is secure when the core is in secure state and the accessed
// address is attributed secure (a secure core accessing a non-secure alias issues a non-secure
// transaction, RM0456 section 3.5).
//

using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public static class STM32U5_TrustZoneContext
    {
        // False when the access has no CPU context: the Monitor or a model without a transaction
        // state. The outputs then describe a secure privileged transaction.
        public static bool TryGetTransaction(IBusController sysbus, out bool secure, out bool privileged)
        {
            if(!sysbus.TryGetCurrentContextState<CortexM.ContextState>(out _, out var state))
            {
                secure = true;
                privileged = true;
                return false;
            }
            secure = state.CpuSecure && state.AttributionSecure;
            privileged = state.Privileged;
            return true;
        }
    }

    // A TrustZone-aware peripheral (RM0456 Table 5). Its clock and reset bits in the RCC become
    // secure as soon as one of its functions is secure (RM0456 section 3.5).
    public interface ISTM32U5_TrustZoneAware
    {
        bool HasSecureFunction { get; }
    }
}
