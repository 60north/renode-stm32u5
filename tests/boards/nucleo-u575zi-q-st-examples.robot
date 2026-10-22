*** Settings ***
Documentation       Official STM32CubeU5 v1.9.0 example projects for NUCLEO-U575ZI-Q, built from
...                 unmodified ST sources (firmware-tests/cmake/st-examples.cmake), on the board
...                 description platforms/boards/nucleo_u575zi_q.repl. The pass criteria are those
...                 of the example readme files (LED states).
Resource            ${CURDIR}/../common/stm32u575.resource

*** Variables ***
${BOARD}            ${ROOT}/platforms/boards/nucleo_u575zi_q.repl
${FIRMWARE}         ${ROOT}/build/firmware
${LED1}             sysbus.gpioPortC.led1
${LED2}             sysbus.gpioPortB.led2
${LED3}             sysbus.gpioPortG.led3
${USER_BUTTON}      sysbus.gpioPortC.userButton
# I2C slave that answers a read with the bytes last written to it.
${I2C_ECHO_SLAVE}   echo: Mocks.DummyI2CSlave @ i2c1 0x30F
${I2C_ECHO}         python "echo = monitor.Machine['sysbus.i2c1.echo']; echo.DataReceived += lambda data: echo.EnqueueResponseBytes(data)"
${UNMODELLED_ACCESS}    Unhandled (read|write)|non existing peripheral

*** Keywords ***
Run ST Example
    [Arguments]                     ${elf}
    Create STM32U575 Machine        ${BOARD}
    Create Log Tester               0
    Execute Command                 $bin=@${FIRMWARE}/${elf}
    Execute Command                 macro reset "sysbus LoadELF $bin"
    Execute Command                 runMacro $reset

LED State Should Be
    [Arguments]                     ${led}    ${expected}
    ${state}=                       Execute Command    ${led} State
    ${state}=                       Strip String       ${state}
    Should Be Equal                 ${state}           ${expected}    msg=${led}

Press And Release User Button
    Execute Command                 ${USER_BUTTON} Press
    Execute Command                 emulation RunFor "0.1"
    Execute Command                 ${USER_BUTTON} Release

*** Test Cases ***
SPI_FullDuplex_ComPolling_Master Should Transfer Through A Loopback
    # SPI1 MOSI looped back to MISO; the example waits for the user button while LED1
    # toggles, then LED1 and LED2 on means the received buffer equals the sent one, LED3 on
    # means an error.
    Run ST Example                  st-spi-fullduplex-polling-master.elf
    Execute Command                 machine LoadPlatformDescriptionFromString "loopback: SPI.SPILoopback @ spi1"
    ${led1}=                        Create LED Tester    ${LED1}    defaultTimeout=1
    ${led2}=                        Create LED Tester    ${LED2}    defaultTimeout=1
    ${led3}=                        Create LED Tester    ${LED3}    defaultTimeout=1
    Assert LED Is Blinking          testDuration=1    onDuration=0.1    offDuration=0.1    tolerance=0.2    testerId=${led1}
    Assert LED State                false    testerId=${led2}
    Execute Command                 ${USER_BUTTON} Press
    Assert LED State                true     testerId=${led2}
    Assert LED State                true     testerId=${led1}
    Assert LED State                false    timeout=0.5    testerId=${led3}
    Should Not Be In Log            ${UNMODELLED_ACCESS}    treatAsRegex=true

I2C_TwoBoards_ComPolling Master Should Write And Read Back Through An Echo Slave
    # Master build (MASTER_BOARD). First button press: HAL_I2C_Master_Transmit of aTxBuffer to
    # the 10-bit address 0x30F, then LED2 on. Second press: HAL_I2C_Master_Receive, LED2 off,
    # and the received buffer must equal aTxBuffer; LED3 on means an error.
    Run ST Example                  st-i2c-twoboards-polling-master.elf
    Execute Command                 machine LoadPlatformDescriptionFromString "${I2C_ECHO_SLAVE}"
    Execute Command                 ${I2C_ECHO}
    Execute Command                 emulation RunFor "0.1"
    Press And Release User Button
    Execute Command                 emulation RunFor "0.5"
    LED State Should Be             ${LED2}            True
    LED State Should Be             ${LED3}            False
    Press And Release User Button
    Execute Command                 emulation RunFor "0.5"
    LED State Should Be             ${LED2}            False
    LED State Should Be             ${LED3}            False
    Should Not Be In Log            ${UNMODELLED_ACCESS}    treatAsRegex=true

DMA_FLASHToRAM Should Copy The Flash Buffer To RAM
    # GPDMA1 channel 0, memory to memory, 32 words (BNDT = 128 bytes) with interrupts; the
    # destination must equal the source. Success: LED1 toggles every 250 ms; error: LED3 on.
    Run ST Example                  st-dma-flash-to-ram.elf
    ${led1}=                        Create LED Tester    ${LED1}    defaultTimeout=1
    ${led3}=                        Create LED Tester    ${LED3}    defaultTimeout=1
    Assert LED Is Blinking          testDuration=2    onDuration=0.25    offDuration=0.25    tolerance=0.2    testerId=${led1}
    Assert LED State                false    timeout=0    testerId=${led3}
    Should Not Be In Log            ${UNMODELLED_ACCESS}    treatAsRegex=true
