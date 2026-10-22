*** Settings ***
Documentation       STM32U5 I2C register behaviour without firmware (RM0456 Rev 7 section 65):
...                 STM32U5 fields, controller write and read with a Mocks.DummyI2CSlave target, NACK
...                 for an address without a target. Reset values are in stm32u575-reset-values.robot.
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Create Machine With Target

*** Variables ***
${I2C1}             0x40005400
${CR1}              0x00
${CR2}              0x04
${TIMINGR}          0x10
${ISR}              0x18
${ICR}              0x1C
${RXDR}             0x24
${TXDR}             0x28
${AUTOCR}           0x2C
${TARGET}           sysbus.i2c1.target
# I2C_CR1 (65.9.1), I2C_CR2 (65.9.2), I2C_ISR (65.9.7).
${PE}               ${{1 << 0}}
${FMP}              ${{1 << 24}}
${RD_WRN}           ${{1 << 10}}
${START}            ${{1 << 13}}
${NACK}             ${{1 << 15}}
${AUTOEND}          ${{1 << 25}}
${TXIS}             ${{1 << 1}}
${RXNE}             ${{1 << 2}}
${NACKF}            ${{1 << 4}}
${STOPF}            ${{1 << 5}}

*** Keywords ***
Create Machine With Target
    Create STM32U575 Machine
    Create Log Tester               0
    Execute Command                 machine LoadPlatformDescriptionFromString "target: Mocks.DummyI2CSlave @ i2c1 0x42"
    Execute Command                 logLevel 0 ${TARGET}
    Write Word                      ${I2C1} + ${CR1}     ${PE}

Start Transfer
    [Arguments]                     ${address}    ${bytes}    ${direction}=0
    # 7-bit address in SADD[7:1].
    Write Word                      ${I2C1} + ${CR2}     (${address} << 1) | ${direction} | (${bytes} << 16) | ${AUTOEND} | ${START}

*** Test Cases ***
Should Store The STM32U5 Fields Written By HAL Initialisation
    Write Word                      ${I2C1} + ${TIMINGR}    0x00C01F67
    Word Should Be                  ${I2C1} + ${TIMINGR}    0x00C01F67
    Write Word                      ${I2C1} + ${CR1}     ${PE} | ${FMP}
    Bits Should Be Set              ${I2C1} + ${CR1}     ${FMP}
    Write Word                      ${I2C1} + ${CR2}     ${NACK}
    Bits Should Be Set              ${I2C1} + ${CR2}     ${NACK}
    Word Should Be                  ${I2C1} + ${AUTOCR}  0
    Should Not Be In Log            Unhandled write    treatAsRegex=false

Should Write Bytes To The Target And Stop Automatically
    Start Transfer                  0x42    2
    Bits Should Be Set              ${I2C1} + ${ISR}     ${TXIS}
    Write Word                      ${I2C1} + ${TXDR}    0x11
    Write Word                      ${I2C1} + ${TXDR}    0x22
    Wait For Log Entry              Received 2 bytes: [0x11, 0x22]    timeout=0
    Bits Should Be Set              ${I2C1} + ${ISR}     ${STOPF}
    Write Word                      ${I2C1} + ${ICR}     ${STOPF}
    Bits Should Be Clear            ${I2C1} + ${ISR}     ${STOPF}

Should Read Bytes From The Target
    Execute Command                 ${TARGET} EnqueueResponseByte 0xA1
    Execute Command                 ${TARGET} EnqueueResponseByte 0xB2
    Start Transfer                  0x42    2    direction=${RD_WRN}
    Bits Should Be Set              ${I2C1} + ${ISR}     ${RXNE}
    Word Should Be                  ${I2C1} + ${RXDR}    0xA1
    Word Should Be                  ${I2C1} + ${RXDR}    0xB2
    Bits Should Be Clear            ${I2C1} + ${ISR}     ${RXNE}
    Bits Should Be Set              ${I2C1} + ${ISR}     ${STOPF}

Should Report NACK For An Address Without Target
    Start Transfer                  0x50    1
    Bits Should Be Set              ${I2C1} + ${ISR}     ${NACKF} | ${STOPF}
    Write Word                      ${I2C1} + ${ICR}     ${NACKF}
    Bits Should Be Clear            ${I2C1} + ${ISR}     ${NACKF}
