*** Settings ***
Documentation       STM32U5 RTC and TAMP register behaviour without firmware (RM0456 Rev 7 sections 63
...                 and 64).
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Create Halted Machine

*** Variables ***
${RTC}              0x46007800
${TAMP}             0x46007C00
${TAMP_BKP4R}       0x110
${PWR_DBPR}         0x46020828
${RCC_BDCR}         0x46020CF0

*** Keywords ***
Create Halted Machine
    Create STM32U575 Machine
    # No firmware: the core is halted.
    Execute Command                 cpu IsHalted true

*** Test Cases ***
Should Count The Calendar From Reset
    # The calendar counter is stopped only in initialization mode (RM0456 63.3.11).
    Word Should Be                  ${RTC} + 0x00      0x00000000
    Execute Command                 emulation RunFor "2.1"
    Word Should Be                  ${RTC} + 0x00      0x00000002

Should Keep Backup Registers Over A System Reset
    # TAMP_BKPxR are not reset by a system reset (RM0456 64.4.10).
    Write Word                      ${TAMP} + ${TAMP_BKP4R}    0xA55AA55A
    Execute Command                 machine Reset
    Execute Command                 cpu IsHalted true
    Word Should Be                  ${TAMP} + ${TAMP_BKP4R}    0xA55AA55A

Should Keep The Backup Domain Over A System Reset
    # RTC registers and RCC_BDCR are not affected by a system reset (RM0456 63.6, 11.8.49); the
    # RTC write protection is enabled again (RTC_WPR reset value 0, 63.6.10).
    Write Word                      ${PWR_DBPR}        1
    # LSEON, RTCSEL = LSE, RTCEN.
    Write Word                      ${RCC_BDCR}        0x00008101
    Write Word                      ${RTC} + 0x24      0xCA
    Write Word                      ${RTC} + 0x24      0x53
    Execute Command                 emulation RunFor "2.1"
    Execute Command                 machine Reset
    Execute Command                 cpu IsHalted true
    Word Should Be                  ${RCC_BDCR}        0x00008101
    Word Should Be                  ${RTC} + 0x00      0x00000002
    Execute Command                 emulation RunFor "1"
    Word Should Be                  ${RTC} + 0x00      0x00000003
    # Locked again: INIT is ignored without the key, INITF stays 0.
    Write Word                      ${RTC} + 0x0C      0x80
    Bits Should Be Clear            ${RTC} + 0x0C      0x40
