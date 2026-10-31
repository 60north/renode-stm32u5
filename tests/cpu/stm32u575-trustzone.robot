*** Settings ***
Documentation       STM32U575 with TrustZone enabled (platforms/cpus/stm32u575-trustzone.repl): secure
...                 boot from SECBOOTADD0, IDAU and SAU attribution, a call from non-secure code through a
...                 non-secure callable veneer, a SecureFault on a non-secure access to secure SRAM, the
...                 secure aliases of memories and peripherals, and the GTZC firewall of securable
...                 peripherals, the flash memory interfaces (RM0456 Rev 7 sections 2.2, 2.3, 3.5, 5
...                 and 7.5). Platform: the TrustZone platform with flash bank 2 non-secure
...                 (tests/common/stm32u575-trustzone-bank2-nonsecure.repl).
...                 Firmware: firmware-tests/baremetal/trustzone (build with CMake before running).
Library             String
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Create STM32U575 Machine    ${TRUSTZONE_PLATFORM}

*** Variables ***
${TRUSTZONE_PLATFORM}    ${ROOT}/tests/common/stm32u575-trustzone-bank2-nonsecure.repl
${SECURE_ELF}       ${ROOT}/build/firmware/trustzone-secure.elf
${NONSECURE_ELF}    ${ROOT}/build/firmware/trustzone-nonsecure.elf
${FLASH_OPTR_S}     0x50022040
${FLASH_OPTR_NS}    0x40022040
${TZEN}             ${{1 << 31}}
# Secure SRAM window marked non-secure by the SAU; the IDAU keeps it non-secure callable.
${SECURE_RAM_WINDOW}    0x30010000
# SCB_SFSR (Armv8-M): AUVIOL (bit 3), attribution unit violation, and SFARVALID (bit 6).
${SFSR_AUVIOL_SFARVALID}    ${{(1 << 3) | (1 << 6)}}
${USART1_BRR}       0x4001380C
${GTZC_CFGR2_USART1}    ${{1 << 3}}
# GTZC1 MPCBB1 and MPCBB3 (non-secure aliases), SECCFGRx at 0x100 + 4x, PRIVCFGRx at 0x200 + 4x.
${MPCBB1_SECCFGR0}      0x40032D00
${MPCBB1_PRIVCFGR0}     0x40032E00
${MPCBB3_SECCFGR0}      0x40033500
${MPCBB3_SECCFGR31}     0x4003357C
${SECURE_SRAM3_BLOCK}   0x200BC000
${NON_SECURE_SRAM3_WORD}    0x20080000
${GTZC_CFGR4_SRAM3}     ${{1 << 28}}
${GPIOA_SECCFGR}     0x42020030
${GPIOC_MODER}       0x42020800
${GPIOC_SECCFGR}     0x42020830
${GPIOI_SECCFGR}     0x42022030
${GPIOA_MODER_NS}   0x42020000
${GPIOA_MODER_S}    0x52020000
# Marker words at 0x100 in the flash pages of the flash test (trustzone.h): bank 1 (secure)
# pages 120 and 121, bank 2 (non-secure) pages 10 and 11.
${SECURE_PAGE_A_MARKER}        0x080F0100
${SECURE_PAGE_B_MARKER}        0x080F2100
${NON_SECURE_PAGE_A_MARKER}    0x08114100
${NON_SECURE_PAGE_B_MARKER}    0x08116100
${FLASH_MARKER}     0x12345678
${RTC_ICSR}         0x4600780C
${RCC}              0x46020C00
${RCC_CR_HSION}     ${{1 << 8}}
${RCC_APB2ENR_USART1EN}    ${{1 << 14}}
${RCC_AHB2ENR1_GPIOCEN}    ${{1 << 2}}
${RCC_APB1ENR1_USART2EN}    ${{1 << 17}}
${GTZC_CFGR2_RCC}   ${{1 << 4}}
${GTZC_CFGR2_PWR}   ${{1 << 3}}
${PWR}              0x46020800
${EXTI}             0x46022000
${GPDMA}            0x40020000
${GPDMA_DESTINATION}    0x20080100
${GTZC_CFGR4_GPDMA1}    ${{1 << 0}}
${GTZC_CFGR2_EXTI}  ${{1 << 6}}
${GTZC_CFGR2_SYSCFG}    ${{1 << 0}}
${GTZC_CFGR4_BKPSRAM}   ${{1 << 18}}
${BKPSRAM}          0x40036400
${SYSCFG}           0x46000400
${RTC_ICSR_INITF}   ${{1 << 6}}
${FLASH_SR_WRPERR}  ${{1 << 4}}
# GTZC1_TZIC_SR4: FLASH_REG (bit 1) and FLASH (bit 2).
${GTZC_CFGR4_FLASH_REG}    ${{1 << 1}}
${GTZC_CFGR4_FLASH}        ${{1 << 2}}

