/* Memory partition shared by the secure and non-secure TrustZone test images
 * (stm32u575xx.h *_BASE_S / *_BASE_NS; SAU registers: Armv8-M Architecture Reference Manual,
 * Security Attribution Unit). */

#ifndef TRUSTZONE_H
#define TRUSTZONE_H

#include <stdint.h>

/* Non-secure image: second half of the flash, SRAM3 and the upper half of SRAM4. */
#define NS_FLASH_START 0x08100000u
#define NS_FLASH_END 0x081FFFFFu
#define NS_RAM_START 0x20040000u
#define NS_RAM_END 0x200BFFFFu

/* SRAM3 in GTZC1 MPCBB3: super-blocks 0-30 are made non-secure and unprivileged for the
 * non-secure image (data up to 0x200B BFFF, nonsecure.ld); super-block 31 stays secure. The SAU
 * still marks it non-secure, so a non-secure read reaches the MPCBB (RM0456 section 5.4.4). */
#define MPCBB3_S 0x50033400u
#define MPCBB_SECCFGR(base, superBlock) REG32((base) + 0x100u + 4u * (superBlock))
#define MPCBB_PRIVCFGR(base, superBlock) REG32((base) + 0x200u + 4u * (superBlock))
#define SRAM3_NON_SECURE_SUPER_BLOCKS 31u
#define SECURE_SRAM3_BLOCK_NS 0x200BC000u
#define SECURE_SRAM3_BLOCK_S 0x300BC000u
/* A non-secure SRAM3 block, read by the secure image through the secure alias: an illegal
 * secure access while MPCBB3_CR.SRWILADIS is cleared. */
#define NON_SECURE_SRAM3_WORD_NS 0x20080000u
#define NON_SECURE_SRAM3_WORD_S 0x30080000u
/* SRAM4 in GTZC2 MPCBB4 (one super-block): blocks 16-31, the upper 8 KB, are non-secure. */
#define MPCBB4_S 0x56023800u
#define SRAM4_SECURE_BLOCKS 0x0000FFFFu
#define GTZC_CFGR4_SRAM3 (1u << 28)
#define NS_SRAM4_START 0x28002000u
#define NS_SRAM4_END 0x28003FFFu
#define NS_PERIPHERALS_START 0x40000000u
#define NS_PERIPHERALS_END 0x4FFFFFFFu

/* Non-secure callable veneers at the end of the secure flash image. */
#define NSC_START 0x0C0FE000u
#define NSC_END 0x0C0FFFFFu

/* A window of the secure SRAM1 alias that the SAU marks non-secure. The IDAU attributes the
 * secure alias as non-secure callable, the stricter of both applies (RM0456 Table 8), so the
 * window stays secure for data: the non-secure image reads it to raise a SecureFault. */
#define SECURE_RAM_WINDOW_START 0x30010000u
#define SECURE_RAM_WINDOW_END 0x3001FFFFu

/* USART baud rate register: a register that keeps any written value (RM0456 USART_BRR). */
#define USART1_BRR_NS 0x4001380Cu
#define USART1_BRR_S 0x5001380Cu
#define USART2_BRR_NS 0x4000440Cu

/* GTZC1 (stm32u575xx.h GTZC_TZSC1_BASE_S, GTZC_TZIC1_BASE_S; GTZC_CFGRx bit positions). */
#define GTZC1_TZSC_SECCFGR2_S 0x50032414u
#define GTZC1_TZSC_PRIVCFGR1_S 0x50032420u
#define GTZC1_TZIC_IER1_S 0x50032800u
#define GTZC1_TZIC_IER2_S 0x50032804u
#define GTZC1_TZIC_IER4_S 0x5003280Cu
#define GTZC1_TZIC_SR1_S 0x50032810u
#define GTZC1_TZIC_SR2_S 0x50032814u
#define GTZC1_TZIC_SR4_S 0x5003281Cu
#define GTZC1_TZIC_FCR1_S 0x50032820u
#define GTZC1_TZIC_FCR2_S 0x50032824u
#define GTZC1_TZIC_FCR4_S 0x5003282Cu
#define GTZC_CFGR1_USART2 (1u << 9)
#define GTZC_CFGR2_USART1 (1u << 3)
#define GTZC_IRQ 8u

/* Backup SRAM behind GTZC1 MPCWM4 (RM0456 Table 31: 32-byte granularity). Sub-region A makes
 * the lower 1 KB non-secure and unprivileged; the upper 1 KB stays secure privileged (the
 * default outside the sub-regions). TZSC MPCWM4ACFGR/AR: offsets 0x70/0x74. */
