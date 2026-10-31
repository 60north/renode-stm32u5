*** Settings ***
Documentation       Official STM32CubeU5 v1.9.0 TrustZone projects for NUCLEO-U575ZI-Q, built from
...                 unmodified ST sources as a secure and a non-secure image
...                 (firmware-tests/cmake/st-examples.cmake, add_st_trustzone_example), on the board
...                 description platforms/boards/nucleo_u575zi_q-trustzone.repl (TZEN = 1). The pass
...                 criteria are those of the project readme files.
Library             String
Resource            ${CURDIR}/../common/stm32u575.resource

*** Variables ***
${BOARD}            ${ROOT}/platforms/boards/nucleo_u575zi_q-trustzone.repl
${FIRMWARE}         ${ROOT}/build/firmware
${LED1}             sysbus.gpioPortC.led1
${LED2}             sysbus.gpioPortB.led2
${LED3}             sysbus.gpioPortG.led3
${USER_BUTTON}      sysbus.gpioPortC.userButton
${UNMODELLED_ACCESS}    Unhandled (read|write)|non existing peripheral

*** Keywords ***
Run ST TrustZone Project
    [Arguments]                     ${name}
    Create STM32U575 Machine        ${BOARD}
    Create Log Tester               0
    # The non-secure image first, so that the secure image sets the start-up state.
    Execute Command                 sysbus LoadELF @${FIRMWARE}/${name}-nonsecure.elf
    Execute Command                 sysbus LoadELF @${FIRMWARE}/${name}-secure.elf
    # The projects wait in HAL_Delay and status loops. Fewer instructions per virtual second
    # (default 100 MIPS) cut the host time about 15 times on the ArrayMemory SRAMs; SysTick,
    # RTC and LED timing are unchanged.
    Execute Command                 cpu PerformanceInMips 10

*** Test Cases ***
TrustZoneEnabled Template Should Blink LED1 From Non-Secure Code
    # Secure: SAU and interrupt targets from partition_stm32u575xx.h, MPCBB3, PC7 made
    # non-secure, TZIC events enabled, then the non-secure image. Non-secure: system clock and
    # LED1 (PC7) toggled every 250 ms. With the TrustZone GPIO model, LED1 blinks only if the
    # secure image made PC7 non-secure.
    Run ST TrustZone Project        st-trustzone-template
    ${led1}=                        Create LED Tester    ${LED1}    defaultTimeout=1
    Assert LED Is Blinking          testDuration=1    onDuration=0.25    offDuration=0.25    tolerance=0.05    testerId=${led1}
    Should Not Be In Log            ${UNMODELLED_ACCESS}    treatAsRegex=true

GTZC_TZSC_MPCBB_TrustZone Should Detect A Non-Secure Access To Secure SRAM1
    # Secure: SRAM3 non-secure through MPCBB3, TZIC events enabled. Non-secure: LED1 toggles
    # every 1000 ms; a USER button press reads SRAM1, which stays secure in MPCBB1. The MPCBB
    # illegal access raises GTZC_IRQn in the secure image, which reports it to the non-secure
    # image: LED1 stays on, longer than a toggle period.
    Run ST TrustZone Project        st-gtzc-tzsc-mpcbb
    ${led1}=                        Create LED Tester    ${LED1}    defaultTimeout=1
    Execute Command                 emulation RunFor "0.5"
    Assert LED State                true     timeout=0    testerId=${led1}
    Execute Command                 emulation RunFor "1"
    Assert LED State                false    timeout=0    testerId=${led1}
    Execute Command                 ${USER_BUTTON} Press
    Execute Command                 emulation RunFor "0.1"
    Execute Command                 ${USER_BUTTON} Release
    Assert And Hold LED State       true    timeoutAssert=0.1    timeoutHold=1.2    testerId=${led1}
    Should Not Be In Log            ${UNMODELLED_ACCESS}    treatAsRegex=true

GPIO_IOToggle_TrustZone Should Toggle A Secure And A Non-Secure LED From Two SysTicks
    # Secure: PC7 (LED1) stays secure and toggles every 1000 ms on the secure SysTick.
    # Non-secure: PB7 (LED2) toggles every 500 ms on the non-secure SysTick. The states are
    # sampled in the middle of the expected phases.
    Run ST TrustZone Project        st-gpio-iotoggle-trustzone
    ${led1}=                        Create LED Tester    ${LED1}    defaultTimeout=1
    ${led2}=                        Create LED Tester    ${LED2}    defaultTimeout=1
    Execute Command                 emulation RunFor "0.5"
    Assert LED State                false    timeout=0    testerId=${led1}
    Execute Command                 emulation RunFor "0.375"
    Assert LED State                true     timeout=0    testerId=${led2}
    Execute Command                 emulation RunFor "0.5"
    Assert LED State                false    timeout=0    testerId=${led2}
    Execute Command                 emulation RunFor "0.125"
    Assert LED State                true     timeout=0    testerId=${led1}
    Execute Command                 emulation RunFor "0.375"
    Assert LED State                true     timeout=0    testerId=${led2}
    Should Not Be In Log            ${UNMODELLED_ACCESS}    treatAsRegex=true