*** Keywords ***
Load TrustZone Firmware
    # The non-secure image first, so that the secure image sets the start-up state.
    Execute Command                 sysbus LoadELF @${NONSECURE_ELF}
    Execute Command                 sysbus LoadELF @${SECURE_ELF}

Read Symbol
    [Arguments]                     ${symbol}
    ${address}=                     Execute Command    sysbus GetSymbolAddress "${symbol}"
    ${value}=                       Read Word          ${address}
    RETURN                          ${value}

Symbol Should Be
    [Arguments]                     ${symbol}    ${expected}
    ${value}=                       Read Symbol        ${symbol}
    Should Be Equal As Integers     ${value}           ${expected}    msg=${symbol}    values=False

*** Test Cases ***
Should Report TrustZone Enabled In FLASH_OPTR Through Both Aliases
    Bits Should Be Set              ${FLASH_OPTR_S}    ${TZEN}
    Bits Should Be Set              ${FLASH_OPTR_NS}   ${TZEN}

Should Reach One Peripheral Through Its Secure And Non-Secure Alias
    Write Word                      ${GPIOA_MODER_S}   0x00000001
    Word Should Be                  ${GPIOA_MODER_NS}  0x00000001

Should Boot Secure And Call A Secure Function From Non-Secure Code
    Load TrustZone Firmware
    Execute Command                 emulation RunFor "0.01"
    Symbol Should Be                nonSecureStarted    1
    Symbol Should Be                secureCalls         1
    Symbol Should Be                nonSecureResult     42

Should Keep A Secure Alias Secure For Data Although The SAU Marks It Non-Secure
    Load TrustZone Firmware
    Execute Command                 emulation RunFor "0.01"
    Symbol Should Be                secureFaults        1
    Symbol Should Be                secureFaultAddress  ${SECURE_RAM_WINDOW}
    ${status}=                      Read Symbol        secureFaultStatus
    ${flags}=                       Evaluate           ${status} & ${SFSR_AUVIOL_SFARVALID}
    Should Be Equal As Integers     ${flags}           ${SFSR_AUVIOL_SFARVALID}    msg=SFSR ${status}

Should Keep The IDAU Attribution After A Machine Reset
    Execute Command                 machine Reset
    Load TrustZone Firmware
    Execute Command                 emulation RunFor "0.01"
    Symbol Should Be                secureCalls         1
    Symbol Should Be                secureFaults        1
    Symbol Should Be                secureFaultAddress  ${SECURE_RAM_WINDOW}

Should Block A Secure Peripheral For Non-Secure Code With An Illegal Access Interrupt
    Load TrustZone Firmware
    Execute Command                 emulation RunFor "0.01"
    # USART1 is secure (GTZC1_TZSC_SECCFGR2): the non-secure write is ignored, the read is zero,
    # and the illegal accesses raise the enabled GTZC_IRQn. Two back-to-back events may set the
    # flag before the interrupt is taken, so only one interrupt is required.
    Symbol Should Be                nonSecureSecurePeripheralRead    0
    Word Should Be                  ${USART1_BRR}      0x5678
    ${interrupts}=                  Read Symbol        gtzcInterrupts
    Should Be True                  ${interrupts} >= 1    msg=gtzcInterrupts ${interrupts}
    Symbol Should Be                gtzcStatus         ${GTZC_CFGR2_USART1}

Should Read A Privileged Peripheral As Zero From Unprivileged Code Without An Event
    Load TrustZone Firmware
    Execute Command                 emulation RunFor "0.01"
    # USART2 is privileged (GTZC1_TZSC_PRIVCFGR1); its illegal access event is enabled too.
    Symbol Should Be                nonSecurePrivilegedRead    0x2222
    Symbol Should Be                nonSecureUnprivilegedRead    0
    Symbol Should Be                gtzcPrivilegeStatus    0

Should Start With Every SRAM Block Secure And Privileged
    # RM0456 section 5.4.6, TZEN = 1: MPCBBz_SECCFGRx and PRIVCFGRx are 0xFFFF FFFF.
    Word Should Be                  ${MPCBB1_SECCFGR0}     0xFFFFFFFF
    Word Should Be                  ${MPCBB1_PRIVCFGR0}    0xFFFFFFFF
    Word Should Be                  ${MPCBB3_SECCFGR31}    0xFFFFFFFF

