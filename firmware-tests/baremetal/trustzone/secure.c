/* TrustZone test firmware, secure image (TZEN = 1, boot from SECBOOTADD0 = 0x0C00 0000).
 *
 * Configures the SAU and the GTZC (USART1 secure, USART2 privileged, illegal access interrupt),
 * erases and programs flash pages through both flash interfaces, starts the non-secure image and
 * serves one non-secure callable function. GTZC interrupts, the flash error flags and a
 * SecureFault caused by the non-secure image are recorded in secure SRAM. */

#include <arm_cmse.h>
#include <stdint.h>

#include "test_support.h"
#include "trustzone.h"

#define SCB_SHCSR REG32(0xE000ED24u)
#define SCB_SHCSR_SECUREFAULTENA (1u << 19)
#define SCB_NS_VTOR REG32(0xE002ED08u)
#define SAU_CTRL REG32(0xE000EDD0u)
#define SAU_RNR REG32(0xE000EDD8u)
#define SAU_RBAR REG32(0xE000EDDCu)
#define SAU_RLAR REG32(0xE000EDE0u)
#define SAU_RLAR_ENABLE (1u << 0)
#define SAU_RLAR_NSC (1u << 1)
#define SAU_CTRL_ENABLE (1u << 0)
#define SCB_SFSR REG32(0xE000EDE4u)
#define SCB_SFAR REG32(0xE000EDE8u)

typedef void __attribute__((cmse_nonsecure_call)) NonSecureFunction(void);

volatile uint32_t secureCalls;
volatile uint32_t secureFaults;
volatile uint32_t secureFaultStatus;
volatile uint32_t secureFaultAddress;
volatile uint32_t gtzcInterrupts;
volatile uint32_t gtzcStatus;
volatile uint32_t gtzcPrivilegeStatus;
volatile uint32_t gtzcMemoryStatus;
volatile uint32_t secureReadNonSecureBlock = 0xFFFFFFFFu;
volatile uint32_t gtzcFlashStatus;
volatile uint32_t gtzc2Status;
volatile uint32_t secureReadNonSecureBackupSram = 0xFFFFFFFFu;
volatile uint32_t secureBackupSramEvent;
static volatile uint32_t dmaSource[4] = {0x11111111u, 0x22222222u, 0x33333333u, 0x44444444u};
volatile uint32_t dmaSecureCopy[4];
volatile uint32_t dmaNonSecureSourceCopy[4] = {0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu};
volatile uint32_t dmaUnprivilegedCopy[4] = {0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu};
volatile uint32_t flashNonSecureEraseSecurePage = 0xFFFFFFFFu;
volatile uint32_t flashSecureEraseSecurePage = 0xFFFFFFFFu;
volatile uint32_t flashSecureEraseNonSecurePage = 0xFFFFFFFFu;
volatile uint32_t flashNonSecureEraseNonSecurePage = 0xFFFFFFFFu;
volatile uint32_t flashSecureEraseNonSecureBank = 0xFFFFFFFFu;
volatile uint32_t flashSecureProgramNonSecurePage = 0xFFFFFFFFu;
/* GTZC1_TZIC_SR4 flash events of each flash operation above, in the same order. */
volatile uint32_t flashEvents[6] = {0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu};

static void setSauRegion(uint32_t region, uint32_t start, uint32_t end, uint32_t attributes)
{
    SAU_RNR = region;
    SAU_RBAR = start & ~0x1Fu;
    SAU_RLAR = (end & ~0x1Fu) | attributes | SAU_RLAR_ENABLE;
}

uint32_t __attribute__((cmse_nonsecure_entry)) secureIncrement(uint32_t value)
{
    secureCalls += 1u;
    return value + 1u;
}

/* Only GTZC_IRQn is enabled. USART1 events are in TZIC_SR2, USART2 events would be in SR1,
 * SRAM3 and FLASH events are in SR4. */
