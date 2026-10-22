*** Settings ***
Documentation       STM32U5 SPI register behaviour without firmware (RM0456 Rev 7 section 68):
...                 SPI3 limitations, transfers through a loopback slave, RxFIFO threshold, end of
...                 transfer and interrupt request. Reset values are in stm32u575-reset-values.robot.
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Create Machine With Loopback

*** Variables ***
${SPI1}             0x40013000
${SPI3}             0x46002000
${CR1}              0x00
${CR2}              0x04
${CFG1}             0x08
${CFG2}             0x0C
${IER}              0x10
${SR}               0x14
${IFCR}             0x18
${TXDR}             0x20
${RXDR}             0x30
# SPI_CR1 (68.8.1), SPI_CFG2 (68.8.4), SPI_IER (68.8.5), SPI_SR (68.8.6).
${SPE}              ${{1 << 0}}
${CSTART}           ${{1 << 9}}
${SSI}              ${{1 << 12}}
${MASTER}           ${{1 << 22}}
${SSM}              ${{1 << 26}}
${RXPIE}            ${{1 << 0}}
${RXP}              ${{1 << 0}}
${EOT}              ${{1 << 3}}
${TXTF}             ${{1 << 4}}
${EOTC}             ${{1 << 3}}

*** Keywords ***
Create Machine With Loopback
    Create STM32U575 Machine
    Create Log Tester               0
    Execute Command                 machine LoadPlatformDescriptionFromString "loopback1: SPI.SPILoopback @ spi1"

Start Master Transfer
    [Arguments]                     ${base}    ${frames}    ${configuration}=0x00070007
    Write Word                      ${base} + ${CFG1}    ${configuration}
    Write Word                      ${base} + ${CFG2}    ${MASTER} | ${SSM}
    Write Word                      ${base} + ${CR2}     ${frames}
    Write Word                      ${base} + ${CR1}     ${SSI} | ${SPE}
    Write Word                      ${base} + ${CR1}     ${SSI} | ${SPE} | ${CSTART}

*** Test Cases ***
Should Limit SPI3 Data Size, Threshold And Transfer Size
    # Table 696: SPI3 supports 8- and 16-bit data, a threshold of 1 to 4 frames and TSIZE up to
    # 1023; SPI1 4 to 32 bits, 1 to 16 frames and 65535. FTHLV[3:2] reserved on SPI3 (68.8.3).
    Write Word                      ${SPI1} + ${CFG1}    0x000701E7
    Write Word                      ${SPI3} + ${CFG1}    0x000701E7
    Field Should Be                 ${SPI1} + ${CFG1}    5    4    0xF
    Field Should Be                 ${SPI3} + ${CFG1}    5    4    0x3
    Write Word                      ${SPI1} + ${CFG1}    0x0007001F
    Write Word                      ${SPI3} + ${CFG1}    0x0007001F
    Field Should Be                 ${SPI1} + ${CFG1}    0    5    0x1F
    Field Should Be                 ${SPI3} + ${CFG1}    0    5    0x0F
    Write Word                      ${SPI1} + ${CR2}     0xFFFF
    Write Word                      ${SPI3} + ${CR2}     0xFFFF
    Field Should Be                 ${SPI1} + ${CR2}     0    16    0xFFFF
    Field Should Be                 ${SPI3} + ${CR2}     0    16    0x03FF

Should Receive Each Frame Sent Through A Loopback
    Start Master Transfer           ${SPI1}    2
    Write Word                      ${SPI1} + ${TXDR}    0xA5
    Bits Should Be Set              ${SPI1} + ${SR}      ${RXP}
    Bits Should Be Clear            ${SPI1} + ${SR}      ${EOT} | ${TXTF}
    Write Word                      ${SPI1} + ${TXDR}    0x5A
    Bits Should Be Set              ${SPI1} + ${SR}      ${EOT} | ${TXTF}
    # CTSIZE counts down to 0; CSTART is cleared at the end of the transfer.
    Field Should Be                 ${SPI1} + ${SR}      16    16    0
    Bits Should Be Clear            ${SPI1} + ${CR1}     ${CSTART}
    Word Should Be                  ${SPI1} + ${RXDR}    0xA5
    Word Should Be                  ${SPI1} + ${RXDR}    0x5A
    Bits Should Be Clear            ${SPI1} + ${SR}      ${RXP}
    Write Word                      ${SPI1} + ${IFCR}    ${EOTC}
    Bits Should Be Clear            ${SPI1} + ${SR}      ${EOT}

Should Count Remaining Frames On SPI1 Only
    # CTSIZE is not available on the limited SPI3 (68.8.6).
    Execute Command                 machine LoadPlatformDescriptionFromString "loopback3: SPI.SPILoopback @ spi3"
    Start Master Transfer           ${SPI1}    3
    Start Master Transfer           ${SPI3}    3
    Write Word                      ${SPI1} + ${TXDR}    0x01
    Write Word                      ${SPI3} + ${TXDR}    0x01
    Field Should Be                 ${SPI1} + ${SR}      16    16    2
    Field Should Be                 ${SPI3} + ${SR}      16    16    0
    Word Should Be                  ${SPI3} + ${RXDR}    0x01

Should Set RXP Only When The FIFO Threshold Is Reached
    # FTHLV = 1: two frames per packet.
    Start Master Transfer           ${SPI1}    4    configuration=0x00070027
    Write Word                      ${SPI1} + ${TXDR}    0x11
    Bits Should Be Clear            ${SPI1} + ${SR}      ${RXP}
    Write Word                      ${SPI1} + ${TXDR}    0x22
    Bits Should Be Set              ${SPI1} + ${SR}      ${RXP}
    Word Should Be                  ${SPI1} + ${RXDR}    0x11
    Bits Should Be Clear            ${SPI1} + ${SR}      ${RXP}

Should Transfer 16-Bit Frames
    Start Master Transfer           ${SPI1}    1    configuration=0x0007000F
    Write Word                      ${SPI1} + ${TXDR}    0xBEEF
    Word Should Be                  ${SPI1} + ${RXDR}    0xBEEF

Should Request An Interrupt When A Packet Is Received
    Start Master Transfer           ${SPI1}    1
    Write Word                      ${SPI1} + ${IER}     ${RXPIE}
    ${irq}=                         Execute Command    sysbus.spi1 IRQ IsSet
    Should Contain                  ${irq}             False
    Write Word                      ${SPI1} + ${TXDR}    0x42
    ${irq}=                         Execute Command    sysbus.spi1 IRQ IsSet
    Should Contain                  ${irq}             True
    Read Word                       ${SPI1} + ${RXDR}
    ${irq}=                         Execute Command    sysbus.spi1 IRQ IsSet
    Should Contain                  ${irq}             False

Should Report Slave Mode As Unsupported
    Write Word                      ${SPI1} + ${CR1}     ${SPE}
    Wait For Log Entry              Only master mode is supported    timeout=0