Should Block A Non-Secure Read Of A Secure SRAM Block
    Load TrustZone Firmware
    Execute Command                 emulation RunFor "0.01"
    # The last SRAM3 super-block stays secure in MPCBB3; the SAU marks it non-secure.
    Symbol Should Be                nonSecureSecureBlockRead    0
    Word Should Be                  ${SECURE_SRAM3_BLOCK}    0x5EC0
    ${status}=                      Read Symbol        gtzcMemoryStatus
    Should Be True                  ${status} & ${GTZC_CFGR4_SRAM3}    msg=gtzcMemoryStatus ${status}
    Word Should Be                  ${MPCBB3_SECCFGR31}    0xFFFFFFFF
    Word Should Be                  ${MPCBB3_SECCFGR0}     0x00000000

Should Block A Secure Data Access To A Non-Secure SRAM Block
    Load TrustZone Firmware
    Execute Command                 emulation RunFor "0.01"
    # MPCBB3_CR.SRWILADIS is cleared: a secure read of a non-secure block is illegal.
    Symbol Should Be                secureReadNonSecureBlock    0
    Word Should Be                  ${NON_SECURE_SRAM3_WORD}    0xABCD

Should Start With Every GPIO Pin Secure
    # RM0456 section 13.4.13: GPIOx_SECCFGR 0x0000 FFFF, port I 0x0000 00FF on STM32U575.
    Word Should Be                  ${GPIOA_SECCFGR}    0x0000FFFF
    Word Should Be                  ${GPIOI_SECCFGR}    0x000000FF

Should Hide The Secure GPIO Pins From Non-Secure Code
    Load TrustZone Firmware
    Execute Command                 emulation RunFor "0.01"
    # Only PC7 is non-secure: its MODER field becomes output (01), the others keep the reset
    # value (11); the non-secure read shows the PC7 field only.
    Word Should Be                  ${GPIOC_MODER}      0xFFFF7FFF
    Symbol Should Be                nonSecureGpioMode    0x00004000
    Word Should Be                  ${GPIOC_SECCFGR}    0x0000FF7F

Should Erase And Program Only The Flash Pages Of The Security Of The Interface
    Load TrustZone Firmware
    FOR    ${marker}    IN    ${SECURE_PAGE_A_MARKER}    ${SECURE_PAGE_B_MARKER}    ${NON_SECURE_PAGE_A_MARKER}    ${NON_SECURE_PAGE_B_MARKER}
        Execute Command             sysbus WriteDoubleWord ${marker} ${FLASH_MARKER}
    END
    Execute Command                 emulation RunFor "0.01"
    # RM0456 Table 68: a page erase of the other security is ignored and sets WRPERR.
    Symbol Should Be                flashNonSecureEraseSecurePage       ${FLASH_SR_WRPERR}
    Word Should Be                  ${SECURE_PAGE_A_MARKER}             ${FLASH_MARKER}
    Symbol Should Be                flashSecureEraseSecurePage          0
    Word Should Be                  ${SECURE_PAGE_B_MARKER}             0xFFFFFFFF
    Symbol Should Be                flashSecureEraseNonSecurePage       ${FLASH_SR_WRPERR}
    Word Should Be                  ${NON_SECURE_PAGE_A_MARKER}         ${FLASH_MARKER}
    Symbol Should Be                flashNonSecureEraseNonSecurePage    0
    Word Should Be                  ${NON_SECURE_PAGE_B_MARKER}         0xFFFFFFFF
    # Table 70: a secure erase of the non-secure bank 2 is ignored; the non-secure image runs.
    Symbol Should Be                flashSecureEraseNonSecureBank       ${FLASH_SR_WRPERR}
    Symbol Should Be                nonSecureStarted                    1
    Symbol Should Be                flashSecureProgramNonSecurePage     ${FLASH_SR_WRPERR}
    # Tables 68 and 70: the page erases report no event, the bank erase and the programming a
    # flash illegal access (TZIC_SR4.FLASH).
    ${events}=                      Execute Command    sysbus GetSymbolAddress "flashEvents"
    ${events}=                      Strip String       ${events}
    FOR    ${index}    ${expected}    IN    0    0    1    0    2    0    3    0    4    ${GTZC_CFGR4_FLASH}    5    ${GTZC_CFGR4_FLASH}
        ${address}=                 Evaluate           ${events} + 4 * ${index}
        Word Should Be              ${address}         ${expected}
    END
    # FLASH_SECCR (reset 0x8000 0000) is RAZ for non-secure code, with a register illegal
    # access (TZIC_SR4.FLASH_REG) after the secure image cleared the flash events.
    Symbol Should Be                nonSecureFlashSecureControl         0
    Symbol Should Be                gtzcFlashStatus                     ${GTZC_CFGR4_FLASH_REG}

