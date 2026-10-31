/* TrustZone test firmware, non-secure image, started by the secure image.
 *
 * Calls the secure function through its non-secure callable veneer, then reads a secure SRAM
 * window that the SAU marks non-secure; the IDAU keeps the secure alias non-secure callable, so
 * the read raises a SecureFault handled by the secure image. */

#include <stdint.h>

#include "test_support.h"
#include "trustzone.h"

volatile uint32_t nonSecureStarted;
volatile uint32_t nonSecureResult;
volatile uint32_t nonSecureReadSecureRam;
volatile uint32_t nonSecurePrivilegedRead;
volatile uint32_t nonSecureSecurePeripheralRead = 0xFFFFFFFFu;
volatile uint32_t nonSecureUnprivilegedRead = 0xFFFFFFFFu;
volatile uint32_t nonSecureSecureBlockRead = 0xFFFFFFFFu;
volatile uint32_t nonSecureGpioMode = 0xFFFFFFFFu;
volatile uint32_t nonSecureFlashSecureControl = 0xFFFFFFFFu;
volatile uint32_t nonSecureRccControl = 0xFFFFFFFFu;
volatile uint32_t nonSecureRccApb2 = 0xFFFFFFFFu;
volatile uint32_t nonSecureRccAhb2 = 0xFFFFFFFFu;
volatile uint32_t nonSecureRccApb1Privileged;
volatile uint32_t nonSecureRccApb1Unprivileged = 0xFFFFFFFFu;
volatile uint32_t nonSecurePwrControl = 0xFFFFFFFFu;
volatile uint32_t nonSecurePwrWakeup = 0xFFFFFFFFu;
volatile uint32_t nonSecurePwrPullUp = 0xFFFFFFFFu;
volatile uint32_t nonSecurePwrVoltage = 0xFFFFFFFFu;
volatile uint32_t nonSecureRccPwrEnable = 0xFFFFFFFFu;
volatile uint32_t nonSecureExtiMask = 0xFFFFFFFFu;
volatile uint32_t nonSecureExtiSelection = 0xFFFFFFFFu;
volatile uint32_t nonSecureExtiSecurity = 0xFFFFFFFFu;
volatile uint32_t nonSecureExtiLock = 0xFFFFFFFFu;
volatile uint32_t nonSecureDmaSecureChannelSource = 0xFFFFFFFFu;
volatile uint32_t nonSecureDmaSecurity = 0xFFFFFFFFu;
volatile uint32_t nonSecureBackupSram = 0xFFFFFFFFu;
volatile uint32_t nonSecureSecureBackupSram = 0xFFFFFFFFu;
volatile uint32_t nonSecureSyscfgSecurity = 0xFFFFFFFFu;
volatile uint32_t nonSecureSyscfgSecureLock = 0xFFFFFFFFu;

