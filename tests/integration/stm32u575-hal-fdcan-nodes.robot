*** Settings ***
Documentation       Two STM32U575 machines on a Renode CAN hub with unmodified STM32CubeU5 HAL firmware
...                 (firmware-tests/stm32cube/fdcan-node): FDCAN1 at 250 kbit/s, 29-bit identifiers. The
...                 sender queues 6 frames in the 3-element Tx FIFO; frames take their transmission time
...                 on the bus (RM0456 section 70); a receiver that reads late loses the frames that find
...                 its 3-element Rx FIFO full; a node in initialization mode receives nothing.
Resource            ${CURDIR}/../common/stm32u575.resource
Test Teardown       Reset Emulation

*** Variables ***
${TX_ELF}           ${ROOT}/build/firmware/hal-fdcan-tx.elf
${RX_ELF}           ${ROOT}/build/firmware/hal-fdcan-rx.elf
${RX_SLOW_ELF}      ${ROOT}/build/firmware/hal-fdcan-rx-slow.elf
${FIRST_ID}         0x18EEFF00
${STEP_DONE}        9
${STEP_RECEIVING}   8
${FDCAN1}           0x4000A400
${FDCAN_CCCR}       0x18
${FDCAN_RXF0S}      0x90
${FDCAN_IR}         0x50
${RF0L}             0x4

*** Keywords ***
Create Node
    [Arguments]                     ${name}    ${elf}=${EMPTY}
    Execute Command                 mach create "${name}"
    Execute Command                 machine LoadPlatformDescription @${PLATFORM_WITH_OSCILLATORS}
    IF    $elf
        Execute Command             sysbus LoadELF @${elf}
    ELSE
        Execute Command             cpu IsHalted true
    END
    Execute Command                 connector Connect sysbus.fdcan1 canHub

Create Bus
    Execute Command                 include @${ROOT}/scripts/load-models.resc
    Execute Command                 emulation CreateCANHub "canHub"
    # Frames are exchanged between machines at synchronization points: keep them close.
    Execute Command                 emulation SetGlobalQuantum "0.0001"

Read Symbol
    [Arguments]                     ${machine}    ${symbol}    ${index}=0
    Execute Command                 mach set "${machine}"
    ${address}=                     Execute Command    sysbus GetSymbolAddress "${symbol}"
    ${address}=                     Evaluate           int("${address.strip()}", 16) + 4 * ${index}
    ${value}=                       Read Word          ${address}
    RETURN                          ${value}

Symbol Should Be
    [Arguments]                     ${machine}    ${symbol}    ${expected}    ${index}=0
    ${value}=                       Read Symbol        ${machine}    ${symbol}    ${index}
    Should Be Equal As Integers     ${value}           ${expected}    msg=${machine} ${symbol} word ${index}

*** Test Cases ***
Should Exchange Frames Between Two Nodes
    Create Bus
    Create Node                     tx    ${TX_ELF}
    Create Node                     rx    ${RX_ELF}
    Execute Command                 emulation RunFor "0.05"
    Symbol Should Be                tx    failedStep             0
    Symbol Should Be                tx    step                   ${STEP_DONE}
    Symbol Should Be                tx    sentCount              6
    # Three frames queued at once: the Tx FIFO is full while they are transmitted.
    Symbol Should Be                tx    freeLevelAfterBurst    0
    Symbol Should Be                rx    failedStep             0
    Symbol Should Be                rx    receivedCount          6
    Symbol Should Be                rx    messageLost            0
    FOR    ${i}    IN RANGE    6
        ${id}=                      Evaluate           ${FIRST_ID} + ${i}
        ${byte}=                    Evaluate           0xA0 + ${i}
        Symbol Should Be            rx    receivedIds          ${id}      ${i}
        Symbol Should Be            rx    receivedFirstByte    ${byte}    ${i}
    END

Should Lose Frames When The Rx FIFO Is Full
    Create Bus
    Create Node                     tx    ${TX_ELF}
    Create Node                     rx    ${RX_SLOW_ELF}
    Execute Command                 emulation RunFor "0.05"
    Symbol Should Be                tx    sentCount              6
    # Blocking mode: the first three frames are kept, the others are lost (RF0L).
    Symbol Should Be                rx    receivedCount          3
    Symbol Should Be                rx    messageLost            1
    FOR    ${i}    IN RANGE    3
        ${id}=                      Evaluate           ${FIRST_ID} + ${i}
        Symbol Should Be            rx    receivedIds          ${id}      ${i}
    END

Should Put The Frames On The Bus
    Create Bus
    Create Node                     tx    ${TX_ELF}
    Create CAN Tester               canHub    defaultTimeout=0.1
    ${data}=                        Wait For Frame With Id    ${FIRST_ID}
    Should Be Equal As Integers     ${data[0]}         0xA0
    ${last}=                        Evaluate           ${FIRST_ID} + 5
    ${data}=                        Wait For Frame With Id    ${last}
    Should Be Equal As Integers     ${data[0]}         0xA5

Should Not Receive In Initialization Mode
    Create Bus
    # FDCAN1 after reset: CCCR.INIT = 1, non-matching frames accepted in Rx FIFO 0 (RXGFC = 0).
    Create Node                     idle
    Create Node                     tx    ${TX_ELF}
    Execute Command                 emulation RunFor "0.05"
    Execute Command                 mach set "idle"
    ${rxf0s}=                       Read Word          ${FDCAN1} + ${FDCAN_RXF0S}
    Should Be Equal As Integers     ${rxf0s & 0x7F}    0    msg=frames received in initialization mode

Should Receive After Leaving Initialization Mode
    Create Bus
    Create Node                     idle
    Write Word                      ${FDCAN1} + ${FDCAN_CCCR}    0
    Create Node                     tx    ${TX_ELF}
    Execute Command                 emulation RunFor "0.05"
    Execute Command                 mach set "idle"
    # Nobody reads: three frames in the FIFO, the others lost.
    ${rxf0s}=                       Read Word          ${FDCAN1} + ${FDCAN_RXF0S}
    Should Be Equal As Integers     ${rxf0s & 0x7F}    3
    Bits Should Be Set              ${FDCAN1} + ${FDCAN_IR}    ${RF0L}