Should Keep The Secure RTC Initialization Mode On A Non-Secure RTC_ICSR Write
    Load TrustZone Firmware
    Execute Command                 emulation RunFor "0.01"
    # RM0456 63.3.4: with INITSEC, INIT is written by secure accesses only. The secure image
    # set INIT, the non-secure image wrote 0 to RTC_ICSR: INITF (bit 6) stays set.
    Bits Should Be Set              ${RTC_ICSR}        ${RTC_ICSR_INITF}

Should Hide The Secure RCC Bits From Non-Secure Code
    Load TrustZone Firmware
    Execute Command                 emulation RunFor "0.01"
    # RM0456 section 11.5.1: HSION/HSIRDY (HSISEC), USART1EN (USART1 secure in the TZSC) and
    # GPIOCEN (GPIOC has secure pins) read as 0 for the non-secure image and keep their value
    # after its writes, without an event; its RCC_SECCFGR write is ignored with an event.
    Symbol Should Be                nonSecureRccControl     0
    Bits Should Be Set              ${RCC} + 0x000          ${RCC_CR_HSION}
    Symbol Should Be                nonSecureRccApb2        0
    Bits Should Be Set              ${RCC} + 0x0A4          ${RCC_APB2ENR_USART1EN}
    Symbol Should Be                nonSecureRccAhb2        0
    Bits Should Be Set              ${RCC} + 0x08C          ${RCC_AHB2ENR1_GPIOCEN}
    # HSISEC and SYSCLKSEC.
    Word Should Be                  ${RCC} + 0x110          0x00000021
    ${status}=                      Read Symbol             gtzc2Status
    Should Be True                  ${status} & ${GTZC_CFGR2_RCC}    msg=gtzc2Status ${status}
    # Section 11.5.2: with NSPRIV the non-secure bits read as 0 for unprivileged code only.
    Symbol Should Be                nonSecureRccApb1Privileged      ${RCC_APB1ENR1_USART2EN}
    Symbol Should Be                nonSecureRccApb1Unprivileged    0

Should Hide The Secure PWR Bits From Non-Secure Code
    Load TrustZone Firmware
    Execute Command                 emulation RunFor "0.01"
    # RM0456 Table 109: PWR_CR1 (LPMSEC) and WUPEN1 (WUP1SEC) read as 0 for the non-secure
    # image and keep their value after its writes; WUPEN2 is non-secure and cleared.
    Symbol Should Be                nonSecurePwrControl     0
    Word Should Be                  ${PWR} + 0x000          0x00000002
    Symbol Should Be                nonSecurePwrWakeup      2
    Word Should Be                  ${PWR} + 0x014          0x00000001
    # PWR_PUCRC follows the GPIOC secure pins: only the PC7 pull-up is visible.
    Symbol Should Be                nonSecurePwrPullUp      0x00000080
    # VOS follows RCC SYSCLKSEC; VOSRDY stays readable.
    Symbol Should Be                nonSecurePwrVoltage     0x00008000
    # PWREN is secure in the RCC because a PWR function is secure.
    Symbol Should Be                nonSecureRccPwrEnable   0
    # The non-secure PWR_SECCFGR write is ignored and illegal.
    Word Should Be                  ${PWR} + 0x030          0x00001001
    ${status}=                      Read Symbol             gtzc2Status
    Should Be True                  ${status} & ${GTZC_CFGR2_PWR}    msg=gtzc2Status ${status}

Should Hide The Secure EXTI Lines From Non-Secure Code
    Load TrustZone Firmware
    Execute Command                 emulation RunFor "0.01"
    # RM0456 section 23.5.1: line 13 is secure. Its EXTI_IMR1 bit and EXTI_EXTICR4 field read
    # as 0 for the non-secure image and keep their value after its writes; line 5 is non-secure
    # and its mask bit is cleared.
    Symbol Should Be                nonSecureExtiMask       0x00000020
    Word Should Be                  ${EXTI} + 0x080         0x00002000
    Symbol Should Be                nonSecureExtiSelection  0
    Word Should Be                  ${EXTI} + 0x06C         0x00000200
    # EXTI_SECCFGR1 reads back; LOCK kept the secure write of 0 and the non-secure write from
    # changing it. EXTI_LOCKR reads 0 for non-secure code; both accesses are illegal.
    Symbol Should Be                nonSecureExtiSecurity   0x00002000
    Word Should Be                  ${EXTI} + 0x014         0x00002000
    Symbol Should Be                nonSecureExtiLock       0
    ${status}=                      Read Symbol             gtzc2Status
    Should Be True                  ${status} & ${GTZC_CFGR2_EXTI}    msg=gtzc2Status ${status}