void Irq_Handler(void)
{
    const uint32_t status1 = REG32(GTZC1_TZIC_SR1_S);
    const uint32_t status4 = REG32(GTZC1_TZIC_SR4_S);
    gtzcStatus = REG32(GTZC1_TZIC_SR2_S);
    gtzcPrivilegeStatus |= status1;
    gtzcMemoryStatus |= status4 & ~(GTZC_CFGR4_FLASH_REG | GTZC_CFGR4_FLASH);
    gtzcFlashStatus |= status4 & (GTZC_CFGR4_FLASH_REG | GTZC_CFGR4_FLASH);
    REG32(GTZC1_TZIC_FCR1_S) = status1;
    REG32(GTZC1_TZIC_FCR2_S) = gtzcStatus;
    REG32(GTZC1_TZIC_FCR4_S) = status4;
    const uint32_t status2 = REG32(GTZC2_TZIC_SR2_S);
    gtzc2Status |= status2;
    REG32(GTZC2_TZIC_FCR2_S) = status2;
    gtzcInterrupts += 1u;
}

/* SRAM3 for the non-secure image except its last super-block, the upper half of SRAM4. */
static void configureMpcbb(void)
{
    for (uint32_t superBlock = 0u; superBlock < SRAM3_NON_SECURE_SUPER_BLOCKS; ++superBlock)
    {
        MPCBB_SECCFGR(MPCBB3_S, superBlock) = 0u;
        MPCBB_PRIVCFGR(MPCBB3_S, superBlock) = 0u;
    }
    MPCBB_SECCFGR(MPCBB4_S, 0u) = SRAM4_SECURE_BLOCKS;
    MPCBB_PRIVCFGR(MPCBB4_S, 0u) = SRAM4_SECURE_BLOCKS;
}

/* One erase or programming through the non-secure (NSCR) or secure (SECCR) interface; returns
 * the error flags of its status register and clears them. */
static uint32_t flashOperation(uint32_t control, uint32_t status, uint32_t value)
{
    REG32(FLASH_S + control) = value;
    const uint32_t errors = REG32(FLASH_S + status) & FLASH_SR_ERRORS;
    REG32(FLASH_S + status) = errors;
    REG32(FLASH_S + control) = 0u;
    return errors;
}

/* The flash events raised since the previous call (GTZC_IRQn is taken before the barrier
 * completes). */
static uint32_t takeFlashEvents(void)
{
    __asm volatile("dsb\n\tisb" ::: "memory");
    const uint32_t events = gtzcFlashStatus;
    gtzcFlashStatus = 0u;
    return events;
}

static uint32_t erasePage(uint32_t control, uint32_t status, uint32_t bank2, uint32_t page)
{
    return flashOperation(control, status, FLASH_CR_PER | (page << FLASH_CR_PNB_POS) | (bank2 ? FLASH_CR_BKER : 0u) | FLASH_CR_STRT);
}

/* RM0456 section 7.7 Tables 68 and 70: each interface erases and programs only the pages of its
 * own security; the test writes a marker word into each page first. */