RTC_TrustZone Should Keep The Secure RTC Features From Non-Secure Code
    # Secure: every RTC feature secure except alarm B, backup register zones from DR10 and DR20;
    # alarm A at 02:20:05 and the wake-up timer (0.2 s) on RTC_S_IRQn. Non-secure: backup
    # registers of zone 1 read as 0 and ignore writes, zone 2 ignores writes, zone 3 is open,
    # each denied access counted once through GTZC_IRQn; deactivating alarm A is denied; alarm B
    # at 02:20:06 on RTC_IRQn. LED1 goes on when the non-secure image saw alarm B and the secure
    # image saw alarm A and more than 25 wake-ups; LED3 goes on at the first wrong count.
    Run ST TrustZone Project        st-rtc-trustzone
    ${led1}=                        Create LED Tester    ${LED1}    defaultTimeout=0
    ${led3}=                        Create LED Tester    ${LED3}    defaultTimeout=0
    Execute Command                 emulation RunFor "1"
    Assert LED State                false    testerId=${led3}
    Assert LED State                false    testerId=${led1}
    Execute Command                 emulation RunFor "6"
    Assert LED State                true     testerId=${led1}
    Assert LED State                false    testerId=${led3}
    Should Not Be In Log            ${UNMODELLED_ACCESS}    treatAsRegex=true

FLASH_EraseProgram_TrustZone Should Erase And Program Secure And Non-Secure Pages
    # Secure: erases and programs bank 1 pages 4-127 (secure) through FLASH_SECCR, then bank 2
    # page 6 (non-secure) through FLASH_NSCR; LED1 on when the read-back matches, LED3 on
    # otherwise. Non-secure: erases and programs bank 2 page 6 again; its LEDs stay secure, so
    # its result is the final loop of main (a branch to itself) instead of the error loop. Zero
    # words in pages 4 and 126 make a missing erase fail with PROGERR.
    Run ST TrustZone Project        st-flash-erase-program-trustzone
    Execute Command                 sysbus WriteDoubleWord 0x08008000 0
    Execute Command                 sysbus WriteDoubleWord 0x080FC000 0
    ${led1}=                        Create LED Tester    ${LED1}    defaultTimeout=0
    ${led3}=                        Create LED Tester    ${LED3}    defaultTimeout=0
    Execute Command                 emulation RunFor "5"
    Assert LED State                true     testerId=${led1}
    Assert LED State                false    testerId=${led3}
    ${pc}=                          Execute Command    cpu PC
    ${pc}=                          Strip String       ${pc}
    Should Be True                  0x08100000 <= ${pc} < 0x08200000    msg=PC ${pc} outside the non-secure image
    ${instruction}=                 Execute Command    sysbus ReadWord ${pc}
    ${instruction}=                 Strip String       ${instruction}
    Should Be Equal As Integers     ${instruction}    0xE7FE    msg=not the final loop at ${pc}
    Should Not Be In Log            ${UNMODELLED_ACCESS}    treatAsRegex=true

Tx_SecureLEDToggle_TrustZone Should Toggle The Green LED From A ThreadX Thread
    # Non-secure ThreadX thread (TIM6 time base, secure stacks of the Cortex-M33 port) calls a
    # secure non-secure-callable function that toggles LED_GREEN (LED1), then sleeps 1 s.
    # Success: LED1 toggles every 1 s; error: LED_RED (LED3) toggles.
    Run ST TrustZone Project        st-tx-secure-led-toggle
    ${led1}=                        Create LED Tester    ${LED1}    defaultTimeout=2
    ${led3}=                        Create LED Tester    ${LED3}    defaultTimeout=0
    Assert LED Is Blinking          testDuration=4    onDuration=1    offDuration=1    tolerance=0.1    testerId=${led1}
    Assert LED State                false    timeout=0    testerId=${led3}
    Should Not Be In Log            ${UNMODELLED_ACCESS}    treatAsRegex=true