int main(void)
{
    nonSecureStarted = 1u;
    nonSecureResult = secureIncrement(41u);

    /* USART2 is privileged: reachable while privileged, silently zero when unprivileged. */
    REG32(USART2_BRR_NS) = 0x2222u;
    nonSecurePrivilegedRead = REG32(USART2_BRR_NS);
    /* USART1 is secure: the write is ignored, the read returns zero, both are illegal. */
    REG32(USART1_BRR_NS) = 0x1111u;
    nonSecureSecurePeripheralRead = REG32(USART1_BRR_NS);
    /* Only PC7 is non-secure: the write changes its mode only, the read shows its bits only,
     * and the write to GPIOC_SECCFGR is discarded. */
    REG32(GPIOC_MODER_NS) = 0x55555555u;
    nonSecureGpioMode = REG32(GPIOC_MODER_NS);
    REG32(GPIOC_SECCFGR_NS) = 0u;
    /* FLASH_SECCR is a secure register (reset value 0x8000 0000): RAZ, a register illegal
     * access event. */
    nonSecureFlashSecureControl = REG32(FLASH_NS + FLASH_SECCR);
    /* INIT is secure: the non-secure write of 0 to RTC_ICSR keeps the initialization mode. */
    REG32(RTC_ICSR_NS) = 0u;
    /* RCC: the HSI bits (HSISEC), USART1EN (USART1 secure) and GPIOCEN (GPIOC with secure pins)
     * read as 0 and ignore writes; the RCC_SECCFGR write is ignored and illegal. */
    nonSecureRccControl = REG32(RCC_NS + RCC_CR) & (RCC_CR_HSION | RCC_CR_HSIRDY);
    REG32(RCC_NS + RCC_CR) &= ~RCC_CR_HSION;
    nonSecureRccApb2 = REG32(RCC_NS + RCC_APB2ENR) & RCC_APB2ENR_USART1EN;
    REG32(RCC_NS + RCC_APB2ENR) = 0u;
    nonSecureRccAhb2 = REG32(RCC_NS + RCC_AHB2ENR1) & RCC_AHB2ENR1_GPIOCEN;
    REG32(RCC_NS + RCC_AHB2ENR1) = 0u;
    REG32(RCC_NS + RCC_SECCFGR) = 0u;
    nonSecureRccApb1Privileged = REG32(RCC_NS + RCC_APB1ENR1) & RCC_APB1ENR1_USART2EN;
    /* PWR: PWR_CR1 (LPMSEC) and WUPEN1 (WUP1SEC) read as 0 and keep their value; WUPEN2 is
     * non-secure. PUCRC shows the PC7 pull-up only. VOS (SYSCLKSEC) reads 0, VOSRDY 1. PWREN
     * in the RCC is secure (a PWR function is secure). The PWR_SECCFGR write is illegal. */
    nonSecurePwrControl = REG32(PWR_NS + PWR_CR1);
    REG32(PWR_NS + PWR_CR1) = 0u;
    nonSecurePwrWakeup = REG32(PWR_NS + PWR_WUCR1);
    REG32(PWR_NS + PWR_WUCR1) = 0u;
    nonSecurePwrPullUp = REG32(PWR_NS + PWR_PUCRC);
    nonSecurePwrVoltage = REG32(PWR_NS + PWR_VOSR) & (PWR_VOSR_VOSRDY | PWR_VOSR_VOS_RANGE1);
    REG32(PWR_NS + PWR_SECCFGR) = 0u;
    nonSecureRccPwrEnable = REG32(RCC_NS + RCC_AHB3ENR) & RCC_AHB3ENR_PWREN;
    /* EXTI: the line 13 bits and its EXTICR4 field read as 0 and keep their value; line 5 is
     * non-secure. EXTI_SECCFGR1 reads back, its write is illegal; EXTI_LOCKR reads 0, illegal. */
    nonSecureExtiMask = REG32(EXTI_NS + EXTI_IMR1);
    REG32(EXTI_NS + EXTI_IMR1) = 0u;
    nonSecureExtiSelection = REG32(EXTI_NS + EXTI_EXTICR4);
    REG32(EXTI_NS + EXTI_EXTICR4) = 0u;
    nonSecureExtiSecurity = REG32(EXTI_NS + EXTI_SECCFGR1);
    REG32(EXTI_NS + EXTI_SECCFGR1) = 0u;
    nonSecureExtiLock = REG32(EXTI_NS + EXTI_LOCKR);
    /* GPDMA: the registers of the secure channel read as 0 (illegal); GPDMA_SECCFGR reads back
     * and its non-secure write is ignored (illegal). */
    nonSecureDmaSecureChannelSource = REG32(GPDMA_NS + GPDMA_CHANNEL(GPDMA_SECURE_CHANNEL) + GPDMA_CXSAR);
    nonSecureDmaSecurity = REG32(GPDMA_NS + GPDMA_SECCFGR);
    REG32(GPDMA_NS + GPDMA_SECCFGR) = 0u;
    /* BKPSRAM (MPCWM4): the lower 1 KB is non-secure; the upper 1 KB is secure, read as zero,
     * illegal. */
    REG32(BKPSRAM_NS) = 0x4B4Bu;
    nonSecureBackupSram = REG32(BKPSRAM_NS);
    nonSecureSecureBackupSram = REG32(BKPSRAM_NS + BKPSRAM_SECURE_OFFSET);
    /* SYSCFG: SYSCFG_SECCFGR reads back, its write is ignored and illegal; SYSCFG_CSLCKR is
     * secure (zero, illegal); SYSCFG_CFGR2 is secure with CLASSBSEC: the write is ignored. */
    nonSecureSyscfgSecurity = REG32(SYSCFG_NS + SYSCFG_SECCFGR);
    REG32(SYSCFG_NS + SYSCFG_SECCFGR) = 0u;
    nonSecureSyscfgSecureLock = REG32(SYSCFG_NS + SYSCFG_CSLCKR);
    REG32(SYSCFG_NS + SYSCFG_CFGR2) = SYSCFG_CFGR2_CLL;
    /* The last SRAM3 super-block is secure in MPCBB3: the read returns zero, it is illegal. */
    nonSecureSecureBlockRead = REG32(SECURE_SRAM3_BLOCK_NS);
    __asm volatile("msr control, %0\n\tisb" : : "r"(1u) : "memory");
    nonSecureUnprivilegedRead = REG32(USART2_BRR_NS);
    /* NSPRIV: the non-secure RCC bits read as 0 for unprivileged code. */
    nonSecureRccApb1Unprivileged = REG32(RCC_NS + RCC_APB1ENR1);
    nonSecureReadSecureRam = REG32(SECURE_RAM_WINDOW_START);
    for (;;)
    {
    }
}