static void exerciseFlash(void)
{
    REG32(FLASH_S + FLASH_NSKEYR) = FLASH_KEY1;
    REG32(FLASH_S + FLASH_NSKEYR) = FLASH_KEY2;
    REG32(FLASH_S + FLASH_SECKEYR) = FLASH_KEY1;
    REG32(FLASH_S + FLASH_SECKEYR) = FLASH_KEY2;
    (void)takeFlashEvents();
    flashNonSecureEraseSecurePage = erasePage(FLASH_NSCR, FLASH_NSSR, 0u, FLASH_SECURE_PAGE_A);
    flashEvents[0] = takeFlashEvents();
    flashSecureEraseSecurePage = erasePage(FLASH_SECCR, FLASH_SECSR, 0u, FLASH_SECURE_PAGE_B);
    flashEvents[1] = takeFlashEvents();
    flashSecureEraseNonSecurePage = erasePage(FLASH_SECCR, FLASH_SECSR, 1u, FLASH_NON_SECURE_PAGE_A);
    flashEvents[2] = takeFlashEvents();
    flashNonSecureEraseNonSecurePage = erasePage(FLASH_NSCR, FLASH_NSSR, 1u, FLASH_NON_SECURE_PAGE_B);
    flashEvents[3] = takeFlashEvents();
    /* Bank 2 holds the non-secure image: denied, with a flash illegal access event. */
    flashSecureEraseNonSecureBank = flashOperation(FLASH_SECCR, FLASH_SECSR, FLASH_CR_MER2 | FLASH_CR_STRT);
    flashEvents[4] = takeFlashEvents();
    /* A secure programming write through the secure alias of a non-secure page. */
    REG32(FLASH_S + FLASH_SECCR) = FLASH_CR_PG;
    REG32(FLASH_NON_SECURE_PAGE_A_S) = 0u;
    flashSecureProgramNonSecurePage = flashOperation(FLASH_SECCR, FLASH_SECSR, 0u);
    flashEvents[5] = takeFlashEvents();
    REG32(FLASH_S + FLASH_NSCR) = FLASH_CR_LOCK;
    REG32(FLASH_S + FLASH_SECCR) = FLASH_CR_LOCK;
}

/* A memory-to-memory block of 16 bytes on channel `channel`, started by software. */
static void dmaCopy(uint32_t channel, uint32_t source, uint32_t destination, uint32_t security)
{
    const uint32_t base = GPDMA_S + GPDMA_CHANNEL(channel);
    REG32(base + GPDMA_CXTR1) = GPDMA_CXTR1_WORDS | security;
    REG32(base + GPDMA_CXTR2) = GPDMA_CXTR2_SWREQ;
    REG32(base + GPDMA_CXBR1) = 16u;
    REG32(base + GPDMA_CXSAR) = source;
    REG32(base + GPDMA_CXDAR) = destination;
    REG32(base + GPDMA_CXLLR) = 0u;
    REG32(base + GPDMA_CXCR) = GPDMA_CXCR_EN;
}

/* RM0456 section 17.4.14: a secure channel transfers with the security of SSEC and DSEC; a
 * non-secure channel issues non-secure transfers, also when secure code started it. */
static void exerciseDma(void)
{
    /* Privileged channels: the SRAM blocks are privileged after reset (MPCBB PRIVCFGR). */
    REG32(GPDMA_S + GPDMA_PRIVCFGR) = (1u << GPDMA_SECURE_CHANNEL) | (1u << GPDMA_NON_SECURE_CHANNEL);
    REG32(GPDMA_S + GPDMA_SECCFGR) = (1u << GPDMA_SECURE_CHANNEL) | (1u << GPDMA_UNPRIVILEGED_CHANNEL);
    dmaCopy(GPDMA_SECURE_CHANNEL, (uint32_t)dmaSource, (uint32_t)dmaSecureCopy, GPDMA_CXTR1_SSEC | GPDMA_CXTR1_DSEC);
    /* A secure channel with a non-secure source: the secure SRAM3 super-block through the
     * non-secure alias, refused by MPCBB3. */
    dmaCopy(GPDMA_SECURE_CHANNEL, SECURE_SRAM3_BLOCK_NS, (uint32_t)dmaNonSecureSourceCopy, GPDMA_CXTR1_DSEC);
    /* A non-secure channel reading the secure buffer through the secure alias: refused by
     * MPCBB1, the non-secure destination receives zeros. */
    REG32(GPDMA_NON_SECURE_DESTINATION_NS) = 0xFFFFFFFFu;
    dmaCopy(GPDMA_NON_SECURE_CHANNEL, (uint32_t)dmaSource, GPDMA_NON_SECURE_DESTINATION_NS, 0u);
    /* A secure unprivileged channel: the privileged SRAM1 blocks refuse its transfers. */
    dmaCopy(GPDMA_UNPRIVILEGED_CHANNEL, (uint32_t)dmaSource, (uint32_t)dmaUnprivilegedCopy, GPDMA_CXTR1_SSEC | GPDMA_CXTR1_DSEC);
}