Should Transfer With The Security Of The GPDMA Channel
    Load TrustZone Firmware
    Execute Command                 emulation RunFor "0.01"
    # RM0456 section 17.4.14: channel 1 is secure. With SSEC and DSEC it copies the secure
    # buffer; with a non-secure source the secure SRAM3 super-block reads as 0 (MPCBB3).
    ${copy}=                        Execute Command    sysbus GetSymbolAddress "dmaSecureCopy"
    ${copy}=                        Strip String       ${copy}
    Word Should Be                  ${copy}            0x11111111
    Word Should Be                  ${copy} + 12       0x44444444
    ${blocked}=                     Execute Command    sysbus GetSymbolAddress "dmaNonSecureSourceCopy"
    ${blocked}=                     Strip String       ${blocked}
    Word Should Be                  ${blocked}         0
    # Channel 2 is non-secure: started by secure code, it still reads the secure buffer as 0.
    Word Should Be                  ${GPDMA_DESTINATION}    0
    # Channel 3 is secure but unprivileged (17.4.15): the privileged SRAM1 blocks refuse it and
    # the destination keeps its value.
    ${unprivileged}=                Execute Command    sysbus GetSymbolAddress "dmaUnprivilegedCopy"
    ${unprivileged}=                Strip String       ${unprivileged}
    Word Should Be                  ${unprivileged}    0xFFFFFFFF
    # The non-secure image reads the secure channel registers as 0 and cannot clear SECCFGR;
    # both accesses are illegal.
    Symbol Should Be                nonSecureDmaSecureChannelSource    0
    Symbol Should Be                nonSecureDmaSecurity    0x0000000A
    Word Should Be                  ${GPDMA} + 0x000        0x0000000A
    ${status}=                      Read Symbol             gtzcMemoryStatus
    Should Be True                  ${status} & ${GTZC_CFGR4_GPDMA1}    msg=gtzcMemoryStatus ${status}

Should Protect The Backup SRAM With MPCWM4
    Load TrustZone Firmware
    Execute Command                 emulation RunFor "0.01"
    # RM0456 sections 5.4.2 and 5.6.8: sub-region A makes the lower 1 KB of BKPSRAM non-secure;
    # the rest stays secure privileged. The secure read of the non-secure part is illegal (0);
    # the non-secure image writes and reads its part, and reads the secure part as 0, illegal.
    Symbol Should Be                secureReadNonSecureBackupSram    0
    Symbol Should Be                secureBackupSramEvent            ${GTZC_CFGR4_BKPSRAM}
    Symbol Should Be                nonSecureBackupSram              0x4B4B
    Symbol Should Be                nonSecureSecureBackupSram        0
    Word Should Be                  ${BKPSRAM} + 0x400               0xB5B5
    ${status}=                      Read Symbol                      gtzcMemoryStatus
    Should Be True                  ${status} & ${GTZC_CFGR4_BKPSRAM}    msg=gtzcMemoryStatus ${status}

Should Apply The SYSCFG Secure Configuration To Non-Secure Code
    Load TrustZone Firmware
    Execute Command                 emulation RunFor "0.01"
    # RM0456 Table 131: SYSCFG_SECCFGR reads back for non-secure code and keeps the secure
    # value after the non-secure write; SYSCFG_CSLCKR reads 0; with CLASSBSEC the non-secure
    # SYSCFG_CFGR2 write is ignored. The non-secure accesses raise the SYSCFG event.
    Symbol Should Be                nonSecureSyscfgSecurity          0x2
    Word Should Be                  ${SYSCFG}                        0x2
    Symbol Should Be                nonSecureSyscfgSecureLock        0
    Word Should Be                  ${SYSCFG} + 0x10                 0x4
    Word Should Be                  ${SYSCFG} + 0x14                 0
    ${status}=                      Read Symbol                      gtzc2Status
    Should Be True                  ${status} & ${GTZC_CFGR2_SYSCFG}    msg=gtzc2Status ${status}
