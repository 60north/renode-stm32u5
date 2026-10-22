*** Settings ***
Documentation       Official STM32CubeU5 v1.9.0 example projects for STM32U575I-EV, built from
...                 unmodified ST sources (firmware-tests/cmake/st-examples.cmake), on the board
...                 description platforms/boards/stm32u575i_ev.repl. The pass criteria are those
...                 of the example readme files (LED states).
Resource            ${CURDIR}/../common/stm32u575.resource

*** Variables ***
${BOARD}            ${ROOT}/platforms/boards/stm32u575i_ev.repl
${FIRMWARE}         ${ROOT}/build/firmware
${LED5}             sysbus.gpioPortB.led5
${LED6}             sysbus.gpioPortH.led6
${UNMODELLED_ACCESS}    Unhandled (read|write)|non existing peripheral

*** Keywords ***
Run ST Example
    [Arguments]                     ${elf}
    Create STM32U575 Machine        ${BOARD}
    Create Log Tester               0
    Execute Command                 $bin=@${FIRMWARE}/${elf}
    Execute Command                 macro reset "sysbus LoadELF $bin"
    Execute Command                 runMacro $reset

*** Test Cases ***
FDCAN_Loopback Should Receive Its Own Frames
    # External loop-back mode: three frames are sent, one must arrive in Rx FIFO 0 and two in
    # Rx FIFO 1 with the sent data. Success: LED5 toggles every 100 ms; error: LED6 on.
    Run ST Example                  st-fdcan-loopback.elf
    ${led5}=                        Create LED Tester    ${LED5}    defaultTimeout=1
    ${led6}=                        Create LED Tester    ${LED6}    defaultTimeout=1
    Assert LED Is Blinking          testDuration=1    onDuration=0.1    offDuration=0.1    tolerance=0.2    testerId=${led5}
    Assert LED State                false    timeout=0    testerId=${led6}
    Should Not Be In Log            ${UNMODELLED_ACCESS}    treatAsRegex=true