#define BKPSRAM_NS 0x40036400u
#define BKPSRAM_S 0x50036400u
#define BKPSRAM_SECURE_OFFSET 0x400u
#define GTZC1_TZSC_MPCWM4ACFGR_S 0x50032470u
#define GTZC1_TZSC_MPCWM4AR_S 0x50032474u
#define MPCWM_CFGR_SREN (1u << 0)
#define MPCWM4_NON_SECURE_LENGTH 32u
#define GTZC_CFGR4_BKPSRAM (1u << 18)

/* SYSCFG (stm32u575xx.h SYSCFG_BASE_NS/_S; RM0456 section 15.3); its illegal access event is
 * GTZC2 TZIC_SR2 bit 0. */
#define SYSCFG_NS 0x46000400u
#define SYSCFG_S 0x56000400u
#define SYSCFG_SECCFGR 0x00u
#define SYSCFG_CSLCKR 0x10u
#define SYSCFG_CFGR2 0x14u
#define SYSCFG_SECCFGR_CLASSBSEC (1u << 1)
#define SYSCFG_CSLCKR_LOCKSAU (1u << 2)
#define SYSCFG_CFGR2_CLL (1u << 0)
#define GTZC_CFGR2_SYSCFG (1u << 0)

/* GPIOC (stm32u575xx.h GPIOC_BASE_NS/_S): all pins are secure after reset; the secure image
 * makes PC7 non-secure (RM0456 section 13.3.18). */
#define GPIOC_MODER_NS 0x42020800u
#define GPIOC_SECCFGR_NS 0x42020830u
#define GPIOC_SECCFGR_S 0x52020830u
#define GPIO_PIN7 (1u << 7)

/* FLASH interface registers through the secure alias (stm32u575xx.h FLASH_R_BASE_S; RM0456
 * section 7.9) and its non-secure alias for the non-secure image. */
#define FLASH_S 0x50022000u
#define FLASH_NS 0x40022000u
#define FLASH_NSKEYR 0x08u
#define FLASH_SECKEYR 0x0Cu
#define FLASH_NSSR 0x20u
#define FLASH_SECSR 0x24u
#define FLASH_NSCR 0x28u
#define FLASH_SECCR 0x2Cu
#define FLASH_KEY1 0x45670123u
#define FLASH_KEY2 0xCDEF89ABu
#define FLASH_CR_PG (1u << 0)
#define FLASH_CR_PER (1u << 1)
#define FLASH_CR_PNB_POS 3u
#define FLASH_CR_BKER (1u << 11)
#define FLASH_CR_MER2 (1u << 15)
#define FLASH_CR_STRT (1u << 16)
#define FLASH_CR_LOCK (1u << 31)
#define FLASH_SR_WRPERR (1u << 4)
#define FLASH_SR_ERRORS 0xFAu
/* With the watermarks of the test platform, bank 1 is secure and bank 2 non-secure
 * (tests/common/stm32u575-trustzone-bank2-nonsecure.repl). Pages used by the flash tests:
 * secure bank 1 pages 120 and 121, non-secure bank 2 pages 10 and 11. */
#define FLASH_SECURE_PAGE_A 120u
#define FLASH_SECURE_PAGE_B 121u
#define FLASH_NON_SECURE_PAGE_A 10u
#define FLASH_NON_SECURE_PAGE_B 11u
#define FLASH_NON_SECURE_PAGE_A_S 0x0C114000u
/* GTZC1_TZIC_IER4 FLASH_REGIE (bit 1) and FLASHIE (bit 2). */
#define GTZC_CFGR4_FLASH_REG (1u << 1)
#define GTZC_CFGR4_FLASH (1u << 2)

/* RTC (stm32u575xx.h RTC_BASE_NS/_S; RM0456 section 63.6): RTC_ICSR, RTC_SECCFGR, RTC_WPR. */
#define RTC_ICSR_NS 0x4600780Cu
#define RTC_ICSR_S 0x5600780Cu
#define RTC_SECCFGR_S 0x56007820u
#define RTC_WPR_S 0x56007824u
#define RTC_ICSR_INIT (1u << 7)
#define RTC_SECCFGR_INITSEC (1u << 14)

