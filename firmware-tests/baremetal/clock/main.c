/* Clock initialisation test firmware.
 *
 * Raises SYSCLK from MSIS 4 MHz to 160 MHz with PLL1, following the order used by
 * STM32CubeU5 (voltage range first, then PLL, flash latency, clock switch). Every wait has a
 * timeout; the stage that timed out is stored in failedStage so a hang is diagnosable.
 *
 * Register addresses and bits: stm32u575xx.h. Sequence and values: RM0456 Rev 7. */

#include <stdint.h>

#include "test_support.h"

#define RCC_BASE 0x46020C00u
#define RCC_CR REG32(RCC_BASE + 0x000u)
#define RCC_CFGR1 REG32(RCC_BASE + 0x01Cu)
#define RCC_PLL1CFGR REG32(RCC_BASE + 0x028u)
#define RCC_PLL1DIVR REG32(RCC_BASE + 0x034u)
#define RCC_AHB3ENR REG32(RCC_BASE + 0x094u)

#define PWR_BASE 0x46020800u
#define PWR_VOSR REG32(PWR_BASE + 0x00Cu)
#define PWR_SVMSR REG32(PWR_BASE + 0x03Cu)

#define FLASH_ACR REG32(0x40022000u)

#define RCC_CR_PLL1ON (1u << 24)
#define RCC_CR_PLL1RDY (1u << 25)
#define RCC_AHB3ENR_PWREN (1u << 2)
#define RCC_CFGR1_SW_PLL1 3u
#define RCC_CFGR1_SWS_SHIFT 2u

/* PLL1SRC = MSIS (01), PLL1RGE = 00 (input 4-8 MHz), PLL1M = 0 (/1), PLL1REN
 * (RM0456 section 11.8.9). */
#define PLL1CFGR_VALUE ((1u << 0) | (1u << 18))
/* PLL1N = 80 (field 79), PLL1P = PLL1Q = PLL1R = 2 (field 1) (RM0456 section 11.8.12). */
#define PLL1DIVR_VALUE ((79u << 0) | (1u << 9) | (1u << 16) | (1u << 24))

#define PWR_VOSR_BOOSTRDY (1u << 14)
#define PWR_VOSR_VOSRDY (1u << 15)
#define PWR_VOSR_RANGE1 (3u << 16)
#define PWR_VOSR_BOOSTEN (1u << 18)
#define PWR_SVMSR_ACTVOSRDY (1u << 15)
#define PWR_SVMSR_ACTVOS_SHIFT 16u

/* 4 wait states for HCLK <= 160 MHz in range 1 (RM0456 Table 54, p.295). */
#define FLASH_LATENCY_160MHZ 4u

#define TARGET_HCLK_HZ 160000000u
#define SYSTICK_RELOAD (TARGET_HCLK_HZ / 1000u - 1u)
#define WAIT_LIMIT 100000u

enum Stage
{
    StageStart = 0,
    StageVoltageRange = 1,
    StageBooster = 2,
    StagePll1Lock = 3,
    StageFlashLatency = 4,
    StageClockSwitch = 5,
    StageDone = 6,
};

volatile uint32_t stage;
volatile uint32_t failedStage;
volatile uint32_t sysTickCount;
volatile uint32_t loopCount;

void SysTick_Handler(void)
{
    sysTickCount += 1u;
}

static int waitFor(volatile uint32_t *reg, uint32_t mask, uint32_t value, uint32_t stageId)
{
    for (uint32_t attempt = 0u; attempt < WAIT_LIMIT; ++attempt)
    {
        if ((*reg & mask) == value)
        {
            return 1;
        }
    }
    failedStage = stageId;
    return 0;
}

static int configureClocks(void)
{
    RCC_AHB3ENR |= RCC_AHB3ENR_PWREN;

    stage = StageVoltageRange;
    PWR_VOSR = PWR_VOSR_RANGE1;
    if (!waitFor(&PWR_VOSR, PWR_VOSR_VOSRDY, PWR_VOSR_VOSRDY, StageVoltageRange) ||
        !waitFor(&PWR_SVMSR, PWR_SVMSR_ACTVOSRDY | (3u << PWR_SVMSR_ACTVOS_SHIFT),
                 PWR_SVMSR_ACTVOSRDY | (3u << PWR_SVMSR_ACTVOS_SHIFT), StageVoltageRange))
    {
        return 0;
    }

    /* The EPOD booster is required above 55 MHz in range 1 (RM0456 section 10.10.4). */
    stage = StageBooster;
    PWR_VOSR = PWR_VOSR_RANGE1 | PWR_VOSR_BOOSTEN;
    if (!waitFor(&PWR_VOSR, PWR_VOSR_BOOSTRDY, PWR_VOSR_BOOSTRDY, StageBooster))
    {
        return 0;
    }

    stage = StagePll1Lock;
    RCC_PLL1CFGR = PLL1CFGR_VALUE;
    RCC_PLL1DIVR = PLL1DIVR_VALUE;
    RCC_CR |= RCC_CR_PLL1ON;
    if (!waitFor(&RCC_CR, RCC_CR_PLL1RDY, RCC_CR_PLL1RDY, StagePll1Lock))
    {
        return 0;
    }

    stage = StageFlashLatency;
    FLASH_ACR = (FLASH_ACR & ~0xFu) | FLASH_LATENCY_160MHZ;
    if (!waitFor(&FLASH_ACR, 0xFu, FLASH_LATENCY_160MHZ, StageFlashLatency))
    {
        return 0;
    }

    stage = StageClockSwitch;
    RCC_CFGR1 = (RCC_CFGR1 & ~3u) | RCC_CFGR1_SW_PLL1;
    if (!waitFor(&RCC_CFGR1, 3u << RCC_CFGR1_SWS_SHIFT, RCC_CFGR1_SW_PLL1 << RCC_CFGR1_SWS_SHIFT,
                 StageClockSwitch))
    {
        return 0;
    }
    return 1;
}

int main(void)
{
    if (configureClocks())
    {
        SYST_RVR = SYSTICK_RELOAD;
        SYST_CVR = 0u;
        SYST_CSR = SYST_CSR_ENABLE | SYST_CSR_TICKINT | SYST_CSR_CLKSOURCE;
        stage = StageDone;
    }

    for (;;)
    {
        loopCount += 1u;
    }
}
