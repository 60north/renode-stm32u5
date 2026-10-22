/* Register definitions shared by the bare-metal test firmware.
 * System Control Space addresses are architectural (Armv8-M Architecture Reference Manual,
 * System Control Space; SysTick at 0xE000E010, NVIC at 0xE000E100). */

#ifndef TEST_SUPPORT_H
#define TEST_SUPPORT_H

#include <stdint.h>

#define REG32(address) (*(volatile uint32_t *)(address))

#define SYST_CSR REG32(0xE000E010u)
#define SYST_RVR REG32(0xE000E014u)
#define SYST_CVR REG32(0xE000E018u)
#define SYST_CSR_ENABLE (1u << 0)
#define SYST_CSR_TICKINT (1u << 1)
#define SYST_CSR_CLKSOURCE (1u << 2)

#define NVIC_ISER(n) REG32(0xE000E100u + 4u * (n))
#define NVIC_ISPR(n) REG32(0xE000E200u + 4u * (n))

static inline uint32_t activeException(void)
{
    uint32_t ipsr;
    __asm volatile("mrs %0, ipsr" : "=r"(ipsr));
    return ipsr & 0x1FFu;
}

static inline void enableAndPendIrq(uint32_t irq)
{
    NVIC_ISER(irq / 32u) = 1u << (irq % 32u);
    NVIC_ISPR(irq / 32u) = 1u << (irq % 32u);
    __asm volatile("dsb\n\tisb" ::: "memory");
}

#endif