/* RCC (stm32u575xx.h RCC_BASE_NS/_S; RM0456 section 11.8). */
#define RCC_NS 0x46020C00u
#define RCC_S 0x56020C00u
#define RCC_CR 0x000u
#define RCC_AHB2ENR1 0x08Cu
#define RCC_APB1ENR1 0x09Cu
#define RCC_APB2ENR 0x0A4u
#define RCC_SECCFGR 0x110u
#define RCC_PRIVCFGR 0x114u
#define RCC_CR_HSION (1u << 8)
#define RCC_CR_HSIRDY (1u << 10)
#define RCC_AHB2ENR1_GPIOCEN (1u << 2)
#define RCC_APB1ENR1_USART2EN (1u << 17)
#define RCC_APB2ENR_USART1EN (1u << 14)
#define RCC_SECCFGR_HSISEC (1u << 0)
#define RCC_PRIVCFGR_NSPRIV (1u << 1)
/* GTZC2 TZIC (stm32u575xx.h GTZC_TZIC2_BASE_S): RCC illegal accesses in IER2/SR2 bit 4. */
#define GTZC2_TZIC_IER2_S 0x56023404u
#define GTZC2_TZIC_SR2_S 0x56023414u
#define GTZC2_TZIC_FCR2_S 0x56023424u
#define GTZC_CFGR2_RCC (1u << 4)

/* PWR (stm32u575xx.h PWR_BASE_NS/_S; RM0456 section 10.10). */
#define PWR_NS 0x46020800u
#define PWR_S 0x56020800u
#define PWR_CR1 0x000u
#define PWR_VOSR 0x00Cu
#define PWR_WUCR1 0x014u
#define PWR_SECCFGR 0x030u
#define PWR_PUCRC 0x060u
#define PWR_CR1_LPMS_STOP2 0x2u
#define PWR_VOSR_VOSRDY (1u << 15)
#define PWR_VOSR_VOS_RANGE1 (3u << 16)
#define PWR_WUCR1_WUPEN1 (1u << 0)
#define PWR_WUCR1_WUPEN2 (1u << 1)
#define PWR_SECCFGR_WUP1SEC (1u << 0)
#define PWR_SECCFGR_LPMSEC (1u << 12)
#define RCC_AHB3ENR 0x094u
#define RCC_AHB3ENR_PWREN (1u << 2)
#define RCC_SECCFGR_SYSCLKSEC (1u << 5)
#define GTZC_CFGR2_PWR (1u << 3)

/* EXTI (stm32u575xx.h EXTI_BASE_NS/_S; RM0456 section 23.6). Line 13 is secure, line 5 not. */
#define EXTI_NS 0x46022000u
#define EXTI_S 0x56022000u
#define EXTI_RTSR1 0x000u
#define EXTI_SECCFGR1 0x014u
#define EXTI_EXTICR4 0x06Cu
#define EXTI_LOCKR 0x070u
#define EXTI_IMR1 0x080u
#define EXTI_LINE5 (1u << 5)
#define EXTI_LINE13 (1u << 13)
#define EXTI_EXTICR4_LINE13_PORTC (0x02u << 8)
#define GTZC_CFGR2_EXTI (1u << 6)

/* GPDMA1 (stm32u575xx.h GPDMA1_BASE_NS/_S; RM0456 section 17.8). Channel x registers at
 * 0x50 + 0x80 * x: CxCR 0x14, CxTR1 0x40, CxTR2 0x44, CxBR1 0x48, CxSAR 0x4C, CxDAR 0x50,
 * CxLLR 0x7C. */
#define GPDMA_NS 0x40020000u
#define GPDMA_S 0x50020000u
#define GPDMA_SECCFGR 0x000u
#define GPDMA_PRIVCFGR 0x004u
#define GPDMA_CHANNEL(x) (0x50u + 0x80u * (x))
#define GPDMA_CXCR 0x14u
#define GPDMA_CXTR1 0x40u
#define GPDMA_CXTR2 0x44u
#define GPDMA_CXBR1 0x48u
#define GPDMA_CXSAR 0x4Cu
#define GPDMA_CXDAR 0x50u
#define GPDMA_CXLLR 0x7Cu
#define GPDMA_CXCR_EN (1u << 0)
/* 32-bit source and destination, both incremented. */
#define GPDMA_CXTR1_WORDS ((2u << 0) | (1u << 3) | (2u << 16) | (1u << 19))
#define GPDMA_CXTR1_SSEC (1u << 15)
#define GPDMA_CXTR1_DSEC (1u << 31)
#define GPDMA_CXTR2_SWREQ (1u << 9)
#define GPDMA_SECURE_CHANNEL 1u
#define GPDMA_NON_SECURE_CHANNEL 2u
#define GPDMA_UNPRIVILEGED_CHANNEL 3u
/* Destination of the non-secure channel: a non-secure SRAM3 block. */
#define GPDMA_NON_SECURE_DESTINATION_NS 0x20080100u
#define GTZC_CFGR4_GPDMA1 (1u << 0)

uint32_t secureIncrement(uint32_t value);

#endif