void SecureFault_Handler(void)
{
    secureFaultStatus = SCB_SFSR;
    secureFaultAddress = SCB_SFAR;
    secureFaults += 1u;
    for (;;)
    {
    }
}

int main(void)
{
    setSauRegion(0u, NSC_START, NSC_END, SAU_RLAR_NSC);
    setSauRegion(1u, NS_FLASH_START, NS_FLASH_END, 0u);
    setSauRegion(2u, NS_RAM_START, NS_RAM_END, 0u);
    setSauRegion(3u, NS_SRAM4_START, NS_SRAM4_END, 0u);
    setSauRegion(4u, NS_PERIPHERALS_START, NS_PERIPHERALS_END, 0u);
    setSauRegion(5u, SECURE_RAM_WINDOW_START, SECURE_RAM_WINDOW_END, 0u);
    SAU_CTRL = SAU_CTRL_ENABLE;
    SCB_SHCSR |= SCB_SHCSR_SECUREFAULTENA;
    __asm volatile("dsb\n\tisb" ::: "memory");

    /* USART1 becomes secure with a value only secure code can write; USART2 stays non-secure
     * but privileged. Illegal accesses to USART1 raise GTZC_IRQn, a secure interrupt. */
    REG32(USART1_BRR_S) = 0x5678u;
    REG32(GTZC1_TZSC_SECCFGR2_S) = GTZC_CFGR2_USART1;
    REG32(GTZC1_TZSC_PRIVCFGR1_S) = GTZC_CFGR1_USART2;
    REG32(GTZC1_TZIC_IER2_S) = GTZC_CFGR2_USART1;
    /* Enabled too, to show that the privilege violation raises no event. */
    REG32(GTZC1_TZIC_IER1_S) = GTZC_CFGR1_USART2;
    REG32(GTZC1_TZIC_IER4_S) = GTZC_CFGR4_SRAM3 | GTZC_CFGR4_FLASH_REG | GTZC_CFGR4_FLASH | GTZC_CFGR4_GPDMA1 | GTZC_CFGR4_BKPSRAM;
    NVIC_ISER(GTZC_IRQ / 32u) = 1u << (GTZC_IRQ % 32u);

    /* A marker in the secure SRAM3 super-block, written through the secure alias. Then a
     * secure read of a non-secure SRAM3 block through the secure alias: illegal, it reads 0. */
    configureMpcbb();
    REG32(GPIOC_SECCFGR_S) &= ~GPIO_PIN7;
    REG32(SECURE_SRAM3_BLOCK_S) = 0x5EC0u;
    REG32(NON_SECURE_SRAM3_WORD_NS) = 0xABCDu;
    secureReadNonSecureBlock = REG32(NON_SECURE_SRAM3_WORD_S);
    exerciseFlash();
    exerciseDma();
    /* MPCWM4: the lower 1 KB of BKPSRAM non-secure. A marker in the secure upper part, then a
     * secure read of the non-secure part: illegal (RM0456 section 5.4.2), it reads 0; its event
     * is kept apart from the later non-secure one. */
    REG32(GTZC1_TZSC_MPCWM4AR_S) = MPCWM4_NON_SECURE_LENGTH << 16;
    REG32(GTZC1_TZSC_MPCWM4ACFGR_S) = MPCWM_CFGR_SREN;
    REG32(BKPSRAM_S + BKPSRAM_SECURE_OFFSET) = 0xB5B5u;
    secureReadNonSecureBackupSram = REG32(BKPSRAM_S);
    __asm volatile("dsb\n\tisb" ::: "memory");
    secureBackupSramEvent = gtzcMemoryStatus & GTZC_CFGR4_BKPSRAM;
    gtzcMemoryStatus &= ~GTZC_CFGR4_BKPSRAM;
    /* SYSCFG: SYSCFG_CFGR2 secure (CLASSBSEC), the SAU registers locked in SYSCFG_CSLCKR. */
    REG32(SYSCFG_S + SYSCFG_CSLCKR) = SYSCFG_CSLCKR_LOCKSAU;
    REG32(SYSCFG_S + SYSCFG_SECCFGR) = SYSCFG_SECCFGR_CLASSBSEC;
    /* The RTC initialization is secure (INITSEC); the secure image enters initialization mode
     * and leaves RTC_WPR unlocked. */
    REG32(RTC_SECCFGR_S) = RTC_SECCFGR_INITSEC;
    REG32(RTC_WPR_S) = 0xCAu;
    REG32(RTC_WPR_S) = 0x53u;
    REG32(RTC_ICSR_S) |= RTC_ICSR_INIT;
    /* RCC: HSI on and secure (HSISEC); the clocks of USART1 (secure in the TZSC) and GPIOC (with
     * secure pins) on; USART2 on with NSPRIV: its bit is privileged for non-secure code. */
    REG32(RCC_S + RCC_CR) |= RCC_CR_HSION;
    REG32(RCC_S + RCC_SECCFGR) = RCC_SECCFGR_HSISEC | RCC_SECCFGR_SYSCLKSEC;
    REG32(RCC_S + RCC_AHB3ENR) |= RCC_AHB3ENR_PWREN;
    REG32(RCC_S + RCC_APB2ENR) |= RCC_APB2ENR_USART1EN;
    REG32(RCC_S + RCC_AHB2ENR1) |= RCC_AHB2ENR1_GPIOCEN;
    REG32(RCC_S + RCC_APB1ENR1) |= RCC_APB1ENR1_USART2EN;
    REG32(RCC_S + RCC_PRIVCFGR) = RCC_PRIVCFGR_NSPRIV;
    REG32(GTZC2_TZIC_IER2_S) = GTZC_CFGR2_RCC | GTZC_CFGR2_PWR | GTZC_CFGR2_EXTI | GTZC_CFGR2_SYSCFG;
    /* EXTI: line 13 (PC13) secure, configured with line 5; then the security configuration is
     * locked, and a later write to it is ignored. */
    REG32(EXTI_S + EXTI_RTSR1) = EXTI_LINE13 | EXTI_LINE5;
    REG32(EXTI_S + EXTI_IMR1) = EXTI_LINE13 | EXTI_LINE5;
    REG32(EXTI_S + EXTI_EXTICR4) = EXTI_EXTICR4_LINE13_PORTC;
    REG32(EXTI_S + EXTI_SECCFGR1) = EXTI_LINE13;
    REG32(EXTI_S + EXTI_LOCKR) = 1u;
    REG32(EXTI_S + EXTI_SECCFGR1) = 0u;
    /* PWR: low-power mode and wake-up pin 1 secure; pull-ups on PC0 (secure pin) and PC7
     * (non-secure pin); VOS follows SYSCLKSEC. */
    REG32(PWR_S + PWR_CR1) = PWR_CR1_LPMS_STOP2;
    REG32(PWR_S + PWR_WUCR1) = PWR_WUCR1_WUPEN1 | PWR_WUCR1_WUPEN2;
    REG32(PWR_S + PWR_PUCRC) = 1u | GPIO_PIN7;
    REG32(PWR_S + PWR_VOSR) = PWR_VOSR_VOS_RANGE1;
    REG32(PWR_S + PWR_SECCFGR) = PWR_SECCFGR_LPMSEC | PWR_SECCFGR_WUP1SEC;

    /* The non-secure vector table: initial stack pointer, then reset handler. */
    const volatile uint32_t *vectors = (const volatile uint32_t *)NS_FLASH_START;
    SCB_NS_VTOR = NS_FLASH_START;
    __asm volatile("msr msp_ns, %0" : : "r"(vectors[0]));
    NonSecureFunction *nonSecureReset = (NonSecureFunction *)cmse_nsfptr_create(vectors[1]);
    nonSecureReset();
    for (;;)
    {
    }
}
