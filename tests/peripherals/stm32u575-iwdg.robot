*** Settings ***
Documentation       STM32U5 independent watchdog without firmware (RM0456 Rev 7 section 61) clocked by
...                 the 32 kHz LSI.
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Start Watchdog

*** Variables ***
${IWDG}             0x40003000
${RESET_LOG}        Watchdog reset triggered!

*** Keywords ***
Start Watchdog
    Create STM32U575 Machine
    # No firmware: the core is halted.
    Execute Command                 cpu IsHalted true
    Create Log Tester               0
    # Key sequence of HAL_IWDG_Init: start, unlock, PR = 0 (/4: 8 kHz), RLR = 79 (10 ms), reload.
    Write Word                      ${IWDG} + 0x00     0xCCCC
    Write Word                      ${IWDG} + 0x00     0x5555
    Write Word                      ${IWDG} + 0x04     0
    Write Word                      ${IWDG} + 0x08     79
    Write Word                      ${IWDG} + 0x00     0xAAAA

*** Test Cases ***
Should Reset The Machine When Not Reloaded
    Wait For Log Entry              ${RESET_LOG}       timeout=0.1

Should Not Reset While Reloaded
    FOR    ${i}    IN RANGE    10
        Execute Command             emulation RunFor "0.005"
        Write Word                  ${IWDG} + 0x00     0xAAAA
    END
    Should Not Be In Log            ${RESET_LOG}

Should Divide The LSI By 1024 With PR = 8
    # PR = 1000: divider 1024 (RM0456 61.7.2), 31.25 Hz; RLR = 3: about 96 ms.
    Write Word                      ${IWDG} + 0x00     0x5555
    Write Word                      ${IWDG} + 0x04     8
    Write Word                      ${IWDG} + 0x08     3
    Write Word                      ${IWDG} + 0x00     0xAAAA
    Execute Command                 emulation RunFor "0.05"
    Should Not Be In Log            ${RESET_LOG}
    Wait For Log Entry              ${RESET_LOG}       timeout=0.1

Should Store The Early Wake-Up Comparator
    # IWDG_EWCR as written by HAL_IWDG_Init with the early wake-up interrupt disabled.
    Write Word                      ${IWDG} + 0x14     0x4000
    Word Should Be                  ${IWDG} + 0x14     0x0000
    Write Word                      ${IWDG} + 0x14     0x0123
    Word Should Be                  ${IWDG} + 0x14     0x0123
