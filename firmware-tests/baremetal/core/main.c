/* Core platform test firmware.
 *
 * Each check stores its result in a global variable that the robot test reads through the
 * ELF symbol table. Nothing is printed, so the test does not depend on any UART model. */

#include <stdint.h>

#include "test_support.h"

/* SysTick reload for 1 ms at the 4 MHz reset clock (MSIS range 4, RM0456 section 11.8.2). */
#define RESET_CLOCK_HZ 4000000u
#define SYSTICK_RELOAD (RESET_CLOCK_HZ / 1000u - 1u)

#define FLASH_SIZE_DATA (*(volatile const uint16_t *)0x0BFA07A0u)

#define FIRST_IRQ 0u
#define LAST_IRQ 125u

enum
{
    CheckData = 1u << 0,
    CheckBss = 1u << 1,
    CheckFlashRead = 1u << 2,
    CheckSram1 = 1u << 3,
    CheckSram2 = 1u << 4,
    CheckSram3 = 1u << 5,
    CheckSram4 = 1u << 6,
    CheckFirstIrq = 1u << 7,
    CheckLastIrq = 1u << 8,
    CheckDone = 1u << 31,
};

volatile uint32_t passedChecks;
volatile uint32_t flashSizeKb;
volatile uint32_t sysTickCount;
volatile uint32_t loopCount;
volatile uint32_t irqLog[4];
volatile uint32_t irqLogCount;

volatile uint32_t initializedWord = 0xC0FFEE01u;
volatile uint32_t zeroedWord;

static const uint32_t flashTable[8] = {
    0x01234567u, 0x89ABCDEFu, 0xDEADBEEFu, 0x00000000u,
    0xFFFFFFFFu, 0x55AA55AAu, 0x0F0F0F0Fu, 0x80000001u,
};
#define FLASH_TABLE_XOR 0x737F933Cu

void SysTick_Handler(void)
{
    sysTickCount += 1u;
}

void Irq_Handler(void)
{
    const uint32_t irq = activeException() - 16u;
    if (irqLogCount < sizeof(irqLog) / sizeof(irqLog[0]))
    {
        irqLog[irqLogCount] = irq;
    }
    irqLogCount += 1u;
}

static uint32_t checkSram(uint32_t address, uint32_t flag)
{
    volatile uint32_t *const word = (volatile uint32_t *)address;
    word[0] = 0xA5A5A5A5u ^ address;
    word[1] = 0x5A5A5A5Au ^ address;
    return (word[0] == (0xA5A5A5A5u ^ address)) && (word[1] == (0x5A5A5A5Au ^ address)) ? flag : 0u;
}

static uint32_t irqWasLogged(uint32_t irq)
{
    for (uint32_t index = 0u; index < irqLogCount && index < 4u; ++index)
    {
        if (irqLog[index] == irq)
        {
            return 1u;
        }
    }
    return 0u;
}

int main(void)
{
    uint32_t passed = 0u;

    passed |= (initializedWord == 0xC0FFEE01u) ? CheckData : 0u;
    passed |= (zeroedWord == 0u) ? CheckBss : 0u;

    uint32_t checksum = 0u;
    for (uint32_t index = 0u; index < 8u; ++index)
    {
        checksum ^= flashTable[index];
    }
    passed |= (checksum == FLASH_TABLE_XOR) ? CheckFlashRead : 0u;
    flashSizeKb = FLASH_SIZE_DATA;

    /* Addresses away from .data/.bss (SRAM1 start), the stack (SRAM3 end) and the start-up
     * counter (SRAM4 start). */
    passed |= checkSram(0x20020000u, CheckSram1);
    passed |= checkSram(0x20038000u, CheckSram2);
    passed |= checkSram(0x20080000u, CheckSram3);
    passed |= checkSram(0x28001000u, CheckSram4);

    enableAndPendIrq(FIRST_IRQ);
    enableAndPendIrq(LAST_IRQ);
    passed |= irqWasLogged(FIRST_IRQ) ? CheckFirstIrq : 0u;
    passed |= irqWasLogged(LAST_IRQ) ? CheckLastIrq : 0u;

    SYST_RVR = SYSTICK_RELOAD;
    SYST_CVR = 0u;
    SYST_CSR = SYST_CSR_ENABLE | SYST_CSR_TICKINT | SYST_CSR_CLKSOURCE;

    passedChecks = passed | CheckDone;

    for (;;)
    {
        loopCount += 1u;
    }
}
